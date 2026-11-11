using PotatoPleaseMP.Core.Net;

namespace PotatoPleaseMP.Core.Wire;

public enum NetLayerEventKind : byte { Connected, Disconnected, Message }

/// <summary>
/// One event out of <see cref="NetLayer.Poll"/>. For messages, <see cref="Payload"/> is a slice of the envelope body
/// (valid until the next poll; copy to keep). For disconnects, <see cref="FinalId"/>/<see cref="Payload"/> carry the
/// peer's final message (Goodbye or Reject) when its Bye had one.
/// </summary>
public readonly struct NetLayerEvent
{
    public NetLayerEventKind Kind { get; init; }
    public PeerId Peer { get; init; }
    public MsgId Id { get; init; }
    public byte[]? Body { get; init; }
    public int Offset { get; init; }
    public int Length { get; init; }
    public Delivery Delivery { get; init; }
    /// <summary>Unreliable: the envelope stamp (host-tick space). Reliable: the envelope seq.</summary>
    public ushort Stamp { get; init; }
    public DisconnectReason Reason { get; init; }
    public byte Code { get; init; }
    public string? Detail { get; init; }
    public MsgId? FinalId { get; init; }

    public ReadOnlySpan<byte> Payload => Body == null ? default : Body.AsSpan(Offset, Length);
}

/// <summary>
/// The envelope layer between a transport and the session (spec 01 §3, §5): per-peer <see cref="PeerLink"/> and
/// <see cref="Batcher"/>, envelope parsing and validation against the <see cref="MessageRegistry"/> (unknown ids
/// skipped by length; wrong direction, wrong channel or <c>len &gt; MaxLen</c> dropped and counted), a reliable seq gap
/// drops the peer as <see cref="DisconnectReason.ConnectionLost"/>. Sending is queued; <see cref="Flush"/> (once per
/// net tick) builds the envelopes. Main thread only.
/// </summary>
public sealed class NetLayer : IDisposable
{
    sealed class PeerState : IDisposable
    {
        public readonly PeerLink Link = new();
        public readonly Batcher Out = new();
        public bool EpochLogged;
        public void Dispose() => Out.Dispose();
    }

    readonly Dictionary<PeerId, PeerState> _peers = new();
    /// <summary>Peers we disconnected ourselves: the transport's later Disconnected event for them is not reported.</summary>
    readonly HashSet<PeerId> _selfDropped = new();
    readonly List<NetEvent> _events = new();
    readonly ParsedEnvelope _parsed = new();
    readonly NetWriter _tmp = new(256);

    public NetLayer(ITransport transport, MessageRegistry registry, bool isHost)
    {
        Transport = transport;
        Registry = registry;
        IsHost = isHost;
    }

    public ITransport Transport { get; }
    public MessageRegistry Registry { get; }
    public bool IsHost { get; }

    /// <summary>Stamp for unreliable envelopes at a given time (host: its tick; client: its estimate of the host tick).</summary>
    public Func<double, ushort>? StampSource { get; set; }
    /// <summary>Called during <see cref="Flush"/> so stream slots can top up each peer's datagram.</summary>
    public Action<PeerId, DatagramBuilder>? FillUnreliable { get; set; }
    /// <summary>Called for every accepted unreliable envelope (client clock sync).</summary>
    public Action<PeerId, ushort, double>? UnreliableStamp { get; set; }
    /// <summary>Diagnostics: a dropped envelope or message (PeerHealth input).</summary>
    public Action<PeerId, string>? Warning { get; set; }

    public long DroppedEnvelopes { get; private set; }
    public long DroppedMessages { get; private set; }
    public IEnumerable<PeerId> Peers => _peers.Keys;
    public bool HasPeer(PeerId p) => _peers.ContainsKey(p);
    public PeerLink? Link(PeerId p) => _peers.TryGetValue(p, out var s) ? s.Link : null;

    /// <summary>Bytes queued here plus the transport's unsent reliable bytes (blob flow control).</summary>
    public int PendingReliableBytes(PeerId p)
    {
        if (!_peers.TryGetValue(p, out var s)) return -1;
        int t = Transport.PendingReliableBytes(p);
        return s.Out.QueuedReliableBytes + Math.Max(0, t);
    }

    // ---------------------------------------------------------------- send

    public bool Send(PeerId to, MsgId id, ReadOnlySpan<byte> payload)
    {
        if (!_peers.TryGetValue(to, out var s)) return false;
        s.Out.QueueReliable((byte)id, payload);
        return true;
    }

    public bool Send<T>(PeerId to, in T msg) where T : IWireMessage
    {
        _tmp.Reset();
        msg.Write(_tmp);
        return Send(to, msg.Id, _tmp.Span);
    }

    public bool SendUnreliable(PeerId to, MsgId id, ReadOnlySpan<byte> payload)
    {
        if (!_peers.TryGetValue(to, out var s)) return false;
        s.Out.QueueUnreliable((byte)id, payload);
        return true;
    }

    public bool SendUnreliable<T>(PeerId to, in T msg) where T : IWireMessage
    {
        _tmp.Reset();
        msg.Write(_tmp);
        return SendUnreliable(to, msg.Id, _tmp.Span);
    }

    /// <summary>Builds and sends this tick's envelopes for every peer.</summary>
    public void Flush(double now)
    {
        foreach (var p in _peers.Keys.ToList()) FlushPeer(p, now);
    }

    public void FlushPeer(PeerId p, double now)
    {
        if (!_peers.TryGetValue(p, out var s)) return;
        ushort stamp = StampSource?.Invoke(now) ?? 0;
        var fill = FillUnreliable;
        s.Out.Flush(s.Link, now, stamp, (env, d) => Transport.Send(p, env, d), fill == null ? null : dg => fill(p, dg));
    }

    /// <summary>
    /// Flushes what is queued for the peer, then disconnects with a final message (Goodbye/Reject) carried in the
    /// transport's Bye. No event is raised for a peer we drop ourselves.
    /// </summary>
    public void Disconnect(PeerId peer, double now, MsgId? finalId = null, ReadOnlySpan<byte> finalPayload = default, byte code = 0)
    {
        if (!_peers.TryGetValue(peer, out var s)) { _selfDropped.Add(peer); Transport.Disconnect(peer, code); return; }
        FlushPeer(peer, now);
        byte[] final = Array.Empty<byte>();
        if (finalId is { } fid)
        {
            using var w = new NetWriter(finalPayload.Length + 8);
            w.U8((byte)fid);
            w.VarU32((uint)finalPayload.Length);
            w.Raw(finalPayload);
            final = w.ToArray();
        }
        _peers.Remove(peer);
        s.Dispose();
        _selfDropped.Add(peer);
        Transport.Disconnect(peer, code, final);
    }

    public void Disconnect<T>(PeerId peer, double now, in T finalMsg, byte code = 0) where T : IWireMessage
    {
        using var w = new NetWriter(64);
        finalMsg.Write(w);
        Disconnect(peer, now, finalMsg.Id, w.Span, code);
    }

    /// <summary>Stops the transport (Bye to all). Queued data is flushed first.</summary>
    public void Stop(double now, byte code = 0)
    {
        Flush(now);
        foreach (var (p, s) in _peers) { s.Dispose(); _selfDropped.Add(p); }
        _peers.Clear();
        Transport.Stop(code);
    }

    // ---------------------------------------------------------------- receive

    public void Poll(double now, List<NetLayerEvent> into)
    {
        _events.Clear();
        Transport.Poll(_events);
        foreach (var e in _events)
        {
            switch (e.Kind)
            {
                case NetEventKind.Connected:
                    _selfDropped.Remove(e.Peer);
                    if (_peers.Remove(e.Peer, out var old)) old.Dispose();
                    _peers[e.Peer] = new PeerState();
                    into.Add(new NetLayerEvent { Kind = NetLayerEventKind.Connected, Peer = e.Peer });
                    break;

                case NetEventKind.Disconnected:
                    if (_selfDropped.Remove(e.Peer)) break; // we dropped it ourselves
                    // Unknown peers are reported too: a connect that failed or was refused never had a Connected.
                    if (_peers.Remove(e.Peer, out var gone)) gone.Dispose();
                    MsgId? fid = null;
                    byte[]? body = null;
                    int off = 0, len = 0;
                    if (e.Data is { Length: > 0 } fin)
                    {
                        try
                        {
                            var r = new NetReader(fin);
                            byte id = r.U8();
                            int l = (int)r.VarU32();
                            int at = r.Position;
                            r.Raw(l);
                            fid = (MsgId)id; body = fin; off = at; len = l;
                        }
                        catch (WireFormatException) { Warning?.Invoke(e.Peer, "malformed final message"); }
                    }
                    into.Add(new NetLayerEvent
                    {
                        Kind = NetLayerEventKind.Disconnected, Peer = e.Peer, Reason = e.Reason, Code = e.Code, Detail = e.Detail,
                        FinalId = fid, Body = body, Offset = off, Length = len,
                    });
                    break;

                case NetEventKind.Data when e.Data != null:
                    OnEnvelope(e.Peer, e.Data, e.Delivery, now, into);
                    break;
            }
        }
    }

    void OnEnvelope(PeerId peer, byte[] data, Delivery channel, double now, List<NetLayerEvent> into)
    {
        if (!_peers.TryGetValue(peer, out var s)) return;
        var link = s.Link;
        link.EnvelopesIn++;
        link.BytesIn += data.Length;
        var err = Envelope.TryParse(data, channel, _parsed);
        if (err != ProtocolError.None)
        {
            link.DroppedEnvelopes++;
            link.LastError = err;
            DroppedEnvelopes++;
            if (err == ProtocolError.EpochMismatch && !s.EpochLogged) { s.EpochLogged = true; Warning?.Invoke(peer, "protocol epoch mismatch"); }
            else if (err != ProtocolError.EpochMismatch) Warning?.Invoke(peer, $"envelope dropped: {err}");
            return;
        }
        var h = _parsed.Header;
        if (h.Kind == EnvelopeKind.Reliable)
        {
            if (link.AcceptReliable(h.Seq, now) == ProtocolError.Gap)
            {
                // A gap or duplicate means a restarted session (lost data) or a bug: never resync silently.
                link.LastError = ProtocolError.Gap;
                Warning?.Invoke(peer, $"reliable seq gap: got {h.Seq}, expected {(ushort)(link.LastReliableIn + 1)}");
                _peers.Remove(peer);
                s.Dispose();
                _selfDropped.Add(peer);
                Transport.Disconnect(peer);
                into.Add(new NetLayerEvent { Kind = NetLayerEventKind.Disconnected, Peer = peer, Reason = DisconnectReason.ConnectionLost, Detail = "reliable sequence gap" });
                return;
            }
        }
        else
        {
            link.AcceptUnreliable(h.Seq, now);
            UnreliableStamp?.Invoke(peer, h.Seq, now);
        }
        if (h.HasAck) link.OnAck(h.AckRel, h.AckStamp, now);

        foreach (var m in _parsed.Messages)
        {
            if (!Registry.TryGet(m.Id, out var info)) { link.UnknownIds++; continue; }
            bool dirOk = IsHost ? info.Dir != MsgDir.HostToClient : info.Dir != MsgDir.ClientToHost;
            bool chOk = channel == Delivery.Reliable ? info.AllowsReliable : info.AllowsUnreliable;
            if (!dirOk || !chOk || m.Length > info.MaxLen)
            {
                link.RejectedMessages++;
                DroppedMessages++;
                Warning?.Invoke(peer, $"{info.Name} dropped: {(!dirOk ? "wrong direction" : !chOk ? "wrong channel" : $"len {m.Length} > {info.MaxLen}")}");
                continue;
            }
            link.MessagesIn++;
            link.InCountById[m.Id]++;
            link.InBytesById[m.Id] += m.Length;
            into.Add(new NetLayerEvent
            {
                Kind = NetLayerEventKind.Message, Peer = peer, Id = (MsgId)m.Id, Body = _parsed.Body, Offset = m.Offset,
                Length = m.Length, Delivery = channel, Stamp = h.Seq,
            });
        }
    }

    public void Dispose()
    {
        foreach (var s in _peers.Values) s.Dispose();
        _peers.Clear();
        _tmp.Dispose();
    }
}
