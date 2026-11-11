using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Presence;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Time;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Testing;

/// <summary>Bot lifecycle as the harness reports it (spec 18 §2).</summary>
public enum BotState : byte { Idle, Connecting, Handshake, Downloading, Loading, Ready, Ended }

public sealed class BotOptions
{
    public string Name { get; set; } = "Bot";
    /// <summary>Versions the bot claims in Hello; set to the host's for a normal join, change one field to test refusals.</summary>
    public VersionInfo Version { get; set; } = new(Envelope.ProtocolVersion, "0.0.0", "0.0.0", 0, 0);
    /// <summary>Simulated world load time after the snapshot arrived (spec 18 <c>load=</c>, default 2 s).</summary>
    public double LoadSeconds { get; set; } = 2;
    public Vec3 Spawn { get; set; }
    public double PoseRateHz { get; set; } = 20;
    public double IdlePoseRateHz { get; set; } = 5;
    public ClientConfig? Session { get; set; }
    /// <summary>Claimed SteamId (0: the bot's own peer id).</summary>
    public ulong SteamId { get; set; }
}

/// <summary>
/// A bot's world (spec 02 §3.4 "Bots"): accepts the snapshot into memory (never touches a disk or a scene), checks its
/// CRC, "loads" in <see cref="LoadSeconds"/>, reports the host's manifest so ClientReady matches.
/// </summary>
public sealed class BotClientWorld : IClientWorld
{
    double _loadStart = double.NaN;

    public BotClientWorld(VersionInfo version, double loadSeconds)
    {
        Version = version;
        LoadSeconds = loadSeconds;
    }

    public VersionInfo Version { get; set; }
    public double LoadSeconds { get; set; }
    /// <summary>Test hook: the next load fails with this text.</summary>
    public string? FailNextLoad { get; set; }

    public ClientSnapshot? Snapshot { get; private set; }
    public int Loads { get; private set; }
    public LeaveReason? EndedWith { get; private set; }
    bool _pendingStart;

    /// <summary>Test hook: report another scene manifest after loading than the one claimed in Hello.</summary>
    public uint? ManifestOverride { get; set; }
    public uint ManifestHash => ManifestOverride ?? Version.ManifestHash;

    public void BeginLoad(ClientSnapshot snapshot)
    {
        Snapshot = snapshot;
        Loads++;
        _loadStart = double.NaN;
        _pendingStart = true;
    }

    public ClientLoadStatus PollLoad(double now)
    {
        if (_pendingStart) { _loadStart = now; _pendingStart = false; }
        if (FailNextLoad is { } why) { FailNextLoad = null; _loadStart = double.NaN; return ClientLoadStatus.Failed(why); }
        double t = now - _loadStart;
        if (t >= LoadSeconds) { _loadStart = double.NaN; return ClientLoadStatus.Done; }
        return new ClientLoadStatus(LoadState.Loading, (byte)Math.Clamp(t / Math.Max(1e-6, LoadSeconds) * 100, 0, 99));
    }

    public void OnSessionEnded(LeaveReason reason)
    {
        EndedWith = reason;
        _loadStart = double.NaN;
        _pendingStart = false;
    }
}

/// <summary>
/// One simulated client (spec 18 §3): the real <see cref="ClientSession"/> over the real <see cref="P2PTransport"/> on a
/// <see cref="LoopbackLink"/>, with an in-memory world. It handshakes, receives the snapshot blobs, "loads", replays
/// world changes, then sends 20 Hz poses (5 Hz idle) from a <see cref="BotWalker"/> and records the poses relayed to
/// it. Modules whose client logic is all Unity get a counting stub (<see cref="MessagesSeen"/>). Time is passed in.
/// </summary>
public sealed class BotClient : IDisposable
{
    readonly BotOptions _opt;
    readonly StreamPacer _movingPacer, _idlePacer;
    readonly Dictionary<byte, PoseUpdate> _remotePoses = new();
    readonly Dictionary<MsgId, long> _seen = new();
    double _startedAt = double.NaN, _lastUpdate = double.NaN;
    byte _poseSeq;

    public BotClient(LoopbackHub hub, int index, PeerId host, BotOptions options)
    {
        _opt = options;
        Index = index;
        Id = BotIds.Make(index);
        HostId = host;
        Link = hub.CreateLink(Id);
        Transport = new P2PTransport(Link, () => hub.Now);
        World = new BotClientWorld(options.Version, options.LoadSeconds);
        var cfg = options.Session ?? new ClientConfig();
        // Bots have no Steam account: the address doubles as the id (universe 0, never a user) unless a test sets one.
        cfg.SteamId = options.SteamId != 0 ? options.SteamId : Id.Value;
        cfg.Name = options.Name;
        cfg.Flags |= HelloFlags.Bot;
        Session = new ClientSession(Transport, World, cfg);
        Session.MessageHandler = OnMessage;
        Session.WorldReady += () => JoinMs ??= (_lastUpdate - _startedAt) * 1000.0;
        Walker = new BotWalker(options.Spawn);
        _movingPacer = new StreamPacer(options.PoseRateHz);
        _idlePacer = new StreamPacer(options.IdlePoseRateHz);
    }

    public int Index { get; }
    public PeerId Id { get; }
    public PeerId HostId { get; }
    public string Name => _opt.Name;
    public LoopbackLink Link { get; }
    public P2PTransport Transport { get; }
    public ClientSession Session { get; }
    public BotClientWorld World { get; }
    public BotWalker Walker { get; }

    public BotState State => Session.State switch
    {
        ClientState.Idle => BotState.Idle,
        ClientState.Connecting => BotState.Connecting,
        ClientState.Handshaking or ClientState.Queued => BotState.Handshake,
        ClientState.Downloading or ClientState.Resyncing => BotState.Downloading,
        ClientState.Loading or ClientState.Applying => BotState.Loading,
        ClientState.Playing => BotState.Ready,
        _ => BotState.Ended,
    };

    /// <summary>Time from Connect to the world being ready, in ms (spec 18 <c>joinMs</c>).</summary>
    public double? JoinMs { get; private set; }
    public long SnapshotBytes => World.Snapshot?.Save.Length ?? 0;
    public uint SnapshotCrc => World.Snapshot?.SaveCrc ?? 0;
    public long PosesSent { get; private set; }
    public long PosesReceived { get; private set; }
    /// <summary>Newest pose per remote slot (host relays carry the sender's slot).</summary>
    public IReadOnlyDictionary<byte, PoseUpdate> RemotePoses => _remotePoses;
    public IReadOnlyDictionary<MsgId, long> MessagesSeen => _seen;
    /// <summary>Raw messages handled live or replayed, in order (world-change checks in tests).</summary>
    public List<(MsgId Id, byte[] Payload)> Received { get; } = new();
    public bool RecordMessages { get; set; } = true;

    public void Start(double now)
    {
        _startedAt = now;
        _lastUpdate = now;
        JoinMs = null;
        Session.Connect(HostId, now);
    }

    public void Update(double now)
    {
        double dt = double.IsNaN(_lastUpdate) ? 0 : now - _lastUpdate;
        _lastUpdate = now;
        Session.Update(now);
        if (State != BotState.Ready) return;
        Walker.Update(dt);
        uint tick = Session.Clock.EstHostTick(now);
        bool due = Walker.Moving ? _movingPacer.Due(tick) : _idlePacer.Due(tick);
        if (due) SendPose(now);
    }

    public void Walk(IEnumerable<Vec3> path, bool sprint = false) => Walker.SetPath(path, sprint);

    /// <summary>Graceful leave: Goodbye, then the transport's Bye.</summary>
    public void Leave(string text = "") => Session.Leave(LeaveReason.Left, text);

    void SendPose(double now)
    {
        var w = Walker;
        var flags = PoseFlags.Grounded | (w.Sprint && w.Moving ? PoseFlags.Sprint : 0);
        Session.Send(new PoseUpdate
        {
            Slot = Session.LocalSlot, Seq = ++_poseSeq, TimeMs = (ushort)((long)(now * 1000) & 0xFFFF), TeleportSeq = w.TeleportSeq,
            Pos = w.Position, YawDeg = w.YawDeg, PitchDeg = w.PitchDeg, Vel = w.Velocity, Crouch = w.Crouch ? 1 : 0,
            Flags = flags, Warmth = 1,
        });
        PosesSent++;
    }

    void OnMessage(MsgId id, ReadOnlySpan<byte> payload, Delivery delivery, ushort stamp)
    {
        _seen[id] = _seen.TryGetValue(id, out var n) ? n + 1 : 1;
        if (RecordMessages && id != MsgId.PoseUpdate) Received.Add((id, payload.ToArray()));
        switch (id)
        {
            case MsgId.PoseUpdate:
            {
                PoseUpdate p;
                try { p = SessionMessages.Decode<PoseUpdate>(payload); }
                catch (WireFormatException) { return; }
                if (_remotePoses.TryGetValue(p.Slot, out var prev) && !SeqMath.Newer8(p.Seq, prev.Seq)) return;
                _remotePoses[p.Slot] = p;
                PosesReceived++;
                break;
            }
            case MsgId.ForceTeleport:
            {
                // spec 04 §4.4: pos 3 x f32 | yaw f32 | pitch f32 | tflags u8
                var r = new NetReader(payload);
                try
                {
                    var pos = new Vec3(r.F32(), r.F32(), r.F32());
                    float yaw = r.F32();
                    Walker.PitchDeg = r.F32();
                    Walker.Teleport(pos, yaw);
                }
                catch (WireFormatException) { }
                break;
            }
        }
    }

    public void Dispose()
    {
        Session.Dispose();
        Transport.Dispose();
        Link.Dispose();
    }
}

/// <summary>
/// Owns a set of bots on one hub (spec 18 §2 <c>BotHub</c>): add, graceful leave, vanish, link cut/heal, per-bot
/// latency/jitter/loss, golden-angle spawn spread. The plugin harness pumps it from <c>MpBehaviour.Update</c>.
/// </summary>
public sealed class BotHub : IDisposable
{
    readonly List<BotClient> _bots = new();

    public BotHub(LoopbackHub hub, PeerId host, Func<BotOptions>? defaults = null)
    {
        Hub = hub;
        Host = host;
        Defaults = defaults ?? (() => new BotOptions());
    }

    public LoopbackHub Hub { get; }
    public PeerId Host { get; }
    public Func<BotOptions> Defaults { get; set; }
    public Vec3 SpawnCentre { get; set; }
    public IReadOnlyList<BotClient> Bots => _bots;

    public BotClient Add(string name, double now, Action<BotOptions>? configure = null)
    {
        int index = Enumerable.Range(1, BotIds.MaxBots).FirstOrDefault(i => _bots.All(b => b.Index != i));
        if (index == 0) throw new InvalidOperationException($"At most {BotIds.MaxBots} bots.");
        var opt = Defaults();
        opt.Name = name;
        opt.Spawn = BotWalker.SpreadAround(SpawnCentre, index - 1);
        configure?.Invoke(opt);
        var bot = new BotClient(Hub, index, Host, opt);
        _bots.Add(bot);
        bot.Start(now);
        return bot;
    }

    public BotClient? Find(string nameOrIndex)
        => _bots.FirstOrDefault(b => string.Equals(b.Name, nameOrIndex, StringComparison.OrdinalIgnoreCase)
                                     || b.Index.ToString() == nameOrIndex.TrimStart('@'));

    /// <summary>Graceful leave (Goodbye + Bye); the bot is removed once it ended.</summary>
    public void Leave(BotClient bot) => bot.Leave();

    /// <summary>The bot vanishes without a word (crash): the host notices by timeout.</summary>
    public void Remove(BotClient bot)
    {
        _bots.Remove(bot);
        bot.Link.Dispose();
    }

    /// <summary>Cuts the bot's link both ways (reliable too); the host drops it at its timeout.</summary>
    public void Cut(BotClient bot) => Hub.SetLinkDown(bot.Id, Host, true);
    public void Heal(BotClient bot) => Hub.SetLinkDown(bot.Id, Host, false);
    public void SetConditions(BotClient bot, LinkConditions? c) => Hub.SetConditions(bot.Id, c);

    public void Update(double now)
    {
        foreach (var b in _bots.ToList()) b.Update(now);
    }

    /// <summary>Drops bots that ended (after their Bye had a frame to leave).</summary>
    public int Prune()
    {
        var gone = _bots.Where(b => b.State == BotState.Ended).ToList();
        foreach (var b in gone) { _bots.Remove(b); b.Dispose(); }
        return gone.Count;
    }

    public void Dispose()
    {
        foreach (var b in _bots) b.Dispose();
        _bots.Clear();
    }
}
