using System.Text.Json;
using InzoneBridge;

namespace InzoneMicSwitch;

internal static class ActionTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Action test failed: " + message);
    }
    private static void Reject(string json)
    {
        try { BridgeConfiguration.Parse(json, Environment.CurrentDirectory); }
        catch (Exception ex) when (ex is ArgumentException or JsonException) { return; }
        throw new Exception("Invalid configuration was accepted: " + json);
    }
    public static async Task RunAsync()
    {
        var defaults = BridgeConfiguration.Parse("{}", Environment.CurrentDirectory);
        Check(defaults.Bindings.Count == 1 && defaults.Bindings["ambient-triple"].Target == "toggle", "default mapping");
        Check(BridgeConfiguration.Parse("{\"bindings\":{}}", Environment.CurrentDirectory).Bindings.Count == 0, "empty bindings replace defaults");
        Reject("{\"version\":2}");
        Reject("{\"windowMs\":0}");
        Reject("{\"bindings\":null}");
        Reject("{\"microphones\":null}");
        Reject("{\"binding\":{}}");
        Reject("{\"bindings\":{\"tap\":{\"type\":\"none\"}}}");
        Reject("{\"bindings\":{\"volume-return\":{\"type\":\"typo\"}}}");
        Reject("{\"bindings\":{\"volume-return\":{\"type\":\"command\"}}}");
        Reject("{\"bindings\":{\"volume-return\":{\"type\":\"microphone\",\"target\":\"typo\"}}}");
        Reject("{\"bindings\":{\"volume-return\":{\"type\":\"command\",\"fileName\":\"test\",\"arguments\":null}}}");

        var audio = new FakeAudio();
        var mic = new MicrophoneAction(defaults.Microphones, audio);
        var messages = new List<string>();
        Action<string> log = message => { lock (messages) messages.Add(message); };
        var toggle = defaults.Bindings["ambient-triple"];
        await mic.ExecuteAsync(toggle, new(true, false, log), default);
        await mic.ExecuteAsync(toggle, new(true, false, log), default);
        Check(audio.Writes.Count == 0 && messages[0].Contains("Desk Yeti") && messages[1].Contains("INZONE Buds"), "preview toggles without writing");
        await mic.ExecuteAsync(toggle, new(false, false, log), default);
        await mic.ExecuteAsync(toggle, new(false, true, log), default);
        Check(audio.Writes.SequenceEqual(["desk"]), "muted Buds are skipped");
        await mic.ExecuteAsync(toggle, new(false, false, log), default);
        Check(audio.Writes.SequenceEqual(["desk", "buds"]), "real toggle uses actual current default");
        var chosen = new MicrophoneAction(new() { BudsId = "buds", DeskId = "other", DeskName = "anything" }, audio);
        await chosen.ExecuteAsync(new() { Target = "desk" }, new(false, false, log), default);
        Check(audio.Writes.Last() == "other", "exact ID overrides name");

        var process = new ProcessAction(Environment.CurrentDirectory);
        var preview = new ActionDefinition { Type = "command", FileName = "nonexistent-self-test-program.exe", Arguments = ["two words", "quote\"", "&", "trailing\\"] };
        await process.ExecuteAsync(preview, new(true, null, log), default);
        var info = process.CreateStartInfo(preview);
        Check(!info.UseShellExecute && info.ArgumentList.SequenceEqual(preview.Arguments), "arguments preserved without shell concatenation");
        await process.ExecuteAsync(new() { Type = "command", FileName = "cmd.exe", Arguments = ["/d", "/c", "exit 0"] }, new(false, null, log), default);
        bool failed = false;
        try { await process.ExecuteAsync(new() { Type = "command", FileName = "cmd.exe", Arguments = ["/d", "/c", "exit 7"] }, new(false, null, log), default); }
        catch (IOException) { failed = true; }
        Check(failed, "nonzero exit reported");
        bool timedOut = false;
        try { await process.ExecuteAsync(new() { Type = "command", FileName = "powershell.exe", Arguments = ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], TimeoutMs = 100 }, new(false, null, log), default); }
        catch (TimeoutException) { timedOut = true; }
        Check(timedOut, "long command stopped at timeout");

        var blocking = new BlockingAction();
        var bindings = new Dictionary<string, ActionDefinition>
        {
            ["volume-return"] = new() { Type = "command" },
            ["mic-muted"] = new() { Type = "none" }
        };
        await using (var dispatcher = new ActionDispatcher(bindings, new Dictionary<string, IGestureAction> { ["command"] = blocking }, false, log))
        {
            dispatcher.Dispatch("mic-muted", false);
            dispatcher.Dispatch("mic-unmuted", false);
            Check(blocking.Calls == 0, "unmapped/none ignored");
            dispatcher.Dispatch("volume-return", false);
            dispatcher.Dispatch("volume-return", false);
            Check(blocking.Calls == 1 && blocking.Preview, "busy action skipped and preview propagated");
        }
        Check(blocking.Cancelled, "shutdown cancels active action");
    }

    private sealed class FakeAudio : IAudioBackend
    {
        public List<string> Writes { get; } = [];
        private string current = "buds";
        public IReadOnlyList<CaptureDevice> ListCapture() => [new() { Id = "buds", Name = "INZONE Buds" }, new() { Id = "desk", Name = "Desk Yeti" }, new() { Id = "other", Name = "Other microphone" }];
        public string? CurrentCapture() => current;
        public void SetCapture(string id) { current = id; Writes.Add(id); }
    }
    private sealed class BlockingAction : IGestureAction
    {
        public int Calls;
        public bool Preview, Cancelled;
        public async Task ExecuteAsync(ActionDefinition action, ActionContext context, CancellationToken cancellationToken)
        {
            Calls++; Preview = context.Preview;
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { Cancelled = true; }
        }
    }
}
