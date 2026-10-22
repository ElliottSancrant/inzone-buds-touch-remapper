using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InzoneMicSwitch;

// Read-only access to the vendor HID collection used by INZONE Hub.
internal static class BudsHid
{
    private static readonly Guid HidGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public uint Size; public Guid ClassGuid; public uint Flags; public UIntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, IntPtr caps);

    public static List<string> FindPaths()
    {
        Guid guid = HidGuid;
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12); // present + interfaces
        if (set == new IntPtr(-1)) throw new Win32Exception();
        var paths = new List<string>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal(checked((int)size));
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero)) throw new Win32Exception();
                    string path = Marshal.PtrToStringUni(detail + 4)!;
                    if (path.Contains("vid_054c&pid_0ec2&mi_05&col03", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    public static FileStream OpenRead(string path)
    {
        var handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        try
        {
            if (!HidD_GetPreparsedData(handle, out IntPtr data)) throw new Win32Exception();
            try
            {
                IntPtr caps = Marshal.AllocHGlobal(64);
                try
                {
                    if (HidP_GetCaps(data, caps) != 0x110000) throw new IOException("Cannot read HID capabilities.");
                    ushort usagePage = (ushort)Marshal.ReadInt16(caps, 2);
                    ushort inputLength = (ushort)Marshal.ReadInt16(caps, 4);
                    if (usagePage != 0xff04 || inputLength != 64)
                        throw new IOException($"Unexpected HID descriptor: page {usagePage:X4}, input length {inputLength}.");
                }
                finally { Marshal.FreeHGlobal(caps); }
            }
            finally { HidD_FreePreparsedData(data); }
            return new FileStream(handle, FileAccess.Read, 64, true);
        }
        catch { handle.Dispose(); throw; }
    }
}
