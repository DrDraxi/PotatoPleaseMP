using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Time;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Session;

/// <summary>Host session settings (spec 02; timers in seconds of unscaled real time).</summary>
public sealed class HostConfig
{
    public int MaxPlayers { get; set; } = 4;
    public uint SessionId { get; set; } = (uint)Random.Shared.Next(1, int.MaxValue);
    public ulong HostSteamId { get; set; }
    public string HostName { get; set; } = "Host";
    public int TickRate { get; set; } = NetClock.DefaultRate;

    public double HelloTimeout { get; set; } = 10;
    public double QueueTimeout { get; set; } = 180;
    public double QueueRecheckInterval { get; set; } = 0.5;
    public double NotRespondingAfter { get; set; } = 10;
    public double TimeoutAfter { get; set; } = 45;
    /// <summary>Downloading/Loading/Applying peers may stall in a scene load: 180 s instead of 45 s.</summary>
    public double LoadingTimeoutAfter { get; set; } = 180;
    /// <summary>No blob bytes acknowledged for this long while some are in flight: the join fails.</summary>
    public double DownloadStallTimeout { get; set; } = 30;
    public double ReservationSeconds { get; set; } = 300;
    public double KickBanSeconds { get; set; } = 600;
    public double RttBroadcastInterval { get; set; } = 5;
    public int MaxConcurrentSnapshots { get; set; } = 2;

    /// <summary>On Steam, Hello.SteamId must equal the transport PeerId (universe-0 bots/UDP are exempt).</summary>
    public bool RequireSteamIdMatchesPeer { get; set; } = true;
    /// <summary>Dev only: two copies on one Steam account (Direct UDP).</summary>
    public bool AllowDuplicateSteamId { get; set; }

    public BlobCodec SaveCodec { get; set; } = BlobCodec.Brotli;
    public BlobCodec ExtrasCodec { get; set; } = BlobCodec.Deflate;
    /// <summary>Compress join blobs on a worker thread (the plugin); tests turn it off for determinism.</summary>
    public bool PrepareBlobsOnWorker { get; set; } = true;
}

/// <summary>A non-session message from a client, after the session's class/phase checks.</summary>
public delegate void HostMessageHandler(PlayerInfo from, MsgId id, ReadOnlySpan<byte> payload, Delivery delivery, ushort stamp);

/// <summary>
/// The host side of the session (spec 02 §3.1-3.3, §3.5-3.6): admission (Hello -> Welcome or Reject), join gates and
/// the queue, the gather point (SnapshotInfo) and snapshot fan-out as blobs, liveness, leave/kick/timeout, resync, and
/// shutdown. Game actions go through <see cref="IHostWorld"/>, so it runs headless. Call <see cref="Update"/> once per
/// frame with unscaled real time; it polls the transport and flushes at the 30 Hz net tick. Main thread only.
/// </summary>
public sealed class HostSession : IDisposable
{
    enum Step { AwaitHello, AwaitCapture, Streaming, Playing }

    sealed class Client
    {
        public PeerId Peer;
        public PlayerInfo? Info;
        public Step Step;
        public double ConnectedAt, QueuedSince, NextGateCheck, LastRttBroadcast;
        public QueueReason LastQueue;
        public bool Gathered, WasPlaying;
        public uint SnapshotId;
        public ushort SaveBlobId, ExtrasBlobId;
        public SnapshotReason PendingReason;
        public readonly BlobSender Blobs = new();
        public readonly BlobReceiver In = new();
        public readonly Liveness Live = new();
        public long RejectedNonJoin;
    }

    sealed class Capture
    {
        public uint SnapshotId;
        public HostSnapshot Snap = null!;
        public byte[] Extras = Array.Empty<byte>();
        public readonly List<Client> Targets = new();
        public Task<(PreparedBlob Save, PreparedBlob Extras)>? Prepared;
    }

    readonly NetLayer _net;
    readonly IHostWorld _world;
    readonly HostConfig _cfg;
    readonly NetClock _clock;
    readonly TickScheduler _ticks = new();
    readonly Dictionary<PeerId, Client> _clients = new();
    readonly List<NetLayerEvent> _events = new();
    readonly List<Func<QueueReason?>> _gates = new();
    readonly List<IJoinStateProvider> _providers = new();
    readonly List<Capture> _captures = new();
    readonly Dictionary<ulong, double> _bans = new();
    readonly NetWriter _w = new(256);
    uint _snapshotCounter;
    double _now;
    bool _ended;
    (SessionPhase, int) _lastStateSent = (SessionPhase.Idle, -1);

    public HostSession(ITransport transport, IHostWorld world, HostConfig? config = null, MessageRegistry? registry = null, double now = 0)
    {
        _cfg = config ?? new HostConfig();
        _world = world;
        _net = new NetLayer(transport, registry ?? MessageRegistry.CreateDefault(), isHost: true);
        _clock = new NetClock(now, _cfg.TickRate);
        _net.StampSource = t => (ushort)_clock.TickAt(t);
        _net.Warning = (p, s) => Warning?.Invoke($"{p}: {s}");
        Players.MaxPlayers = _cfg.MaxPlayers;
        _now = now;
    }

    public HostConfig Config => _cfg;
    public NetLayer Net => _net;
    public ITransport Transport => _net.Transport;
    public MessageRegistry Registry => _net.Registry;
    public PlayerRegistry Players { get; } = new();
    public PlayerInfo? Local => Players.BySlot(0);
    public SessionPhase Phase { get; private set; } = SessionPhase.Idle;
    public uint SessionId => _cfg.SessionId;
    public uint CurrentTick => _clock.TickAt(_now);
    public NetClock Clock => _clock;
    public bool IsActive => Phase is not (SessionPhase.Idle or SessionPhase.Closing);

    /// <summary>Non-session messages from clients (after direction, class and phase checks).</summary>
    public HostMessageHandler? MessageHandler { get; set; }
    /// <summary>Stream slots per peer for the unreliable datagram (presence, bodies).</summary>
    public Action<PeerId, DatagramBuilder>? FillUnreliable { get => _net.FillUnreliable; set => _net.FillUnreliable = value; }

    public event Action<PlayerInfo>? PlayerAdded;
    /// <summary>A client reached Playing (spawn its avatar).</summary>
    public event Action<PlayerInfo>? PlayerJoined;
    /// <summary>A player who had been Playing was removed.</summary>
    public event Action<PlayerInfo, LeaveReason>? PlayerLeft;
    /// <summary>Any removal, including joins that never reached Playing.</summary>
    public event Action<PlayerInfo, LeaveReason>? PlayerRemoved;
    public event Action<PlayerInfo>? PlayerUpdated;
    public event Action? SessionStarted;
    public event Action<LeaveReason>? SessionEnded;
    public event Action<SessionPhase>? PhaseChanged;
    /// <summary>Hosting reached (world loaded).</summary>
    public event Action? WorldReady;
    /// <summary>Right before a gather point: modules flush pending world-change batches now.</summary>
    public event Action? OnBeforeSnapshot;
    public event Action<PlayerInfo, byte, uint>? ResyncRequested;
    public event Action<PlayerInfo, byte, bool>? ReadyChanged;
    /// <summary>A blob a client sent us (module data such as a carve snapshot).</summary>
    public event Action<PlayerInfo, BlobResult>? BlobReceived;
    /// <summary>A peer was refused (diagnostics: code and detail).</summary>
    public event Action<PeerId, RejectCode, string>? Rejected;
    public event Action<string>? Warning;

    public void AddJoinGate(Func<QueueReason?> gate) => _gates.Add(gate);
    public void Register(IJoinStateProvider provider) => _providers.Add(provider);

    // ================================================================ lifecycle

    /// <summary>
    /// Starts hosting: the transport listens and the host's own row (slot 0) is created. With
    /// <paramref name="worldReady"/> false the session sits in HostLoading (joins queue) until <see cref="SetWorldReady"/>.
    /// </summary>
    public void Start(double now, bool worldReady = true, Func<PeerId, bool>? acceptFilter = null)
    {
        if (Phase != SessionPhase.Idle) throw new InvalidOperationException($"Session already {Phase}.");
        _now = now;
        _clock.Align(now, 0);
        _ended = false;
        Players.Clear();
        Players.MaxPlayers = _cfg.MaxPlayers;
        Players.Add(new PlayerInfo
        {
            Peer = Transport.LocalId, Slot = 0, SteamId = _cfg.HostSteamId, Name = PlayerRegistry.SanitizeName(_cfg.HostName),
            Phase = PlayerPhase.Playing, Flags = PlayerFlags.Host, IsLocal = true, StationIndex = 0,
        });
        // The transport admits up to MaxPlayers - 1 connected peers; the session's own Full check runs at Hello.
        Transport.StartHost(Math.Max(1, _cfg.MaxPlayers - 1) + 1, acceptFilter);
        SetPhase(worldReady ? SessionPhase.Hosting : SessionPhase.HostLoading);
        SessionStarted?.Invoke();
        if (worldReady) WorldReady?.Invoke();
    }

    /// <summary>The host's world finished loading (first load or a reload): Hosting, and resyncing clients get their snapshot.</summary>
    public void SetWorldReady()
    {
        if (Phase is SessionPhase.HostLoading or SessionPhase.HostArming)
        {
            SetPhase(SessionPhase.Hosting);
            WorldReady?.Invoke();
        }
    }

    /// <summary>
    /// The host is loading another world while clients are in (pause-menu load, crash recovery): every client keeps its
    /// slot and gets a <see cref="SnapshotReason.HostReload"/> snapshot once <see cref="SetWorldReady"/> is called.
    /// </summary>
    public void BeginHostReload()
    {
        SetPhase(SessionPhase.HostLoading);
        foreach (var c in _clients.Values)
        {
            if (c.Info == null) continue;
            CancelBlobs(c);
            c.Gathered = false;
            c.Step = Step.AwaitCapture;
            c.PendingReason = SnapshotReason.HostReload;
            c.QueuedSince = _now;
            c.NextGateCheck = _now;
            c.LastQueue = QueueReason.None;
            SetPlayerPhase(c, PlayerPhase.Queued);
        }
    }

    public void EnterFinal() { if (Phase == SessionPhase.Hosting) SetPhase(SessionPhase.HostingFinal); }
    public void ExitFinal() { if (Phase == SessionPhase.HostingFinal) SetPhase(SessionPhase.Hosting); }

    /// <summary>The ending is showing: queued joiners are refused (ShiftOver), the lobby should become not joinable.</summary>
    public void ShowEnding()
    {
        if (!IsActive) return;
        SetPhase(SessionPhase.Ending);
        foreach (var c in _clients.Values.Where(c => c.Step is Step.AwaitHello or Step.AwaitCapture).ToList())
            RejectClient(c, RejectCode.ShiftOver, "The shift is over.");
    }

    /// <summary>
    /// Ends the session: Goodbye(reason) to every client as the transport's final message, transport stopped,
    /// registry cleared, <see cref="SessionEnded"/> fired once.
    /// </summary>
    public void Shutdown(LeaveReason reason = LeaveReason.HostLeft, string text = "")
    {
        if (_ended) return;
        _ended = true;
        SetPhase(SessionPhase.Closing);
        foreach (var c in _clients.Values.ToList())
        {
            c.Blobs.Clear();
            c.In.Clear();
            _net.Disconnect(c.Peer, _now, new Goodbye { Reason = reason, Text = text });
        }
        _clients.Clear();
        _captures.Clear();
        _net.Stop(_now);
        Players.Clear();
        SetPhase(SessionPhase.Idle);
        SessionEnded?.Invoke(reason);
    }

    public void Dispose()
    {
        if (!_ended && Phase != SessionPhase.Idle) Shutdown();
        _net.Dispose();
        _w.Dispose();
    }

    /// <summary>Removes a client (host only): Goodbye(Kicked), and its SteamId is refused for 10 minutes.</summary>
    public bool Kick(byte slot, string text = "")
    {
        var c = _clients.Values.FirstOrDefault(x => x.Info?.Slot == slot);
        if (c == null) return false;
        if (c.Info!.SteamId != 0) _bans[c.Info.SteamId] = _now + _cfg.KickBanSeconds;
        RemoveClient(c, LeaveReason.Kicked, new Goodbye { Reason = LeaveReason.Kicked, Text = text });
        return true;
    }

    /// <summary>Host-side ready flag of the host's own row.</summary>
    public void SetLocalReady(byte context, bool ready)
    {
        var me = Local;
        if (me == null) return;
        var f = ready ? me.Flags | PlayerFlags.Ready : me.Flags & ~PlayerFlags.Ready;
        if (f == me.Flags) return;
        me.Flags = f;
        BroadcastPlayerUpdate(me);
        ReadyChanged?.Invoke(me, context, ready);
    }

    /// <summary>Sends a fresh snapshot to one playing client (a module found a divergence it cannot repair).</summary>
    public bool ResyncClient(byte slot, SnapshotReason reason = SnapshotReason.Resync)
    {
        var c = _clients.Values.FirstOrDefault(x => x.Info?.Slot == slot);
        if (c?.Info == null || c.Step == Step.AwaitHello) return false;
        CancelBlobs(c);
        c.Gathered = false;
        c.Step = Step.AwaitCapture;
        c.PendingReason = reason;
        c.QueuedSince = _now;
        c.NextGateCheck = _now;
        c.LastQueue = QueueReason.None;
        SetPlayerPhase(c, PlayerPhase.Queued);
        return true;
    }

    // ================================================================ sending (modules)

    /// <summary>
    /// Sends to every client the id's session class allows (Session: welcomed; WorldChange: past its gather point;
    /// State: Playing). Delivery follows the registry (RU ids take <paramref name="delivery"/>).
    /// </summary>
    public int Broadcast(MsgId id, ReadOnlySpan<byte> payload, byte exceptSlot = 0xFF, Delivery? delivery = null)
    {
        var info = Registry[id] ?? throw new InvalidOperationException($"{id} is not registered.");
        var d = PickDelivery(info, delivery);
        int n = 0;
        foreach (var c in _clients.Values)
        {
            if (c.Info == null || c.Info.Slot == exceptSlot || !Accepts(c, info)) continue;
            if (d == Delivery.Reliable ? _net.Send(c.Peer, id, payload) : _net.SendUnreliable(c.Peer, id, payload)) n++;
        }
        return n;
    }

    public int Broadcast<T>(in T msg, byte exceptSlot = 0xFF, Delivery? delivery = null) where T : IWireMessage
    {
        _w.Reset();
        msg.Write(_w);
        return Broadcast(msg.Id, _w.Span, exceptSlot, delivery);
    }

    /// <summary>Sends to one client if the id's class allows it now.</summary>
    public bool SendTo(byte slot, MsgId id, ReadOnlySpan<byte> payload, Delivery? delivery = null)
    {
        var info = Registry[id] ?? throw new InvalidOperationException($"{id} is not registered.");
        var c = _clients.Values.FirstOrDefault(x => x.Info?.Slot == slot);
        if (c == null || !Accepts(c, info)) return false;
        return PickDelivery(info, delivery) == Delivery.Reliable ? _net.Send(c.Peer, id, payload) : _net.SendUnreliable(c.Peer, id, payload);
    }

    public bool SendTo<T>(byte slot, in T msg, Delivery? delivery = null) where T : IWireMessage
    {
        _w.Reset();
        msg.Write(_w);
        return SendTo(slot, msg.Id, _w.Span, delivery);
    }

    /// <summary>
    /// Relays a client's message to the others byte for byte (spec 01 §5.2); the slot byte at
    /// <paramref name="slotOffset"/> is overwritten from the sender (identity never comes from the payload).
    /// </summary>
    public int Relay(PlayerInfo from, MsgId id, ReadOnlySpan<byte> payload, int slotOffset = -1, bool includeSender = false, Delivery? delivery = null)
    {
        _w.Reset();
        _w.Raw(payload);
        if (slotOffset >= 0 && slotOffset < payload.Length) _w.PatchU8(slotOffset, from.Slot);
        var bytes = _w.ToArray();
        return Broadcast(id, bytes, includeSender ? (byte)0xFF : from.Slot, delivery);
    }

    static Delivery PickDelivery(MsgInfo info, Delivery? asked)
        => info.Delivery switch
        {
            DeliveryClass.R => Delivery.Reliable,
            DeliveryClass.U or DeliveryClass.UE => Delivery.Unreliable,
            _ => asked ?? Delivery.Reliable,
        };

    static bool Accepts(Client c, MsgInfo info) => info.Class switch
    {
        SessionClass.Session => c.Info != null,
        SessionClass.WorldChange => c.Gathered,
        _ => c.Info?.Phase == PlayerPhase.Playing,
    };

    /// <summary>Starts a module blob to one client (e.g. a carve snapshot); returns its id or 0.</summary>
    public ushort SendBlob(byte slot, PreparedBlob blob)
    {
        var c = _clients.Values.FirstOrDefault(x => x.Info?.Slot == slot);
        return c == null ? (ushort)0 : c.Blobs.Enqueue(blob);
    }

    // ================================================================ update

    public void Update(double now)
    {
        _now = now;
        if (Phase == SessionPhase.Idle) return;
        _events.Clear();
        _net.Poll(now, _events);
        foreach (var e in _events)
        {
            try { Handle(e); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Warning?.Invoke($"handler error for {e.Peer}: {ex.Message}");
                if (_clients.TryGetValue(e.Peer, out var bad)) RejectClient(bad, RejectCode.Malformed, "Malformed message.");
            }
        }
        if (Phase == SessionPhase.Idle) return;

        Players.ExpireReservations(now);
        foreach (var c in _clients.Values.ToList()) Timers(c);
        RunCaptures();
        CompleteCaptures();
        foreach (var c in _clients.Values.ToList()) PumpBlobs(c);
        foreach (var c in _clients.Values)
            if (c.Info != null && c.Live.HeartbeatDue(now))
                _net.SendUnreliable(c.Peer, c.Live.Build(now, HeartbeatFlags.None));
        BroadcastSessionStateIfChanged();
        if (_ticks.ShouldFlush(_clock.TickAt(now))) _net.Flush(now);
    }

    void Handle(in NetLayerEvent e)
    {
        switch (e.Kind)
        {
            case NetLayerEventKind.Connected:
            {
                var c = new Client { Peer = e.Peer, ConnectedAt = _now, Step = Step.AwaitHello };
                c.Live.Start(_now);
                c.In.AllowKind = k => k is not (BlobKind.WorldSave or BlobKind.JoinExtras);
                _clients[e.Peer] = c;
                break;
            }
            case NetLayerEventKind.Disconnected:
            {
                if (!_clients.TryGetValue(e.Peer, out var c)) break;
                var reason = e.Reason switch
                {
                    DisconnectReason.Timeout or DisconnectReason.ConnectionLost => LeaveReason.TimedOut,
                    DisconnectReason.Replaced => LeaveReason.Replaced,
                    _ => LeaveReason.Left,
                };
                if (e.FinalId == MsgId.Goodbye)
                {
                    try { reason = Sanitize(SessionMessages.Decode<Goodbye>(e.Payload).Reason); }
                    catch (WireFormatException) { }
                }
                RemoveClient(c, reason, null, transportGone: true);
                break;
            }
            case NetLayerEventKind.Message:
                if (_clients.TryGetValue(e.Peer, out var from)) OnMessage(from, e);
                break;
        }
    }

    /// <summary>A client may only claim leave reasons that are its own to give.</summary>
    static LeaveReason Sanitize(LeaveReason r) => r is LeaveReason.Left or LeaveReason.FellBehind or LeaveReason.LoadFailed or LeaveReason.SaveGuard
        ? r : LeaveReason.Left;

    void OnMessage(Client c, in NetLayerEvent e)
    {
        c.Live.OnPacket(_now);
        var p = e.Payload;
        if (c.Info == null)
        {
            if (e.Id == MsgId.Hello) OnHello(c, SessionMessages.Decode<Hello>(p));
            else c.RejectedNonJoin++; // nothing but Hello before the handshake
            return;
        }
        switch (e.Id)
        {
            case MsgId.Hello: return; // duplicate
            case MsgId.Heartbeat:
                c.Live.OnHeartbeat(SessionMessages.Decode<Heartbeat>(p), _now);
                UpdateRtt(c);
                return;
            case MsgId.LoadProgress: OnLoadProgress(c, SessionMessages.Decode<LoadProgress>(p)); return;
            case MsgId.ClientReady: OnClientReady(c, SessionMessages.Decode<ClientReady>(p)); return;
            case MsgId.Goodbye:
            {
                var g = SessionMessages.Decode<Goodbye>(p);
                RemoveClient(c, Sanitize(g.Reason), null);
                return;
            }
            case MsgId.SetReady:
            {
                var m = SessionMessages.Decode<SetReady>(p);
                var f = m.Ready ? c.Info.Flags | PlayerFlags.Ready : c.Info.Flags & ~PlayerFlags.Ready;
                if (f != c.Info.Flags) { c.Info.Flags = f; BroadcastPlayerUpdate(c.Info); }
                ReadyChanged?.Invoke(c.Info, m.Context, m.Ready);
                return;
            }
            case MsgId.Resync:
            {
                var m = SessionMessages.Decode<ResyncMsg>(p);
                if (!m.IsRequest || c.Step != Step.Playing) return;
                ResyncRequested?.Invoke(c.Info, m.ModuleId, m.Detail);
                ResyncClient(c.Info.Slot, SnapshotReason.Resync);
                return;
            }
            case MsgId.BlobDone:
            {
                var m = SessionMessages.Decode<BlobDone>(p);
                c.Blobs.OnDone(m.BlobId, m.Status);
                return;
            }
            case MsgId.BlobCancel:
            {
                var m = SessionMessages.Decode<BlobCancel>(p);
                c.Blobs.OnCancel(m.BlobId);
                c.In.OnCancel(m.BlobId);
                return;
            }
            case MsgId.BlobBegin:
                HandleBlobResult(c, c.In.OnBegin(SessionMessages.Decode<BlobBegin>(p)));
                return;
            case MsgId.BlobChunk:
            {
                var data = BlobChunkMsg.Parse(p, out var id, out var off);
                HandleBlobResult(c, c.In.OnChunk(id, off, data));
                return;
            }
            case MsgId.Welcome or MsgId.Reject or MsgId.Queued or MsgId.SnapshotInfo or MsgId.PlayerAdded or MsgId.PlayerRemoved
                or MsgId.PlayerUpdate or MsgId.SessionState:
                return; // host-to-client ids (the registry already drops them; belt and braces)
        }

        var info = Registry[e.Id];
        if (info == null) return;
        // A joiner may only send session-class ids; world intents and state need Playing (spec 01 §7.2).
        if (info.Class != SessionClass.Session && c.Info.Phase != PlayerPhase.Playing) { c.RejectedNonJoin++; return; }
        MessageHandler?.Invoke(c.Info, e.Id, p, e.Delivery, e.Stamp);
    }

    void HandleBlobResult(Client c, BlobResult? r)
    {
        if (r == null) return;
        _net.Send(c.Peer, new BlobDone { BlobId = r.Id, Status = r.Status });
        if (r.Ok) { BlobReceived?.Invoke(c.Info!, r); return; }
        Warning?.Invoke($"blob from {c.Info}: {r.Error}");
        RejectClient(c, RejectCode.Malformed, "Bad blob: " + r.Error);
    }

    // ================================================================ join pipeline

    void OnHello(Client c, Hello h)
    {
        if (Phase is SessionPhase.Ending or SessionPhase.Closing || _world.IsShiftOver)
        {
            RejectClient(c, RejectCode.ShiftOver, "The shift is over.");
            return;
        }
        if (_cfg.RequireSteamIdMatchesPeer && !c.Peer.IsUniverseZero && h.SteamId != c.Peer.Value)
        {
            RejectClient(c, RejectCode.NotMember, "Steam id does not match the connection.");
            return;
        }
        bool banned = h.SteamId != 0 && _bans.TryGetValue(h.SteamId, out var until) && until > _now;
        // Same account again (reconnect before the old peer timed out): the old peer is replaced, so it does not count.
        Client? old = null;
        if (h.SteamId != 0 && !_cfg.AllowDuplicateSteamId)
            old = _clients.Values.FirstOrDefault(x => x != c && x.Info != null && x.Info.SteamId == h.SteamId);
        int live = Players.Count - (old != null ? 1 : 0);
        bool full = live >= Players.MaxPlayers;
        var verdict = HandshakeValidator.Validate(_world.Version, h, full, banned);
        if (verdict is { } v) { RejectClient(c, v.Code, v.Detail); return; }

        if (old != null) RemoveClient(old, LeaveReason.Replaced, new Goodbye { Reason = LeaveReason.Replaced, Text = "Connected again elsewhere." });

        var slot = Players.AllocateSlot(h.SteamId, _now);
        if (slot == null) { RejectClient(c, RejectCode.Full, "The shift is full."); return; }
        var info = new PlayerInfo
        {
            Peer = c.Peer, Slot = slot.Value, SteamId = h.SteamId, Name = Players.UniqueName(h.Name),
            Phase = PlayerPhase.Queued, Flags = (h.Flags & HelloFlags.Bot) != 0 ? PlayerFlags.Bot : PlayerFlags.None,
            StationIndex = slot.Value,
        };
        Players.Add(info);
        c.Info = info;
        c.Step = Step.AwaitCapture;
        c.PendingReason = SnapshotReason.Join;
        c.QueuedSince = _now;
        c.NextGateCheck = _now; // evaluated this frame

        _net.Send(c.Peer, new Welcome
        {
            SessionId = _cfg.SessionId, YourSlot = info.Slot, MaxPlayers = (byte)Players.MaxPlayers, TickRate = (ushort)_cfg.TickRate,
            ManifestHash = _world.Version.ManifestHash, Phase = Phase, Difficulty = _world.Difficulty,
            Players = Players.Players.ToList(), HostTick = _clock.TickAt(_now),
        });
        foreach (var o in _clients.Values)
            if (o != c && o.Info != null) _net.Send(o.Peer, new PlayerAdded { Player = info });
        PlayerAdded?.Invoke(info);
    }

    /// <summary>Spec 02 §3.3, evaluated at Welcome and 2 Hz while queued. Full is checked at Hello (Reject, not queued).</summary>
    QueueReason? Gate()
    {
        if (Phase is SessionPhase.HostLoading or SessionPhase.HostArming) return QueueReason.HostLoading;
        var g = _world.JoinGate();
        if (g is { } wg && wg != QueueReason.None) return wg;
        foreach (var gate in _gates)
        {
            var r = gate();
            if (r is { } mr && mr != QueueReason.None) return mr;
        }
        return null;
    }

    int StreamingCount => _clients.Values.Count(c => c.Step == Step.Streaming && c.Info?.Phase == PlayerPhase.Downloading);

    void RunCaptures()
    {
        if (Phase is SessionPhase.Ending or SessionPhase.Closing or SessionPhase.Idle) return;
        var ready = new List<Client>();
        int streaming = StreamingCount;
        foreach (var c in _clients.Values.Where(c => c.Step == Step.AwaitCapture).OrderBy(c => c.QueuedSince))
        {
            if (_now < c.NextGateCheck) continue;
            c.NextGateCheck = _now + _cfg.QueueRecheckInterval;
            var reason = Gate();
            if (reason == null && streaming + ready.Count >= _cfg.MaxConcurrentSnapshots) reason = QueueReason.OtherJoin;
            if (reason == null) { ready.Add(c); continue; }
            if (reason == QueueReason.FinalScene && _world.IsShiftOver) { RejectClient(c, RejectCode.ShiftOver, "The shift is over."); continue; }
            if (_now - c.QueuedSince > _cfg.QueueTimeout) { RejectClient(c, RejectCode.QueueTimeout, $"Waited {(int)(_now - c.QueuedSince)} s to join."); continue; }
            if (reason != c.LastQueue)
            {
                c.LastQueue = reason.Value;
                _net.Send(c.Peer, new Queued
                {
                    Reason = reason.Value, Position = (byte)(1 + _clients.Values.Count(o => o.Step == Step.AwaitCapture && o.QueuedSince < c.QueuedSince)),
                    WaitedSec = (ushort)Math.Min(ushort.MaxValue, _now - c.QueuedSince),
                });
            }
        }
        if (ready.Count == 0) return;

        // One capture per frame, shared by everyone ready in this frame.
        // Modules flush their own pending batches now: a change made earlier this frame then goes to gathered peers
        // only and is inside the snapshot for the new ones; anything after the gather point reaches both.
        OnBeforeSnapshot?.Invoke();
        var reasonForWorld = ready.Any(c => c.PendingReason == SnapshotReason.HostReload) ? SnapshotReason.HostReload
            : ready.Any(c => c.PendingReason == SnapshotReason.Resync) ? SnapshotReason.Resync : SnapshotReason.Join;
        HostSnapshot snap;
        byte[] extras;
        try
        {
            snap = _world.CaptureSnapshot(reasonForWorld);
            extras = JoinExtras.Build(_providers);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Warning?.Invoke("snapshot capture failed: " + ex.Message);
            foreach (var c in ready) RejectClient(c, RejectCode.SnapshotFailed, "The host could not capture the shift.");
            return;
        }
        var cap = new Capture { SnapshotId = ++_snapshotCounter, Snap = snap, Extras = extras };
        foreach (var c in ready)
        {
            c.SaveBlobId = c.Blobs.ReserveId();
            c.ExtrasBlobId = c.Blobs.ReserveId();
            c.SnapshotId = cap.SnapshotId;
            if (c.PendingReason != SnapshotReason.Join) _net.Send(c.Peer, new ResyncMsg { IsRequest = false });
            _net.Send(c.Peer, new SnapshotInfo
            {
                SnapshotId = cap.SnapshotId, Reason = c.PendingReason, SaveUtcTicks = snap.SaveUtcTicks, SaveVersion = snap.SaveVersion,
                SaveBlobId = c.SaveBlobId, ExtrasBlobId = c.ExtrasBlobId,
            });
            c.Gathered = true; // world changes from now on go to this client too
            c.Step = Step.Streaming;
            c.LastQueue = QueueReason.None;
            SetPlayerPhase(c, PlayerPhase.Downloading);
            cap.Targets.Add(c);
        }
        _captures.Add(cap);
    }

    void CompleteCaptures()
    {
        for (int i = _captures.Count - 1; i >= 0; i--)
        {
            var cap = _captures[i];
            if (cap.Prepared == null)
            {
                if (!cap.Snap.Save.IsCompleted) continue;
                if (cap.Snap.Save.IsFaulted || cap.Snap.Save.IsCanceled)
                {
                    _captures.RemoveAt(i);
                    Warning?.Invoke("snapshot write failed: " + cap.Snap.Save.Exception?.GetBaseException().Message);
                    foreach (var c in cap.Targets.Where(t => IsCurrent(t, cap)).ToList())
                        RejectClient(c, RejectCode.SnapshotFailed, "The host could not write the shift.");
                    continue;
                }
                var save = cap.Snap.Save.Result;
                var extras = cap.Extras;
                var sc = _cfg.SaveCodec;
                var ec = _cfg.ExtrasCodec;
                (PreparedBlob, PreparedBlob) Prep() => (PreparedBlob.Prepare(BlobKind.WorldSave, cap.SnapshotId, save, sc),
                                                        PreparedBlob.Prepare(BlobKind.JoinExtras, cap.SnapshotId, extras, ec));
                cap.Prepared = _cfg.PrepareBlobsOnWorker ? Task.Run(Prep) : Task.FromResult(Prep());
            }
            if (!cap.Prepared.IsCompleted) continue;
            _captures.RemoveAt(i);
            if (!cap.Prepared.IsCompletedSuccessfully)
            {
                foreach (var c in cap.Targets.Where(t => IsCurrent(t, cap)).ToList())
                    RejectClient(c, RejectCode.SnapshotFailed, "The host could not pack the shift.");
                continue;
            }
            var (sb, eb) = cap.Prepared.Result;
            foreach (var c in cap.Targets.Where(t => IsCurrent(t, cap)))
            {
                c.Blobs.Enqueue(sb, c.SaveBlobId);
                c.Blobs.Enqueue(eb, c.ExtrasBlobId);
            }
        }
    }

    bool IsCurrent(Client c, Capture cap) => _clients.ContainsKey(c.Peer) && _clients[c.Peer] == c && c.SnapshotId == cap.SnapshotId && c.Step == Step.Streaming;

    void PumpBlobs(Client c)
    {
        if (c.Blobs.OpenCount > 0)
        {
            var link = _net.Link(c.Peer);
            if (link != null)
                c.Blobs.Pump(_now, _net.PendingReliableBytes(c.Peer), link.UnackedBytes, (id, payload) => _net.Send(c.Peer, id, payload));
        }
        foreach (var (id, status) in c.Blobs.TakeFinished())
        {
            if (status == BlobStatus.Ok) continue;
            if (id == c.SaveBlobId || id == c.ExtrasBlobId)
            {
                Warning?.Invoke($"{c.Info}: snapshot blob {id} failed ({status})");
                RemoveClient(c, LeaveReason.LoadFailed, new Reject { Code = RejectCode.SnapshotFailed, Detail = $"Transfer failed ({status})." });
                return;
            }
        }
    }

    void OnLoadProgress(Client c, LoadProgress m)
    {
        if (c.Step != Step.Streaming) return;
        if (m.Phase is not (PlayerPhase.Downloading or PlayerPhase.Loading or PlayerPhase.Applying)) return;
        SetPlayerPhase(c, m.Phase);
    }

    void OnClientReady(Client c, ClientReady m)
    {
        if (c.Step != Step.Streaming || m.SnapshotId != c.SnapshotId)
        {
            Warning?.Invoke($"stale ClientReady from {c.Info?.Name}: step {c.Step}, snapshot {m.SnapshotId} vs {c.SnapshotId}");
            return;
        }
        uint mine = _world.Version.ManifestHash;
        if (mine != 0 && m.ManifestHash != 0 && m.ManifestHash != mine)
        {
            RejectClient(c, RejectCode.Manifest, $"Scene manifest {m.ManifestHash:X8}, host has {mine:X8}.");
            return;
        }
        c.Step = Step.Playing;
        bool first = !c.WasPlaying;
        c.WasPlaying = true;
        SetPlayerPhase(c, PlayerPhase.Playing);
        if (first) PlayerJoined?.Invoke(c.Info!);
    }

    void Timers(Client c)
    {
        if (c.Info == null)
        {
            if (_now - c.ConnectedAt > _cfg.HelloTimeout) RejectClient(c, RejectCode.Protocol, "No Hello received.");
            return;
        }
        double silence = c.Live.Silence(_now);
        bool loading = c.Info.Phase is PlayerPhase.Downloading or PlayerPhase.Loading or PlayerPhase.Applying;
        double limit = loading ? _cfg.LoadingTimeoutAfter : _cfg.TimeoutAfter;
        if (silence > limit)
        {
            RemoveClient(c, LeaveReason.TimedOut, new Goodbye { Reason = LeaveReason.TimedOut, Text = "No answer from you." });
            return;
        }
        bool nr = silence > _cfg.NotRespondingAfter;
        if (nr != c.Info.Flags.HasFlag(PlayerFlags.NotResponding))
        {
            c.Info.Flags = nr ? c.Info.Flags | PlayerFlags.NotResponding : c.Info.Flags & ~PlayerFlags.NotResponding;
            BroadcastPlayerUpdate(c.Info);
        }
        // A blob in flight that nobody acknowledges: the joiner is gone in all but name.
        if (c.Step == Step.Streaming && c.Blobs.OpenCount > 0)
        {
            var link = _net.Link(c.Peer);
            if (link != null && link.UnackedBytes > 0 && _now - link.LastAckProgressAt > _cfg.DownloadStallTimeout)
                RemoveClient(c, LeaveReason.TimedOut, new Goodbye { Reason = LeaveReason.TimedOut, Text = "The shift transfer stalled." });
        }
    }

    void UpdateRtt(Client c)
    {
        if (c.Info == null || c.Live.RttMs < 0) return;
        ushort rtt = (ushort)Math.Min(ushort.MaxValue, Math.Round(c.Live.RttMs));
        if (rtt == c.Info.RttMs) return;
        bool first = c.Info.RttMs == 0;
        c.Info.RttMs = rtt;
        if (first || _now - c.LastRttBroadcast >= _cfg.RttBroadcastInterval)
        {
            c.LastRttBroadcast = _now;
            BroadcastPlayerUpdate(c.Info);
        }
    }

    // ================================================================ removal

    void RejectClient(Client c, RejectCode code, string detail)
    {
        Rejected?.Invoke(c.Peer, code, detail);
        RemoveClient(c, LeaveReason.Rejected, new Reject { Code = code, Detail = detail });
    }

    void RemoveClient(Client c, LeaveReason reason, IWireMessage? final, bool transportGone = false)
    {
        if (!_clients.TryGetValue(c.Peer, out var cur) || cur != c) return;
        _clients.Remove(c.Peer);
        CancelBlobs(c, notify: false);
        c.In.Clear();
        if (!transportGone)
        {
            if (final != null)
            {
                _w.Reset();
                final.Write(_w);
                _net.Disconnect(c.Peer, _now, final.Id, _w.Span);
            }
            else _net.Disconnect(c.Peer, _now);
        }
        if (c.Info == null) return;
        var info = c.Info;
        Players.Remove(info.Slot);
        info.Phase = PlayerPhase.Leaving;
        // Only players who were in the game keep their slot for a rejoin (spec 02 §3.5).
        if (c.WasPlaying && reason is LeaveReason.Left or LeaveReason.TimedOut)
            Players.Reserve(info.SteamId, info.Slot, _now, _cfg.ReservationSeconds);
        foreach (var o in _clients.Values)
            if (o.Info != null) _net.Send(o.Peer, new PlayerRemoved { Slot = info.Slot, Reason = reason });
        PlayerRemoved?.Invoke(info, reason);
        if (c.WasPlaying) PlayerLeft?.Invoke(info, reason);
    }

    void CancelBlobs(Client c, bool notify = true)
    {
        if (notify)
            foreach (var id in new[] { c.SaveBlobId, c.ExtrasBlobId })
                if (id != 0 && c.Blobs.IsOpen(id)) c.Blobs.Cancel(id, 0, (mid, pl) => _net.Send(c.Peer, mid, pl));
        c.Blobs.Clear();
        c.SaveBlobId = c.ExtrasBlobId = 0;
    }

    // ================================================================ registry broadcast

    void SetPlayerPhase(Client c, PlayerPhase phase)
    {
        if (c.Info == null || c.Info.Phase == phase) return;
        c.Info.Phase = phase;
        BroadcastPlayerUpdate(c.Info);
    }

    void BroadcastPlayerUpdate(PlayerInfo p)
    {
        var m = new PlayerUpdate { Slot = p.Slot, Phase = p.Phase, Flags = p.Flags, RttMs = p.RttMs, StationIndex = p.StationIndex };
        foreach (var o in _clients.Values)
            if (o.Info != null) _net.Send(o.Peer, m);
        PlayerUpdated?.Invoke(p);
    }

    /// <summary>Changes a player's station (stations module) and tells everyone.</summary>
    public void SetStation(byte slot, byte station)
    {
        var p = Players.BySlot(slot);
        if (p == null || p.StationIndex == station) return;
        p.StationIndex = station;
        BroadcastPlayerUpdate(p);
    }

    void BroadcastSessionStateIfChanged()
    {
        var key = (Phase, Players.Count);
        if (key == _lastStateSent) return;
        _lastStateSent = key;
        var m = new SessionStateMsg { Phase = Phase, Gate = Gate() ?? QueueReason.None, MaxPlayers = (byte)Players.MaxPlayers, Count = (byte)Players.Count };
        foreach (var o in _clients.Values)
            if (o.Info != null) _net.Send(o.Peer, m);
    }

    void SetPhase(SessionPhase p)
    {
        if (Phase == p) return;
        Phase = p;
        PhaseChanged?.Invoke(p);
    }

    // ================================================================ diagnostics

    /// <summary>Messages a client sent before it was allowed to (handshake or Playing), per slot.</summary>
    public long RejectedNonJoinCount(byte slot) => _clients.Values.FirstOrDefault(c => c.Info?.Slot == slot)?.RejectedNonJoin ?? 0;
    public PeerLink? LinkOf(byte slot) => _clients.Values.FirstOrDefault(c => c.Info?.Slot == slot) is { } c ? _net.Link(c.Peer) : null;
    public int ConnectedPeers => _clients.Count;
    public float BlobProgress(byte slot)
    {
        var c = _clients.Values.FirstOrDefault(x => x.Info?.Slot == slot);
        if (c == null || c.SaveBlobId == 0) return 0;
        return c.Blobs.Progress(c.SaveBlobId);
    }
}
