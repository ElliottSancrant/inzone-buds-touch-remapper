using System;
using System.Collections.Generic;
using System.Linq;

namespace InzoneMicSwitch;

internal static class ProtocolTests
{
    internal static void Run()
    {
        var decoder = new ProtocolDecoder();
        // Fixed wire vector: checksum A2 is the low byte of the sum from dummy
        // through payload, independently of the synthetic frame builder below.
        byte[] frame = Convert.FromHexString("04FF0B0096C31424A03412012AA2");
        var one = decoder.FeedReport(Report(frame));
        Check(one.Count == 1 && one[0].EventId == 0x24 && one[0].EventType == 0xA0 &&
              one[0].Source == 4 && one[0].Destination == 1 &&
              one[0].TransactionId == 0x1234 && one[0].Payload.SequenceEqual(new byte[] { 1, 42 }),
            "field decoding and checksum");

        for (int split = 1; split < frame.Length; split++)
        {
            decoder.Reset();
            Check(decoder.FeedReport(Report(frame[..split])).Count == 0, "split prefix");
            Check(decoder.FeedReport(Report(frame[split..])).Count == 1, "split suffix");
        }
        decoder.Reset();
        Check(decoder.FeedReport(Report(frame.Concat(frame).ToArray())).Count == 2,
            "concatenated packets and preserved duplicate notifications");
        byte[] changed = Frame(new byte[] { 0, 42 }, 0x1234);
        var reused = decoder.FeedReport(Report(frame.Concat(changed).ToArray()));
        Check(reused.Count == 2 && reused[0].Payload[0] != reused[1].Payload[0],
            "same transaction is not state deduplication");

        foreach (int offset in new[] { 0, 1, 2, 3, 4, 5, 6, 8, frame.Length - 1 })
        {
            decoder.Reset();
            byte[] broken = (byte[])frame.Clone();
            broken[offset] = offset == 2 ? (byte)1 : (byte)(broken[offset] ^ 0x55);
            Check(decoder.FeedReport(Report(broken)).Count == 0, "reject malformed field " + offset);
            Check(decoder.FeedReport(Report(frame)).Count == 1, "recover malformed field " + offset);
        }
        decoder.Reset();
        Check(decoder.FeedReport(Report(new byte[] { 99, 4, 7, 0 }.Concat(frame).ToArray())).Count == 1,
            "garbage prefix recovery");
        decoder.Reset();
        decoder.FeedReport(Report(frame[..7]));
        byte[] otherReport = Report(new byte[] { 99 });
        otherReport[0] = 7;
        Check(decoder.FeedReport(otherReport).Count == 0 &&
              decoder.FeedReport(Report(frame[7..])).Count == 1, "ignore unrelated report ID");
        byte[] excessive = Report(Array.Empty<byte>());
        excessive[1] = 63;
        Check(decoder.FeedReport(excessive).Count == 0, "reject excessive HID data length");
        Check(decoder.FeedReport(new byte[63]).Count == 0, "reject truncated HID report");
        Check(decoder.FeedReport(new byte[65]).Count == 0, "reject oversized HID report");
        Check(decoder.FeedReport(Report(Array.Empty<byte>())).Count == 0, "empty report");
        decoder.FeedReport(Report(frame[..7]));
        decoder.Reset();
        Check(decoder.FeedReport(Report(frame[7..])).Count == 0 &&
              decoder.FeedReport(Report(frame)).Count == 1, "reset on reconnect");

        decoder.Reset();
        byte[] largestPayload = Enumerable.Range(0, 246).Select(i => (byte)i).ToArray();
        var largest = Feed(decoder, Frame(largestPayload));
        Check(largest.Count == 1 && largest[0].Payload.SequenceEqual(largestPayload),
            "maximum frame reassembly");
        decoder.Reset();
        byte[] chunk = Enumerable.Range(0, 50).Select(i => (byte)i).ToArray();
        Check(Feed(decoder, Frame(chunk, 7)).Count == 0, "50-byte transaction fragment");
        Check(Feed(decoder, Frame(new byte[] { 8 }, 8)).Single().Payload[0] == 8,
            "interleaved distinct transaction");
        Check(Feed(decoder, Frame(new byte[] { 9 }, 7)).Single().Payload.SequenceEqual(chunk.Append((byte)9)),
            "transaction fragment assembly");
        decoder.Reset();
        for (int i = 0; i < 1000; i++)
            Feed(decoder, Frame(chunk, (ushort)i));
        Check(Feed(decoder, Frame(new byte[] { 5 }, 1001)).Single().Payload.Length == 1,
            "bounded fragment cache remains usable");
        decoder.Reset();
        for (int i = 0; i < 100; i++)
            Check(Feed(decoder, Frame(chunk, 1)).Count == 0, "bounded oversized transaction");
        Check(Feed(decoder, Frame(new byte[] { 5 }, 1)).Count == 0,
            "discard oversized transaction through terminator");
        Check(Feed(decoder, Frame(new byte[] { 5 }, 1)).Count == 1,
            "recover after oversized transaction terminator");
        decoder.Reset();
        for (int i = 0; i < 1000; i++)
            decoder.FeedReport(Report(Enumerable.Repeat((byte)0xFF, 62).ToArray()));
        Check(decoder.FeedReport(Report(frame)).Count == 1, "sustained garbage recovery");
    }

    private static List<ProtocolEvent> Feed(ProtocolDecoder decoder, byte[] stream)
    {
        List<ProtocolEvent> result = new();
        for (int i = 0; i < stream.Length; i += 62)
            result.AddRange(decoder.FeedReport(Report(stream.AsSpan(i, Math.Min(62, stream.Length - i)).ToArray())));
        return result;
    }

    private static byte[] Frame(byte[] payload, ushort transaction = 0)
    {
        byte[] bytes = new byte[12 + payload.Length];
        bytes[0] = 4;
        bytes[1] = 0xFF;
        bytes[2] = checked((byte)(9 + payload.Length));
        bytes[4] = 0x96;
        bytes[5] = 0xC3;
        bytes[6] = 0x14;
        bytes[7] = 0x24;
        bytes[8] = 0xA0;
        bytes[9] = (byte)transaction;
        bytes[10] = (byte)(transaction >> 8);
        payload.CopyTo(bytes, 11);
        bytes[^1] = unchecked((byte)bytes.Skip(3).Take(bytes.Length - 4).Sum(b => b));
        return bytes;
    }

    private static byte[] Report(byte[] data)
    {
        if (data.Length > 62) throw new ArgumentException("Test report exceeds HID size.");
        byte[] report = new byte[64];
        report[0] = 2;
        report[1] = (byte)data.Length;
        data.CopyTo(report, 2);
        return report;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Protocol self-test failed: " + name);
    }
}
