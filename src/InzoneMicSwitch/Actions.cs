using System.Diagnostics;
using InzoneBridge;

namespace InzoneMicSwitch;

public sealed record ActionContext(bool Preview, bool? MicMuted, Action<string> Log);

// Handlers know nothing about HID or gesture recognition. Add new action types here.
public interface IGestureAction
{
    Task ExecuteAsync(ActionDefinition action, ActionContext context, CancellationToken cancellationToken);
}

public interface IAudioBackend
{
    IReadOnlyList<CaptureDevice> ListCapture();
    string? CurrentCapture();
    void SetCapture(string id);
}

internal sealed class WindowsAudioBackend : IAudioBackend
{
    public IReadOnlyList<CaptureDevice> ListCapture() => AudioDevices.ListCapture(false).ToArray();
    public string? CurrentCapture() => AudioDevices.GetDefaultCaptureId(2);
    public void SetCapture(string id) => AudioDevices.SetDefaultCapture(id);
}

public sealed class MicrophoneAction(MicrophoneSettings settings, IAudioBackend audio) : IGestureAction
{
    private string? simulated;
    public Task ExecuteAsync(ActionDefinition action, ActionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoints = audio.ListCapture();
        CaptureDevice Resolve(string? id, string name)
        {
            var matches = endpoints.Where(e => id != null ? e.Id.Equals(id, StringComparison.OrdinalIgnoreCase) : e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new IOException($"Expected one active microphone matching {name}; found {matches.Length}. Use --list and an exact ID if needed.");
            return matches[0];
        }
        var buds = Resolve(settings.BudsId, settings.BudsName);
        var desk = Resolve(settings.DeskId, settings.DeskName);
        if (buds.Id == desk.Id) throw new ArgumentException("Buds and desk endpoints must differ.");
        string? current = context.Preview ? simulated ?? audio.CurrentCapture() : audio.CurrentCapture();
        var target = action.Target == "buds" ? buds : action.Target == "desk" ? desk : current == buds.Id ? desk : buds;
        if (target == buds && context.MicMuted == true)
        {
            context.Log("SKIP: Buds mic is hardware-muted. Unmute it before switching to Buds.");
            return Task.CompletedTask;
        }
        if (context.Preview)
        {
            simulated = target.Id;
            context.Log($"PREVIEW would select {target.Name}");
        }
        else
        {
            audio.SetCapture(target.Id);
            context.Log($"SWITCHED to {target.Name} (all three Windows capture roles verified)");
        }
        return Task.CompletedTask;
    }
}

public sealed class ProcessAction(string baseDirectory) : IGestureAction
{
    public ProcessStartInfo CreateStartInfo(ActionDefinition action)
    {
        string file = Environment.ExpandEnvironmentVariables(action.FileName!);
        if (!Path.IsPathRooted(file) && (file.Contains('/') || file.Contains('\\')))
            file = Path.GetFullPath(file, baseDirectory);
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(action.WorkingDirectory ?? "."), baseDirectory)
        };
        foreach (string arg in action.Arguments) info.ArgumentList.Add(Environment.ExpandEnvironmentVariables(arg));
        return info;
    }

    public async Task ExecuteAsync(ActionDefinition action, ActionContext context, CancellationToken cancellationToken)
    {
        var info = CreateStartInfo(action);
        if (context.Preview)
        {
            context.Log($"PREVIEW would {action.Type}: {info.FileName} {System.Text.Json.JsonSerializer.Serialize(info.ArgumentList)}");
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new IOException("Process did not start.");
        if (action.Type == "launch")
        {
            context.Log($"LAUNCHED {info.FileName}");
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(action.TimeoutMs);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Process already exited. */ }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"Command exceeded {action.TimeoutMs}ms and was stopped.");
        }
        if (process.ExitCode != 0) throw new IOException($"Command exited with code {process.ExitCode}.");
        context.Log($"COMMAND completed: {info.FileName}");
    }
}

// One action at a time. No delayed queue: a gesture during a running command is skipped.
// HID reads continue while commands run. Call Dispatch only from the listener thread.
public sealed class ActionDispatcher(
    IReadOnlyDictionary<string, ActionDefinition> bindings,
    IReadOnlyDictionary<string, IGestureAction> handlers,
    bool enabled, Action<string> log) : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task pending = Task.CompletedTask;

    public void Dispatch(string gesture, bool? micMuted)
    {
        if (!bindings.TryGetValue(gesture, out var definition) || definition.Type == "none") return;
        if (!pending.IsCompleted) { log($"SKIP {gesture}: previous action is still running."); return; }
        pending = ExecuteAsync(definition, micMuted);
    }

    private async Task ExecuteAsync(ActionDefinition definition, bool? micMuted)
    {
        try { await handlers[definition.Type].ExecuteAsync(definition, new(!enabled, micMuted, log), stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { log($"ACTION FAILED: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        await pending;
        stop.Dispose();
    }
}
