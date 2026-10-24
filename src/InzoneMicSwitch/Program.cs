using System.Diagnostics;
using System.Globalization;
using InzoneBridge;
using InzoneMicSwitch;

BridgeLog? log = null;
try
{
    var options = new Dictionary<string, string>();
    foreach (string flag in new[] { "--list", "--self-test", "--enable-switch", "--enable-actions", "--validate-config", "--background", "--help" }) options[flag] = "false";
    for (int i = 0; i < args.Length; i++)
    {
        string arg = args[i];
        if (arg is "--list" or "--self-test" or "--enable-switch" or "--enable-actions" or "--validate-config" or "--background" or "--help") options[arg] = "true";
        else if (arg is "--config" or "--seconds" or "--trigger" or "--window-ms" or "--buds-id" or "--desk-id" or "--buds-name" or "--desk-name" or "--log" or "--replay")
        {
            if (++i == args.Length) throw new ArgumentException($"Missing value for {arg}.");
            options[arg] = args[i];
        }
        else throw new ArgumentException($"Unknown argument: {arg}. Use --help.");
    }
    bool enabled = options["--enable-switch"] == "true" || options["--enable-actions"] == "true";
    bool background = options["--background"] == "true";
    if (options["--help"] == "true")
    {
        Console.WriteLine("INZONE gesture bridge (default action: microphone switching; preview only)\n" +
            "  --list                 List active microphones and Buds HID path\n" +
            "  --self-test            Run offline protocol and gesture tests\n" +
            "  --enable-actions       Execute configured actions (otherwise preview)\n" +
            "  --enable-switch        Compatibility alias for --enable-actions\n" +
            "  --config PATH          Load JSON gesture bindings and microphone settings\n" +
            "  --validate-config      Validate configuration and exit without device access\n" +
            "  --background           Quiet operation with a bounded log in LocalAppData\n" +
            "  --trigger ambient-triple|volume-return|mute-state  Default: ambient-triple\n" +
            "  --window-ms N          Whole gesture window, 250..10000; default 4000\n" +
            "  --seconds N            Stop after N seconds; 0 means Ctrl+C (default)\n" +
            "  --buds-id ID --desk-id ID  Optional exact endpoint IDs\n" +
            "  --buds-name TEXT       Name contains TEXT; default INZONE Buds\n" +
            "  --desk-name TEXT       Name contains TEXT; default Yeti\n" +
            "  --log PATH             Append raw reports and decoded events\n" +
            "  --replay PATH          Decode a saved log offline; never changes audio\n\n" +
            "Default: change noise-canceling/ambient mode three times within four seconds.\n" +
            "First active mode change counts. Two-second cooldown. Keep Buds mic unmuted.\n" +
            "Volume mode: first volume event establishes baseline; then down, up, down, up\n" +
            "(one step each) within four seconds toggles. Keep Buds mic unmuted. Two-second cooldown.\n" +
            "Mute mode: first mute event establishes baseline; later muted selects desk,\n" +
            "unmuted selects Buds. This repurposes mute; it does not mute both microphones.\n" +
            "Use Default input in your calling app.");
        return;
    }
    if (options["--self-test"] == "true")
    {
        ProtocolDecoder.RunSelfTests(); GestureDetector.RunSelfTests(); await ActionTests.RunAsync();
        Console.WriteLine("Protocol, gesture, configuration, and action self-tests passed."); return;
    }
    if (options["--list"] == "true")
    {
        foreach (var endpoint in AudioDevices.ListCapture(false)) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(endpoint));
        for (int role = 0; role < 3; role++) Console.WriteLine($"Default role {role}: {AudioDevices.GetDefaultCaptureId(role)}");
        foreach (string path in BudsHid.FindPaths()) Console.WriteLine($"Buds HID: {path}");
        return;
    }
    bool replay = options.TryGetValue("--replay", out string? replayPath);
    if (replay && enabled) throw new ArgumentException("Replay cannot be combined with --enable-actions or --enable-switch.");
    var config = options.TryGetValue("--config", out string? configPath) ? BridgeConfiguration.Load(configPath) : new BridgeConfiguration();
    string trigger = options.GetValueOrDefault("--trigger", "ambient-triple");
    if (trigger is not ("ambient-triple" or "volume-return" or "mute-state")) throw new ArgumentException("Unknown trigger.");
    if (configPath != null && options.ContainsKey("--trigger")) throw new ArgumentException("Use bindings in --config instead of --trigger.");
    if (configPath == null && trigger == "volume-return") config.Bindings = new()
    {
        ["volume-return"] = new() { Type = "microphone", Target = "toggle" }
    };
    if (configPath == null && trigger == "mute-state") config.Bindings = new()
    {
        ["mic-muted"] = new() { Type = "microphone", Target = "desk" },
        ["mic-unmuted"] = new() { Type = "microphone", Target = "buds" }
    };
    if (options.TryGetValue("--desk-name", out var deskName)) config.Microphones.DeskName = deskName;
    if (options.TryGetValue("--buds-name", out var budsName)) config.Microphones.BudsName = budsName;
    if (options.TryGetValue("--desk-id", out var deskId)) config.Microphones.DeskId = deskId;
    if (options.TryGetValue("--buds-id", out var budsId)) config.Microphones.BudsId = budsId;
    if (options.TryGetValue("--window-ms", out var windowText)) config.WindowMs = int.Parse(windowText, CultureInfo.InvariantCulture);
    config.Validate();
    if (options["--validate-config"] == "true") { Console.WriteLine("Configuration valid; no actions executed."); return; }
    int seconds = int.Parse(options.GetValueOrDefault("--seconds", "0"), CultureInfo.InvariantCulture);
    if (seconds < 0) throw new ArgumentException("--seconds must be nonnegative.");
    var decoder = new ProtocolDecoder();
    var gesture = new GestureDetector(config.WindowMs);
    if (replay && options.ContainsKey("--log")) throw new ArgumentException("Do not combine --replay with --log.");
    if (replay && background) throw new ArgumentException("Do not combine --replay with --background.");
    string? logPath = options.GetValueOrDefault("--log");
    if (background && logPath == null)
        logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InzoneMicSwitch", "logs", "bridge.log");
    var outputLock = new object();
    void Say(string text) { lock (outputLock) { if (!background) Console.WriteLine(text); log?.WriteLine(text); } }
    ActionDispatcher? dispatcher = null;
    void ProcessReport(byte[] report, DateTimeOffset timestamp, long ms)
    {
        foreach (var e in decoder.FeedReport(report))
        {
            string detail = e.EventId switch
            {
                0x21 when e.Payload.Length == 3 => $"volume={e.Payload[1]} hpMute={e.Payload[0]}",
                0x41 when e.Payload.Length == 4 => $"ambientMode={e.Payload[0]}",
                0x24 when e.Payload.Length == 3 => $"micMuted={e.Payload[0]}",
                _ => $"payload={Convert.ToHexString(e.Payload)}"
            };
            Say($"{timestamp:O} EVENT id=0x{e.EventId:X2} type=0x{e.EventType:X2} {detail}");
            string? action = gesture.Feed(e, ms);
            if (action == null) continue;
            if (replay)
            {
                string mapped = config.Bindings.TryGetValue(action, out var binding) ? binding.Type : "none";
                Say($"{timestamp:O} GESTURE {action} => {mapped} (offline; not executed)"); continue;
            }
            dispatcher!.Dispatch(action, gesture.MicMuted);
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
        Say($"Replayed {reports} raw reports; no device access or actions executed."); return;
    }
    using var singleton = new Semaphore(1, 1, "Local\\InzoneMicSwitchBridge");
    if (!singleton.WaitOne(0)) throw new IOException("Another bridge instance is running.");
    try
    {
        if (logPath != null) log = new BridgeLog(logPath);
        if (enabled && config.Bindings.Values.Any(a => a.Type == "microphone") && !AudioDevices.IsPolicyConfigAvailable())
            throw new NotSupportedException("Windows audio policy interface is unavailable.");
        var processAction = new ProcessAction(config.BaseDirectory);
        await using var activeDispatcher = new ActionDispatcher(config.Bindings, new Dictionary<string, IGestureAction>
        {
            ["microphone"] = new MicrophoneAction(config.Microphones, new WindowsAudioBackend()),
            ["command"] = processAction,
            ["launch"] = processAction
        }, enabled, message => Say($"{DateTimeOffset.Now:O} {message}"));
        dispatcher = activeDispatcher;
        Say($"{DateTimeOffset.Now:O} START pid={Environment.ProcessId}");
        Say($"{(enabled ? "ACTIONS ENABLED" : "PREVIEW ONLY")} | bindings={string.Join(", ", config.Bindings.Keys)}, window={config.WindowMs}ms. Ctrl+C stops.");
        Say("Default gesture: three ANC/ambient mode changes within four seconds. Keep Buds mic unmuted when using it as an input.");
        using var stop = new CancellationTokenSource();
        if (seconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var watch = Stopwatch.StartNew();
        while (!stop.IsCancellationRequested)
        {
            FileStream? source = null;
            try
            {
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
