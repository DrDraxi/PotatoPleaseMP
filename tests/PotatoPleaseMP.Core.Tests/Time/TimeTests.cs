using PotatoPleaseMP.Core.Time;

namespace PotatoPleaseMP.Core.Tests.Time;

public class ClockSyncTests
{
    /// <summary>Host stamps at 30 Hz; each arrives after rtt/2 ± jitter; host clock = local + offset (+ drift).</summary>
    static ClockSync Simulate(double seconds, double offset, double rttMs, double jitterMs, int seed, ClockSync? cs = null,
        double start = 0, Func<double, double>? hostClock = null, double outlierEvery = 0)
    {
        cs ??= new ClockSync();
        var rng = new Random(seed);
        hostClock ??= t => t + offset;
        for (double t = start; t < start + seconds; t += 1 / 30.0)
        {
            double hostTime = Math.Floor(hostClock(t) * 30) / 30.0;
            double oneWay = rttMs / 2 / 1000 + rng.NextDouble() * jitterMs / 1000;
            if (outlierEvery > 0 && rng.NextDouble() < outlierEvery) oneWay += 0.3;
            double arrival = t + oneWay;
            cs.AddRtt(rttMs + rng.NextDouble() * jitterMs, arrival);
            cs.AddSample(hostTime, arrival);
        }
        return cs;
    }

    [Fact]
    public void Converges_within_10ms_in_2s()
    {
        // 80 ms ± 20 ms RTT, host 5 s ahead. Samples are stamped exactly on host ticks.
        var cs = new ClockSync();
        var rng = new Random(1);
        double offset = 5.0;
        for (double t = 0; t < 2.0; t += 1 / 30.0)
        {
            double hostTime = Math.Round((t + offset) * 30) / 30.0;
            double sendLocal = hostTime - offset;
            double oneWay = 0.030 + rng.NextDouble() * 0.020;
            cs.AddRtt(60 + rng.NextDouble() * 40, sendLocal + oneWay);
            cs.AddSample(hostTime, sendLocal + oneWay);
        }
        Assert.True(cs.HasOffset);
        Assert.True(Math.Abs(cs.Offset - offset) <= 0.010, $"offset {cs.Offset}");
    }

    [Fact]
    public void Delayed_outliers_are_ignored()
    {
        var cs = Simulate(3, 2.0, 60, 0, 2, outlierEvery: 0.2);
        Assert.True(Math.Abs(cs.Offset - 2.0) < 0.035, $"offset {cs.Offset}"); // tick quantisation, never the 300 ms outliers
    }

    [Fact]
    public void Big_host_jump_snaps_small_drift_slews()
    {
        var cs = Simulate(3, 1.0, 40, 0, 3);
        int snaps = cs.Snaps;
        Simulate(3, 2.0, 40, 0, 4, cs, start: 3); // +1 s jump
        Assert.Equal(snaps + 1, cs.Snaps);
        Assert.True(Math.Abs(cs.Offset - 2.0) < 0.04);

        // A target 100 ms away (below the snap threshold): the offset moves at most 5 ms/s.
        var s2 = new ClockSync();
        s2.Reset(10, 0);
        s2.AddSample(10.1 + 1.0, 1.0);
        Assert.True(s2.Offset - 10 <= 0.005 + 1e-9, $"{s2.Offset}");
        s2.AddSample(10.1 + 2.0, 2.0);
        Assert.True(s2.Offset - 10 <= 0.010 + 1e-9);
        Assert.Equal(0, s2.Snaps);
    }

    [Fact]
    public void Unwrap16_across_the_wrap()
    {
        Assert.Equal(65540u, SeqMath.Unwrap16(4, 65530));
        Assert.Equal(65530u, SeqMath.Unwrap16(65530, 65540));
        Assert.Equal(100u, SeqMath.Unwrap16(100, 90));
        Assert.Equal(3u, SeqMath.Unwrap16(3, 0));
        Assert.Equal(131072u + 5, SeqMath.Unwrap16(5, 131072));
    }
}

public class TickTests
{
    [Fact]
    public void Stream_pacer_rates_and_phases()
    {
        var p15a = new StreamPacer(15, 0);
        var p15b = new StreamPacer(15, 1);
        var p20 = new StreamPacer(20);
        var t15a = new List<uint>();
        var t15b = new List<uint>();
        var t20 = new List<uint>();
        for (uint t = 0; t < 300; t++)
        {
            if (p15a.Due(t)) t15a.Add(t);
            if (p15b.Due(t)) t15b.Add(t);
            if (p20.Due(t)) t20.Add(t);
        }
        Assert.Equal(150, t15a.Count); // 15/s over 10 s
        Assert.InRange(t15b.Count, 150, 151); // the phase shifts the window by one tick
        Assert.Empty(t15a.Intersect(t15b)); // different slots on different ticks
        Assert.InRange(t20.Count, 200, 201);
        var gaps = t20.Zip(t20.Skip(1), (a, b) => b - a).Distinct().ToList();
        Assert.All(gaps, g => Assert.InRange(g, 1u, 2u)); // <= 1 tick jitter
    }

    [Fact]
    public void Hitch_gives_one_flush_and_the_tick_jumps()
    {
        var clock = new NetClock(0);
        var sched = new TickScheduler();
        int flushes = 0;
        for (double t = 0; t < 1; t += 1 / 60.0) if (sched.ShouldFlush(clock.TickAt(t))) flushes++;
        Assert.InRange(flushes, 29, 31);
        uint before = sched.LastTick;
        // 500 ms hitch: one frame later.
        Assert.True(sched.ShouldFlush(clock.TickAt(1.5)));
        Assert.InRange(sched.LastTick, before + 14, before + 16);
        Assert.False(sched.ShouldFlush(clock.TickAt(1.5)));
        var pacer = new StreamPacer(15);
        pacer.Due(10);
        Assert.True(pacer.Due(25));
        Assert.False(pacer.Due(25));
    }

    [Fact]
    public void Phase_for_slot()
    {
        Assert.Equal(0, TickScheduler.PhaseForSlot(0, 15));
        Assert.Equal(1, TickScheduler.PhaseForSlot(1, 15));
        Assert.Equal(0, TickScheduler.PhaseForSlot(2, 15));
    }
}

public class InterpolatorTests
{
    static Interpolator.Pose P(float x, float yaw = 0, uint tag = 0) => new(new Vec3(x, 0, 0), yaw, 0, tag);

    [Fact]
    public void Delay_formula()
    {
        Assert.Equal(80, Interpolator.ComputeDelayMs(20, 0));
        Assert.Equal(100, Interpolator.ComputeDelayMs(50, 12.5));
        Assert.Equal(250, Interpolator.ComputeDelayMs(200, 50));
        Assert.Equal(0.1, new Interpolator(50) { JitterMs = 12.5 }.DelaySeconds, 6);
    }

    [Fact]
    public void Interpolates_between_samples_and_wraps_yaw()
    {
        var i = new Interpolator(50);
        i.Push(1.00, P(0, 350, 1));
        i.Push(1.05, P(1, 10, 2));
        var s = i.SampleAt(1.025);
        Assert.Equal(0.5f, s.Pos.X, 3);
        Assert.True(Math.Abs(((s.Yaw % 360) + 360) % 360 - 0) < 0.01 || Math.Abs(s.Yaw - 360) < 0.01);
        Assert.Equal(1u, s.Tag);
        Assert.Equal(0f, i.SampleAt(0.5).Pos.X); // before the oldest: oldest
    }

    [Fact]
    public void Extrapolates_at_most_100ms_then_holds()
    {
        var i = new Interpolator(50);
        i.Push(1.00, P(0));
        i.Push(1.05, P(1)); // 20 m/s
        Assert.Equal(2f, i.SampleAt(1.10).Pos.X, 3);   // 50 ms ahead
        Assert.Equal(3f, i.SampleAt(1.15).Pos.X, 3);   // 100 ms ahead (cap)
        Assert.Equal(3f, i.SampleAt(1.18).Pos.X, 3);   // capped
        Assert.False(i.Holding);
        var held = i.SampleAt(1.3);                     // > 3 intervals silent
        Assert.True(i.Holding);
        Assert.Equal(1f, held.Pos.X);
    }

    [Fact]
    public void Resume_after_gap_starts_from_rest()
    {
        var i = new Interpolator(50);
        i.Push(1.0, P(0));
        i.Push(2.0, P(10)); // 1 s gap: the old pose is re-stamped at 1.95
        Assert.Equal(0f, i.SampleAt(1.5).Pos.X);
        Assert.Equal(5f, i.SampleAt(1.975).Pos.X, 3);
        i.Push(1.9, P(99)); // out of order: ignored
        Assert.Equal(10f, i.SampleAt(2.0).Pos.X);
    }
}
