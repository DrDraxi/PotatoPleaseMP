namespace PotatoPleaseMP.Core.Bodies;

/// <summary>A tank: potatoes in a vacuum, bucket or barrow as counts (spec 07 §7). Invariant: 0 ≤ peeled ≤ count.</summary>
public sealed class Tank
{
    public ContainerKind Kind;
    public ushort Holder;
    public byte OwnerSlot;
    public int Count;
    public int Peeled;
    public int Raw => Count - Peeled;
    /// <summary>Newest props (variant, scale) for remote tool visuals, newest last, ≤ 8.</summary>
    public readonly List<ContainerProp> Props = new();
    /// <summary>Changed since the last <see cref="ContainerLedger.TakeDirty"/>.</summary>
    public bool Dirty;
}

/// <summary>What the host decided about one consumed id.</summary>
public enum ConsumeVerdict : byte { Accepted, NotOwned, TooFar, Unknown }

/// <summary>Host's view of a body named in a <see cref="BodyConsume"/>: who owns it, how far from the eye, peeled.</summary>
public readonly record struct ConsumeCandidate(bool Known, AuthRecord? Auth, float EyeDistance, bool Peeled, byte Variant = 0, float Scale = 1f);

/// <summary>
/// Host container truth (spec 07 §7): tanks per holder (per-player vacuum/bucket keyed by slot, the barrow by its
/// static id), consume and spill validation, and the per-player temp→net id map that lets a client keep using a
/// temp id after <see cref="SpawnAdopt"/>.
/// </summary>
public sealed class ContainerLedger
{
    readonly Dictionary<(ContainerKind, ushort), Tank> _tanks = new();
    readonly Dictionary<(byte Slot, ushort Temp), NetId> _tempMap = new();

    /// <summary>A loose <c>HostSim</c> body may be consumed by a client within this distance of its eye (4 m).</summary>
    public float ConsumeRange { get; set; } = 4f;

    public IEnumerable<Tank> Tanks => _tanks.Values;

    public Tank Get(ContainerKind kind, ushort holder, byte ownerSlot = BodyCodec.HostOwner)
    {
        if (!_tanks.TryGetValue((kind, holder), out var t))
            _tanks[(kind, holder)] = t = new Tank { Kind = kind, Holder = holder, OwnerSlot = ownerSlot };
        return t;
    }

    public Tank? Find(ContainerKind kind, ushort holder) => _tanks.TryGetValue((kind, holder), out var t) ? t : null;

    /// <summary>Adds one potato (host intake, accepted consume).</summary>
    public void Add(Tank t, bool peeled, byte variant = 0, float scale = 1f)
    {
        t.Count++;
        if (peeled) t.Peeled++;
        t.Props.Add(new ContainerProp(variant, scale));
        if (t.Props.Count > ContainerCount.MaxProps) t.Props.RemoveAt(0);
        t.Dirty = true;
    }

    /// <summary>
    /// Takes one potato out (spill/pour). Prefers what was asked for (peeled or raw) and falls back to the other kind;
    /// false when empty. <paramref name="tookPeeled"/> says which came out.
    /// </summary>
    public bool TryTake(Tank t, bool wantPeeled, out bool tookPeeled)
    {
        tookPeeled = false;
        if (t.Count <= 0) return false;
        tookPeeled = wantPeeled ? t.Peeled > 0 : t.Raw <= 0;
        t.Count--;
        if (tookPeeled) t.Peeled--;
        if (t.Props.Count > 0) t.Props.RemoveAt(t.Props.Count - 1);
        t.Dirty = true;
        return true;
    }

    public void Set(Tank t, int count, int peeled)
    {
        t.Count = Math.Max(0, count);
        t.Peeled = Math.Clamp(peeled, 0, t.Count);
        t.Dirty = true;
    }

    /// <summary>
    /// Validates a predicted swallow/collect (spec 07 §7 vacuum/bucket): each id must be owned by
    /// <paramref name="slot"/>, or be a <c>HostSim</c> body within <see cref="ConsumeRange"/> of its eye. Accepted ids
    /// increment the tank; the caller despawns them (<c>Swallowed</c>/<c>Bucketed</c>). Ids may be temp ids.
    /// </summary>
    public List<(NetId Id, ConsumeVerdict Verdict)> Consume(byte slot, in BodyConsume msg, Func<NetId, ConsumeCandidate> lookup)
    {
        var kind = msg.Kind == ConsumeKind.Vacuum ? ContainerKind.Vacuum : ContainerKind.Bucket;
        var tank = Get(kind, msg.Holder, slot);
        var result = new List<(NetId, ConsumeVerdict)>();
        var seen = new HashSet<ushort>();
        foreach (var raw in msg.Ids ?? new List<NetId>())
        {
            var id = Resolve(slot, raw);
            if (!seen.Add(id.Value)) continue;
            var c = lookup(id);
            ConsumeVerdict v;
            if (!c.Known || c.Auth == null) v = ConsumeVerdict.Unknown;
            else if (c.Auth.IsRemoteOwned) v = c.Auth.Owner == slot ? ConsumeVerdict.Accepted : ConsumeVerdict.NotOwned;
            else if (c.Auth.Mode != BodyMode.HostSim) v = ConsumeVerdict.NotOwned;
            else v = c.EyeDistance <= ConsumeRange ? ConsumeVerdict.Accepted : ConsumeVerdict.TooFar;
            if (v == ConsumeVerdict.Accepted) Add(tank, c.Peeled, c.Variant, c.Scale);
            result.Add((id, v));
        }
        return result;
    }

    /// <summary>
    /// A predicted spawn from a client's container. Checks the holder (the client's own vacuum/bucket, or the barrow
    /// when <paramref name="barrowEngaged"/>), takes one from the tank, allocates a net id and maps the temp id. The
    /// returned <see cref="SpawnAdopt"/> goes back to the owner; NetId 0 = rejected.
    /// </summary>
    public SpawnAdopt AdoptSpawn(byte slot, in ClientSpawn msg, HostIdAllocator ids, double now, out bool tookPeeled,
        bool barrowEngaged = false)
    {
        tookPeeled = false;
        var reject = new SpawnAdopt { TempId = msg.TempId, NetId = NetId.None };
        if (!msg.TempId.IsTemp || msg.TempId.TempSlot != slot) return reject with { Reason = AdoptReason.BadHolder };
        if (_tempMap.ContainsKey((slot, msg.TempId.Value))) return reject with { Reason = AdoptReason.Duplicate };
        Tank? tank = msg.Source switch
        {
            ClientSpawnSource.BucketPour => msg.Holder == slot ? Get(ContainerKind.Bucket, slot, slot) : null,
            ClientSpawnSource.VacuumSpill or ClientSpawnSource.VacuumDelivery => msg.Holder == slot ? Get(ContainerKind.Vacuum, slot, slot) : null,
            ClientSpawnSource.BarrowPour => barrowEngaged ? Find(ContainerKind.Barrow, msg.Holder) : null,
            _ => null,
        };
        if (tank == null) return reject with { Reason = AdoptReason.BadHolder };
        if (tank.Count <= 0) return reject with { Reason = AdoptReason.Empty };
        var id = ids.Allocate(now);
        if (!id.IsValid) return reject with { Reason = AdoptReason.NoIds };
        TryTake(tank, (msg.Flags & ClientSpawnFlags.PeeledOnSpill) != 0, out tookPeeled);
        _tempMap[(slot, msg.TempId.Value)] = id;
        return new SpawnAdopt { TempId = msg.TempId, NetId = id, Reason = AdoptReason.Ok };
    }

    /// <summary>Maps a client-sent id: temp ids of that player to their adopted net id; anything else unchanged.</summary>
    public NetId Resolve(byte slot, NetId id)
        => id.IsTemp && _tempMap.TryGetValue((slot, id.Value), out var net) ? net : id;

    public void MapTemp(byte slot, NetId temp, NetId net) => _tempMap[(slot, temp.Value)] = net;

    /// <summary>Forgets a net id's temp alias once the body despawns.</summary>
    public void Unmap(NetId net)
    {
        List<(byte, ushort)>? del = null;
        foreach (var kv in _tempMap) if (kv.Value == net) (del ??= new()).Add(kv.Key);
        if (del != null) foreach (var k in del) _tempMap.Remove(k);
    }

    /// <summary>
    /// A player left: drops their temp map and returns their tanks (the host spills them at the last position to keep
    /// conservation, spec 07 §11). The tanks are removed from the ledger.
    /// </summary>
    public List<Tank> PlayerLeft(byte slot)
    {
        foreach (var k in _tempMap.Keys.Where(k => k.Slot == slot).ToList()) _tempMap.Remove(k);
        var tanks = _tanks.Values.Where(t => t.OwnerSlot == slot && t.Kind != ContainerKind.Barrow).ToList();
        foreach (var t in tanks) _tanks.Remove((t.Kind, t.Holder));
        return tanks;
    }

    public ContainerCount ToMessage(Tank t) => new()
    {
        Holder = t.Holder, Kind = t.Kind, OwnerSlot = t.OwnerSlot, Count = (ushort)Math.Min(t.Count, ushort.MaxValue),
        Peeled = (ushort)Math.Min(t.Peeled, ushort.MaxValue), Props = new List<ContainerProp>(t.Props),
    };

    /// <summary>Changed tanks as <see cref="ContainerCount"/> messages (caller rate-limits to ≤ 10 Hz per holder).</summary>
    public List<ContainerCount> TakeDirty()
    {
        var l = new List<ContainerCount>();
        foreach (var t in _tanks.Values)
            if (t.Dirty) { t.Dirty = false; l.Add(ToMessage(t)); }
        return l;
    }

    /// <summary>Sum over all tanks (conservation checks).</summary>
    public int TotalCount => _tanks.Values.Sum(t => t.Count);
}
