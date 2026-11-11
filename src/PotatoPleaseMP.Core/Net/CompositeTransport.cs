namespace PotatoPleaseMP.Core.Net;

/// <summary>
/// One <see cref="ITransport"/> over two (spec 17 §1, ngg-mp port): traffic for bot ids goes through an in-process
/// loopback transport (a <see cref="P2PTransport"/> on a <see cref="LoopbackHub"/>), everything else through the real
/// transport. The session above sees one peer namespace, so a bot is just another client of the host.
/// <para>
/// Only the loopback side is owned. While <see cref="LoopbackEnabled"/> is false (not hosting), bot traffic is dropped
/// and loopback events are discarded, so the wrapper is exactly the primary transport. <see cref="Dispose"/> stops and
/// detaches the loopback side but keeps passing the primary through (its owner may still say goodbye with it).
/// </para>
/// </summary>
public sealed class CompositeTransport : ITransport
{
    readonly ITransport _primary;
    readonly ITransport _loopback;
    readonly Func<PeerId, bool> _isLoopbackPeer;
    readonly List<NetEvent> _scratch = new();
    bool _disposed;

    /// <param name="primary">The real transport (not owned).</param>
    /// <param name="loopback">This peer's transport on the bots' hub (owned); must use the primary's local id.</param>
    /// <param name="isLoopbackPeer">Which peers live on the hub; <see cref="BotIds.IsBot"/> when null.</param>
    public CompositeTransport(ITransport primary, ITransport loopback, Func<PeerId, bool>? isLoopbackPeer = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _loopback = loopback ?? throw new ArgumentNullException(nameof(loopback));
        if (primary.LocalId != loopback.LocalId)
            throw new ArgumentException($"The loopback side ({loopback.LocalId}) must use the primary's local id ({primary.LocalId}).");
        _isLoopbackPeer = isLoopbackPeer ?? BotIds.IsBot;
    }

    public TransportKind Kind => TransportKind.Composite;
    public PeerId LocalId => _primary.LocalId;
    public bool IsRunning => _primary.IsRunning || (!_disposed && _loopback.IsRunning);
    public ITransport Primary => _primary;
    public ITransport Loopback => _loopback;
    public bool IsDisposed => _disposed;

    /// <summary>Bots are reachable. Off by default; the harness turns it on while this peer hosts.</summary>
    public bool LoopbackEnabled { get; set; }
    /// <summary>Messages and events for or from bots dropped because the loopback was disabled.</summary>
    public long LoopbackDropped { get; private set; }

    public bool IsLoopbackPeer(PeerId peer) => _isLoopbackPeer(peer);

    public void StartHost(int maxPeers, Func<PeerId, bool>? acceptFilter)
    {
        _primary.StartHost(maxPeers, acceptFilter);
        // Bots always pass the filter (spec 17 §4); the session's own gates still apply.
        if (!_disposed) _loopback.StartHost(maxPeers, null);
    }

    public void Connect(PeerId host, string? endpoint = null)
    {
        if (_isLoopbackPeer(host) && !_disposed) _loopback.Connect(host, endpoint);
        else _primary.Connect(host, endpoint);
    }

    public void Expect(PeerId peer)
    {
        if (_isLoopbackPeer(peer)) { if (!_disposed) _loopback.Expect(peer); }
        else _primary.Expect(peer);
    }

    public bool Send(PeerId to, ReadOnlySpan<byte> data, Delivery d)
    {
        if (!_isLoopbackPeer(to)) return _primary.Send(to, data, d);
        if (!LoopbackEnabled || _disposed) { LoopbackDropped++; return false; }
        return _loopback.Send(to, data, d);
    }

    public void Poll(List<NetEvent> into)
    {
        _primary.Poll(into);
        if (_disposed) return;
        _scratch.Clear();
        _loopback.Poll(_scratch);
        foreach (var e in _scratch)
        {
            // Only bots live on the hub; anything else claiming to come from there is not trusted.
            if (!LoopbackEnabled || !_isLoopbackPeer(e.Peer))
            {
                LoopbackDropped++;
                if (e.Kind == NetEventKind.Connected) _loopback.Disconnect(e.Peer);
                continue;
            }
            into.Add(e);
        }
        _scratch.Clear();
    }

    public void Disconnect(PeerId peer, byte code = 0, ReadOnlySpan<byte> final = default)
    {
        if (_isLoopbackPeer(peer)) { if (!_disposed) _loopback.Disconnect(peer, code, final); }
        else _primary.Disconnect(peer, code, final);
    }

    public void Stop(byte code = 0)
    {
        _primary.Stop(code);
        if (!_disposed) _loopback.Stop(code);
    }

    public int PendingReliableBytes(PeerId peer)
        => _isLoopbackPeer(peer) ? (_disposed ? -1 : _loopback.PendingReliableBytes(peer)) : _primary.PendingReliableBytes(peer);

    public bool TryGetStats(PeerId peer, out PeerStats stats)
    {
        if (!_isLoopbackPeer(peer)) return _primary.TryGetStats(peer, out stats);
        if (_disposed) { stats = null!; return false; }
        return _loopback.TryGetStats(peer, out stats);
    }

    public void CollectPeers(List<PeerId> into)
    {
        _primary.CollectPeers(into);
        if (!_disposed && LoopbackEnabled) _loopback.CollectPeers(into);
    }

    /// <summary>Stops and drops the loopback side for good; from now on this is the primary alone (left to its owner).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LoopbackEnabled = false;
        if (_loopback.IsRunning) _loopback.Stop();
        _loopback.Dispose();
        if (_loopback is P2PTransport { Link: LoopbackLink link }) link.Dispose();
    }
}
