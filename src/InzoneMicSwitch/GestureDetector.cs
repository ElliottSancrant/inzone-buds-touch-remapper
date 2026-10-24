namespace InzoneMicSwitch;

// Pure state machine; the caller performs any requested Windows change.
public sealed class GestureDetector(int windowMilliseconds = 4000)
{
    private int? volume;
    private int step;
    private long startedAt;
    private long lastToggle = long.MinValue / 2;
    private int? ambientMode;
    private int ambientSteps;
    private long ambientStartedAt;
    private long lastAmbientToggle = long.MinValue / 2;
    public bool? MicMuted { get; private set; }
    public void Reset()
    {
        volume = null;
        step = 0;
        MicMuted = null;
        lastToggle = long.MinValue / 2;
        ambientMode = null;
        ambientSteps = 0;
        lastAmbientToggle = long.MinValue / 2;
    }
    public string? Feed(ProtocolEvent e, long milliseconds)
    {
        if (e.EventId == 1 && e.Source == 2) { Reset(); return null; }
        if (e.Source != 4 || e.Destination != 1) return null;
        if (e.EventId == 0x41)
        {
            if (e.Payload.Length != 4 || e.Payload[0] > 2)
            {
                ambientMode = null; ambientSteps = 0;
                return null;
            }
            int mode = e.Payload[0];
            int? previousMode = ambientMode;
            ambientMode = mode;
            if (e.EventType != 0xA0) { ambientSteps = 0; return null; }
            if (previousMode == mode) return null; // Level changes and repeated state reports are not taps.
            if (ambientSteps > 0 && (milliseconds < ambientStartedAt || milliseconds - ambientStartedAt > windowMilliseconds))
                ambientSteps = 0;
            // An active notification is itself a change; the first one counts without a prior sample.
            if (ambientSteps == 0) ambientStartedAt = milliseconds;
            if (++ambientSteps < 3) return null;
            ambientSteps = 0;
            if (milliseconds - lastAmbientToggle < 2000) return null;
            lastAmbientToggle = milliseconds;
            return "ambient-triple";
        }
        if (e.EventId == 0x24 && e.Payload.Length == 3 && e.Payload[0] <= 1)
        {
            bool muted = e.Payload[0] == 1;
            bool changed = MicMuted.HasValue && MicMuted.Value != muted;
            MicMuted = muted;
            if (changed && e.EventType == 0xA0)
                return muted ? "mic-muted" : "mic-unmuted";
        }
        if (e.EventId != 0x21 || e.Payload.Length != 3 || e.Payload[1] == 255) return null;
        int next = e.Payload[1];
        int? previous = volume;
        volume = next;
        if (e.EventType != 0xA0) { step = 0; return null; }
        if (previous == next) return null;
        if (!previous.HasValue) return null; // First sample only establishes the baseline.
        if (step > 0 && (milliseconds < startedAt || milliseconds - startedAt > windowMilliseconds)) step = 0;
        int delta = next - previous.Value;
        int expected = step % 2 == 0 ? -1 : 1;
        if (delta == expected)
        {
            if (step == 0) startedAt = milliseconds;
            if (++step < 4) return null;
            step = 0; // Completed sequences never overlap.
            if (milliseconds - lastToggle >= 2000)
            {
                lastToggle = milliseconds;
                return "volume-return";
            }
            return null;
        }
        // A new single downward step can begin a fresh attempt after a mismatch.
        step = delta == -1 ? 1 : 0;
        if (step == 1) startedAt = milliseconds;
        return null;
    }
    public static void RunSelfTests()
    {
        var d = new GestureDetector();
        ProtocolEvent V(byte value, byte type = 0xA0) => new(0x21, type, 4, 1, 1, [0, value, 255]);
        ProtocolEvent M(byte value) => new(0x24, 0xA0, 4, 1, 1, [value, 255, 255]);
        void Check(bool ok) { if (!ok) throw new Exception("Gesture test failed."); }
        Check(d.Feed(V(20), 0) == null); // Baseline.
        Check(d.Feed(V(19), 100) == null);
        Check(d.Feed(V(19), 110) == null); // Duplicate reports do not add steps.
        Check(d.Feed(V(20), 1000) == null);
        Check(d.Feed(V(19), 2000) == null); // Down-up-down must not fire.
        Check(d.Feed(V(20), 3000) == "volume-return");
        Check(d.Feed(V(20), 3010) == null);
        Check(d.Feed(V(19), 3100) == null && d.Feed(V(20), 3200) == null);
        Check(d.Feed(V(19), 3300) == null && d.Feed(V(20), 3400) == null); // Cooldown.
        Check(d.Feed(V(19), 5000) == null && d.Feed(V(20), 5100) == null);
        Check(d.Feed(V(19), 5200) == null && d.Feed(V(20), 5300) == "volume-return");
        d.Reset();
        Check(d.Feed(V(20), 0) == null && d.Feed(V(21), 100) == null);
        Check(d.Feed(V(20), 200) == null); // Old up-down gesture no longer fires.
        Check(d.Feed(V(21), 300) == null); // Nor does baseline-down/up/down/up (only 3 known steps).
        d.Reset();
        Check(d.Feed(V(20), 0) == null && d.Feed(V(19), 100) == null);
        Check(d.Feed(V(20), 2000) == null && d.Feed(V(19), 3000) == null);
        Check(d.Feed(V(20), 4101) == null); // Window covers the whole gesture.
        d = new GestureDetector(1500);
        Check(d.Feed(V(20), 0) == null && d.Feed(V(19), 100) == null);
        Check(d.Feed(V(20), 600) == null && d.Feed(V(19), 1100) == null);
        Check(d.Feed(V(20), 1600) == "volume-return"); // Inclusive custom-window boundary.
        d.Reset();
        Check(d.Feed(V(20), 0) == null && d.Feed(V(18), 100) == null); // Multi-step changes do not qualify.
        Check(d.Feed(V(19), 200) == null && d.Feed(V(18), 300) == null && d.Feed(V(19), 400) == null);
        d.Reset();
        Check(d.Feed(V(20), 0) == null && d.Feed(V(19), 100) == null);
        Check(d.Feed(V(20, 0x20), 200) == null); // Non-notification interrupts sequence.
        Check(d.Feed(V(19), 300) == null && d.Feed(V(20), 400) == null);
        d.Reset();
        Check(d.Feed(V(20), 0) == null && d.Feed(V(19), 100) == null);
        Check(d.Feed(V(20), 200) == null && d.Feed(V(19), 300) == null);
        Check(d.Feed(new(1, 0xA0, 2, 1, 1, []), 350) == null); // Reconnect reset.
        Check(d.Feed(V(20), 400) == null);
        d = new GestureDetector();
        Check(d.Feed(M(1), 0) == null && d.Feed(M(0), 500) == "mic-unmuted");
        Check(d.Feed(M(0), 600) == null && d.Feed(M(1), 1000) == "mic-muted");
        ProtocolEvent A(byte mode, byte type = 0xA0, byte level = 20) => new(0x41, type, 4, 1, 1, [mode, level, 255, 0]);
        d = new GestureDetector();
        Check(d.Feed(A(2), 0) == null); // First active change counts; no extra priming tap.
        Check(d.Feed(A(2), 100) == null && d.Feed(A(2, level: 10), 200) == null);
        Check(d.Feed(A(1), 1000) == null);
        Check(d.Feed(A(2), 2000) == "ambient-triple");
        Check(d.Feed(A(1), 2100) == null && d.Feed(A(2), 2200) == null && d.Feed(A(1), 2300) == null); // Cooldown.
        Check(d.Feed(A(2), 4000) == null && d.Feed(A(1), 5000) == null && d.Feed(A(2), 6000) == "ambient-triple");
        d.Reset();
        Check(d.Feed(A(1, 0x20), 0) == null); // Passive state does not count.
        Check(d.Feed(A(2), 100) == null && d.Feed(A(1), 2000) == null);
        Check(d.Feed(A(2), 4100) == "ambient-triple"); // Inclusive four-second window.
        d.Reset();
        Check(d.Feed(A(2), 0) == null && d.Feed(A(1), 2000) == null && d.Feed(A(2), 4001) == null);
        Check(d.Feed(A(1), 5000) == null && d.Feed(A(2), 6000) == "ambient-triple"); // Fresh attempt after timeout.
        d.Reset();
        Check(d.Feed(A(2), 0) == null && d.Feed(A(1), 100) == null);
        Check(d.Feed(A(2, 0x20), 200) == null && d.Feed(A(1), 300) == null); // Host update breaks sequence.
        d.Reset();
        Check(d.Feed(A(2), 0) == null && d.Feed(A(1), 100) == null);
        Check(d.Feed(new(1, 0xA0, 2, 1, 1, []), 150) == null && d.Feed(A(2), 200) == null);
        d.Reset();
        Check(d.Feed(A(2) with { Source = 2 }, 0) == null);
        Check(d.Feed(A(1), 100) == null && d.Feed(A(2), 200) == null);
        Check(d.Feed(A(255), 300) == null); // Invalid mode clears partial sequence.
        Check(d.Feed(A(1), 400) == null && d.Feed(A(2), 500) == null);
        Check(d.Feed(A(0), 600) == "ambient-triple"); // Off counts if included in the device's cycle.
        d = new GestureDetector(500);
        Check(d.Feed(A(2), 0) == null && d.Feed(A(1), 250) == null && d.Feed(A(2), 501) == null);
    }
}
