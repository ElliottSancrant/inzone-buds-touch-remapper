namespace InzoneMicSwitch;

// Pure state machine; the caller performs any requested Windows change.
public sealed class GestureDetector(string trigger, int windowMilliseconds = 4000)
{
    private int? volume;
    private int? origin;
    private long risingAt;
    private long lastToggle = long.MinValue / 2;
    public bool? MicMuted { get; private set; }
    public void Reset()
    {
        volume = origin = null;
        MicMuted = null;
        lastToggle = long.MinValue / 2;
    }
    public string? Feed(ProtocolEvent e, long milliseconds)
    {
        if (e.EventId == 1 && e.Source == 2) { Reset(); return null; }
        if (e.Source != 4 || e.Destination != 1) return null;
        if (e.EventId == 0x24 && e.Payload.Length == 3 && e.Payload[0] <= 1)
        {
            bool muted = e.Payload[0] == 1;
            bool changed = MicMuted.HasValue && MicMuted.Value != muted;
            MicMuted = muted;
            if (trigger == "mute-state" && changed && e.EventType == 0xA0)
                return muted ? "desk" : "buds";
        }
        if (e.EventId != 0x21 || e.Payload.Length != 3 || e.Payload[1] == 255) return null;
        int next = e.Payload[1];
        int? previous = volume;
        volume = next;
        if (e.EventType != 0xA0) { origin = null; return null; }
        if (previous == next) return null;
        if (origin.HasValue && next == origin.Value && previous == origin.Value + 1 &&
            milliseconds - risingAt >= 0 && milliseconds - risingAt <= windowMilliseconds)
        {
            origin = null;
            if (trigger == "volume-return" && milliseconds - lastToggle >= 2000)
            {
                lastToggle = milliseconds;
                return "toggle";
            }
            return null;
        }
        origin = previous.HasValue && next == previous.Value + 1 ? previous : null;
        risingAt = milliseconds;
        return null;
    }
    public static void RunSelfTests()
    {
        var d = new GestureDetector("volume-return", 1500);
        ProtocolEvent V(byte value, byte type = 0xA0) => new(0x21, type, 4, 1, 1, [0, value, 255]);
        ProtocolEvent M(byte value) => new(0x24, 0xA0, 4, 1, 1, [value, 255, 255]);
        void Check(bool ok) { if (!ok) throw new Exception("Gesture test failed."); }
        Check(d.Feed(V(20), 0) == null);
        Check(d.Feed(V(21), 100) == null);
        Check(d.Feed(V(21), 110) == null);
        Check(d.Feed(V(20), 500) == "toggle");
        Check(d.Feed(V(20), 600) == null);
        Check(d.Feed(V(21), 700) == null && d.Feed(V(20), 800) == null);
        Check(d.Feed(V(21), 3000) == null && d.Feed(V(20), 5000) == null);
        Check(d.Feed(V(21, 0x20), 6000) == null && d.Feed(V(20), 6200) == null);
        d.Reset();
        Check(d.Feed(V(21), 7000) == null && d.Feed(V(20), 7100) == null);
        Check(d.Feed(V(21), 7200) == null && d.Feed(V(22), 7300) == null);
        Check(d.Feed(V(23), 7400) == null && d.Feed(V(22), 7600) == "toggle");
        d = new GestureDetector("mute-state");
        Check(d.Feed(M(1), 0) == null && d.Feed(M(0), 500) == "buds");
        Check(d.Feed(M(0), 600) == null && d.Feed(M(1), 1000) == "desk");
        d = new GestureDetector("volume-return");
        Check(d.Feed(V(20), 0) == null && d.Feed(V(21), 100) == null);
        Check(d.Feed(V(20), 3000) == "toggle");
    }
}
