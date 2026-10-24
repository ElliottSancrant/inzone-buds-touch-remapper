using System.Text.Json;
using System.Text.Json.Serialization;

namespace InzoneMicSwitch;

public sealed class BridgeConfiguration
{
    public int Version { get; set; } = 1;
    public int WindowMs { get; set; } = 4000;
    public MicrophoneSettings Microphones { get; set; } = new();
    public Dictionary<string, ActionDefinition> Bindings { get; set; } = new()
    {
        ["ambient-triple"] = new() { Type = "microphone", Target = "toggle" }
    };
    [JsonIgnore] public string BaseDirectory { get; private set; } = Environment.CurrentDirectory;

    public static BridgeConfiguration Load(string path) => Parse(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path))!);
    public static BridgeConfiguration Parse(string json, string baseDirectory)
    {
        var config = JsonSerializer.Deserialize<BridgeConfiguration>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new ArgumentException("Configuration must be a JSON object.");
        config.BaseDirectory = Path.GetFullPath(baseDirectory);
        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (Version != 1) throw new ArgumentException("Unsupported configuration version. Expected 1.");
        if (WindowMs is < 250 or > 10000) throw new ArgumentException("windowMs must be 250..10000.");
        if (Microphones == null || string.IsNullOrWhiteSpace(Microphones.BudsName) || string.IsNullOrWhiteSpace(Microphones.DeskName))
            throw new ArgumentException("Microphone names must not be empty.");
        if (Bindings == null) throw new ArgumentException("bindings must be an object (use {} to disable all actions).");
        foreach (var (gesture, action) in Bindings)
        {
            if (gesture is not ("ambient-triple" or "volume-return" or "mic-muted" or "mic-unmuted")) throw new ArgumentException($"Unknown gesture: {gesture}.");
            if (action == null) throw new ArgumentException($"Missing action for {gesture}.");
            if (action.Type is not ("microphone" or "command" or "launch" or "none")) throw new ArgumentException($"Unknown action type: {action.Type}.");
            if (action.Type == "microphone" && action.Target is not ("toggle" or "buds" or "desk"))
                throw new ArgumentException("Microphone target must be toggle, buds, or desk.");
            if (action.Type is "command" or "launch")
            {
                if (string.IsNullOrWhiteSpace(action.FileName)) throw new ArgumentException("command/launch requires fileName.");
                if (action.Arguments == null || action.Arguments.Any(a => a == null)) throw new ArgumentException("arguments must be an array of strings.");
                if (action.TimeoutMs is < 100 or > 300000) throw new ArgumentException("timeoutMs must be 100..300000.");
            }
        }
    }
}

public sealed class MicrophoneSettings
{
    public string BudsName { get; set; } = "INZONE Buds";
    public string DeskName { get; set; } = "Yeti";
    public string? BudsId { get; set; }
    public string? DeskId { get; set; }
}

public sealed class ActionDefinition
{
    public string Type { get; set; } = "microphone";
    public string Target { get; set; } = "toggle";
    public string? FileName { get; set; }
    public string[] Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public int TimeoutMs { get; set; } = 10000;
}
