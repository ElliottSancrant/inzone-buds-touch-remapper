namespace InzoneMicSwitch;

// Keep an unattended bridge from growing an unlimited diagnostic file.
internal sealed class BridgeLog : IDisposable
{
    private readonly string path;
    private StreamWriter writer;
    public BridgeLog(string path)
    {
        this.path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        writer = Open();
    }
    private StreamWriter Open() => new(path, append: true) { AutoFlush = true };
    public void WriteLine(string text)
    {
        if (writer.BaseStream.Length >= 5 * 1024 * 1024)
        {
            writer.Dispose();
            File.Move(path, path + ".1", overwrite: true);
            writer = Open();
        }
        writer.WriteLine(text);
    }
    public void Dispose() => writer.Dispose();
}
