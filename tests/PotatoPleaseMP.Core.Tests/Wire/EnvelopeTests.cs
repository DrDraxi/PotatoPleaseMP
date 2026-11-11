using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Time;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Wire;

public class EnvelopeTests
{
    static byte[] Body(params (byte Id, byte[] P)[] msgs)
    {
        using var b = new EnvelopeBody();
        foreach (var (id, p) in msgs) Assert.True(b.TryAppend(id, p, int.MaxValue));
        return b.Span.ToArray();
    }

    [Fact]
    public void Two_messages_round_trip_unknown_skipped_len0_legal()
    {
        var env = Envelope.Build(new EnvelopeHeader { Kind = EnvelopeKind.Reliable, Seq = 7 },
            Body((0x50, new byte[] { 1, 2, 3 }), (0xFE, new byte[] { 9, 9 }), (0x51, Array.Empty<byte>())));
        var p = new ParsedEnvelope();
        Assert.Equal(ProtocolError.None, Envelope.TryParse(env, Delivery.Reliable, p));
        Assert.Equal(7, p.Header.Seq);
        Assert.Equal(3, p.Messages.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, p.Payload(p.Messages[0]).ToArray());
        Assert.Equal(0xFE, p.Messages[1].Id);
        Assert.Equal(0, p.Messages[2].Length);
        Assert.Equal(3 + (2 + 3) + (2 + 2) + 2, env.Length); // header 3 B (no acks) + framed messages
    }

    [Fact]
    public void Bad_envelopes_are_dropped()
    {
        var p = new ParsedEnvelope();
        var good = Envelope.Build(new EnvelopeHeader { Kind = EnvelopeKind.Reliable, Seq = 1 }, Body((0x50, new byte[] { 1 })));
        Assert.Equal(ProtocolError.KindMismatch, Envelope.TryParse(good, Delivery.Unreliable, p));
        var badEpoch = (byte[])good.Clone();
        badEpoch[0] = (byte)((badEpoch[0] & 0x0F) | 0xF0);
        Assert.Equal(ProtocolError.EpochMismatch, Envelope.TryParse(badEpoch, Delivery.Reliable, p));
        Assert.Equal(ProtocolError.BadHeader, Envelope.TryParse(new byte[] { 1, 0 }, Delivery.Reliable, p));
        // len past the body: the whole envelope goes, no partial delivery.
        var past = Envelope.Build(new EnvelopeHeader { Kind = EnvelopeKind.Reliable, Seq = 1 }, Body((0x50, new byte[] { 1 }), (0x51, new byte[] { 2, 3 })));
        past[^3] = 40; // second message's length byte
        Assert.Equal(ProtocolError.LengthPastBody, Envelope.TryParse(past, Delivery.Reliable, p));
        Assert.Empty(p.Messages);
    }

    [Fact]
    public void Deflate_only_when_at_least_12_percent_smaller()
    {
        var compressible = Body((0x50, new byte[4000]));
        var env = Envelope.Build(new EnvelopeHeader { Kind = EnvelopeKind.Reliable, Seq = 1 }, compressible);
        Assert.True((env[0] & 8) != 0);
        var p = new ParsedEnvelope();
        Assert.Equal(ProtocolError.None, Envelope.TryParse(env, Delivery.Reliable, p));
        Assert.Equal(4000, p.Messages[0].Length);

        var random = Body((0x50, TestData.Random(4000, 1)));
        var env2 = Envelope.Build(new EnvelopeHeader { Kind = EnvelopeKind.Reliable, Seq = 1 }, random);
        Assert.True((env2[0] & 8) == 0);

        var small = Body((0x50, new byte[400]));
        Assert.True((Envelope.Build(new EnvelopeHeader { Kind = EnvelopeKind.Reliable, Seq = 1 }, small)[0] & 8) == 0);
        var unrel = Envelope.Build(new EnvelopeHeader { Kind = EnvelopeKind.Unreliable, Seq = 1 }, Body((0x10, new byte[1000])));
        Assert.True((unrel[0] & 8) == 0);
    }

    [Fact]
    public void Inflate_over_256_KB_rejected()
    {
        // Hand-made deflated envelope claiming a 300 KB body.
        var big = Compress.Deflate(new byte[300 * 1024]);
        using var w = new NetWriter();
        Envelope.WriteHeader(w, new EnvelopeHeader { Kind = EnvelopeKind.Reliable, Deflated = true, Epoch = Envelope.Epoch, Seq = 1 });
        w.VarU32(300 * 1024);
        w.Raw(big);
        Assert.Equal(ProtocolError.InflateFailed, Envelope.TryParse(w.Span, Delivery.Reliable, new ParsedEnvelope()));
    }

    [Fact]
    public void Reliable_seq_rules()
    {
        var l = new PeerLink();
        Assert.Equal(ProtocolError.None, l.AcceptReliable(1, 0));
        Assert.Equal(ProtocolError.None, l.AcceptReliable(2, 0));
        Assert.Equal(ProtocolError.Gap, l.AcceptReliable(2, 0)); // duplicate
        Assert.Equal(ProtocolError.Gap, l.AcceptReliable(4, 0)); // gap
        var w = new PeerLink();
        for (int i = 1; i <= 65535; i++) Assert.Equal(ProtocolError.None, w.AcceptReliable((ushort)i, 0));
        Assert.Equal(ProtocolError.None, w.AcceptReliable(0, 0)); // 65535 -> 0
        Assert.Equal(ProtocolError.None, w.AcceptReliable(1, 0));
    }

    [Fact]
    public void Stamp_filter_is_per_key_and_wraps()
    {
        var f = new StampFilter();
        Assert.True(f.Accept(1, 65530));
        Assert.True(f.Accept(1, 5));     // 5 is newer than 65530 (wrapped)
        Assert.False(f.Accept(1, 65530)); // stale
        Assert.False(f.Accept(1, 5));     // not newer
        Assert.True(f.Accept(2, 65530));  // keys are independent
        Assert.True(SeqMath.Newer(5, 65530));
        Assert.False(SeqMath.Newer(65530, 5));
    }

    [Fact]
    public void Acks_free_unacked_bytes()
    {
        var l = new PeerLink();
        for (ushort s = 1; s <= 3; s++) l.OnReliableSent(l.TakeReliableSeq(), 100, 0);
        Assert.Equal(300, l.UnackedBytes);
        l.OnAck(0, 0, 1);
        Assert.Equal(300, l.UnackedBytes);
        l.OnAck(2, 0, 1);
        Assert.Equal(100, l.UnackedBytes);
        l.OnAck(3, 0, 1);
        Assert.Equal(0, l.UnackedBytes);
    }

    [Fact]
    public void Ack_only_envelope_after_100ms_then_two_repeats()
    {
        var l = new PeerLink();
        Assert.False(l.NeedsAckOnlyEnvelope(0));
        l.AcceptReliable(1, 1.0);
        Assert.False(l.NeedsAckOnlyEnvelope(1.05));
        Assert.True(l.NeedsAckOnlyEnvelope(1.1));
        var h = new EnvelopeHeader();
        l.FillAck(ref h, 1.1);
        Assert.Equal(1, h.AckRel);
        Assert.False(l.NeedsAckOnlyEnvelope(1.2));
        Assert.True(l.NeedsAckOnlyEnvelope(1.35));
        l.FillAck(ref h, 1.35);
        Assert.True(l.NeedsAckOnlyEnvelope(1.6));
        l.FillAck(ref h, 1.6);
        Assert.False(l.NeedsAckOnlyEnvelope(5)); // repeats used up: two idle peers do not ack each other forever
        Assert.True(l.WantsAck(5));            // but a reliable envelope still carries it after 250 ms
    }
}

public class BatcherTests
{
    static List<(byte[] Env, Delivery D)> Flush(Batcher b, PeerLink l, double now, Action<DatagramBuilder>? fill = null)
    {
        var sent = new List<(byte[], Delivery)>();
        b.Flush(l, now, 42, (e, d) => { sent.Add((e, d)); return true; }, fill);
        return sent;
    }

    [Fact]
    public void Over_64KB_in_one_tick_makes_consecutive_envelopes()
    {
        using var b = new Batcher();
        var l = new PeerLink();
        for (int i = 0; i < 3; i++) b.QueueReliable(0x50, TestData.Random(40_000, i));
        var sent = Flush(b, l, 0);
        var rel = sent.Where(s => s.D == Delivery.Reliable).ToList();
        Assert.True(rel.Count >= 2);
        var p = new ParsedEnvelope();
        ushort expect = 1;
        int msgs = 0;
        foreach (var (env, _) in rel)
        {
            Assert.True(env.Length <= Envelope.MaxReliable);
            Assert.Equal(ProtocolError.None, Envelope.TryParse(env, Delivery.Reliable, p));
            Assert.Equal(expect++, p.Header.Seq);
            msgs += p.Messages.Count;
        }
        Assert.Equal(3, msgs);
        Assert.Throws<ArgumentException>(() => b.QueueReliable(0x50, new byte[61 * 1024]));
        Assert.Throws<ArgumentException>(() => b.QueueUnreliable(0x10, new byte[1200]));
    }

    [Fact]
    public void Full_datagram_defers_not_loses_the_slot()
    {
        using var b = new Batcher();
        using var acc = new PriorityAccumulator();
        var l = new PeerLink();
        for (uint k = 0; k < 3; k++)
        {
            uint key = k;
            acc.Set(key, MsgId.BodyState, 1, w => w.Raw(new byte[500]));
        }
        var s1 = Flush(b, l, 0, d => acc.Fill(d, 1 / 30.0));
        Assert.Single(s1);
        Assert.True(s1[0].Env.Length <= Envelope.MaxUnreliable);
        Assert.Equal(1, acc.Deferred);
        Assert.Equal(1, Enumerable.Range(0, 3).Count(k => acc.IsDirty((uint)k)));
        Flush(b, l, 1 / 30.0, d => acc.Fill(d, 1 / 30.0));
        Assert.Equal(0, Enumerable.Range(0, 3).Count(k => acc.IsDirty((uint)k)));
    }

    [Fact]
    public void Priority_accumulator_300_bodies_high_first_none_starves()
    {
        using var acc = new PriorityAccumulator();
        var firstWritten = new Dictionary<uint, int>();
        for (uint k = 0; k < 300; k++) acc.Set(k, MsgId.BodyState, k < 30 ? 10f : 1f, w => w.Raw(new byte[10]));
        using var b = new Batcher();
        var l = new PeerLink();
        for (int t = 1; t <= 6; t++)
        {
            Flush(b, l, t / 30.0, d => acc.Fill(d, 1 / 30.0));
            for (uint k = 0; k < 300; k++)
                if (!firstWritten.ContainsKey(k) && acc.LastWrittenTick(k) > 0) firstWritten[k] = acc.LastWrittenTick(k);
        }
        Assert.Equal(300, firstWritten.Count); // every body within 6 ticks
        for (uint k = 0; k < 30; k++) Assert.Equal(1, firstWritten[k]); // high priority in the first datagram
    }

    [Fact]
    public void Empty_unreliable_envelope_only_carries_acks()
    {
        using var b = new Batcher();
        var l = new PeerLink();
        Assert.Empty(Flush(b, l, 0));
        l.AcceptReliable(1, 0);
        Assert.Empty(Flush(b, l, 0.05));
        var s = Flush(b, l, 0.1);
        Assert.Single(s);
        Assert.Equal(Delivery.Unreliable, s[0].D);
        Assert.Equal(Envelope.HeaderSize + Envelope.AckSize, s[0].Env.Length);
        Assert.Equal(1, l.NextReliableSeq); // no reliable seq burnt
    }
}
