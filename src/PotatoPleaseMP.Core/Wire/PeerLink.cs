using PotatoPleaseMP.Core.Time;

namespace PotatoPleaseMP.Core.Wire;

/// <summary>
/// Per-peer envelope state (spec 01 §3): reliable sequence numbers both ways, the newest unreliable stamp received,
/// what to acknowledge, and what the peer acknowledged of ours (bytes in flight, used by blob flow control).
/// There is no retransmission here: the transport is reliable; the seq check only detects a restarted session.
/// </summary>
public sealed class PeerLink
{
    ushort _nextRelSeq = 1;
    ushort _lastRelIn;
    ushort _newestStampIn;
    bool _anyStampIn;
    readonly Queue<(ushort Seq, int Bytes)> _unacked = new();

    public ushort NextReliableSeq => _nextRelSeq;
    public ushort LastReliableIn => _lastRelIn;
    public ushort NewestStampIn => _newestStampIn;

    /// <summary>Bytes of our reliable envelopes the peer has not acknowledged yet.</summary>
    public long UnackedBytes { get; private set; }
    public ushort RemoteAckRel { get; private set; }
    public ushort RemoteAckStamp { get; private set; }
    public double LastAckProgressAt { get; private set; }

    /// <summary>The ack values changed since we last sent them.</summary>
    public bool AckDirty { get; private set; }
    public double AckDirtySince { get; private set; }
    public double LastAckSentAt { get; private set; } = double.NegativeInfinity;
    public double LastReceiveAt { get; private set; } = double.NegativeInfinity;

    // stats (spec 20 PeerHealth inputs)
    public long EnvelopesIn, EnvelopesOut, BytesIn, BytesOut, MessagesIn, MessagesOut;
    public long DroppedEnvelopes, UnknownIds, RejectedMessages, DeflatedOut;
    public ProtocolError LastError;
    public readonly long[] InCountById = new long[256];
    public readonly long[] InBytesById = new long[256];
    public readonly long[] OutCountById = new long[256];
    public readonly long[] OutBytesById = new long[256];

    /// <summary>Takes the next reliable seq for an envelope we are about to send.</summary>
    public ushort TakeReliableSeq() => _nextRelSeq++;

    public void OnReliableSent(ushort seq, int bytes, double now)
    {
        if (_unacked.Count == 0) LastAckProgressAt = now;
        _unacked.Enqueue((seq, bytes));
        UnackedBytes += bytes;
        EnvelopesOut++;
        BytesOut += bytes;
    }

    public void OnUnreliableSent(int bytes)
    {
        EnvelopesOut++;
        BytesOut += bytes;
    }

    /// <summary>Checks an incoming reliable seq: must be exactly last + 1 (wrapping). Anything else is a gap.</summary>
    public ProtocolError AcceptReliable(ushort seq, double now)
    {
        if (seq != (ushort)(_lastRelIn + 1)) return ProtocolError.Gap;
        _lastRelIn = seq;
        MarkDirty(now);
        return ProtocolError.None;
    }

    /// <summary>
    /// Records an incoming unreliable stamp (acknowledged as the newest one seen). It does not make the ack dirty by
    /// itself: the stamp ack rides on whatever we send next, otherwise two idle peers would ack each other's acks forever.
    /// </summary>
    public void AcceptUnreliable(ushort stamp, double now)
    {
        LastReceiveAt = now;
        if (!_anyStampIn || SeqMath.Newer(stamp, _newestStampIn))
        {
            _newestStampIn = stamp;
            _anyStampIn = true;
        }
    }

    int _ackRepeats = int.MaxValue;
    const int AckRepeats = 2;

    void MarkDirty(double now)
    {
        LastReceiveAt = now;
        if (!AckDirty) AckDirtySince = now;
        AckDirty = true;
        _ackRepeats = 0;
    }

    /// <summary>Applies the peer's acknowledgement of our envelopes.</summary>
    public void OnAck(ushort ackRel, ushort ackStamp, double now)
    {
        RemoteAckRel = ackRel;
        RemoteAckStamp = ackStamp;
        bool progressed = false;
        while (_unacked.Count > 0)
        {
            var (seq, bytes) = _unacked.Peek();
            if (seq != ackRel && !SeqMath.Newer(ackRel, seq)) break;
            _unacked.Dequeue();
            UnackedBytes -= bytes;
            progressed = true;
        }
        if (progressed || _unacked.Count == 0) LastAckProgressAt = now;
    }

    /// <summary>Whether a reliable envelope should carry acks: they moved, or 250 ms passed (spec 01 §3).</summary>
    public bool WantsAck(double now) => AckDirty || now - LastAckSentAt >= 0.25;

    /// <summary>
    /// Nothing else to send: an empty unreliable envelope is due to deliver a pending ack (spec 01 §3) - 100 ms after
    /// the reliable ack moved, then repeated twice at 250 ms in case the unreliable copy is lost.
    /// </summary>
    public bool NeedsAckOnlyEnvelope(double now)
        => (AckDirty && now - AckDirtySince >= 0.1) || (!AckDirty && _ackRepeats < AckRepeats && now - LastAckSentAt >= 0.25);

    public void FillAck(ref EnvelopeHeader h, double now)
    {
        h.HasAck = true;
        h.AckRel = _lastRelIn;
        h.AckStamp = _newestStampIn;
        if (!AckDirty && _ackRepeats < int.MaxValue) _ackRepeats++;
        AckDirty = false;
        LastAckSentAt = now;
    }
}

/// <summary>
/// Latest-wins filter for unreliable state streams (spec 01 §5.1): a sample whose stamp is not newer than the last one
/// accepted for the same stream key is dropped. Keys are independent.
/// </summary>
public sealed class StampFilter
{
    readonly Dictionary<uint, ushort> _last = new();

    public long Dropped { get; private set; }

    public bool Accept(uint key, ushort stamp)
    {
        if (_last.TryGetValue(key, out var prev) && !SeqMath.Newer(stamp, prev)) { Dropped++; return false; }
        _last[key] = stamp;
        return true;
    }

    public void Forget(uint key) => _last.Remove(key);
    public void Clear() => _last.Clear();
}
