using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Bodies;

/// <summary>One encoded body message for one client (or the host). The envelope adds the host tick.</summary>
public readonly record struct BodyOut(byte Slot, MsgId Id, byte[] Payload, bool Reliable)
{
    /// <summary>Bytes on the wire inside an envelope: id + varint length + payload.</summary>
    public int WireBytes => DatagramBuilder.Cost(Payload.Length);
}

/// <summary>
/// Headless host side of body sync (spec 07 §4-§7), usable by the plugin (feed it poses from Unity every net tick) and
/// by bots/tests (feed it simulated poses). Owns the id allocator, authority table, rest detector, scheduler and
/// container ledger; <see cref="Tick"/> returns every message to send this tick, per client: reliable
/// spawns/despawns/rests/auth/adopts/counts first, then one budgeted <c>BodyState</c>.
/// </summary>
public sealed class HostBodySync
{
    sealed class Body
    {
        public NetId Id;
        public Vec3 Pos, Vel, PrevPos;
        public Quat Rot = Quat.Identity;
        public bool Basin, Barrow, Held, Sleeping, Teleported, HasPrev;
        public SpawnEntry Spawn;
    }

    sealed class Client
    {
        public byte Slot;
        public ReceiverView View;
        public readonly List<SpawnEntry> JoinSpawns = new();
        public readonly List<AuthEntry> JoinAuth = new();
        public readonly List<ContainerCount> JoinCounts = new();
        public readonly List<SpawnAdopt> Adopts = new();
        /// <summary>Denials go to the claimant only (grants and returns go to all).</summary>
        public readonly List<AuthEntry> Denials = new();
        public readonly List<SpawnEntry> Resend = new();
        public long BytesSent, ReliableBytes, StateBytes;
    }

    readonly SortedDictionary<ushort, Body> _bodies = new();
    readonly Dictionary<byte, Client> _clients = new();
    readonly List<SpawnEntry> _spawns = new();
    readonly List<DespawnEntry> _despawns = new();
    readonly List<RestEntry> _rests = new();
    readonly List<AuthEntry> _auths = new();
    readonly List<SchedBody> _sched = new();
    readonly List<BodyOut> _out = new();
    double _lastTickTime = double.NaN, _lastDigest = double.NegativeInfinity;

    public HostBodySync(SchedulerConfig? scheduler = null)
    {
        Scheduler = new PriorityScheduler(scheduler);
    }

    public HostIdAllocator Ids { get; } = new();
    /// <summary>Diagnostics: why claims were denied.</summary>
    public Dictionary<DenyReason, int> DenyCounts { get; } = new();
    public string LastDeny = "";
    public AuthorityTable Authority { get; } = new();
    public RestDetector Rest { get; } = new();
    public ContainerLedger Ledger { get; } = new();
    public PriorityScheduler Scheduler { get; }
    /// <summary>Seconds between <c>BodyAudit</c> digests (5 s); 0 disables.</summary>
    public double DigestInterval { get; set; } = 5;

    public int Count => _bodies.Count;
    public IEnumerable<NetId> LiveIds => _bodies.Values.Select(b => b.Id);
    public bool Exists(NetId id) => _bodies.ContainsKey(id.Value);
    public (Vec3 Pos, Quat Rot) PoseOf(NetId id) => _bodies.TryGetValue(id.Value, out var b) ? (b.Pos, b.Rot) : default;
    public IEnumerable<byte> Clients => _clients.Keys;

    /// <summary>Body bytes sent to a client so far (reliable + state, with message framing).</summary>
    public long BytesTo(byte slot) => _clients.TryGetValue(slot, out var c) ? c.BytesSent : 0;
    public long StateBytesTo(byte slot) => _clients.TryGetValue(slot, out var c) ? c.StateBytes : 0;

    // ------------------------------------------------------------------ host world

    /// <summary>Registers a new activation (host <c>PotatoInstance.OnEnable</c>) and queues its spawn to all clients.</summary>
    public NetId Spawn(Vec3 pos, Quat rot, double now, SpawnOrigin origin = SpawnOrigin.Activate, SpawnEntry? template = null,
        Vec3 vel = default, bool resting = false)
    {
        var id = Ids.Allocate(now);
        if (!id.IsValid) return id;
        AddBody(id, pos, rot, vel, now, origin, template, resting);
        return id;
    }

    /// <summary>Registers a body with a known id (static scene body, adopted spawn).</summary>
    public void SpawnWithId(NetId id, Vec3 pos, Quat rot, double now, SpawnOrigin origin, SpawnEntry? template = null,
        Vec3 vel = default, bool resting = false)
    {
        if (id.IsHost && !Ids.IsLive(id) && !Ids.TryReserve(id, now)) throw new InvalidOperationException($"{id} is not free.");
        AddBody(id, pos, rot, vel, now, origin, template, resting);
    }

    void AddBody(NetId id, Vec3 pos, Quat rot, Vec3 vel, double now, SpawnOrigin origin, SpawnEntry? template, bool resting)
    {
        var e = template ?? new SpawnEntry { TrueScale = 1f };
        e.Id = id; e.Origin = origin; e.Pos = pos; e.Rot = rot; e.Vel = vel;
        if (resting) e.Flags |= SpawnFlags.Resting;
        var b = new Body { Id = id, Pos = pos, Rot = rot, Vel = vel, Spawn = e };
        _bodies[id.Value] = b;
        var auth = Authority.Add(id);
        auth.Resting = resting;
        e.Auth = auth.AuthByte;
        b.Spawn = e;
        Rest.Add(id, pos, rot, now, resting);
        _spawns.Add(e);
    }

    /// <summary>Host despawn (pooled, sold, swallowed...): queued to all; the id enters quarantine.</summary>
    public bool Despawn(NetId id, DespawnReason reason, double now, byte arg = 0)
    {
        if (!_bodies.Remove(id.Value)) return false;
        Authority.Remove(id);
        Rest.Remove(id);
        Scheduler.Forget(id);
        Ledger.Unmap(id);
        Ids.Free(id, now);
        _despawns.Add(new DespawnEntry(id, reason, arg));
        return true;
    }

    /// <summary>
    /// The body's pose this tick. Ignored for client-owned bodies (their pose comes from the owner stream).
    /// <paramref name="teleport"/> sends the snap bit.
    /// </summary>
    public void SetPose(NetId id, Vec3 pos, Quat rot, Vec3? vel = null, bool sleeping = false, bool teleport = false)
    {
        if (!_bodies.TryGetValue(id.Value, out var b)) return;
        if (Authority.Get(id) is { IsRemoteOwned: true }) return;
        SetPoseInternal(b, pos, rot, vel, sleeping, teleport);
    }

    static void SetPoseInternal(Body b, Vec3 pos, Quat rot, Vec3? vel, bool sleeping, bool teleport)
    {
        b.Pos = pos; b.Rot = rot; b.Sleeping = sleeping;
        if (vel.HasValue) b.Vel = vel.Value;
        b.Teleported |= teleport;
    }

    /// <summary>Priority hints: basin resident, wheelbarrow, held/carried by anyone (incl. host systems).</summary>
    public void SetHints(NetId id, bool basin = false, bool barrow = false, bool held = false)
    {
        if (!_bodies.TryGetValue(id.Value, out var b)) return;
        b.Basin = basin; b.Barrow = barrow; b.Held = held;
    }

    // ------------------------------------------------------------------ clients

    /// <summary>A client finished loading: queue the join state (counts, every live body, auth of owned bodies).</summary>
    public void AddClient(byte slot, ReceiverView? view = null)
    {
        var c = new Client { Slot = slot, View = view ?? new ReceiverView(slot, default) };
        _clients[slot] = c;
        foreach (var t in Ledger.Tanks) c.JoinCounts.Add(Ledger.ToMessage(t));
        foreach (var b in _bodies.Values)
        {
            var e = b.Spawn;
            var a = Authority.Get(b.Id)!;
            e.Pos = b.Pos; e.Rot = b.Rot; e.Vel = b.Vel; e.Auth = a.AuthByte;
            e.Flags = Rest.IsResting(b.Id) ? e.Flags | SpawnFlags.Resting : e.Flags & ~SpawnFlags.Resting;
            c.JoinSpawns.Add(e);
            if (a.IsRemoteOwned) c.JoinAuth.Add(new AuthEntry(b.Id, a.AuthByte, AuthResult.Granted, a.ClaimSeq));
        }
    }

    /// <summary>A client left: its bodies return to the host (velocity × 0.5) and its tanks are returned for spilling.</summary>
    public List<Tank> RemoveClient(byte slot, double now)
    {
        _clients.Remove(slot);
        Scheduler.RemoveReceiver(slot);
        foreach (var ch in Authority.PlayerLeft(slot)) Returned(ch, 0.5f, now);
        return Ledger.PlayerLeft(slot);
    }

    public void SetReceiver(ReceiverView view)
    {
        if (_clients.TryGetValue(view.Slot, out var c)) c.View = view;
    }

    /// <summary>Host hands a body to a player on its own initiative (station grant, spec 14) and tells everyone.</summary>
    public AuthEntry GrantTo(NetId id, byte slot, ClaimReason reason, double now)
    {
        var ch = Authority.Grant(id, slot, reason, now);
        _auths.Add(ch.Entry);
        return ch.Entry;
    }

    /// <summary>Host takes a remote-owned body back (the owner placed it on a station, spec 14). False if it was not remote-owned.</summary>
    public bool TakeBack(NetId id, double now)
    {
        if (Authority.Revoke(id) is not { } ch) return false;
        _auths.Add(ch.Entry);
        Rest.Wake(id, now);
        return true;
    }

    void Returned(AuthChange ch, float velScale, double now)
    {
        _auths.Add(ch.Entry);
        if (_bodies.TryGetValue(ch.Entry.Id.Value, out var b))
        {
            b.Vel *= velScale;
            Rest.Wake(b.Id, now);
        }
    }

    // ------------------------------------------------------------------ client intake

    /// <summary>Handles one message from client <paramref name="slot"/>. Malformed bytes throw <see cref="WireFormatException"/>.</summary>
    public void Receive(byte slot, MsgId id, ReadOnlySpan<byte> payload, double now)
    {
        if (!_clients.TryGetValue(slot, out var client)) return;
        switch (id)
        {
            case MsgId.BodyClaim:
                foreach (var e in BodyMessages.Decode<BodyClaim>(payload).Entries)
                {
                    var bid = Ledger.Resolve(slot, e.Id);
                    float dist = _bodies.TryGetValue(bid.Value, out var b) ? Vec3.Distance(b.Pos, client.View.Eye) : float.MaxValue;
                    var ch = Authority.TryClaim(bid, slot, e.Reason, e.ClaimSeq, dist, now);
                    if (ch.Granted) _auths.Add(ch.Entry);
                    else
                    {
                        client.Denials.Add(ch.Entry with { Id = e.Id }); // the claimant's own (possibly temp) id
                        DenyCounts[ch.Deny] = DenyCounts.GetValueOrDefault(ch.Deny) + 1;
                        LastDeny = $"{ch.Deny} id {bid.Value} dist {dist:0.0}";
                    }
                }
                break;
            case MsgId.BodyOwnedState:
                foreach (var e in BodyMessages.Decode<BodyOwnedState>(payload).Entries)
                {
                    var bid = Ledger.Resolve(slot, e.Id);
                    if (!Authority.NoteOwnerSample(bid, slot, now) || !_bodies.TryGetValue(bid.Value, out var b)) continue;
                    SetPoseInternal(b, e.Pos, e.Rot, e.Vel, false, false);
                }
                break;
            case MsgId.BodyRelease:
            {
                var m = BodyMessages.Decode<BodyRelease>(payload);
                var bid = Ledger.Resolve(slot, m.Body);
                var ch = Authority.Release(bid, slot);
                if (ch == null || !_bodies.TryGetValue(bid.Value, out var b)) break;
                SetPoseInternal(b, m.Pos, m.Rot, m.Vel, false, false);
                Rest.Wake(bid, now);
                _auths.Add(ch.Value.Entry);
                break;
            }
            case MsgId.ClientSpawn:
            {
                var m = BodyMessages.Decode<ClientSpawn>(payload);
                bool barrow = m.Source == ClientSpawnSource.BarrowPour && Authority.IsOwnedBy(new NetId(m.Holder), slot);
                var adopt = Ledger.AdoptSpawn(slot, m, Ids, now, out bool peeled, barrow);
                if (adopt.Accepted)
                {
                    var t = new SpawnEntry { Variant = m.Variant, TrueScale = m.TrueScale, FactoryDepth = m.FactoryDepth };
                    if ((m.Flags & ClientSpawnFlags.HasFactoryDepth) != 0) t.Flags |= SpawnFlags.Factory;
                    if (peeled) t.Flags |= SpawnFlags.PeelCounted;
                    AddBody(adopt.NetId, m.Pos, m.Rot, m.Vel, now, OriginOf(m.Source), t, false);
                    if ((m.Flags & ClientSpawnFlags.KeepOwned) != 0)
                    {
                        var g = Authority.Grant(adopt.NetId, slot, ClaimReason.Vacuum, now);
                        _auths.Add(g.Entry);
                        // The spawn already went out as host-owned: patch its auth byte.
                        int i = _spawns.FindLastIndex(s => s.Id == adopt.NetId);
                        if (i >= 0) { var s = _spawns[i]; s.Auth = g.Entry.Auth; _spawns[i] = s; }
                    }
                }
                client.Adopts.Add(adopt);
                break;
            }
            case MsgId.BodyConsume:
            {
                var m = BodyMessages.Decode<BodyConsume>(payload);
                var verdicts = Ledger.Consume(slot, m, bid =>
                {
                    if (!_bodies.TryGetValue(bid.Value, out var b)) return new ConsumeCandidate(false, null, 0, false);
                    return new ConsumeCandidate(true, Authority.Get(bid), Vec3.Distance(b.Pos, client.View.Eye),
                        (b.Spawn.Flags & SpawnFlags.PeelCounted) != 0, b.Spawn.Variant, b.Spawn.TrueScale);
                });
                var reason = m.Kind == ConsumeKind.Vacuum ? DespawnReason.Swallowed : DespawnReason.Bucketed;
                foreach (var (bid, v) in verdicts)
                    if (v == ConsumeVerdict.Accepted) Despawn(bid, reason, now, slot);
                // Rejected ids stay alive; the client re-learns them from the audit digest / its next BodyState.
                var tank = Ledger.Find(m.Kind == ConsumeKind.Vacuum ? ContainerKind.Vacuum : ContainerKind.Bucket, m.Holder);
                if (tank != null) tank.Dirty = true;
                break;
            }
            case MsgId.BodyAudit:
            {
                var m = BodyMessages.Decode<BodyAudit>(payload);
                if (m.Sub != AuditSub.Nack) break;
                foreach (var block in m.NackBlocks)
                {
                    int lo = block * BodyAudit.BlockSize, hi = lo + BodyAudit.BlockSize;
                    foreach (var b in _bodies.Values)
                        if (b.Id.Value >= lo && b.Id.Value < hi)
                        {
                            var e = b.Spawn;
                            e.Pos = b.Pos; e.Rot = b.Rot; e.Vel = b.Vel; e.Auth = Authority.Get(b.Id)!.AuthByte;
                            client.Resend.Add(e);
                        }
                }
                break;
            }
        }
    }

    static SpawnOrigin OriginOf(ClientSpawnSource s) => s switch
    {
        ClientSpawnSource.BucketPour => SpawnOrigin.BucketPour,
        ClientSpawnSource.BarrowPour => SpawnOrigin.BarrowPour,
        _ => SpawnOrigin.VacuumSpill,
    };

    // ------------------------------------------------------------------ tick

    /// <summary>
    /// One network tick (30 Hz): owner timeouts, rest detection, then per client the reliable batches and one
    /// budgeted <c>BodyState</c>. The returned list is reused by the next call.
    /// </summary>
    public IReadOnlyList<BodyOut> Tick(double now)
    {
        _out.Clear();
        double dt = double.IsNaN(_lastTickTime) ? 1.0 / Scheduler.Config.TickRate : Math.Max(1e-3, now - _lastTickTime);
        _lastTickTime = now;

        foreach (var ch in Authority.Timeout(now)) Returned(ch, 0.5f, now);

        _sched.Clear();
        foreach (var b in _bodies.Values)
        {
            var auth = Authority.Get(b.Id)!;
            if (auth.Mode == BodyMode.Contained) continue;
            var ru = Rest.Update(b.Id, b.Pos, b.Rot, now, b.Basin, b.Sleeping && !auth.IsRemoteOwned);
            bool snap = b.Teleported || ru.Snap;
            float speed = b.HasPrev ? Vec3.Distance(b.Pos, b.PrevPos) / (float)dt : b.Vel.Length;
            b.PrevPos = b.Pos; b.HasPrev = true; b.Teleported = false;
            if (ru.BecameRest && !auth.IsRemoteOwned)
            {
                auth.Resting = true;
                _rests.Add(new RestEntry(b.Id, b.Pos, b.Rot, RestFlags.None));
                Scheduler.Forget(b.Id);
                continue;
            }
            if (ru.Awake || auth.IsRemoteOwned) auth.Resting = false;
            if (!ru.Awake && !auth.IsRemoteOwned) continue;
            _sched.Add(new SchedBody
            {
                Id = b.Id, Pos = b.Pos, Rot = b.Rot, Speed = speed, Awake = true, Owner = auth.Owner,
                Held = b.Held || auth.IsRemoteOwned, Basin = b.Basin, Barrow = b.Barrow, Snap = snap,
            });
        }

        var spawnChunks = BodySpawn.EncodeChunks(_spawns);
        byte[]? despawnBytes = _despawns.Count > 0 ? BodyMessages.Encode(new BodyDespawn { Entries = _despawns }) : null;
        byte[]? restBytes = _rests.Count > 0 ? BodyMessages.Encode(new BodyRest { Entries = _rests }) : null;
        byte[]? authBytes = _auths.Count > 0 ? BodyMessages.Encode(new BodyAuth { Entries = _auths }) : null;
        var counts = Ledger.TakeDirty();
        byte[]? digestBytes = null;
        if (DigestInterval > 0 && now - _lastDigest >= DigestInterval)
        {
            _lastDigest = now;
            digestBytes = BodyMessages.Encode(new BodyAudit { Sub = AuditSub.Digest, Digest = BodyAudit.ComputeDigest(LiveIds) });
        }

        foreach (var c in _clients.Values)
        {
            int start = _out.Count;
            // Join state first, then this tick's events (a joiner gets both: events are idempotent on top).
            foreach (var cc in c.JoinCounts) Emit(c, MsgId.ContainerCount, BodyMessages.Encode(cc), true);
            foreach (var chunk in BodySpawn.EncodeChunks(c.JoinSpawns)) Emit(c, MsgId.BodySpawn, chunk, true);
            if (c.JoinAuth.Count > 0) Emit(c, MsgId.BodyAuth, BodyMessages.Encode(new BodyAuth { Entries = c.JoinAuth }), true);
            c.JoinCounts.Clear(); c.JoinSpawns.Clear(); c.JoinAuth.Clear();
            foreach (var chunk in BodySpawn.EncodeChunks(c.Resend)) Emit(c, MsgId.BodySpawn, chunk, true);
            c.Resend.Clear();

            foreach (var chunk in spawnChunks) Emit(c, MsgId.BodySpawn, chunk, true);
            if (despawnBytes != null) Emit(c, MsgId.BodyDespawn, despawnBytes, true);
            if (restBytes != null) Emit(c, MsgId.BodyRest, restBytes, true);
            if (authBytes != null) Emit(c, MsgId.BodyAuth, authBytes, true);
            if (c.Denials.Count > 0) { Emit(c, MsgId.BodyAuth, BodyMessages.Encode(new BodyAuth { Entries = c.Denials }), true); c.Denials.Clear(); }
            foreach (var a in c.Adopts) Emit(c, MsgId.SpawnAdopt, BodyMessages.Encode(a), true);
            c.Adopts.Clear();
            foreach (var cc in counts) Emit(c, MsgId.ContainerCount, BodyMessages.Encode(cc), true);
            if (digestBytes != null) Emit(c, MsgId.BodyAudit, digestBytes, false);

            int extra = 0;
            for (int i = start; i < _out.Count; i++) extra += _out[i].WireBytes;
            var plan = Scheduler.Plan(c.View, _sched, now, extra);
            if (!plan.IsEmpty)
            {
                var bytes = BodyMessages.Encode(new BodyState { Anchor = plan.Anchor, Entries = plan.Entries });
                Emit(c, MsgId.BodyState, bytes, false);
                c.StateBytes += DatagramBuilder.Cost(bytes.Length);
            }
        }

        _spawns.Clear(); _despawns.Clear(); _rests.Clear(); _auths.Clear();
        return _out;
    }

    void Emit(Client c, MsgId id, byte[] payload, bool reliable)
    {
        var o = new BodyOut(c.Slot, id, payload, reliable);
        _out.Add(o);
        c.BytesSent += o.WireBytes;
        if (reliable) c.ReliableBytes += o.WireBytes;
    }
}

/// <summary>
/// Headless client side: applies host body messages into per-body <see cref="InterpBuffer"/>s (spec 07 §6.3), tracks
/// authority and the player's own claims, adoptions and tanks, and answers audit digests. The plugin's
/// <c>ProxyDriver</c> calls <see cref="Sample"/> every FixedUpdate; bots and tests use it as the client world.
/// </summary>
public sealed class ClientBodyMirror
{
    public sealed class Proxy
    {
        public NetId Id;
        public SpawnEntry Spawn;
        public readonly InterpBuffer Buffer;
        public byte Auth;
        public RestFlags RestFlags;
        public Proxy(double tickRate) => Buffer = new InterpBuffer(tickRate);
        public byte Owner => (byte)(Auth & 0xF);
        public BodyMode Mode => (BodyMode)((Auth >> 4) & 0x7);
    }

    readonly Dictionary<ushort, Proxy> _proxies = new();
    readonly Dictionary<ushort, (byte Seq, ClaimReason Reason)> _pendingClaims = new();
    readonly Dictionary<ushort, NetId> _adopted = new();
    readonly Dictionary<(ContainerKind, ushort), ContainerCount> _tanks = new();
    readonly Dictionary<ushort, double> _unknownSince = new();
    readonly HashSet<byte> _pendingNack = new();
    readonly double _tickRate;
    byte _claimSeq;
    double _offsetMean = double.NaN, _jitter;

    public ClientBodyMirror(byte slot, double tickRate = 30)
    {
        Slot = slot;
        _tickRate = tickRate;
        TempIds = new TempIdAllocator(slot);
    }

    public byte Slot { get; }
    public TempIdAllocator TempIds { get; }
    /// <summary>Nominal per-body update interval used for the delay (15 Hz → 100 ms base).</summary>
    public double IntervalSeconds { get; set; } = 1.0 / 15;
    public double JitterSeconds => _jitter;
    public double DelaySeconds => InterpBuffer.ComputeDelaySeconds(IntervalSeconds, _jitter);

    public int Count => _proxies.Count;
    public IEnumerable<Proxy> Proxies => _proxies.Values;
    public Proxy? Get(NetId id) => _proxies.TryGetValue(id.Value, out var p) ? p : null;
    public bool OwnsLocally(NetId id) => Get(id) is { } p && p.Owner == Slot && p.Mode is BodyMode.ClientOwned or BodyMode.PeelHeld;
    public ContainerCount? Tank(ContainerKind kind, ushort holder) => _tanks.TryGetValue((kind, holder), out var t) ? t : null;
    public NetId AdoptedId(NetId temp) => _adopted.TryGetValue(temp.Value, out var n) ? n : NetId.None;

    /// <summary>Raised for every <c>BodyAuth</c> entry (plugin: drop a denied/revoked capture silently).</summary>
    public event Action<AuthEntry>? AuthChanged;
    /// <summary>Raised on <c>SpawnAdopt</c> (plugin: bind or despawn the prediction).</summary>
    public event Action<SpawnAdopt>? Adopted;
    /// <summary>Raised before a proxy is removed (plugin: release from captures, then despawn the object).</summary>
    public event Action<Proxy, DespawnEntry>? Despawned;
    public event Action<Proxy>? Spawned;

    public long UnknownStates { get; private set; }

    /// <summary>
    /// Host time estimate from the stamps of unreliable traffic (offset = arrival − stamp; the mean absorbs latency, so
    /// rendering at <c>EstimatedHostSeconds − DelaySeconds</c> lags by latency + delay).
    /// </summary>
    public double EstimatedHostSeconds(double localNow) => double.IsNaN(_offsetMean) ? localNow : localNow - _offsetMean;

    /// <summary>Applies one host message stamped with host tick <paramref name="hostTick"/>, arriving at <paramref name="localNow"/>.</summary>
    public void Receive(MsgId id, ReadOnlySpan<byte> payload, uint hostTick, double localNow)
    {
        switch (id)
        {
            case MsgId.BodySpawn:
                foreach (var e in BodyMessages.Decode<BodySpawn>(payload).Entries)
                {
                    if (!_proxies.TryGetValue(e.Id.Value, out var p)) _proxies[e.Id.Value] = p = new Proxy(_tickRate) { Id = e.Id };
                    p.Spawn = e; p.Auth = e.Auth;
                    p.Buffer.Reset(hostTick, e.Pos, e.Rot, (e.Flags & SpawnFlags.Resting) != 0);
                    _unknownSince.Remove(e.Id.Value);
                    Spawned?.Invoke(p);
                }
                break;
            case MsgId.BodyDespawn:
                foreach (var e in BodyMessages.Decode<BodyDespawn>(payload).Entries)
                {
                    if (!_proxies.Remove(e.Id.Value, out var p)) continue;
                    _pendingClaims.Remove(e.Id.Value);
                    Despawned?.Invoke(p, e);
                }
                break;
            case MsgId.BodyState:
            {
                var m = BodyMessages.Decode<BodyState>(payload);
                NoteStamp(hostTick, localNow);
                foreach (var e in m.Entries)
                {
                    if (!_proxies.TryGetValue(e.Id.Value, out var p))
                    {
                        UnknownStates++;
                        if (!_unknownSince.TryGetValue(e.Id.Value, out double since)) _unknownSince[e.Id.Value] = localNow;
                        else if (localNow - since >= 1.0 && e.Id.IsHost) _pendingNack.Add((byte)(e.Id.Value / BodyAudit.BlockSize));
                        continue;
                    }
                    if (OwnsLocally(e.Id)) continue;
                    p.Buffer.Push(hostTick, e.Pos, e.Rot, e.Snap);
                }
                break;
            }
            case MsgId.BodyRest:
                foreach (var e in BodyMessages.Decode<BodyRest>(payload).Entries)
                {
                    if (!_proxies.TryGetValue(e.Id.Value, out var p) || OwnsLocally(e.Id)) continue;
                    p.RestFlags = e.Flags;
                    p.Buffer.PushRest(hostTick, e.Pos, e.Rot);
                }
                break;
            case MsgId.BodyAuth:
                foreach (var e in BodyMessages.Decode<BodyAuth>(payload).Entries)
                {
                    if (_proxies.TryGetValue(e.Id.Value, out var p)) p.Auth = e.Auth;
                    // Grants and returns go to everyone (match on owner); denials only to the claimant.
                    if (_pendingClaims.TryGetValue(e.Id.Value, out var pc) && pc.Seq == e.ClaimSeq
                        && (e.Result != AuthResult.Granted || e.Owner == Slot)) _pendingClaims.Remove(e.Id.Value);
                    AuthChanged?.Invoke(e);
                }
                break;
            case MsgId.SpawnAdopt:
            {
                var m = BodyMessages.Decode<SpawnAdopt>(payload);
                if (m.Accepted) _adopted[m.TempId.Value] = m.NetId;
                TempIds.Release(m.TempId);
                Adopted?.Invoke(m);
                break;
            }
            case MsgId.ContainerCount:
            {
                var m = BodyMessages.Decode<ContainerCount>(payload);
                _tanks[(m.Kind, m.Holder)] = m;
                break;
            }
            case MsgId.BodyAudit:
            {
                var m = BodyMessages.Decode<BodyAudit>(payload);
                if (m.Sub != AuditSub.Digest || m.Digest == null) break;
                foreach (var b in BodyAudit.Diff(m.Digest, Digest())) _pendingNack.Add(b);
                break;
            }
        }
    }

    void NoteStamp(uint hostTick, double localNow)
    {
        double offset = localNow - hostTick / _tickRate;
        if (double.IsNaN(_offsetMean)) { _offsetMean = offset; return; }
        double dev = offset - _offsetMean;
        _offsetMean += 0.05 * dev;
        _jitter += 0.05 * (Math.Abs(dev) - _jitter);
    }

    /// <summary>Pose of a proxy at host time <paramref name="hostSeconds"/> minus <see cref="DelaySeconds"/>.</summary>
    public InterpSample Sample(NetId id, double hostSeconds)
        => _proxies.TryGetValue(id.Value, out var p) ? p.Buffer.SampleAtTime(hostSeconds, DelaySeconds) : default;

    /// <summary>Digest of the proxies this client knows (compare with the host's <c>BodyAudit</c>).</summary>
    public ushort[] Digest() => BodyAudit.ComputeDigest(_proxies.Values.Select(p => p.Id));

    /// <summary>Blocks to nack (digest mismatch, or a state for an unknown id repeating for 1 s); null when none.</summary>
    public BodyAudit? TakeNack()
    {
        if (_pendingNack.Count == 0) return null;
        var m = new BodyAudit { Sub = AuditSub.Nack, NackBlocks = _pendingNack.OrderBy(b => b).ToList() };
        _pendingNack.Clear();
        _unknownSince.Clear();
        return m;
    }

    /// <summary>Optimistic claim (spec 07 §4): the caller starts simulating at once and sends this.</summary>
    public ClaimEntry Claim(NetId id, ClaimReason reason)
    {
        _claimSeq++;
        _pendingClaims[id.Value] = (_claimSeq, reason);
        return new ClaimEntry(id, reason, _claimSeq);
    }

    public bool IsClaimPending(NetId id) => _pendingClaims.ContainsKey(id.Value);
}
