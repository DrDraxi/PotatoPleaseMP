using PotatoPleaseMP.Core.Bodies;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Bodies;

public class PrioritySchedulerTests
{
    const double Dt = 1.0 / 30;

    static List<SchedBody> Ring(int n, float minR, float maxR, int firstId = 1, float speed = 2f)
    {
        var rng = new Random(n * 31 + firstId);
        var l = new List<SchedBody>();
        for (int i = 0; i < n; i++)
        {
            float r = minR + (float)rng.NextDouble() * (maxR - minR), a = (float)(rng.NextDouble() * Math.PI * 2);
            l.Add(new SchedBody { Id = new NetId((ushort)(firstId + i)), Pos = new Vec3(MathF.Cos(a) * r, 1, MathF.Sin(a) * r), Rot = Quat.Identity, Speed = speed, Awake = true, Owner = BodyCodec.HostOwner });
        }
        return l;
    }

    [Fact]
    public void Respects_the_byte_budget_and_one_datagram_per_tick()
    {
        var s = new PriorityScheduler();
        var rv = new ReceiverView(2, new Vec3(0, 1.6f, 0));
        var bodies = Ring(400, 0.5f, 28f);
        long total = 0;
        int ticks = 300;
        for (int t = 0; t < ticks; t++)
        {
            var b = s.Plan(rv, bodies, t * Dt);
            Assert.True(b.Bytes <= s.Config.MaxMessageBytes);
            Assert.Equal(b.Bytes, b.IsEmpty ? 0 : BodyState.Measure(b.Anchor, b.Entries) + 3);
            total += b.Bytes;
        }
        double seconds = ticks * Dt;
        Assert.True(total <= s.Config.ClientBudgetBytesPerSec * seconds + s.Config.Overdraft + 2 * s.Config.BudgetPerTick,
            $"{total / seconds:0} B/s over budget");
        Assert.True(total >= s.Config.ClientBudgetBytesPerSec * seconds * 0.85, $"budget under-used: {total / seconds:0} B/s");
    }

    [Fact]
    public void Near_and_held_bodies_get_15_Hz_and_nobody_starves()
    {
        var s = new PriorityScheduler();
        var rv = new ReceiverView(2, new Vec3(0, 1.6f, 0));
        var bodies = Ring(200, 5f, 29f, 100, speed: 6f);
        bodies.AddRange(Ring(4, 0.5f, 2.5f, 1, speed: 0.1f));
        var held = Ring(1, 20f, 20f, 50, speed: 0f)[0];
        held.Held = true;
        bodies.Add(held);
        var sends = new Dictionary<ushort, int>();
        int ticks = 90;
        for (int t = 0; t < ticks; t++)
            foreach (var e in s.Plan(rv, bodies, t * Dt).Entries)
                sends[e.Id.Value] = sends.GetValueOrDefault(e.Id.Value) + 1;
        foreach (ushort id in new ushort[] { 1, 2, 3, 4, 50 })
            Assert.True(sends.GetValueOrDefault(id) >= 15 * 3 - 1, $"#{id} sent {sends.GetValueOrDefault(id)} times in 3 s");
        Assert.All(bodies, b => Assert.True(sends.ContainsKey(b.Id.Value)));
        Assert.True(s.MaxGapSeconds(2) <= 0.4 + Dt + 1e-6, $"max gap {s.MaxGapSeconds(2):0.000} s");
    }

    [Fact]
    public void Interest_radius_ownership_and_rest()
    {
        var s = new PriorityScheduler();
        var rv = new ReceiverView(2, Vec3.Zero);
        var bodies = new List<SchedBody>
        {
            new() { Id = new NetId(1), Pos = new Vec3(31, 0, 0), Awake = true, Owner = 15, Rot = Quat.Identity },
            new() { Id = new NetId(2), Pos = new Vec3(1, 0, 0), Awake = true, Owner = 2, Rot = Quat.Identity },
            new() { Id = new NetId(3), Pos = new Vec3(1, 0, 0), Awake = false, Owner = 15, Rot = Quat.Identity },
            new() { Id = new NetId(4), Pos = new Vec3(29, 0, 0), Awake = true, Owner = 3, Rot = Quat.Identity },
        };
        for (int t = 0; t < 30; t++)
            Assert.All(s.Plan(rv, bodies, t * Dt).Entries, e => Assert.Equal(new NetId(4), e.Id));
    }

    [Fact]
    public void Entering_interest_while_awake_sets_the_snap_bit_once()
    {
        var s = new PriorityScheduler();
        var rv = new ReceiverView(2, Vec3.Zero);
        var b = new SchedBody { Id = new NetId(9), Pos = new Vec3(40, 0, 0), Awake = true, Owner = 15, Rot = Quat.Identity, Speed = 5 };
        Assert.True(s.Plan(rv, new[] { b }, 0).IsEmpty);
        b.Pos = new Vec3(20, 0, 0);
        var first = s.Plan(rv, new[] { b }, Dt);
        Assert.True(Assert.Single(first.Entries).Snap);
        var second = s.Plan(rv, new[] { b }, 2 * Dt);
        Assert.False(Assert.Single(second.Entries).Snap);
    }

    [Fact]
    public void Higher_priority_is_sent_more_often_under_a_tight_budget()
    {
        var s = new PriorityScheduler(new SchedulerConfig { ClientBudgetBytesPerSec = 30 * 22, MaxOverdraftBytes = 0 });
        var rv = new ReceiverView(2, Vec3.Zero, YawDeg: 0);
        var near = new SchedBody { Id = new NetId(1), Pos = new Vec3(0, 0, 5), Awake = true, Owner = 15, Rot = Quat.Identity, Speed = 4 }; // in view
        var far = new SchedBody { Id = new NetId(2), Pos = new Vec3(0, 0, -25), Awake = true, Owner = 15, Rot = Quat.Identity, Speed = 0 }; // behind
        var basin = new SchedBody { Id = new NetId(3), Pos = new Vec3(4, 0, 0), Awake = true, Owner = 15, Rot = Quat.Identity, Speed = 0, Basin = true };
        var counts = new int[4];
        for (int t = 0; t < 300; t++)
            foreach (var e in s.Plan(rv, new[] { near, far, basin }, t * Dt).Entries) counts[e.Id.Value]++;
        Assert.True(counts[1] > counts[3] && counts[3] >= counts[2], $"near {counts[1]}, basin {counts[3]}, far {counts[2]}");
        Assert.True(counts[2] >= 300 / 13, "the far body still gets the 0.4 s floor");
    }

    [Fact]
    public void Weight_matches_the_spec_formula()
    {
        var s = new PriorityScheduler();
        var rv = new ReceiverView(1, Vec3.Zero, YawDeg: 90); // looking along +X
        var b = new SchedBody { Pos = new Vec3(6, 0, 0), Speed = 10 };
        Assert.Equal((1 + 0.5f * 8) * 0.5f * 2f, s.Weight(b, rv, 6), 4);
        b.Pos = new Vec3(-6, 0, 0); b.Basin = true; b.Barrow = true;
        Assert.Equal((1 + 0.5f * 8) * 0.5f * 0.25f * 2f, s.Weight(b, rv, 6), 4);
    }
}

public class InterpBufferTests
{
    static readonly Quat Q0 = Quat.Identity;

    [Fact]
    public void Interpolates_between_samples()
    {
        var b = new InterpBuffer();
        b.Push(0, new Vec3(0, 0, 0), Q0);
        b.Push(3, new Vec3(3, 0, 0), new Quat(0, MathF.Sin(0.5f), 0, MathF.Cos(0.5f)));
        var s = b.Sample(1.5);
        Assert.Equal(InterpState.Interpolating, s.State);
        Assert.Equal(1.5f, s.Pos.X, 4);
        Assert.Equal(28.65f, Quat.AngleDeg(s.Rot, Q0), 1);
        Assert.Equal(0f, b.Sample(-5).Pos.X); // before history: oldest
    }

    [Fact]
    public void Extrapolates_at_most_200_ms_then_holds()
    {
        var b = new InterpBuffer();
        b.Push(0, new Vec3(0, 0, 0), Q0);
        b.Push(1, new Vec3(0.1f, 0, 0), Q0); // 3 m/s
        var e = b.Sample(4); // 100 ms ahead
        Assert.Equal(InterpState.Extrapolating, e.State);
        Assert.Equal(0.4f, e.Pos.X, 3);
        var h = b.Sample(31); // 1 s ahead
        Assert.Equal(InterpState.Holding, h.State);
        Assert.Equal(0.1f + 3f * 0.2f, h.Pos.X, 3);
    }

    [Fact]
    public void Snap_clears_history()
    {
        var b = new InterpBuffer();
        for (uint t = 0; t < 4; t++) b.Push(t, new Vec3(t, 0, 0), Q0);
        b.Push(5, new Vec3(100, 0, 0), Q0, snap: true);
        Assert.Equal(1, b.Count);
        Assert.Equal(100f, b.Sample(4.5).Pos.X);
    }

    [Fact]
    public void Rest_is_exact_final_and_survives_late_unreliable_samples()
    {
        var b = new InterpBuffer();
        for (uint t = 0; t <= 6; t += 2) b.Push(t, new Vec3(t * 0.1f, 0, 0), Q0);
        var rest = new Vec3(0.6123f, 0.01f, 0.002f);
        var rq = new Quat(0.1f, 0.2f, 0.3f, 0.9f).Normalized;
        b.PushRest(8, rest, rq);
        Assert.False(b.Push(7, new Vec3(9, 9, 9), Q0)); // late unreliable
        Assert.False(b.Push(8, new Vec3(9, 9, 9), Q0)); // same tick as the rest
        Assert.Equal(InterpState.Interpolating, b.Sample(7).State);
        var s = b.Sample(8.5);
        Assert.Equal(InterpState.AtRest, s.State);
        Assert.Equal(rest, s.Pos);
        Assert.Equal(rq, s.Rot);
        Assert.Equal(InterpState.AtRest, b.Sample(1000).State);

        // Waking long after: motion starts from the rest pose 2 ticks before the first new sample.
        Assert.True(b.Push(100, rest + new Vec3(0.2f, 0, 0), Q0));
        Assert.Equal(rest, b.Sample(98).Pos);
        Assert.Equal(rest.X + 0.1f, b.Sample(99).Pos.X, 4);
    }

    [Fact]
    public void Drops_out_of_order_and_keeps_the_newest_8()
    {
        var b = new InterpBuffer();
        for (uint t = 0; t < 20; t++) b.Push(t, new Vec3(t, 0, 0), Q0);
        Assert.False(b.Push(10, Vec3.Zero, Q0));
        Assert.Equal(InterpBuffer.Capacity, b.Count);
        Assert.Equal(19u, b.NewestTick);
        Assert.Equal(12f, b.Sample(0).Pos.X); // oldest kept
    }

    [Fact]
    public void Delay_formula()
    {
        Assert.Equal(0.1, InterpBuffer.ComputeDelaySeconds(1.0 / 15, 0), 6);
        Assert.Equal(0.066, InterpBuffer.ComputeDelaySeconds(1.0 / 30, 0), 6);
        Assert.Equal(0.2, InterpBuffer.ComputeDelaySeconds(1.0 / 15, 0.1), 6);
    }
}

public class RestDetectorTests
{
    static readonly Quat Q0 = Quat.Identity;

    [Fact]
    public void Rests_after_0_3_s_still_and_wakes_on_motion()
    {
        var d = new RestDetector();
        var id = new NetId(1);
        d.Add(id, Vec3.Zero, Q0, 0);
        double t = 0;
        int restEvents = 0;
        for (int i = 0; i < 30; i++)
        {
            t += 1.0 / 30;
            var u = d.Update(id, new Vec3(0.001f * (i % 2), 0, 0), Q0, t); // 1 mm jitter is not motion
            if (u.BecameRest) { restEvents++; Assert.True(t >= 0.3 - 1e-9 && t < 0.3 + 0.04); }
        }
        Assert.Equal(1, restEvents);
        Assert.True(d.IsResting(id));
        var w = d.Update(id, new Vec3(0.01f, 0, 0), Q0, t + 0.033);
        Assert.True(w.Awake && w.BecameAwake);
    }

    [Fact]
    public void Slow_creep_counts_as_motion()
    {
        var d = new RestDetector();
        var id = new NetId(1);
        d.Add(id, Vec3.Zero, Q0, 0);
        for (int i = 1; i <= 60; i++)
        {
            var u = d.Update(id, new Vec3(0.0015f * i, 0, 0), Q0, i / 30.0); // 4.5 cm/s
            Assert.False(u.BecameRest);
        }
    }

    [Fact]
    public void Rotation_basin_thresholds_sleep_and_snap()
    {
        var d = new RestDetector();
        var id = new NetId(1);
        d.Add(id, Vec3.Zero, Q0, 0);
        var rot2deg = new Quat(0, MathF.Sin(2f * MathF.PI / 360), 0, MathF.Cos(2f * MathF.PI / 360));
        Assert.True(d.Update(id, Vec3.Zero, rot2deg, 0.1).Awake);
        // In a basin 2° and 4 mm bobbing is not motion.
        var bid = new NetId(2);
        d.Add(bid, Vec3.Zero, Q0, 0);
        bool rested = false;
        for (int i = 1; i <= 15; i++)
            rested |= d.Update(bid, new Vec3(0, 0.004f * (i % 2), 0), i % 2 == 0 ? Q0 : rot2deg, i / 30.0, basin: true).BecameRest;
        Assert.True(rested);
        // Sleeping rests at once; a 1 m+ jump is a snap.
        var sid = new NetId(3);
        d.Add(sid, Vec3.Zero, Q0, 0);
        Assert.True(d.Update(sid, Vec3.Zero, Q0, 0.01, sleeping: true).BecameRest);
        Assert.True(d.Update(sid, new Vec3(2, 0, 0), Q0, 0.05).Snap);
    }
}

public class ContainerLedgerTests
{
    [Fact]
    public void Tank_never_goes_negative_and_peeled_never_exceeds_count()
    {
        var l = new ContainerLedger();
        var t = l.Get(ContainerKind.Vacuum, 2, 2);
        var rng = new Random(9);
        for (int i = 0; i < 20_000; i++)
        {
            if (rng.Next(2) == 0) l.Add(t, rng.Next(3) == 0);
            else l.TryTake(t, rng.Next(2) == 0, out _);
            Assert.InRange(t.Count, 0, int.MaxValue);
            Assert.InRange(t.Peeled, 0, t.Count);
            Assert.True(t.Props.Count <= ContainerCount.MaxProps);
        }
        while (t.Count > 0) Assert.True(l.TryTake(t, false, out _));
        Assert.False(l.TryTake(t, true, out _));
        Assert.Equal(0, t.Peeled);
    }

    [Fact]
    public void Adoption_and_rejection()
    {
        var l = new ContainerLedger();
        var ids = new HostIdAllocator();
        var tank = l.Get(ContainerKind.Bucket, 3, 3);
        l.Add(tank, true);
        var spawn = new ClientSpawn { TempId = NetId.Temp(3, 0), Source = ClientSpawnSource.BucketPour, Holder = 3, Flags = ClientSpawnFlags.PeeledOnSpill };

        var ok = l.AdoptSpawn(3, spawn, ids, 0, out bool peeled);
        Assert.True(ok.Accepted); Assert.True(peeled);
        Assert.Equal(0, tank.Count);
        Assert.Equal(ok.NetId, l.Resolve(3, NetId.Temp(3, 0)));
        Assert.Equal(NetId.Temp(3, 0), l.Resolve(4, NetId.Temp(3, 0))); // other players' temp ids are not mapped

        Assert.Equal(AdoptReason.Duplicate, l.AdoptSpawn(3, spawn, ids, 0, out _).Reason);
        Assert.Equal(AdoptReason.Empty, l.AdoptSpawn(3, spawn with { TempId = NetId.Temp(3, 1) }, ids, 0, out _).Reason);
        Assert.Equal(AdoptReason.BadHolder, l.AdoptSpawn(4, spawn with { TempId = NetId.Temp(3, 2) }, ids, 0, out _).Reason);
        Assert.Equal(AdoptReason.BadHolder, l.AdoptSpawn(3, spawn with { TempId = NetId.Temp(3, 3), Holder = 5 }, ids, 0, out _).Reason);
        Assert.Equal(AdoptReason.BadHolder, l.AdoptSpawn(3, spawn with { TempId = NetId.Temp(3, 4), Source = ClientSpawnSource.BarrowPour }, ids, 0, out _).Reason);
        Assert.Equal(0, tank.Count);
    }

    [Fact]
    public void Consume_accepts_owned_or_near_free_bodies_only()
    {
        var l = new ContainerLedger();
        var auth = new AuthorityTable();
        for (ushort i = 1; i <= 5; i++) auth.Add(new NetId(i));
        auth.TryClaim(new NetId(1), 2, ClaimReason.Vacuum, 1, 1, 0);
        auth.TryClaim(new NetId(2), 3, ClaimReason.Vacuum, 1, 1, 0);
        var dist = new Dictionary<ushort, float> { [1] = 9, [2] = 1, [3] = 3, [4] = 6 };
        l.MapTemp(2, NetId.Temp(2, 9), new NetId(3));
        var msg = new BodyConsume { Kind = ConsumeKind.Vacuum, Holder = 2, Ids = new() { new NetId(1), new NetId(2), NetId.Temp(2, 9), new NetId(4), new NetId(77), new NetId(1) } };
        var v = l.Consume(2, msg, id => dist.ContainsKey(id.Value)
            ? new ConsumeCandidate(true, auth.Get(id), dist[id.Value], id.Value == 3)
            : new ConsumeCandidate(false, null, 0, false));
        Assert.Equal(new[]
        {
            (new NetId(1), ConsumeVerdict.Accepted), (new NetId(2), ConsumeVerdict.NotOwned), (new NetId(3), ConsumeVerdict.Accepted),
            (new NetId(4), ConsumeVerdict.TooFar), (new NetId(77), ConsumeVerdict.Unknown),
        }, v);
        var tank = l.Find(ContainerKind.Vacuum, 2)!;
        Assert.Equal(2, tank.Count);
        Assert.Equal(1, tank.Peeled);
        var cc = Assert.Single(l.TakeDirty());
        Assert.Equal(2, cc.Count);
        Assert.Empty(l.TakeDirty());
    }

    [Fact]
    public void Player_left_returns_their_tanks()
    {
        var l = new ContainerLedger();
        l.Add(l.Get(ContainerKind.Vacuum, 2, 2), false);
        l.Add(l.Get(ContainerKind.Bucket, 2, 2), false);
        l.Add(l.Get(ContainerKind.Vacuum, 3, 3), false);
        l.Add(l.Get(ContainerKind.Barrow, 0xC000), false);
        l.MapTemp(2, NetId.Temp(2, 1), new NetId(5));
        var tanks = l.PlayerLeft(2);
        Assert.Equal(2, tanks.Count);
        Assert.Equal(2, l.TotalCount);
        Assert.Equal(NetId.Temp(2, 1), l.Resolve(2, NetId.Temp(2, 1)));
    }
}
