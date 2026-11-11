using System.Buffers.Binary;
using System.Diagnostics;

namespace PotatoPleaseMP.Core.Net;

/// <summary>
/// Connection layer over a connectionless P2P link (spec 17 §4, appendix C; SoD <c>SteamTransport</c> generalised).
/// Steam messages have no connections, so this adds one on channel 0: Connect / Accept / Reject / Bye / Ping / Pong /
/// Hello. Channel 1 carries reliable data, channel 2 unreliable data. The same code runs over Steam
/// (<c>SteamLink</c> in the plugin) and over <see cref="LoopbackLink"/> for bots and tests.
/// <para>
/// Rules: a failed reliable send on an established peer always ends the connection (no silent gaps);
/// <see cref="LinkFlags.RestartBroken"/> only on Connect; a Bye read in the same poll as earlier data is applied after
/// that data; link status "closed by peer"/"problem" drops a peer within one status sample instead of the 45 s timeout.
/// Main thread only.
/// </para>
/// </summary>
public sealed class P2PTransport : ITransport
{
    public const int ChCtl = 0, ChRel = 1, ChUnrel = 2;
    public const int MaxReliable = TransportLimits.MaxReliable, MaxUnreliable = TransportLimits.MaxUnreliable;
    public const uint Magic = 0x544D5050; // 'PPMT'
    public const ushort LinkVersion = 1;
    public const int MaxByePayload = 1024;

    enum Ctl : byte { Connect = 1, Accept = 2, Reject = 3, Bye = 4, Ping = 5, Pong = 6, Hello = 7 }
    enum PeerState { Connecting, Connected }
    enum CloseMode { Now, Linger, Never }

    sealed class Peer
    {
        public ulong Id;
        public PeerState State;
        public ulong Token;
        public double LastHeard, NextPing, NextConnect, Deadline, NextStatus;
        public bool Lost;
        public string? LostWhy;
        public readonly Queue<byte[]> Queue = new();
        public long QueuedBytes;
        public long SentSinceSample;
        public ushort PingSeq;
        public double RttEwma = -1;
        public readonly PeerStats Stats = new();
    }

    readonly IP2PLink _link;
    readonly Func<double> _clock;
    readonly Dictionary<ulong, Peer> _peers = new();
    readonly Dictionary<ulong, (double Until, double Next)> _expected = new();
    readonly List<(ulong From, byte[] Data)> _inbox = new();
    readonly List<NetEvent> _pending = new();
    readonly List<(Peer P, byte Code, byte[]? Final)> _byes = new();
    readonly List<Peer> _tick = new();
    readonly List<(ulong Id, double At)> _closing = new();
    double _now;
    bool _hosting;
    int _maxPeers;
    Func<PeerId, bool>? _acceptFilter;

    public P2PTransport(IP2PLink link, Func<double>? clock = null)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        var sw = Stopwatch.StartNew();
        _clock = clock ?? (() => sw.Elapsed.TotalSeconds);
        _now = _clock();
    }

    /// <summary>No message at all for this long drops a connected peer (main-thread stalls still let Steam ack).</summary>
    public double TimeoutSeconds { get; set; } = 45;
    /// <summary>Relay route finding can take 5-10 s cold.</summary>
    public double ConnectTimeoutSeconds { get; set; } = 30;
    public double PingIntervalSeconds { get; set; } = 1;
    public double StatusIntervalSeconds { get; set; } = 0.25;
    /// <summary>Bye linger: send buffer / send rate + 5 s (8 MB at 1 MB/s).</summary>
    public double ByeLingerSeconds { get; set; } = 13;
    public double RejectLingerSeconds { get; set; } = 5;
    public double ExpectSeconds { get; set; } = 60;
    public long MaxQueuedBytes { get; set; } = 32L << 20;

    public TransportKind Kind => _link is LoopbackLink ? TransportKind.Loopback : TransportKind.Steam;
    public PeerId LocalId => new(_link.LocalId);
    public bool IsRunning { get; private set; }
    public bool IsHosting => _hosting;
    public IP2PLink Link => _link;
    /// <summary>Messages from peers we have no connection with (dropped, counted).</summary>
    public long Strays { get; private set; }
    public int DeferredCloses => _closing.Count;

    // ---------------------------------------------------------------- lifecycle

    public void StartHost(int maxPeers, Func<PeerId, bool>? acceptFilter)
    {
        _hosting = true;
        _maxPeers = Math.Max(0, maxPeers);
        _acceptFilter = acceptFilter;
        IsRunning = true;
    }

    public void Connect(PeerId host, string? endpoint = null)
    {
        _now = _clock();
        IsRunning = true;
        if (!host.IsValid || host.Value == _link.LocalId)
        {
            _pending.Add(new NetEvent(NetEventKind.Disconnected, host, Delivery.Reliable, null, DisconnectReason.ConnectFailed,
                Detail: host.IsValid ? "cannot connect to ourselves" : "no host id"));
            return;
        }
        if (_peers.TryGetValue(host.Value, out var old)) Drop(old, DisconnectReason.Replaced, 0, null, CloseMode.Never, _pending);
        var p = AddPeer(host.Value, PeerState.Connecting);
        p.Token = NewToken();
        p.Deadline = _now + ConnectTimeoutSeconds;
        p.NextConnect = _now;
    }

    public void Expect(PeerId peer)
    {
        if (!_hosting || !peer.IsValid || peer.Value == _link.LocalId || _peers.ContainsKey(peer.Value)) return;
        _now = _clock();
        _expected[peer.Value] = (_now + ExpectSeconds, _now);
    }

    public void Disconnect(PeerId peer, byte code = 0, ReadOnlySpan<byte> final = default)
    {
        if (!_peers.TryGetValue(peer.Value, out var p)) return;
        _now = _clock();
        Flush(p);
        SendBye(p.Id, code, final);
        Drop(p, DisconnectReason.Local, code, null, CloseMode.Linger, _pending);
    }

    public void Stop(byte code = 0)
    {
        _now = _clock();
        foreach (var p in _peers.Values.ToList())
        {
            if (p.State == PeerState.Connected) { Flush(p); SendBye(p.Id, code, default); }
            Defer(_link, p.Id, _now + ByeLingerSeconds, _clock);
            _pending.Add(new NetEvent(NetEventKind.Disconnected, new PeerId(p.Id), Delivery.Reliable, null, DisconnectReason.Stopped, code));
        }
        foreach (var (id, _) in _closing) Defer(_link, id, _now + ByeLingerSeconds, _clock);
        _closing.Clear();
        _peers.Clear();
        _expected.Clear();
        _hosting = false;
        IsRunning = false;
    }

    public void Dispose()
    {
        if (IsRunning) Stop();
    }

    // ---------------------------------------------------------------- data

    public bool Send(PeerId to, ReadOnlySpan<byte> data, Delivery d)
    {
        if (!_peers.TryGetValue(to.Value, out var p) || p.State != PeerState.Connected || p.Lost || data.IsEmpty) return false;
        if (d == Delivery.Unreliable)
        {
            if (data.Length > MaxUnreliable) { p.Stats.Oversize++; return false; }
            var u = _link.Send(p.Id, data, ChUnrel, LinkFlags.Unreliable);
            if (u == LinkSend.Ok) p.Stats.OnSent(data.Length);
            else if (u == LinkSend.Busy) p.Stats.UnreliableDropped++;
            else Lose(p, "send failed");
            return u == LinkSend.Ok;
        }
        if (data.Length > MaxReliable) { p.Stats.Oversize++; return false; }
        if (p.Queue.Count == 0)
        {
            var r = _link.Send(p.Id, data, ChRel, LinkFlags.Reliable);
            if (r == LinkSend.Ok) { p.Stats.OnSent(data.Length); p.SentSinceSample += data.Length; return true; }
            if (r == LinkSend.Failed) { Lose(p, "send failed"); return false; }
            p.Stats.BusyCount++;
        }
        p.Queue.Enqueue(data.ToArray());
        p.QueuedBytes += data.Length;
        if (p.QueuedBytes > MaxQueuedBytes) Lose(p, "SendOverflow");
        return true;
    }

    public int PendingReliableBytes(PeerId peer)
    {
        if (!_peers.TryGetValue(peer.Value, out var p)) return -1;
        long v = p.QueuedBytes + p.Stats.Link.PendingReliable + p.SentSinceSample;
        return (int)Math.Min(int.MaxValue, v);
    }

    public bool TryGetStats(PeerId peer, out PeerStats stats)
    {
        if (!_peers.TryGetValue(peer.Value, out var p)) { stats = null!; return false; }
        stats = p.Stats.Clone();
        stats.RttMs = p.RttEwma < 0 ? -1 : (int)Math.Round(p.RttEwma);
        stats.LastHeardAgoS = _clock() - p.LastHeard;
        stats.QueuedReliableBytes = p.QueuedBytes;
        return true;
    }

    public void CollectPeers(List<PeerId> into)
    {
        foreach (var p in _peers.Values)
            if (p.State == PeerState.Connected) into.Add(new PeerId(p.Id));
    }

    /// <summary>True while a connection (or connection attempt) to <paramref name="peer"/> exists.</summary>
    public bool HasPeer(PeerId peer) => _peers.ContainsKey(peer.Value);

    // ---------------------------------------------------------------- poll

    public void Poll(List<NetEvent> into)
    {
        _now = _clock();
        into.AddRange(_pending);
        _pending.Clear();
        if (IsRunning)
        {
            _byes.Clear();
            Drain(ChCtl);
            foreach (var (f, d) in _inbox) OnControl(f, d, into);
            Drain(ChRel);
            foreach (var (f, d) in _inbox) OnData(f, d, Delivery.Reliable, into);
            Drain(ChUnrel);
            foreach (var (f, d) in _inbox) OnData(f, d, Delivery.Unreliable, into);
            foreach (var (p, code, fin) in _byes) Drop(p, DisconnectReason.RemoteBye, code, fin, CloseMode.Now, into);
            _tick.Clear();
            _tick.AddRange(_peers.Values);
            foreach (var p in _tick) Tick(p, into);
            TickExpected();
        }
        for (int i = _closing.Count - 1; i >= 0; i--)
            if (_now >= _closing[i].At) { _link.Close(_closing[i].Id); _closing.RemoveAt(i); }
    }

    void Drain(int ch)
    {
        _inbox.Clear();
        _link.Receive(ch, _inbox);
    }

    void OnData(ulong from, byte[] d, Delivery how, List<NetEvent> into)
    {
        if (!_peers.TryGetValue(from, out var p) || p.State != PeerState.Connected || p.Lost) { Strays++; return; }
        p.LastHeard = _now;
        p.Stats.OnRecv(d.Length);
        into.Add(new NetEvent(NetEventKind.Data, new PeerId(from), how, d));
    }

    void OnControl(ulong from, byte[] d, List<NetEvent> into)
    {
        if (d.Length == 0) { Strays++; return; }
        _peers.TryGetValue(from, out var p);
        var span = d.AsSpan();
        switch ((Ctl)d[0])
        {
            case Ctl.Connect when d.Length >= 15:
                OnConnect(from, BinaryPrimitives.ReadUInt32LittleEndian(span[1..]), BinaryPrimitives.ReadUInt16LittleEndian(span[5..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(span[7..]), p, into);
                break;

            case Ctl.Accept when d.Length >= 11 && p is { State: PeerState.Connecting }
                                 && BinaryPrimitives.ReadUInt64LittleEndian(span[1..]) == p.Token:
                p.State = PeerState.Connected;
                p.LastHeard = _now;
                p.NextPing = _now + PingIntervalSeconds;
                p.NextStatus = _now;
                into.Add(new NetEvent(NetEventKind.Connected, new PeerId(from), Delivery.Reliable, null));
                break;

            case Ctl.Reject when d.Length >= 10 && p is { State: PeerState.Connecting }
                                 && BinaryPrimitives.ReadUInt64LittleEndian(span[1..]) == p.Token:
                Drop(p, DisconnectReason.Rejected, d[9], null, CloseMode.Now, into, $"rejected: {(RejectReason)d[9]}");
                break;

            case Ctl.Bye when p != null && d.Length >= 2:
                p.LastHeard = _now;
                _byes.Add((p, d[1], d.Length > 2 ? d[2..] : null));
                break;

            case Ctl.Ping when p is { State: PeerState.Connected } && d.Length >= 11:
                p.LastHeard = _now;
                d[0] = (byte)Ctl.Pong; // echo seq and clock as they came
                _link.Send(from, d, ChCtl, LinkFlags.Unreliable);
                break;

            case Ctl.Pong when p is { State: PeerState.Connected } && d.Length >= 11:
                p.LastHeard = _now;
                double rtt = (_now - BinaryPrimitives.ReadDoubleLittleEndian(span[3..])) * 1000.0;
                if (rtt >= 0 && rtt < 60_000) p.RttEwma = p.RttEwma < 0 ? rtt : p.RttEwma + (rtt - p.RttEwma) / 8.0;
                break;

            case Ctl.Hello when d.Length >= 5 && p is { State: PeerState.Connecting }
                                && BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) == Magic:
                // The host opened the session first: answer with our Connect right away.
                p.NextConnect = _now;
                break;

            default:
                if (p != null) p.LastHeard = _now;
                else Strays++;
                break;
        }
    }

    void OnConnect(ulong from, uint magic, ushort ver, ulong token, Peer? p, List<NetEvent> into)
    {
        if (!_hosting) { SendReject(from, token, RejectReason.NotHosting); return; }
        if (magic != Magic || ver != LinkVersion) { SendReject(from, token, RejectReason.Version); return; }
        if (from == _link.LocalId) { SendReject(from, token, RejectReason.NotAllowed); return; }
        _expected.Remove(from);
        if (p != null)
        {
            if (p.State == PeerState.Connected && p.Token == token)
            {
                p.LastHeard = _now;
                SendAccept(from, token); // our Accept was lost: repeat it
                return;
            }
            // Same id, new connection (left and came back before we noticed): replace it; the session carries on.
            Drop(p, DisconnectReason.Replaced, 0, null, CloseMode.Never, into);
        }
        int connected = _peers.Values.Count(x => x.State == PeerState.Connected);
        if (connected >= _maxPeers) { SendReject(from, token, RejectReason.Full); return; }
        if (_acceptFilter != null && !_acceptFilter(new PeerId(from))) { SendReject(from, token, RejectReason.NotAllowed); return; }
        var np = AddPeer(from, PeerState.Connected);
        np.Token = token;
        np.LastHeard = _now;
        np.NextPing = _now + PingIntervalSeconds;
        np.NextStatus = _now;
        _link.Accept(from);
        SendAccept(from, token);
        into.Add(new NetEvent(NetEventKind.Connected, new PeerId(from), Delivery.Reliable, null));
    }

    void Tick(Peer p, List<NetEvent> into)
    {
        if (p.State == PeerState.Connecting)
        {
            if (_now >= p.Deadline)
            {
                SampleStatus(p);
                string why = p.Stats.Link.State is LinkState.FindingRoute or LinkState.Connecting
                    ? "couldn't reach host (no network route)" : "connect timed out";
                Drop(p, DisconnectReason.ConnectFailed, 0, null, CloseMode.Now, into, why);
                return;
            }
            if (_now >= p.NextConnect) { p.NextConnect = _now + 1; SendConnect(p); }
            return;
        }
        if (p.Lost) { Drop(p, DisconnectReason.ConnectionLost, 0, null, CloseMode.Now, into, p.LostWhy); return; }
        if (_now - p.LastHeard > TimeoutSeconds) { Drop(p, DisconnectReason.Timeout, 0, null, CloseMode.Now, into, "no message received"); return; }
        if (_now >= p.NextPing) { p.NextPing = _now + PingIntervalSeconds; SendPing(p); }
        Flush(p);
        if (_now >= p.NextStatus)
        {
            p.NextStatus = _now + StatusIntervalSeconds;
            SampleStatus(p);
            if (p.Stats.Link.State is LinkState.ClosedByPeer or LinkState.ProblemDetectedLocally)
                Drop(p, DisconnectReason.ConnectionLost, 0, null, CloseMode.Now, into, p.Stats.Link.EndDebug ?? p.Stats.Link.State.ToString());
        }
    }

    void SampleStatus(Peer p)
    {
        p.Stats.Link = _link.Status(p.Id);
        p.SentSinceSample = 0;
    }

    void TickExpected()
    {
        if (_expected.Count == 0) return;
        Span<byte> hello = stackalloc byte[5];
        hello[0] = (byte)Ctl.Hello;
        BinaryPrimitives.WriteUInt32LittleEndian(hello[1..], Magic);
        foreach (var id in _expected.Keys.ToList())
        {
            var (until, next) = _expected[id];
            if (_peers.ContainsKey(id) || _now >= until) { _expected.Remove(id); continue; }
            if (_now < next) continue;
            _link.Accept(id);
            _link.Send(id, hello, ChCtl, LinkFlags.Reliable);
            _expected[id] = (until, _now + 1);
        }
    }

    void Flush(Peer p)
    {
        while (p.Queue.Count > 0 && !p.Lost)
        {
            var m = p.Queue.Peek();
            var r = _link.Send(p.Id, m, ChRel, LinkFlags.Reliable);
            if (r == LinkSend.Busy) { p.Stats.BusyCount++; return; }
            if (r == LinkSend.Failed) { Lose(p, "send failed"); return; }
            p.Queue.Dequeue();
            p.QueuedBytes -= m.Length;
            p.SentSinceSample += m.Length;
            p.Stats.OnSent(m.Length);
        }
    }

    void Lose(Peer p, string why)
    {
        if (p.Lost) return;
        p.Lost = true;
        p.LostWhy = why;
    }

    Peer AddPeer(ulong id, PeerState state)
    {
        // A pending close of an earlier session would kill the new one.
        _closing.RemoveAll(c => c.Id == id);
        CancelDeferred(_link, id);
        var p = new Peer { Id = id, State = state, LastHeard = _now };
        _peers[id] = p;
        return p;
    }

    void Drop(Peer p, DisconnectReason why, byte code, byte[]? fin, CloseMode close, List<NetEvent> into, string? detail = null)
    {
        if (!_peers.TryGetValue(p.Id, out var cur) || cur != p) return; // already dropped or replaced
        _peers.Remove(p.Id);
        if (close == CloseMode.Now) _link.Close(p.Id);
        else if (close == CloseMode.Linger) _closing.Add((p.Id, _now + ByeLingerSeconds));
        into.Add(new NetEvent(NetEventKind.Disconnected, new PeerId(p.Id), Delivery.Reliable, fin, why, code, detail));
    }

    // ---------------------------------------------------------------- control frames

    static ulong NewToken()
    {
        ulong t;
        do t = (ulong)Random.Shared.NextInt64(); while (t == 0);
        return t;
    }

    void SendConnect(Peer p)
    {
        Span<byte> b = stackalloc byte[15];
        b[0] = (byte)Ctl.Connect;
        BinaryPrimitives.WriteUInt32LittleEndian(b[1..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(b[5..], LinkVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(b[7..], p.Token);
        // The only send allowed to restart a broken session: we are opening a fresh connection.
        _link.Send(p.Id, b, ChCtl, LinkFlags.Reliable | LinkFlags.RestartBroken);
    }

    void SendAccept(ulong to, ulong token)
    {
        Span<byte> b = stackalloc byte[11];
        b[0] = (byte)Ctl.Accept;
        BinaryPrimitives.WriteUInt64LittleEndian(b[1..], token);
        BinaryPrimitives.WriteUInt16LittleEndian(b[9..], LinkVersion);
        _link.Send(to, b, ChCtl, LinkFlags.Reliable);
    }

    void SendReject(ulong to, ulong token, RejectReason reason)
    {
        Span<byte> b = stackalloc byte[10];
        b[0] = (byte)Ctl.Reject;
        BinaryPrimitives.WriteUInt64LittleEndian(b[1..], token);
        b[9] = (byte)reason;
        _link.Send(to, b, ChCtl, LinkFlags.Reliable);
        if (!_peers.ContainsKey(to) && !_closing.Any(c => c.Id == to)) _closing.Add((to, _now + RejectLingerSeconds));
    }

    void SendBye(ulong to, byte code, ReadOnlySpan<byte> final)
    {
        if (final.Length > MaxByePayload) final = final[..MaxByePayload];
        var b = new byte[2 + final.Length];
        b[0] = (byte)Ctl.Bye;
        b[1] = code;
        final.CopyTo(b.AsSpan(2));
        _link.Send(to, b, ChCtl, LinkFlags.Reliable | LinkFlags.Urgent);
    }

    void SendPing(Peer p)
    {
        Span<byte> b = stackalloc byte[11];
        b[0] = (byte)Ctl.Ping;
        BinaryPrimitives.WriteUInt16LittleEndian(b[1..], ++p.PingSeq);
        BinaryPrimitives.WriteDoubleLittleEndian(b[3..], _now);
        _link.Send(p.Id, b, ChCtl, LinkFlags.Unreliable);
    }

    // ---------------------------------------------------------------- deferred closes (survive stopped transports)

    static readonly object DeferredLock = new();
    static readonly List<(IP2PLink Link, ulong Id, double At, Func<double> Clock)> Deferred = new();

    static void Defer(IP2PLink link, ulong id, double at, Func<double> clock)
    {
        lock (DeferredLock) Deferred.Add((link, id, at, clock));
    }

    static void CancelDeferred(IP2PLink link, ulong id)
    {
        lock (DeferredLock) Deferred.RemoveAll(d => d.Link == link && d.Id == id);
    }

    /// <summary>Number of session closes still waiting for their Bye to drain (across all transports).</summary>
    public static int PendingDeferredCloses { get { lock (DeferredLock) return Deferred.Count; } }

    /// <summary>
    /// Closes the sessions of stopped transports once their Bye had time to leave. Call every frame (the plugin's
    /// <c>NetRuntime</c> does), also after the transport that deferred them is gone.
    /// </summary>
    public static void PumpDeferredCloses()
    {
        lock (DeferredLock)
        {
            for (int i = Deferred.Count - 1; i >= 0; i--)
            {
                var d = Deferred[i];
                if (d.Clock() >= d.At)
                {
                    d.Link.Close(d.Id);
                    Deferred.RemoveAt(i);
                }
            }
        }
    }
}
