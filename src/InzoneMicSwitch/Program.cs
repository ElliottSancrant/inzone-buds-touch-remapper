using System.Diagnostics;
using System.Globalization;
using InzoneBridge;
using InzoneMicSwitch;

BridgeLog? log = null;
try
{
    var options = new Dictionary<string, string>();
    foreach (string flag in new[] { "--list", "--self-test", "--enable-switch", "--background", "--help" }) options[flag] = "false";
    for (int i = 0; i < args.Length; i++)
    {
        string arg = args[i];
        if (arg is "--list" or "--self-test" or "--enable-switch" or "--background" or "--help") options[arg] = "true";
        else if (arg is "--seconds" or "--trigger" or "--window-ms" or "--buds-id" or "--desk-id" or "--buds-name" or "--desk-name" or "--log" or "--replay")
        {
            if (++i == args.Length) throw new ArgumentException($"Missing value for {arg}.");
            options[arg] = args[i];
        }
        else throw new ArgumentException($"Unknown argument: {arg}. Use --help.");
    }
    bool enabled = options["--enable-switch"] == "true";
    bool background = options["--background"] == "true";
    if (options["--help"] == "true")
    {
        Console.WriteLine("INZONE microphone bridge (default: preview only)\n" +
            "  --list                 List active microphones and Buds HID path\n" +
            "  --self-test            Run offline protocol and gesture tests\n" +
            "  --enable-switch        Apply gestures to Windows capture defaults\n" +
            "  --background           Quiet operation with a bounded log in LocalAppData\n" +
            "  --trigger volume-return|mute-state  Default: volume-return\n" +
            "  --window-ms N          Up/down interval, 250..10000; default 4000\n" +
            "  --seconds N            Stop after N seconds; 0 means Ctrl+C (default)\n" +
            "  --buds-id ID --desk-id ID  Optional exact endpoint IDs\n" +
            "  --buds-name TEXT       Name contains TEXT; default INZONE Buds\n" +
            "  --desk-name TEXT       Name contains TEXT; default Yeti\n" +
            "  --log PATH             Append raw reports and decoded events\n" +
            "  --replay PATH          Decode a saved log offline; never changes audio\n\n" +
            "Volume mode: first volume event establishes baseline; then up one step, down one\n" +
            "step within four seconds toggles. Keep Buds mic unmuted. Two-second cooldown.\n" +
            "Mute mode: first mute event establishes baseline; later muted selects desk,\n" +
            "unmuted selects Buds. This repurposes mute; it does not mute both microphones.\n" +
            "Use Default input in your calling app.");
        return;
    }
    if (options["--self-test"] == "true")
    {
        ProtocolDecoder.RunSelfTests(); GestureDetector.RunSelfTests();
        Console.WriteLine("Protocol and gesture self-tests passed."); return;
    }
    if (options["--list"] == "true")
    {
        foreach (var endpoint in AudioDevices.ListCapture(false)) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(endpoint));
        for (int role = 0; role < 3; role++) Console.WriteLine($"Default role {role}: {AudioDevices.GetDefaultCaptureId(role)}");
        foreach (string path in BudsHid.FindPaths()) Console.WriteLine($"Buds HID: {path}");
        return;
    }
    bool replay = options.TryGetValue("--replay", out string? replayPath);
    if (replay && enabled) throw new ArgumentException("Replay cannot be combined with --enable-switch.");
    string trigger = options.GetValueOrDefault("--trigger", "volume-return");
    if (trigger is not ("volume-return" or "mute-state")) throw new ArgumentException("Unknown trigger.");
    int seconds = int.Parse(options.GetValueOrDefault("--seconds", "0"), CultureInfo.InvariantCulture);
    if (seconds < 0) throw new ArgumentException("--seconds must be nonnegative.");
    int window = int.Parse(options.GetValueOrDefault("--window-ms", "4000"), CultureInfo.InvariantCulture);
    if (window < 250 || window > 10000) throw new ArgumentException("--window-ms must be 250..10000.");
    var decoder = new ProtocolDecoder();
    var gesture = new GestureDetector(trigger, window);
    string? buds = null, desk = null, simulated = null;
    string budsName = "", deskName = "";
    void ResolveEndpoints()
    {
        var endpoints = AudioDevices.ListCapture(false);
        CaptureDevice Resolve(string flag, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Microphone name must not be empty.");
            string? chosen = options.GetValueOrDefault(flag);
            var matches = endpoints.Where(x => chosen != null ? x.Id.Equals(chosen, StringComparison.OrdinalIgnoreCase) : x.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) throw new IOException($"Waiting for active {name} capture endpoint.");
            if (matches.Length > 1) throw new ArgumentException($"Multiple active {name} endpoints. Use --list and {flag} to choose.");
            return matches[0];
        }
        var budsEndpoint = Resolve("--buds-id", options.GetValueOrDefault("--buds-name", "INZONE Buds"));
        var deskEndpoint = Resolve("--desk-id", options.GetValueOrDefault("--desk-name", "Yeti"));
        buds = budsEndpoint.Id; budsName = budsEndpoint.Name;
        desk = deskEndpoint.Id; deskName = deskEndpoint.Name;
        if (buds == desk) throw new ArgumentException("Buds and desk endpoints must differ.");
    }
    if (replay && options.ContainsKey("--log")) throw new ArgumentException("Do not combine --replay with --log.");
    if (replay && background) throw new ArgumentException("Do not combine --replay with --background.");
    string? logPath = options.GetValueOrDefault("--log");
    if (background && logPath == null)
        logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InzoneMicSwitch", "logs", "bridge.log");
    void Say(string text) { if (!background) Console.WriteLine(text); log?.WriteLine(text); }
    void ProcessReport(byte[] report, DateTimeOffset timestamp, long ms)
    {
        foreach (var e in decoder.FeedReport(report))
        {
            string detail = e.EventId switch
            {
                0x21 when e.Payload.Length == 3 => $"volume={e.Payload[1]} hpMute={e.Payload[0]}",
                0x24 when e.Payload.Length == 3 => $"micMuted={e.Payload[0]}",
                _ => $"payload={Convert.ToHexString(e.Payload)}"
            };
            Say($"{timestamp:O} EVENT id=0x{e.EventId:X2} type=0x{e.EventType:X2} {detail}");
            string? action = gesture.Feed(e, ms);
            if (action == null) continue;
            if (replay) { Say($"{timestamp:O} GESTURE {action} (offline)"); continue; }
            try
            {
                ResolveEndpoints(); // Endpoint IDs can change after a reconnect.
                string? current = enabled ? AudioDevices.GetDefaultCaptureId(2) : simulated;
                string target = action == "desk" ? desk! : action == "buds" ? buds! : current == buds ? desk! : buds!;
                string name = target == buds ? budsName : deskName;
                if (target == buds && gesture.MicMuted == true)
                {
                    Say("SKIP: Buds mic is hardware-muted. Unmute it before switching to Buds."); continue;
                }
                if (enabled)
                {
                    AudioDevices.SetDefaultCapture(target);
                    Say($"{timestamp:O} SWITCHED to {name} (all three Windows capture roles verified)");
                }
                else { simulated = target; Say($"{timestamp:O} PREVIEW would select {name}"); }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Say($"{timestamp:O} SWITCH FAILED: {ex.Message}");
            }
        }
    }
    if (replay)
    {
        int reports = 0;
        foreach (string line in File.ReadLines(replayPath!))
        {
            int marker = line.IndexOf(" RAW ", StringComparison.Ordinal);
            if (marker < 0) continue;
            var timestamp = DateTimeOffset.Parse(line[..marker], CultureInfo.InvariantCulture);
            ProcessReport(Convert.FromHexString(line[(marker + 5)..].Trim()), timestamp, timestamp.ToUnixTimeMilliseconds()); reports++;
        }
        Say($"Replayed {reports} raw reports; no device access or audio changes."); return;
    }
    using var singleton = new Semaphore(1, 1, "Local\\InzoneMicSwitchBridge");
    if (!singleton.WaitOne(0)) throw new IOException("Another bridge instance is running.");
    try
    {
        if (logPath != null) log = new BridgeLog(logPath);
        if (enabled && !AudioDevices.IsPolicyConfigAvailable()) throw new NotSupportedException("Windows audio policy interface is unavailable.");
        Say($"{DateTimeOffset.Now:O} START pid={Environment.ProcessId}");
        Say($"{(enabled ? "SWITCHING ENABLED" : "PREVIEW ONLY")} | trigger={trigger}, window={window}ms. Ctrl+C stops.");
        Say("First volume/mute sample establishes baseline. In volume mode, keep the Buds mic unmuted.");
        using var stop = new CancellationTokenSource();
        if (seconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var watch = Stopwatch.StartNew();
        while (!stop.IsCancellationRequested)
        {
            FileStream? source = null;
            try
            {
                ResolveEndpoints();
                simulated = AudioDevices.GetDefaultCaptureId(2);
                var paths = BudsHid.FindPaths();
                if (paths.Count != 1) throw new IOException($"Expected one Buds dongle in PC mode; found {paths.Count}.");
                source = BudsHid.OpenRead(paths[0]);
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
            {
                Say($"Waiting for Buds: {ex.Message}");
                await Task.Delay(2000, stop.Token); continue;
            }
            using (source)
            {
                decoder.Reset(); gesture.Reset();
                Say($"{DateTimeOffset.Now:O} Read-only HID listener connected.");
                var bytes = new byte[64];
                while (!stop.IsCancellationRequested)
                {
                    int read;
                    try { read = await source.ReadAsync(bytes, stop.Token); }
                    catch (IOException ex) { Say($"HID disconnected: {ex.Message}"); break; }
                    if (read == 0) { Say("HID disconnected."); break; }
                    var timestamp = DateTimeOffset.Now;
                    if (!background) Say($"{timestamp:O} RAW {Convert.ToHexString(bytes.AsSpan(0, read))}");
                    ProcessReport(bytes.AsSpan(0, read).ToArray(), timestamp, watch.ElapsedMilliseconds);
                }
            }
            await Task.Delay(1000, stop.Token);
        }
    }
    finally { singleton.Release(); }
}
catch (OperationCanceledException) { Console.WriteLine("Listener stopped."); log?.WriteLine($"{DateTimeOffset.Now:O} STOP"); }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); log?.WriteLine($"{DateTimeOffset.Now:O} ERROR: {ex.Message}"); Environment.ExitCode = 1; }
finally { log?.Dispose(); }
