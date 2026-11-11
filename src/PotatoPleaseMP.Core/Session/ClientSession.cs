using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Time;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Session;

/// <summary>Client session settings (spec 02 §3.4).</summary>
public sealed class ClientConfig
{
    public ulong SteamId { get; set; }
    public string Name { get; set; } = "Player";
    public HelloFlags Flags { get; set; }
    public int TickRate { get; set; } = NetClock.DefaultRate;

    public double WelcomeTimeout { get; set; } = 15;
    /// <summary>The host refuses with QueueTimeout at 180 s; we give up a little later on our own.</summary>
    public double QueueGiveUp { get; set; } = 185;
    public double HostNotRespondingAfter { get; set; } = 10;
    public double HostLostAfter { get; set; } = 45;
    public double LoadProgressInterval { get; set; } = 0.5;
    /// <summary>A buffer overflow (FellBehind) rejoins once by itself.</summary>
    public bool RetryOnFellBehind { get; set; } = true;
    public double RetryDelay { get; set; } = 1.0;
    public long BufferMaxBytes { get; set; } = WorldChangeBuffer.DefaultMaxBytes;
    public int BufferMaxMessages { get; set; } = WorldChangeBuffer.DefaultMaxMessages;
}

public enum ClientState : byte { Idle, Connecting, Handshaking, Queued, Downloading, Loading, Applying, Playing, Resyncing, Ended }

/// <summary>A non-session message from the host, after the session's class/phase routing.</summary>
public delegate void ClientMessageHandler(MsgId id, ReadOnlySpan<byte> payload, Delivery delivery, ushort stamp);

/// <summary>
/// The client side of the session (spec 02 §3.4): connect, Hello, Welcome (or Reject), the queue, the gather point
/// (SnapshotInfo) with buffering of world changes, the snapshot blobs, the world load through <see cref="IClientWorld"/>,
/// replay of the buffer, ClientReady, then live routing; resync, liveness, leave. Bots run exactly this code with an
/// in-memory world. Call <see cref="Update"/> once per frame with unscaled real time. Main thread only.
/// </summary>
public sealed class ClientSession : IDisposable
{
    readonly NetLayer _net;
    readonly IClientWorld _world;
    readonly ClientConfig _cfg;
    readonly TickScheduler _ticks = new();
    readonly List<NetLayerEvent> _events = new();
    readonly List<IJoinStateProvider> _providers = new();
    readonly BlobReceiver _blobs = new();
    readonly Dictionary<ushort, BlobResult> _completed = new();
    readonly HashSet<byte> _joinedFired = new();
    readonly Liveness _live = new();
    readonly NetWriter _w = new(256);
    readonly WorldChangeBuffer _buffer;
    PeerId _host;
    SnapshotInfo? _snapshot;
    bool _buffering, _dropWorldChanges, _worldReady, _retryUsed;
    double _now, _stateSince, _lastProgressAt = double.NegativeInfinity, _nextRttSample, _retryAt = double.NaN;
    PlayerPhase _lastPhaseSent;
    byte _lastPercentSent = 255;

    public ClientSession(ITransport transport, IClientWorld world, ClientConfig? config = null, MessageRegistry? registry = null)
    {
        _cfg = config ?? new ClientConfig();
        _world = world;
        _net = new NetLayer(transport, registry ?? MessageRegistry.CreateDefault(), isHost: false);
        _net.StampSource = t => Clock.HasOffset ? (ushort)Clock.EstHostTick(t, _cfg.TickRate) : (ushort)0;
        _net.UnreliableStamp = OnStamp;
        _net.Warning = (p, s) => Warning?.Invoke($"{p}: {s}");
        _buffer = new WorldChangeBuffer { MaxBytes = _cfg.BufferMaxBytes, MaxMessages = _cfg.BufferMaxMessages };
    }

    public ClientConfig Config => _cfg;
    public NetLayer Net => _net;
    public ITransport Transport => _net.Transport;
    public ClientState State { get; private set; } = ClientState.Idle;
    public PeerId Host => _host;
    public uint SessionId { get; private set; }
    public byte LocalSlot { get; private set; } = 0xFF;
    public PlayerRegistry Players { get; } = new();
    public PlayerInfo? Local => Players.BySlot(LocalSlot);
    public SessionPhase HostPhase { get; private set; }
    public byte Difficulty { get; private set; }
    public uint HostManifest { get; private set; }
    public ClockSync Clock { get; } = new();
    public QueueReason QueueReason { get; private set; }
    public byte QueuePosition { get; private set; }
    public SnapshotInfo? Snapshot => _snapshot;
    public LeaveReason EndReason { get; private set; }
    public RejectCode? RejectCode { get; private set; }
    public string? EndDetail { get; private set; }
    public bool HostNotResponding { get; private set; }
    public int LastReplayedCount { get; private set; }
    public long BufferedBytesPeak { get; private set; }
    public double RttMs => _live.RttMs;
    /// <summary>Fraction of the current snapshot's bytes received (join card).</summary>
    public float DownloadProgress => _snapshot is { } s ? (_completed.ContainsKey(s.SaveBlobId) ? 1f : _blobs.Progress(s.SaveBlobId)) : 0f;

    /// <summary>Non-session messages from the host, routed by class (world changes are buffered while joining).</summary>
    public ClientMessageHandler? MessageHandler { get; set; }
    public Action<PeerId, DatagramBuilder>? FillUnreliable { get => _net.FillUnreliable; set => _net.FillUnreliable = value; }

    public event Action<ClientState>? StateChanged;
    public event Action? Welcomed;
    public event Action<QueueReason, byte>? QueuedChanged;
    public event Action<SnapshotInfo>? SnapshotStarted;
    /// <summary>The local world is loaded and the buffer replayed: the scene is usable.</summary>
    public event Action? WorldReady;
    public event Action<PlayerInfo>? PlayerAdded;
    /// <summary>A remote player is in the game (once per player, only after our own world is ready).</summary>
    public event Action<PlayerInfo>? PlayerJoined;
    public event Action<PlayerInfo, LeaveReason>? PlayerLeft;
    public event Action<PlayerInfo>? PlayerUpdated;
    public event Action<SessionPhase>? HostPhaseChanged;
    public event Action<bool>? HostNotRespondingChanged;
    public event Action<LeaveReason, string?>? Ended;
    /// <summary>A module blob from the host (not one of our snapshot blobs).</summary>
    public event Action<BlobResult>? BlobReceived;
    public event Action<string>? Warning;

    public void Register(IJoinStateProvider provider) => _providers.Add(provider);

    // ================================================================ control

    /// <summary>Connects to the host (spec 02 §3.4 Connecting).</summary>
    public void Connect(PeerId host, double now, string? endpoint = null)
    {
        _now = now;
        _host = host;
        ResetJoinState();
        EndReason = LeaveReason.None;
        RejectCode = null;
        EndDetail = null;
        SetState(ClientState.Connecting);
        Transport.Connect(host, endpoint);
    }

    /// <summary>Leaves: Goodbye as the final message, disconnect, <see cref="Ended"/>.</summary>
    public void Leave(LeaveReason reason = LeaveReason.Left, string text = "")
    {
        if (State is ClientState.Idle or ClientState.Ended) return;
        if (_net.HasPeer(_host)) _net.Disconnect(_host, _now, new Goodbye { Reason = reason, Text = text });
        else _net.Disconnect(_host, _now);
        End(reason, text);
    }

    /// <summary>A module found a divergence it cannot repair: ask the host for a fresh snapshot.</summary>
    public bool RequestResync(byte moduleId, uint detail)
    {
        if (State != ClientState.Playing) return false;
        _net.Send(_host, new ResyncMsg { IsRequest = true, ModuleId = moduleId, Detail = detail });
        _dropWorldChanges = true; // world changes before the new gather point are covered by the new snapshot
        SetState(ClientState.Resyncing);
        return true;
    }

    public void SetReady(byte context, bool ready)
    {
        if (State is ClientState.Idle or ClientState.Connecting or ClientState.Handshaking or ClientState.Ended) return;
        _net.Send(_host, new SetReady { Context = context, Ready = ready });
    }

    /// <summary>Sends a module message to the host (reliable or unreliable per the registry; RU picks reliable).</summary>
    public bool Send(MsgId id, ReadOnlySpan<byte> payload, Delivery? delivery = null)
    {
        if (!_net.HasPeer(_host) || State is ClientState.Connecting or ClientState.Handshaking) return false;
        var info = _net.Registry[id] ?? throw new InvalidOperationException($"{id} is not registered.");
        var d = info.Delivery switch
        {
            DeliveryClass.R => Delivery.Reliable,
            DeliveryClass.U or DeliveryClass.UE => Delivery.Unreliable,
            _ => delivery ?? Delivery.Reliable,
        };
        return d == Delivery.Reliable ? _net.Send(_host, id, payload) : _net.SendUnreliable(_host, id, payload);
    }

    public bool Send<T>(in T msg, Delivery? delivery = null) where T : IWireMessage
    {
        _w.Reset();
        msg.Write(_w);
        return Send(msg.Id, _w.Span, delivery);
    }

    public void Dispose()
    {
        if (State is not (ClientState.Idle or ClientState.Ended)) Leave();
        _net.Dispose();
        _w.Dispose();
    }

    // ================================================================ update

    public void Update(double now)
    {
        _now = now;
        if (!double.IsNaN(_retryAt) && now >= _retryAt)
        {
            _retryAt = double.NaN;
            Connect(_host, now);
            return;
        }
        if (State is ClientState.Idle or ClientState.Ended) { _net.Poll(now, _events); _events.Clear(); return; }

        _events.Clear();
        _net.Poll(now, _events);
        foreach (var e in _events)
        {
            if (State == ClientState.Ended) break;
            try { Handle(e); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Warning?.Invoke($"malformed {e.Id} from host: {ex.Message}");
                Leave(LeaveReason.Rejected, "Malformed message from the host.");
            }
        }
        if (State is ClientState.Ended or ClientState.Idle) return;

        Timers();
        if (State == ClientState.Loading) PollLoad();
        if (State is ClientState.Ended or ClientState.Idle) return;
        SendProgress();
        if (_net.HasPeer(_host) && State >= ClientState.Queued && _live.HeartbeatDue(now))
            _net.SendUnreliable(_host, _live.Build(now, HeartbeatFlags.None));
        if (now >= _nextRttSample && Transport.TryGetStats(_host, out var st))
        {
            _nextRttSample = now + 1;
            if (st.RttMs >= 0) Clock.AddRtt(st.RttMs, now);
        }
        Clock.Update(now);
        if (_ticks.ShouldFlush(Clock.HasOffset ? Clock.EstHostTick(now, _cfg.TickRate) : (uint)(now * _cfg.TickRate)))
            _net.Flush(now);
    }

    void Handle(in NetLayerEvent e)
    {
        if (e.Peer != _host) return; // a client only talks to its host
        switch (e.Kind)
        {
            case NetLayerEventKind.Connected:
                if (State != ClientState.Connecting) return;
                SetState(ClientState.Handshaking);
                _live.Start(_now);
                var v = _world.Version;
                _net.Send(_host, new Hello
                {
                    Proto = v.Proto, ModVersion = v.ModVersion, GameVersion = v.GameVersion, SteamBuildId = v.SteamBuildId,
                    SaveVersion = v.SaveVersion, SteamId = _cfg.SteamId, Name = _cfg.Name, ManifestHash = v.ManifestHash, Flags = _cfg.Flags,
                });
                _net.Flush(_now);
                return;

            case NetLayerEventKind.Disconnected:
                OnDisconnected(e);
                return;

            case NetLayerEventKind.Message:
                _live.OnPacket(_now);
                if (HostNotResponding) { HostNotResponding = false; HostNotRespondingChanged?.Invoke(false); }
                OnMessage(e);
                return;
        }
    }

    void OnDisconnected(in NetLayerEvent e)
    {
        if (e.FinalId == MsgId.Reject)
        {
            var r = TryDecode<Reject>(e.Payload);
            RejectCode = r?.Code;
            End(LeaveReason.Rejected, r?.Detail ?? "Refused by the host.");
            return;
        }
        if (e.FinalId == MsgId.Goodbye && TryDecode<Goodbye>(e.Payload) is { } g)
        {
            End(g.Reason == LeaveReason.None ? LeaveReason.HostLeft : g.Reason, string.IsNullOrEmpty(g.Text) ? null : g.Text);
            return;
        }
        var reason = e.Reason switch
        {
            DisconnectReason.RemoteBye or DisconnectReason.Stopped => LeaveReason.HostLeft,
            DisconnectReason.Rejected => LeaveReason.Rejected,
            _ => LeaveReason.HostLost,
        };
        End(reason, e.Detail ?? e.Reason.ToString());
    }

    static T? TryDecode<T>(ReadOnlySpan<byte> p) where T : struct, IWireMessage
    {
        try { return SessionMessages.Decode<T>(p); }
        catch (WireFormatException) { return null; }
    }

    void OnMessage(in NetLayerEvent e)
    {
        var p = e.Payload;
        if (State == ClientState.Handshaking)
        {
            if (e.Id == MsgId.Welcome) OnWelcome(SessionMessages.Decode<Welcome>(p));
            else if (e.Id == MsgId.Reject) { var r = SessionMessages.Decode<Reject>(p); RejectCode = r.Code; Leave(LeaveReason.Rejected, r.Detail); }
            return; // nothing else before Welcome
        }
        switch (e.Id)
        {
            case MsgId.Welcome: return;
            case MsgId.Reject:
            {
                var r = SessionMessages.Decode<Reject>(p);
                RejectCode = r.Code;
                Leave(LeaveReason.Rejected, r.Detail);
                return;
            }
            case MsgId.Goodbye:
            {
                var g = SessionMessages.Decode<Goodbye>(p);
                _net.Disconnect(_host, _now);
                End(g.Reason, g.Text);
                return;
            }
            case MsgId.Queued:
            {
                var q = SessionMessages.Decode<Queued>(p);
                QueueReason = q.Reason;
                QueuePosition = q.Position;
                if (State is ClientState.Queued or ClientState.Playing or ClientState.Resyncing)
                {
                    if (State != ClientState.Queued) _dropWorldChanges = true;
                    SetState(ClientState.Queued);
                }
                QueuedChanged?.Invoke(q.Reason, q.Position);
                return;
            }
            case MsgId.Resync:
                // "A snapshot follows": world changes up to its gather point are part of it.
                _dropWorldChanges = true;
                if (State == ClientState.Playing) SetState(ClientState.Resyncing);
                return;
            case MsgId.SnapshotInfo: OnSnapshotInfo(SessionMessages.Decode<SnapshotInfo>(p)); return;
            case MsgId.PlayerAdded: OnPlayerAdded(SessionMessages.Decode<PlayerAdded>(p).Player); return;
            case MsgId.PlayerRemoved: OnPlayerRemoved(SessionMessages.Decode<PlayerRemoved>(p)); return;
            case MsgId.PlayerUpdate: OnPlayerUpdate(SessionMessages.Decode<PlayerUpdate>(p)); return;
            case MsgId.Heartbeat:
                _live.OnHeartbeat(SessionMessages.Decode<Heartbeat>(p), _now);
                if (_live.RttMs >= 0) Clock.AddRtt(_live.RttMs, _now);
                return;
            case MsgId.SessionState:
            {
                var s = SessionMessages.Decode<SessionStateMsg>(p);
                if (s.Phase != HostPhase) { HostPhase = s.Phase; HostPhaseChanged?.Invoke(s.Phase); }
                return;
            }
            case MsgId.BlobBegin: OnBlobResult(_blobs.OnBegin(SessionMessages.Decode<BlobBegin>(p))); return;
            case MsgId.BlobChunk:
            {
                var data = BlobChunkMsg.Parse(p, out var id, out var off);
                OnBlobResult(_blobs.OnChunk(id, off, data));
                return;
            }
            case MsgId.BlobCancel: _blobs.OnCancel(SessionMessages.Decode<BlobCancel>(p).BlobId); return;
            case MsgId.BlobDone: return; // we send no blobs of our own here
        }

        var info = _net.Registry[e.Id];
        if (info == null) return;
        switch (info.Class)
        {
            case SessionClass.Session:
                MessageHandler?.Invoke(e.Id, p, e.Delivery, e.Stamp);
                break;
            case SessionClass.WorldChange:
                if (_dropWorldChanges) break;
                if (_buffering)
                {
                    if (!_buffer.Add(e.Id, p)) { FellBehind(); return; }
                    BufferedBytesPeak = Math.Max(BufferedBytesPeak, _buffer.Bytes);
                }
                else if (State == ClientState.Playing) MessageHandler?.Invoke(e.Id, p, e.Delivery, e.Stamp);
                break;
            default:
                if (State == ClientState.Playing) MessageHandler?.Invoke(e.Id, p, e.Delivery, e.Stamp);
                break;
        }
    }

    void OnStamp(PeerId peer, ushort stamp, double now)
    {
        if (peer != _host || !Clock.HasOffset) return;
        uint tick = SeqMath.Unwrap16(stamp, Clock.EstHostTick(now, _cfg.TickRate));
        Clock.AddSample(tick / (double)_cfg.TickRate, now);
    }

    // ================================================================ join

    void OnWelcome(Welcome w)
    {
        SessionId = w.SessionId;
        LocalSlot = w.YourSlot;
        HostPhase = w.Phase;
        Difficulty = w.Difficulty;
        HostManifest = w.ManifestHash;
        Players.Clear();
        Players.MaxPlayers = Math.Max(1, (int)w.MaxPlayers);
        foreach (var p in w.Players)
        {
            p.IsLocal = p.Slot == w.YourSlot;
            Players.Add(p);
        }
        // Host time = its tick at send plus half a round trip; stamps refine it from here (spec 01 §6.2).
        double rtt = Transport.TryGetStats(_host, out var st) && st.RttMs >= 0 ? st.RttMs / 1000.0 : 0;
        Clock.Reset(w.HostTick / (double)Math.Max(1, (int)w.TickRate) + rtt / 2, _now);
        SetState(ClientState.Queued);
        Welcomed?.Invoke();
        foreach (var p in Players.Players)
            if (!p.IsLocal) PlayerAdded?.Invoke(p);
    }

    void OnSnapshotInfo(SnapshotInfo s)
    {
        _snapshot = s;
        _completed.Remove(s.SaveBlobId);
        _completed.Remove(s.ExtrasBlobId);
        _buffer.Clear();
        _buffering = true;          // everything after the gather point is replayed after our load
        _dropWorldChanges = false;
        _worldReady = false;
        _lastPercentSent = 255;
        SetState(ClientState.Downloading);
        if (Local is { } me) me.Phase = PlayerPhase.Downloading;
        SnapshotStarted?.Invoke(s);
        TryStartLoad();
    }

    void OnBlobResult(BlobResult? r)
    {
        if (r == null) return;
        _net.Send(_host, new BlobDone { BlobId = r.Id, Status = r.Status });
        if (!r.Ok)
        {
            Warning?.Invoke($"blob {r.Id} failed: {r.Error}");
            Leave(LeaveReason.LoadFailed, "The shift data arrived damaged.");
            return;
        }
        if (_snapshot is { } s && (r.Id == s.SaveBlobId || r.Id == s.ExtrasBlobId))
        {
            _completed[r.Id] = r;
            TryStartLoad();
        }
        else BlobReceived?.Invoke(r);
    }

    void TryStartLoad()
    {
        if (State != ClientState.Downloading || _snapshot is not { } s) return;
        if (!_completed.TryGetValue(s.SaveBlobId, out var save) || !_completed.TryGetValue(s.ExtrasBlobId, out var extras)) return;
        _completed.Remove(s.SaveBlobId);
        _completed.Remove(s.ExtrasBlobId);
        Dictionary<byte, byte[]> parts;
        try { parts = JoinExtras.Parse(extras.Data!); }
        catch (WireFormatException ex)
        {
            Warning?.Invoke("join extras: " + ex.Message);
            Leave(LeaveReason.LoadFailed, "The shift data arrived damaged.");
            return;
        }
        // Preparing: modules apply what must be in place before the world loads.
        foreach (var pr in _providers)
            if (parts.TryGetValue(pr.ModuleId, out var bytes))
            {
                using var br = new BinaryReader(new MemoryStream(bytes, writable: false));
                pr.ApplyBeforeLoad(br);
            }
        SetState(ClientState.Loading);
        if (Local is { } me) me.Phase = PlayerPhase.Loading;
        _extrasForAfter = parts;
        // Tell the host a stall is expected before the (possibly blocking) load starts.
        SendProgressNow(PlayerPhase.Loading, 0);
        _net.Flush(_now);
        _world.BeginLoad(new ClientSnapshot { Info = s, Save = save.Data!, SaveCrc = Crc32.Compute(save.Data!), Extras = parts });
    }

    Dictionary<byte, byte[]>? _extrasForAfter;

    void PollLoad()
    {
        var st = _world.PollLoad(_now);
        if (st.State == LoadState.Loading) { _progressPercent = st.Percent; return; }
        if (st.State == LoadState.Failed)
        {
            Leave(LeaveReason.LoadFailed, st.Error ?? "The shift failed to load.");
            return;
        }
        // Applying: after-load extras, then the buffered world changes in arrival order, then live.
        SetState(ClientState.Applying);
        if (Local is { } me) me.Phase = PlayerPhase.Applying;
        SendProgressNow(PlayerPhase.Applying, 100);
        if (_extrasForAfter != null)
            foreach (var pr in _providers)
                if (_extrasForAfter.TryGetValue(pr.ModuleId, out var bytes))
                {
                    using var br = new BinaryReader(new MemoryStream(bytes, writable: false));
                    pr.ApplyAfterLoad(br);
                }
        _extrasForAfter = null;
        _buffering = false;
        int replayed = _buffer.Replay((id, payload) => MessageHandler?.Invoke(id, payload, Delivery.Reliable, 0));
        LastReplayedCount = replayed;
        _net.Send(_host, new ClientReady { SnapshotId = _snapshot?.SnapshotId ?? 0, ManifestHash = _world.ManifestHash, Replayed = (ushort)Math.Min(ushort.MaxValue, replayed) });
        SetState(ClientState.Playing);
        if (Local is { } me2) me2.Phase = PlayerPhase.Playing;
        bool first = !_worldReady;
        _worldReady = true;
        if (first)
        {
            WorldReady?.Invoke();
            foreach (var p in Players.Players)
                if (!p.IsLocal && p.Phase == PlayerPhase.Playing && _joinedFired.Add(p.Slot)) PlayerJoined?.Invoke(p);
        }
    }

    byte _progressPercent;

    void SendProgress()
    {
        PlayerPhase phase;
        byte pct;
        switch (State)
        {
            case ClientState.Downloading: phase = PlayerPhase.Downloading; pct = (byte)Math.Clamp(DownloadProgress * 100, 0, 100); break;
            case ClientState.Loading: phase = PlayerPhase.Loading; pct = Math.Min((byte)100, _progressPercent); break;
            default: return;
        }
        if (_now - _lastProgressAt < _cfg.LoadProgressInterval) return;
        if (phase == _lastPhaseSent && pct == _lastPercentSent) return;
        SendProgressNow(phase, pct);
    }

    void SendProgressNow(PlayerPhase phase, byte pct)
    {
        _lastProgressAt = _now;
        _lastPhaseSent = phase;
        _lastPercentSent = pct;
        _net.Send(_host, new LoadProgress { Phase = phase, Percent = pct });
    }

    void FellBehind()
    {
        Warning?.Invoke($"world-change buffer overflow ({_buffer.Count} msgs, {_buffer.Bytes} B)");
        bool retry = _cfg.RetryOnFellBehind && !_retryUsed;
        Leave(LeaveReason.FellBehind, "Too far behind the host.");
        if (retry)
        {
            _retryUsed = true;
            _retryAt = _now + _cfg.RetryDelay;
        }
    }

    // ================================================================ registry mirror

    void OnPlayerAdded(PlayerInfo p)
    {
        p.IsLocal = p.Slot == LocalSlot;
        Players.Add(p);
        _joinedFired.Remove(p.Slot);
        PlayerAdded?.Invoke(p);
    }

    void OnPlayerRemoved(PlayerRemoved m)
    {
        var p = Players.Remove(m.Slot);
        if (p == null) return;
        p.Phase = PlayerPhase.Leaving;
        if (_joinedFired.Remove(m.Slot)) PlayerLeft?.Invoke(p, m.Reason);
    }

    void OnPlayerUpdate(PlayerUpdate m)
    {
        var p = Players.BySlot(m.Slot);
        if (p == null) return;
        bool becamePlaying = p.Phase != PlayerPhase.Playing && m.Phase == PlayerPhase.Playing;
        if (!p.IsLocal) p.Phase = m.Phase; // our own phase is ours to know
        p.Flags = m.Flags;
        p.RttMs = m.RttMs;
        p.StationIndex = m.StationIndex;
        PlayerUpdated?.Invoke(p);
        if (becamePlaying && !p.IsLocal && _worldReady && _joinedFired.Add(p.Slot)) PlayerJoined?.Invoke(p);
    }

    // ================================================================ timers, end

    void Timers()
    {
        double inState = _now - _stateSince;
        if (State == ClientState.Handshaking && inState > _cfg.WelcomeTimeout) { Leave(LeaveReason.HostLost, "No answer from the host."); return; }
        if (State == ClientState.Queued && inState > _cfg.QueueGiveUp) { Leave(LeaveReason.Left, "Gave up waiting to join."); return; }
        if (State < ClientState.Handshaking) return;
        double silence = _live.Silence(_now);
        if (silence > _cfg.HostLostAfter) { Leave(LeaveReason.HostLost, "The host stopped responding."); return; }
        bool nr = silence > _cfg.HostNotRespondingAfter;
        if (nr != HostNotResponding) { HostNotResponding = nr; HostNotRespondingChanged?.Invoke(nr); }
    }

    void End(LeaveReason reason, string? detail)
    {
        if (State == ClientState.Ended) return;
        EndReason = reason;
        EndDetail = detail;
        ResetJoinState();
        SetState(ClientState.Ended);
        _world.OnSessionEnded(reason); // restores whatever the join changed (no-op if nothing was loaded)
        Ended?.Invoke(reason, detail);
    }

    void ResetJoinState()
    {
        _snapshot = null;
        _completed.Clear();
        _blobs.Clear();
        _buffer.Clear();
        _buffering = _dropWorldChanges = _worldReady = false;
        _extrasForAfter = null;
        _joinedFired.Clear();
        HostNotResponding = false;
        QueueReason = QueueReason.None;
    }

    void SetState(ClientState s)
    {
        if (State == s) return;
        State = s;
        _stateSince = _now;
        StateChanged?.Invoke(s);
    }
}
