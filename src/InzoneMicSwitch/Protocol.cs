using System;
using System.Collections.Generic;
using System.Linq;

namespace InzoneMicSwitch;

public sealed record ProtocolEvent(byte EventId, byte EventType, byte Source,
    byte Destination, ushort TransactionId, byte[] Payload);

/// <summary>
/// Decodes input reports for the Sony headset HID collection (usage page FF04).
/// Call from one reader thread and Reset on reconnect. No device I/O is performed.
/// </summary>
public sealed class ProtocolDecoder
{
    public const int ReportLength = 64;
    public const byte ReportId = 2;
    private const int MaxFrameLength = 258; // Three header bytes plus an unsigned byte length.
    private const int MaxFragments = 32;
    private const int MaxAssembledPayload = 4096;
    private const ulong MaxFragmentAge = 128;
    private readonly List<byte> buffer = new(MaxFrameLength + ReportLength);
    private readonly Dictionary<FragmentKey, Fragment> fragments = new();
    private ulong sequence;

    private readonly record struct FragmentKey(byte Id, byte Type, byte Source,
        byte Destination, ushort Transaction);
    private sealed record Fragment(List<byte> Bytes, ulong LastSeen, bool Discard = false);

    /// <summary>
    /// Returns validated events, assembling HID stream splits and Sony's 50-byte
    /// transaction fragments. Repeated events are deliberately preserved: active
    /// notifications can reuse a transaction ID while their state changes.
    /// Unknown event IDs are preserved for diagnostics; callers interpret payloads.
    /// </summary>
    public IReadOnlyList<ProtocolEvent> FeedReport(byte[] report)
    {
        ArgumentNullException.ThrowIfNull(report);
        List<ProtocolEvent> result = new();
        if (report.Length != ReportLength)
        {
            Reset(); // A short or oversized read is a transport discontinuity.
            return result;
        }
        if (report[0] != ReportId)
            return result; // Another report ID is not part of this stream.
        if (report[1] > ReportLength - 2)
        {
            Reset();
            return result;
        }

        buffer.AddRange(report.AsSpan(2, report[1]).ToArray());
        while (buffer.Count != 0)
        {
            // Check each fixed header field as soon as it arrives, so malformed
            // frames cannot force us to await their untrusted declared length.
            if (buffer[0] != 0x04 ||
                (buffer.Count >= 2 && buffer[1] != 0xFF) ||
                (buffer.Count >= 3 && buffer[2] < 9) ||
                (buffer.Count >= 4 && buffer[3] != 0) ||
                (buffer.Count >= 5 && buffer[4] != 0x96) ||
                (buffer.Count >= 6 && buffer[5] != 0xC3) ||
                (buffer.Count >= 7 && buffer[6] is not (0x12 or 0x14)) ||
                (buffer.Count >= 9 && buffer[8] is not (0x10 or 0x20 or 0xA0)))
            {
                buffer.RemoveAt(0);
                continue;
            }
            if (buffer.Count < 3)
                break;
            int frameLength = 3 + buffer[2];
            if (buffer.Count < frameLength)
                break;

            int sum = 0;
            for (int i = 3; i < frameLength - 1; i++)
                sum += buffer[i];
            if ((byte)sum != buffer[frameLength - 1])
            {
                buffer.RemoveAt(0); // Rescan rather than trusting corrupt boundaries.
                continue;
            }

            var packet = new ProtocolEvent(buffer[7], buffer[8],
                (byte)(buffer[6] & 15), (byte)(buffer[6] >> 4),
                (ushort)(buffer[9] | (buffer[10] << 8)),
                buffer.GetRange(11, frameLength - 12).ToArray());
            buffer.RemoveRange(0, frameLength);
            Assemble(packet, result);
        }
        // An incomplete frame is at most 257 bytes. Appending one HID payload
        // temporarily adds at most 62 bytes, bounding storage independently of input.
        if (buffer.Count >= MaxFrameLength)
            Reset();
        return result;
    }

    public void Reset()
    {
        buffer.Clear();
        fragments.Clear();
        sequence = 0;
    }

    private void Assemble(ProtocolEvent packet, List<ProtocolEvent> result)
    {
        sequence++;
        foreach (var expired in fragments.Where(p => sequence - p.Value.LastSeen > MaxFragmentAge)
                     .Select(p => p.Key).ToArray())
            fragments.Remove(expired);
        var key = new FragmentKey(packet.EventId, packet.EventType, packet.Source,
            packet.Destination, packet.TransactionId);
        fragments.TryGetValue(key, out var existing);
        if (existing?.Discard == true)
        {
            if (packet.Payload.Length == 50)
                fragments[key] = existing with { LastSeen = sequence };
            else
                fragments.Remove(key);
            return;
        }
        if (packet.Payload.Length == 50)
        {
            if (existing is null)
            {
                if (fragments.Count >= MaxFragments)
                    fragments.Remove(fragments.MinBy(p => p.Value.LastSeen).Key);
                existing = new Fragment(new List<byte>(), sequence);
            }
            if (existing.Bytes.Count + packet.Payload.Length > MaxAssembledPayload)
            {
                // Keep a bounded tombstone until the terminator, preventing the
                // remainder of an oversized transaction from looking complete.
                fragments[key] = new Fragment(new List<byte>(), sequence, Discard: true);
                return;
            }
            existing.Bytes.AddRange(packet.Payload);
            fragments[key] = existing with { LastSeen = sequence };
            return;
        }
        if (existing is not null)
        {
            fragments.Remove(key);
            if (existing.Bytes.Count + packet.Payload.Length > MaxAssembledPayload)
                return;
            existing.Bytes.AddRange(packet.Payload);
            packet = packet with { Payload = existing.Bytes.ToArray() };
        }
        result.Add(packet);
    }

    public static void RunSelfTests() => ProtocolTests.Run();
}
