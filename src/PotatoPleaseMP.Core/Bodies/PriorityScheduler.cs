using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Bodies;

/// <summary>One awake (or recently awake) body as the scheduler sees it this tick.</summary>
public struct SchedBody
{
    public NetId Id;
    public Vec3 Pos;
    public Quat Rot;
    /// <summary>m/s (linear).</summary>
    public float Speed;
    public bool Awake;
    /// <summary>Owning slot, <see cref="BodyCodec.HostOwner"/> for host-simulated bodies.</summary>
    public byte Owner;
    /// <summary>Held or carried by anyone (15 Hz floor).</summary>
    public bool Held;
    /// <summary>Basin resident (priority × 0.25).</summary>
    public bool Basin;
    /// <summary>The wheelbarrow (priority × 2).</summary>
    public bool Barrow;
    /// <summary>Host teleport / jump over 1 m this tick: send with the snap bit.</summary>
    public bool Snap;
}

/// <summary>Receiving player's view (from presence).</summary>
public readonly record struct ReceiverView(byte Slot, Vec3 Eye, float YawDeg = 0, float PitchDeg = 0);

/// <summary>What to send one receiver this tick.</summary>
public sealed class ScheduledBatch
{
    public Vec3 Anchor;
    public readonly List<StateEntry> Entries = new();
    /// <summary>Payload + message framing bytes.</summary>
    public int Bytes;
    public int Forced;
    public bool IsEmpty => Entries.Count == 0;
}

public sealed class SchedulerConfig
{
    /// <summary><c>Bodies.ClientBudget</c>: body bytes per second per client (16 KB/s).</summary>
    public int ClientBudgetBytesPerSec { get; set; } = 16 * 1024;
    public double TickRate { get; set; } = 30;
    public float InterestRadius { get; set; } = 30f;
    public float NearRadius { get; set; } = 3f;
    public double NearMinHz { get; set; } = 15;
    public double MaxUnsentSeconds { get; set; } = 0.4;
    /// <summary>Half angle of the 70° view cone.</summary>
    public float ViewConeHalfAngleDeg { get; set; } = 35f;
    /// <summary>Largest single <c>BodyState</c> (one datagram).</summary>
    public int MaxMessageBytes { get; set; } = MessageRegistry.MaxUnreliablePayload;
    /// <summary>How far floors may run the budget into debt (repaid by later ticks; 2 ticks by default).</summary>
    public int MaxOverdraftBytes { get; set; } = -1;

    /// <summary>
    /// Fraction of the budget kept back: the bucket refills at (1 − headroom) · budget, so credit carried over and the
    /// floors' overdraft never push a multi-second window over <see cref="ClientBudgetBytesPerSec"/>.
    /// </summary>
    public double Headroom { get; set; } = 0.06;

    /// <summary>Refill rate of the token bucket.</summary>
    public double RefillBytesPerSec => ClientBudgetBytesPerSec * (1 - Headroom);
    public double BudgetPerTick => RefillBytesPerSec / TickRate;
    public int Overdraft => MaxOverdraftBytes >= 0 ? MaxOverdraftBytes : (int)(2 * BudgetPerTick);
}

/// <summary>
/// Per-receiver priority and interest (spec 07 §6.2). For each awake body:
/// <c>w = (1 + 0.5·min(speed, 8)) · 1/(1 + (d/6)²) · (in view cone ? 2 : 1) · (basin ? 0.25 : 1) · (barrow ? 2 : 1)</c>,
/// <c>A += w·dt</c>; bodies farther than 30 m or owned by the receiver are skipped. Each tick sends by descending A
/// until the byte budget (a token bucket at <c>ClientBudget</c>, at most one datagram per tick) is used; sent → A = 0.
/// Floors go first: d &lt; 3 m or held → ≥ 15 Hz; any awake in-interest body unsent for 0.4 s. Floors may overdraw the
/// bucket by <see cref="SchedulerConfig.Overdraft"/>, so the long-run rate stays at the budget. A body entering interest
/// while awake goes with the snap bit.
/// </summary>
public sealed class PriorityScheduler
{
    sealed class Acc
    {
        public double A;
        public double LastSent = double.NegativeInfinity;
        public double Since;
        public bool InInterest;
        public bool NeedSnap;
        public long SeenTick;
    }

    sealed class Receiver
    {
        public readonly Dictionary<ushort, Acc> Bodies = new();
        public double Credit;
        public double LastPlan = double.NaN;
        public long Tick;
        public long BytesPlanned, EntriesPlanned;
        public double MaxGap;
    }

    readonly Dictionary<byte, Receiver> _receivers = new();
    readonly List<(Acc Acc, SchedBody Body, float Dist, bool Forced)> _cands = new();
    readonly List<StateEntry> _trial = new();

    public PriorityScheduler(SchedulerConfig? config = null) => Config = config ?? new SchedulerConfig();

    public SchedulerConfig Config { get; }

    public void RemoveReceiver(byte slot) => _receivers.Remove(slot);

    /// <summary>Drops the body's accumulators everywhere (despawn, rest).</summary>
    public void Forget(NetId id)
    {
        foreach (var r in _receivers.Values) r.Bodies.Remove(id.Value);
    }

    public long BytesPlanned(byte slot) => _receivers.TryGetValue(slot, out var r) ? r.BytesPlanned : 0;
    public long EntriesPlanned(byte slot) => _receivers.TryGetValue(slot, out var r) ? r.EntriesPlanned : 0;
    /// <summary>Longest time an awake in-interest body waited between sends for this receiver (dev stat / tests).</summary>
    public double MaxGapSeconds(byte slot) => _receivers.TryGetValue(slot, out var r) ? r.MaxGap : 0;
    public double Credit(byte slot) => _receivers.TryGetValue(slot, out var r) ? r.Credit : 0;

    /// <summary>Priority weight of a body for a receiver (spec 07 §6.2).</summary>
    public float Weight(in SchedBody b, in ReceiverView rv, float dist)
    {
        float w = (1f + 0.5f * MathF.Min(b.Speed, 8f)) / (1f + (dist / 6f) * (dist / 6f));
        if (InViewCone(rv, b.Pos, Config.ViewConeHalfAngleDeg)) w *= 2f;
        if (b.Basin) w *= 0.25f;
        if (b.Barrow) w *= 2f;
        return w;
    }

    /// <summary>Unity convention: yaw 0 looks along +Z, yaw 90 along +X, positive pitch looks down.</summary>
    public static bool InViewCone(in ReceiverView rv, Vec3 p, float halfAngleDeg)
    {
        var d = p - rv.Eye;
        float len = d.Length;
        if (len < 1e-4f) return true;
        float yaw = rv.YawDeg * (MathF.PI / 180f), pitch = rv.PitchDeg * (MathF.PI / 180f);
        var f = new Vec3(MathF.Sin(yaw) * MathF.Cos(pitch), -MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
        return Vec3.Dot(f, d) / len >= MathF.Cos(halfAngleDeg * (MathF.PI / 180f));
    }

    /// <summary>
    /// Plans this tick's <c>BodyState</c> for one receiver. <paramref name="reliableBytes"/> are body bytes already
    /// sent to it on the reliable channel this tick (rest/spawn/despawn), debited from the same budget.
    /// </summary>
    public ScheduledBatch Plan(in ReceiverView rv, IReadOnlyList<SchedBody> bodies, double now, int reliableBytes = 0)
    {
        var cfg = Config;
        if (!_receivers.TryGetValue(rv.Slot, out var rc)) _receivers[rv.Slot] = rc = new Receiver();
        double dt = double.IsNaN(rc.LastPlan) ? 1.0 / cfg.TickRate : Math.Clamp(now - rc.LastPlan, 0, 0.25);
        rc.LastPlan = now;
        rc.Tick++;
        double perTick = cfg.BudgetPerTick;
        rc.Credit = Math.Min(rc.Credit + cfg.RefillBytesPerSec * dt, 2 * perTick) - reliableBytes;
        rc.Credit = Math.Max(rc.Credit, -cfg.Overdraft - cfg.MaxMessageBytes);

        var batch = new ScheduledBatch { Anchor = BodyCodec.QuantizeAnchor(rv.Eye) };
        _cands.Clear();
        double nearInterval = 1.0 / cfg.NearMinHz - 1e-6;
        foreach (var b in bodies)
        {
            if (!b.Awake || !b.Id.IsValid) continue;
            if (b.Owner == rv.Slot) continue;
            float dist = Vec3.Distance(b.Pos, rv.Eye);
            bool inInterest = dist <= cfg.InterestRadius;
            if (!rc.Bodies.TryGetValue(b.Id.Value, out var acc))
                rc.Bodies[b.Id.Value] = acc = new Acc { Since = now, InInterest = inInterest };
            acc.SeenTick = rc.Tick;
            if (inInterest && !acc.InInterest) { acc.NeedSnap = true; acc.Since = now; acc.LastSent = double.NegativeInfinity; }
            acc.InInterest = inInterest;
            if (!inInterest) continue;
            if (b.Snap) acc.NeedSnap = true;
            acc.A += Weight(b, rv, dist) * dt;
            double sinceSent = now - Math.Max(acc.LastSent, acc.Since);
            bool forced = ((dist < cfg.NearRadius || b.Held) && now - acc.LastSent >= nearInterval)
                          || sinceSent >= cfg.MaxUnsentSeconds - 1e-6;
            _cands.Add((acc, b, dist, forced));
        }
        // Accumulators of bodies that went to rest or vanished.
        if (rc.Bodies.Count > _cands.Count)
        {
            List<ushort>? stale = null;
            foreach (var kv in rc.Bodies) if (kv.Value.SeenTick != rc.Tick) (stale ??= new()).Add(kv.Key);
            if (stale != null) foreach (var k in stale) rc.Bodies.Remove(k);
        }

        _cands.Sort((x, y) => x.Forced != y.Forced ? (x.Forced ? -1 : 1) : y.Acc.A.CompareTo(x.Acc.A));

        int framing = 3; // id + varint len of one message
        double normalAllowance = Math.Min(rc.Credit, cfg.MaxMessageBytes);
        double forcedAllowance = Math.Min(rc.Credit + cfg.Overdraft, cfg.MaxMessageBytes);
        var chosen = new List<int>();
        var taken = new bool[_cands.Count];
        int exact = BodyState.HeaderSize + framing;
        // Greedy with the worst-case entry size, then re-measure exactly and top up with the slack (ids sorted ascending
        // mostly take the 1-byte delta form, so the first pass under-fills).
        for (int pass = 0; pass < 4; pass++)
        {
            int est = exact;
            bool added = false;
            for (int i = 0; i < _cands.Count && chosen.Count < BodyState.MaxEntries; i++)
            {
                if (taken[i]) continue;
                var c = _cands[i];
                int worst = 1 + 2 + (BodyCodec.FitsRel(c.Body.Pos, batch.Anchor) ? 5 : 6) + 4;
                double allowance = c.Forced ? forcedAllowance : normalAllowance;
                if (est + worst > allowance) { if (!c.Forced) break; continue; }
                taken[i] = true; chosen.Add(i); est += worst; added = true;
            }
            if (!added) break;
            _trial.Clear();
            foreach (int i in chosen) _trial.Add(new StateEntry(_cands[i].Body.Id, _cands[i].Body.Pos, _cands[i].Body.Rot, _cands[i].Acc.NeedSnap));
            _trial.Sort((a, b) => a.Id.CompareTo(b.Id));
            exact = BodyState.Measure(batch.Anchor, _trial) + framing;
        }
        if (chosen.Count == 0) return batch;

        foreach (int i in chosen)
        {
            var (acc, _, _, forced) = _cands[i];
            if (!double.IsNegativeInfinity(acc.LastSent)) rc.MaxGap = Math.Max(rc.MaxGap, now - acc.LastSent);
            acc.A = 0;
            acc.LastSent = now;
            acc.NeedSnap = false;
            if (forced) batch.Forced++;
        }
        batch.Entries.AddRange(_trial);
        batch.Bytes = exact;
        rc.Credit -= exact;
        rc.BytesPlanned += exact;
        rc.EntriesPlanned += batch.Entries.Count;
        return batch;
    }
}
