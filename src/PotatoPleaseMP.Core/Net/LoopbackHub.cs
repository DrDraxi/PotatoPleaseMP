namespace PotatoPleaseMP.Core.Net;

/// <summary>Fault settings for traffic to and from one peer (overrides the hub defaults).</summary>
public sealed record LinkConditions(double LatencyMs = 0, double JitterMs = 0, double UnreliableLossPercent = 0);

/// <summary>
/// In-process "fake Steam" (spec 17 §1, spec 18): hands out <see cref="LoopbackLink"/>s that behave like
/// ISteamNetworkingMessages sessions, so bots and tests run the real <see cref="P2PTransport"/> connection code.
/// <list type="bullet">
/// <item>Time comes from the clock passed in, or a manual clock moved by <see cref="Advance"/>; nothing is delivered
/// before its time.</item>
/// <item>One-way latency + uniform jitter per message. Reliable messages are never lost and stay in order per directed
/// pair across all channels (Steam's one ordered stream); unreliable ones can be lost and reordered.</item>
/// <item>Optional send buffer + rate per directed pair (<see cref="SendBufferBytes"/>, <see cref="SendRateBytesPerSec"/>):
/// a send that does not fit returns <see cref="LinkSend.Busy"/>, as Steam's <c>k_EResultLimitExceeded</c>.</item>
/// <item>Fault injection: links down, one reliable message lost (a restarted session), failing sends, closed sessions.</item>
/// <item><see cref="RequireAccept"/>: messages from a peer are held until the receiver accepts it or sends to it first.</item>
/// </list>
/// Not thread-safe, like every transport.
/// </summary>
public sealed class LoopbackHub
{
    sealed class Item
    {
        public double DeliverAt, TransmitAt;
        public long Order;
        public ulong From;
        public int Channel;
        public bool Reliable;
        public byte[] Data = Array.Empty<byte>();
    }

    sealed class Pipe
    {
        public double BufferFreeAt;
        public double LastReliableAt;
        public bool Down, DropNextReliable, FailSends;
        /// <summary>The sender side has closed its session with the receiver.</summary>
        public bool Closed;
        public bool EverUsed;
        public long Sent, Delivered, Queued;
    }

    sealed class Endpoint
    {
        public readonly List<Item> Inbox = new();
        public readonly HashSet<ulong> Accepted = new();
    }

    readonly Dictionary<ulong, Endpoint> _endpoints = new();
    readonly Dictionary<(ulong From, ulong To), Pipe> _pipes = new();
    readonly Dictionary<ulong, LinkConditions> _conditions = new();
    readonly Func<double>? _clock;
    readonly Random _rng;
    double _manualNow;
    long _order;

    public LoopbackHub(Func<double>? clock = null, int seed = 12345)
    {
        _clock = clock;
        _rng = new Random(seed);
    }

    /// <summary>Current time in seconds.</summary>
    public double Now => _clock?.Invoke() ?? _manualNow;

    /// <summary>Moves the manual clock (only when no clock was passed in).</summary>
    public void Advance(double seconds)
    {
        if (_clock != null) throw new InvalidOperationException("This hub uses an external clock.");
        if (!(seconds >= 0) || double.IsInfinity(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        _manualNow += seconds;
    }

    public double LatencyMs { get; set; }
    public double JitterMs { get; set; }
    public double UnreliableLossPercent { get; set; }
    /// <summary>Per directed pair; 0 = unlimited.</summary>
    public int SendBufferBytes { get; set; }
    /// <summary>Per directed pair; 0 = unlimited (instant transmission).</summary>
    public int SendRateBytesPerSec { get; set; }
    public bool RequireAccept { get; set; }

    public long PacketsSent { get; private set; }
    public long PacketsDropped { get; private set; }
    public long PacketsHeld => _endpoints.Values.Sum(e => e.Inbox.Count(i => !Deliverable(e, i)));

    public LoopbackLink CreateLink(PeerId id)
    {
        if (!id.IsValid) throw new ArgumentException("PeerId must be non-zero.", nameof(id));
        if (_endpoints.ContainsKey(id.Value)) throw new InvalidOperationException($"{id} is already attached to this hub.");
        _endpoints[id.Value] = new Endpoint();
        return new LoopbackLink(this, id.Value);
    }

    /// <summary>A <see cref="P2PTransport"/> over a new link on this hub (spec 17's "LoopbackTransport").</summary>
    public P2PTransport CreateTransport(PeerId id) => new(CreateLink(id), () => Now);

    public bool IsAttached(PeerId id) => _endpoints.ContainsKey(id.Value);

    internal void Detach(ulong id)
    {
        _endpoints.Remove(id);
        foreach (var k in _pipes.Keys.Where(k => k.From == id || k.To == id).ToList()) _pipes.Remove(k);
    }

    /// <summary>Overrides latency/jitter/loss for every message to or from <paramref name="peer"/>; null restores the defaults.</summary>
    public void SetConditions(PeerId peer, LinkConditions? c)
    {
        if (c == null) _conditions.Remove(peer.Value); else _conditions[peer.Value] = c;
    }

    /// <summary>All traffic between a and b (both ways, reliable too) vanishes while down: a cut cable.</summary>
    public void SetLinkDown(PeerId a, PeerId b, bool down)
    {
        P(a.Value, b.Value).Down = down;
        P(b.Value, a.Value).Down = down;
    }

    /// <summary>One direction only: traffic from -> to vanishes while down.</summary>
    public void SetPipeDown(PeerId from, PeerId to, bool down) => P(from.Value, to.Value).Down = down;

    /// <summary>The next reliable message from -> to is silently lost (what a restarted Steam session does).</summary>
    public void DropNextReliable(PeerId from, PeerId to) => P(from.Value, to.Value).DropNextReliable = true;

    /// <summary>Sends from -> to return <see cref="LinkSend.Failed"/> (a broken session).</summary>
    public void SetFailSends(PeerId from, PeerId to, bool fail) => P(from.Value, to.Value).FailSends = fail;

    Pipe P(ulong from, ulong to)
    {
        if (!_pipes.TryGetValue((from, to), out var p)) _pipes[(from, to)] = p = new Pipe();
        return p;
    }

    (double Lat, double Jit, double Loss) Conditions(ulong from, ulong to)
    {
        double lat = LatencyMs, jit = JitterMs, loss = UnreliableLossPercent;
        foreach (var id in new[] { from, to })
            if (_conditions.TryGetValue(id, out var c)) { lat = c.LatencyMs; jit = c.JitterMs; loss = c.UnreliableLossPercent; }
        return (lat, jit, loss);
    }

    internal LinkSend Send(ulong from, ulong to, ReadOnlySpan<byte> data, int channel, LinkFlags flags)
    {
        PacketsSent++;
        double now = Now;
        bool reliable = (flags & LinkFlags.Reliable) != 0;
        var pipe = P(from, to);
        var back = P(to, from);
        pipe.Sent++;
        if (pipe.FailSends) return LinkSend.Failed;

        // The receiver closed its session with us: an established session is broken (Failed) unless this send is
        // allowed to restart it (Connect).
        if (back.Closed)
        {
            if ((flags & LinkFlags.RestartBroken) == 0) { PacketsDropped++; return LinkSend.Failed; }
            back.Closed = false;
        }
        pipe.Closed = false;
        pipe.EverUsed = true;
        if (_endpoints.TryGetValue(from, out var self)) self.Accepted.Add(to); // sending to a peer accepts its session

        double transmitAt = now;
        if (SendRateBytesPerSec > 0)
        {
            double start = Math.Max(now, pipe.BufferFreeAt);
            double buffered = (start - now) * SendRateBytesPerSec;
            if (SendBufferBytes > 0 && buffered + data.Length > SendBufferBytes)
            {
                if (!reliable) PacketsDropped++;
                return LinkSend.Busy;
            }
            pipe.BufferFreeAt = start + data.Length / (double)SendRateBytesPerSec;
            transmitAt = pipe.BufferFreeAt;
        }

        if (!_endpoints.TryGetValue(to, out var target) || pipe.Down) { PacketsDropped++; return LinkSend.Ok; }
        var (lat, jit, loss) = Conditions(from, to);
        if (!reliable && loss > 0 && _rng.NextDouble() * 100.0 < loss) { PacketsDropped++; return LinkSend.Ok; }
        if (reliable && pipe.DropNextReliable && channel != 0)
        {
            pipe.DropNextReliable = false;
            PacketsDropped++;
            return LinkSend.Ok;
        }

        double deliverAt = transmitAt + (lat + (jit > 0 ? _rng.NextDouble() * jit : 0)) / 1000.0;
        if (reliable)
        {
            deliverAt = Math.Max(deliverAt, pipe.LastReliableAt);
            pipe.LastReliableAt = deliverAt;
        }
        pipe.Queued++;
        target.Inbox.Add(new Item
        {
            DeliverAt = deliverAt, TransmitAt = transmitAt, Order = _order++, From = from, Channel = channel,
            Reliable = reliable, Data = data.ToArray(),
        });
        return LinkSend.Ok;
    }

    bool Deliverable(Endpoint e, Item i) => !RequireAccept || e.Accepted.Contains(i.From);

    /// <summary>Diagnostics: messages waiting for <paramref name="id"/> and the earliest delivery time.</summary>
    public string DebugPipes() => string.Join("; ", _pipes.Select(kv => $"{kv.Key.From:X}->{kv.Key.To:X}: sent {kv.Value.Sent} queued {kv.Value.Queued} delivered {kv.Value.Delivered} closed {kv.Value.Closed} down {kv.Value.Down}"));

    public string DebugInbox(PeerId id) => _endpoints.TryGetValue(id.Value, out var e)
        ? $"{e.Inbox.Count} waiting, earliest {(e.Inbox.Count > 0 ? e.Inbox.Min(i => i.DeliverAt) : 0):0.000}, now {Now:0.000}, channels {string.Join(",", e.Inbox.Select(i => i.Channel).Distinct())}"
        : "no endpoint";

    internal void Receive(ulong self, int channel, List<(ulong From, byte[] Data)> into)
    {
        if (!_endpoints.TryGetValue(self, out var e) || e.Inbox.Count == 0) return;
        double now = Now;
        List<Item>? ready = null;
        for (int i = 0; i < e.Inbox.Count; i++)
        {
            var it = e.Inbox[i];
            if (it.Channel != channel || it.DeliverAt > now || !Deliverable(e, it)) continue;
            (ready ??= new List<Item>()).Add(it);
        }
        if (ready == null) return;
        ready.Sort((a, b) => a.DeliverAt != b.DeliverAt ? a.DeliverAt.CompareTo(b.DeliverAt) : a.Order.CompareTo(b.Order));
        foreach (var it in ready)
        {
            e.Inbox.Remove(it);
            // A message whose link went down or whose session the receiver closed while in flight is lost.
            var pipe = P(it.From, self);
            if (pipe.Down || P(self, it.From).Closed) { PacketsDropped++; continue; }
            pipe.Delivered++;
            into.Add((it.From, it.Data));
        }
    }

    internal bool Accept(ulong self, ulong peer)
    {
        if (!_endpoints.TryGetValue(self, out var e)) return false;
        e.Accepted.Add(peer);
        P(self, peer).Closed = false;
        return true;
    }

    internal void Close(ulong self, ulong peer)
    {
        double now = Now;
        P(self, peer).Closed = true;
        if (_endpoints.TryGetValue(self, out var e))
        {
            e.Accepted.Remove(peer);
            e.Inbox.RemoveAll(i => i.From == peer); // our end is gone: what was waiting for us is discarded
        }
        // Data still in our send buffer (not transmitted yet) is discarded, like Steam does on close.
        if (_endpoints.TryGetValue(peer, out var other))
            other.Inbox.RemoveAll(i => i.From == self && i.TransmitAt > now);
        P(self, peer).BufferFreeAt = now;
    }

    internal LinkStatus Status(ulong self, ulong peer)
    {
        double now = Now;
        var pipe = P(self, peer);
        var back = P(peer, self);
        var (lat, _, _) = Conditions(self, peer);
        int pending = 0;
        if (_endpoints.TryGetValue(peer, out var other))
            foreach (var i in other.Inbox)
                if (i.From == self && i.Reliable && i.TransmitAt > now) pending += i.Data.Length;
        LinkState state;
        if (pipe.Closed) state = LinkState.None;
        else if (back.Closed) state = LinkState.ClosedByPeer;
        else if (!_endpoints.ContainsKey(peer) || pipe.Down) state = pipe.EverUsed ? LinkState.FindingRoute : LinkState.None;
        else state = pipe.EverUsed || back.EverUsed ? LinkState.Connected : LinkState.None;
        return new LinkStatus
        {
            State = state,
            PingMs = (int)Math.Round(2 * lat),
            QualityLocal = 1, QualityRemote = 1,
            SendRate = SendRateBytesPerSec,
            PendingReliable = pending,
            EndDebug = state == LinkState.ClosedByPeer ? "closed by peer (loopback)" : null,
        };
    }
}

/// <summary>One peer's attachment to a <see cref="LoopbackHub"/>; an <see cref="IP2PLink"/> like the plugin's SteamLink.</summary>
public sealed class LoopbackLink : IP2PLink, IDisposable
{
    readonly LoopbackHub _hub;

    internal LoopbackLink(LoopbackHub hub, ulong id)
    {
        _hub = hub;
        LocalId = id;
    }

    public ulong LocalId { get; }
    public LoopbackHub Hub => _hub;
    public bool IsDisposed { get; private set; }

    public LinkSend Send(ulong to, ReadOnlySpan<byte> data, int channel, LinkFlags flags)
        => IsDisposed ? LinkSend.Failed : _hub.Send(LocalId, to, data, channel, flags);

    public void Receive(int channel, List<(ulong From, byte[] Data)> into)
    {
        if (!IsDisposed) _hub.Receive(LocalId, channel, into);
    }

    public bool Accept(ulong peer) => !IsDisposed && _hub.Accept(LocalId, peer);
    public void Close(ulong peer) { if (!IsDisposed) _hub.Close(LocalId, peer); }
    public LinkStatus Status(ulong peer) => IsDisposed ? default : _hub.Status(LocalId, peer);

    /// <summary>Detaches from the hub; later sends fail.</summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _hub.Detach(LocalId);
    }
}
