// Compile/load with: Add-Type -Path "$PSScriptRoot\AudioDevices.cs"
// Listing is read-only. Only SetDefaultCapture changes Windows settings.
// SetDefaultEndpoint uses Windows' undocumented IPolicyConfig interface.
// No third-party packages, elevation, or audio-device registry writes required.
// Keep this file compatible with Windows PowerShell 5.1's C# compiler, which
// rejects #nullable directives (even in excluded conditional blocks). COM out
// interfaces are null until successful HRESULT checks and released in finally.
// Suppress nullable-flow diagnostics only in this legacy interop file; modern
// callers still receive explicit nullable-return metadata where appropriate.
#if NET
#pragma warning disable 8600, 8601, 8602, 8603, 8604, 8625
#endif
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace InzoneBridge
{
    public sealed class CaptureDevice
    {
        public CaptureDevice() { Id = String.Empty; Name = String.Empty; }
        public string Id { get; set; }
        public string Name { get; set; }
        public uint State { get; set; }
        public override string ToString() { return Name + " [" + Id + "]"; }
    }

    public static class AudioDevices
    {
        private static readonly Guid EnumeratorClass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid PolicyClass = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
        private static readonly object SwitchLock = new object();

        private static IMMDeviceEnumerator Enumerator()
        {
            return (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, true));
        }
        private static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        public static CaptureDevice[] ListCapture(bool includeInactive)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            var result = new List<CaptureDevice>();
            try
            {
                enumerator = Enumerator();
                Check(enumerator.EnumAudioEndpoints(1, includeInactive ? 15u : 1u, out collection));
                uint count;
                Check(collection.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    IPropertyStore properties = null;
                    try
                    {
                        Check(collection.Item(i, out device));
                        string id;
                        uint state;
                        Check(device.GetId(out id));
                        Check(device.GetState(out state));
                        Check(device.OpenPropertyStore(0, out properties));
                        var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
                        PropVariant value;
                        Check(properties.GetValue(ref key, out value));
                        string name;
                        try { name = value.Type == 31 ? (Marshal.PtrToStringUni(value.Pointer) ?? id) : id; }
                        finally { PropVariantClear(ref value); }
                        result.Add(new CaptureDevice { Id = id, Name = name, State = state });
                    }
                    finally { Release(properties); Release(device); }
                }
                return result.ToArray();
            }
            finally { Release(collection); Release(enumerator); }
        }

        // role: 0 = Console, 1 = Multimedia, 2 = Communications.
        // Null means Windows currently has no default capture endpoint for this role.
#if NET
        [return: System.Diagnostics.CodeAnalysis.MaybeNull]
#endif
        public static string GetDefaultCaptureId(int role)
        {
            if (role < 0 || role > 2) throw new ArgumentOutOfRangeException("role");
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            try
            {
                enumerator = Enumerator();
                int hr = enumerator.GetDefaultAudioEndpoint(1, role, out device);
                if (hr == unchecked((int)0x80070490)) return null;
                Check(hr);
                string id;
                Check(device.GetId(out id));
                return id;
            }
            finally { Release(device); Release(enumerator); }
        }

        // A read-only compatibility check: creates/query-interfaces the COM object,
        // but invokes no policy setter. It does not prove a future switch succeeds.
        public static bool IsPolicyConfigAvailable()
        {
            object policy = null;
            try
            {
                policy = Activator.CreateInstance(Type.GetTypeFromCLSID(PolicyClass, true));
                return policy is IPolicyConfig;
            }
            catch (COMException) { return false; }
            catch (TypeLoadException) { return false; }
            finally { Release(policy); }
        }

        // Explicitly selects an ACTIVE CAPTURE endpoint for all three Windows roles.
        // Names are deliberately not accepted: duplicate/stale device names are common.
        // The three setters are not atomic. On failure, restore previous defaults where
        // possible and report any rollback failures to the caller.
        public static void SetDefaultCapture(string endpointId)
        {
            if (String.IsNullOrWhiteSpace(endpointId)) throw new ArgumentException("An endpoint ID is required.", "endpointId");
            lock (SwitchLock)
            {
                bool found = false;
                foreach (CaptureDevice device in ListCapture(false))
                    if (String.Equals(device.Id, endpointId, StringComparison.OrdinalIgnoreCase)) found = true;
                if (!found) throw new InvalidOperationException("Target is not an active capture endpoint: " + endpointId);
                string[] previous = new string[3];
                for (int role = 0; role < 3; role++) previous[role] = GetDefaultCaptureId(role);
                object instance = null;
                var changed = new List<int>();
                try
                {
                    instance = Activator.CreateInstance(Type.GetTypeFromCLSID(PolicyClass, true));
                    var policy = (IPolicyConfig)instance;
                    try
                    {
                        for (int role = 0; role < 3; role++)
                        {
                            if (String.Equals(previous[role], endpointId, StringComparison.OrdinalIgnoreCase)) continue;
                            // Include attempted setters in rollback, even if a failing
                            // COM call changed state before returning its error.
                            changed.Add(role);
                            Check(policy.SetDefaultEndpoint(endpointId, role));
                        }
                        for (int role = 0; role < 3; role++)
                            if (!String.Equals(GetDefaultCaptureId(role), endpointId, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Windows default capture verification failed for role " + role + ".");
                    }
                    catch (Exception error)
                    {
                        var failures = new List<Exception>();
                        failures.Add(error);
                        changed.Reverse();
                        foreach (int role in changed)
                        {
                            try
                            {
                                if (previous[role] == null) throw new InvalidOperationException("Cannot restore an empty default for role " + role + ".");
                                Check(policy.SetDefaultEndpoint(previous[role], role));
                                if (!String.Equals(GetDefaultCaptureId(role), previous[role], StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidOperationException("Restoring the previous default failed verification for role " + role + ".");
                            }
                            catch (Exception restoreError) { failures.Add(restoreError); }
                        }
                        if (failures.Count > 1) throw new AggregateException("Switch failed and some defaults could not be restored.", failures);
                        throw;
                    }
                }
                finally { Release(instance); }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort Type, Reserved1, Reserved2, Reserved3;
            public IntPtr Pointer, Extra;
        }
        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, out IntPtr instance);
            [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }
        // The ten methods preceding SetDefaultEndpoint preserve its vtable position.
        // They are never invoked by this helper. This interface is undocumented.
        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(IntPtr id, IntPtr format);
            [PreserveSig] int GetDeviceFormat(IntPtr id, int useDefault, IntPtr format);
            [PreserveSig] int ResetDeviceFormat(IntPtr id);
            [PreserveSig] int SetDeviceFormat(IntPtr id, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod(IntPtr id, int useDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
            [PreserveSig] int SetProcessingPeriod(IntPtr id, IntPtr period);
            [PreserveSig] int GetShareMode(IntPtr id, IntPtr mode);
            [PreserveSig] int SetShareMode(IntPtr id, IntPtr mode);
            [PreserveSig] int GetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility(IntPtr id, int visible);
        }
    }
}
#if NET
#pragma warning restore 8600, 8601, 8602, 8603, 8604, 8625
#endif
