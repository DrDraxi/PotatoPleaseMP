using PotatoPleaseMP.Core.Net;

namespace PotatoPleaseMP.Core.Wire;

/// <summary>
/// One unreliable datagram being filled at the tick flush (spec 01 §5.3). Modules (through
/// <see cref="PriorityAccumulator"/> or directly) add messages until it is full.
/// </summary>
public sealed class DatagramBuilder : IDisposable
{
    internal readonly EnvelopeBody Body = new();

    /// <summary>Body bytes available in one datagram (1100 B minus the 7-byte header with acks).</summary>
    public const int BodyCap = Envelope.MaxUnreliable - Envelope.HeaderSize - Envelope.AckSize;

    public int Remaining => BodyCap - Body.Length;
    public int Count => Body.Count;

    public bool TryAdd(MsgId id, ReadOnlySpan<byte> payload) => Body.TryAppend((byte)id, payload, BodyCap);

    /// <summary>Bytes one message of <paramref name="payloadLength"/> takes in the datagram.</summary>
    public static int Cost(int payloadLength) => Envelope.FramingSize(payloadLength) + payloadLength;

    internal void Clear() => Body.Clear();
    public void Dispose() => Body.Dispose();
}

/// <summary>
/// Outgoing queues for one peer (spec 01 §5.3): reliable messages go out in call order at the tick flush, packed into
/// envelopes of at most 64 KB (more envelopes with consecutive seqs when a tick has more); unreliable messages and stream
/// slots share one datagram of at most 1100 B per tick. An ack with nothing else to send goes in an empty unreliable
/// envelope (never burning a reliable seq).
/// </summary>
public sealed class Batcher : IDisposable
{
    /// <summary>Body bytes of one reliable envelope.</summary>
    public const int ReliableBodyCap = Envelope.MaxReliable - Envelope.HeaderSize - Envelope.AckSize;
    /// <summary>Queued unreliable messages kept for the next tick when a datagram is full; older ones are dropped.</summary>
    public const int MaxDeferredUnreliable = 256;

    readonly NetWriter _rel = new(4096);
    readonly List<(int Off, int Len)> _relItems = new();
    readonly List<(byte Id, byte[] Payload)> _unrel = new();
    readonly EnvelopeBody _body = new();
    readonly DatagramBuilder _dgram = new();

    public int QueuedReliableBytes => _rel.Length;
    public int QueuedReliableCount => _relItems.Count;
    public int QueuedUnreliableCount => _unrel.Count;
    public long UnreliableDeferred { get; private set; }
    public long UnreliableDropped { get; private set; }

    /// <summary>Queues a reliable message. A payload over 60 KB throws: send it as a blob.</summary>
    public void QueueReliable(byte id, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MessageRegistry.MaxPayload)
            throw new ArgumentException($"Payload of {payload.Length} B exceeds {MessageRegistry.MaxPayload} B; send it as a blob.", nameof(payload));
        int off = _rel.Length;
        _rel.U8(id);
        _rel.VarU32((uint)payload.Length);
        _rel.Raw(payload);
        _relItems.Add((off, _rel.Length - off));
    }

    public void QueueUnreliable(byte id, ReadOnlySpan<byte> payload)
    {
        if (DatagramBuilder.Cost(payload.Length) > DatagramBuilder.BodyCap)
            throw new ArgumentException($"Unreliable payload of {payload.Length} B does not fit one datagram.", nameof(payload));
        _unrel.Add((id, payload.ToArray()));
        if (_unrel.Count > MaxDeferredUnreliable) { _unrel.RemoveAt(0); UnreliableDropped++; }
    }

    public void ClearReliable()
    {
        _rel.Reset();
        _relItems.Clear();
    }

    /// <summary>
    /// Builds this tick's envelopes and hands each to <paramref name="send"/> (reliable first, then the datagram).
    /// <paramref name="fill"/> lets stream slots top the datagram up.
    /// </summary>
    public void Flush(PeerLink link, double now, ushort stamp, Func<byte[], Delivery, bool> send, Action<DatagramBuilder>? fill = null)
    {
        // Reliable: as many 64 KB envelopes as needed, consecutive seqs.
        int i = 0;
        while (i < _relItems.Count)
        {
            _body.Clear();
            while (i < _relItems.Count)
            {
                var (off, len) = _relItems[i];
                if (_body.Length + len > ReliableBodyCap && _body.Length > 0) break;
                AppendFramed(_rel.Span.Slice(off, len), link);
                i++;
            }
            var h = new EnvelopeHeader { Kind = EnvelopeKind.Reliable, Seq = link.TakeReliableSeq() };
            if (link.WantsAck(now)) link.FillAck(ref h, now);
            var env = Envelope.Build(h, _body.Span);
            if ((env[0] & 8) != 0) link.DeflatedOut++;
            link.OnReliableSent(h.Seq, env.Length, now);
            send(env, Delivery.Reliable);
        }
        ClearReliable();

        // Unreliable: queued messages first, then the stream slots.
        _dgram.Clear();
        int taken = 0;
        foreach (var (id, payload) in _unrel)
        {
            if (!_dgram.TryAdd((MsgId)id, payload)) break;
            CountOut(link, id, payload.Length);
            taken++;
        }
        if (taken > 0) _unrel.RemoveRange(0, taken);
        UnreliableDeferred += _unrel.Count;
        int beforeFill = _dgram.Count;
        fill?.Invoke(_dgram);
        if (_dgram.Count > beforeFill) link.MessagesOut += _dgram.Count - beforeFill;
        if (_dgram.Count > 0 || link.NeedsAckOnlyEnvelope(now))
        {
            var h = new EnvelopeHeader { Kind = EnvelopeKind.Unreliable, Seq = stamp };
            link.FillAck(ref h, now); // 4 bytes, always worth it on a datagram we send anyway
            var env = Envelope.Build(h, _dgram.Body.Span, allowDeflate: false);
            link.OnUnreliableSent(env.Length);
            send(env, Delivery.Unreliable);
        }
    }

    void AppendFramed(ReadOnlySpan<byte> framed, PeerLink link)
    {
        // Re-framing is a copy: the queued bytes already are { id, varint len, payload }.
        byte id = framed[0];
        var r = new NetReader(framed[1..]);
        int len = (int)r.VarU32();
        var payload = r.Raw(len);
        _body.TryAppend(id, payload, int.MaxValue);
        CountOut(link, id, len);
    }

    static void CountOut(PeerLink link, byte id, int len)
    {
        link.MessagesOut++;
        link.OutCountById[id]++;
        link.OutBytesById[id] += len;
    }

    public void Dispose()
    {
        _rel.Dispose();
        _body.Dispose();
        _dgram.Dispose();
    }
}
