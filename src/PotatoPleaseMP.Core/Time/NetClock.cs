namespace PotatoPleaseMP.Core.Time;

/// <summary>
/// Session tick clock (spec 01 §6.1): ticks since session start at <see cref="Rate"/> Hz over unscaled real time
/// (the plugin passes <c>Time.realtimeSinceStartupAsDouble</c>; <c>timeScale</c> can be 0).
/// </summary>
public sealed class NetClock
{
    public const int DefaultRate = 30;

    public NetClock(double start, int rate = DefaultRate)
    {
        if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
        Start = start;
        Rate = rate;
    }

    public double Start { get; private set; }
    public int Rate { get; }

    public uint TickAt(double now) => now <= Start ? 0u : (uint)Math.Floor((now - Start) * Rate);
    public double TimeOfTick(uint tick) => Start + tick / (double)Rate;
    public static double TickToMs(uint tick, int rate = DefaultRate) => tick * 1000.0 / rate;

    /// <summary>Re-bases so that <paramref name="now"/> is tick <paramref name="tick"/> (client adopting the host's tick).</summary>
    public void Align(double now, uint tick) => Start = now - tick / (double)Rate;
}

/// <summary>
/// Paces the net flush (spec 01 §6.1): at most one flush per frame and only when the tick advanced, so after a hitch
/// the tick number jumps and nothing bursts.
/// </summary>
public sealed class TickScheduler
{
    bool _any;

    public uint LastTick { get; private set; }

    /// <summary>True (once) when <paramref name="currentTick"/> is newer than the last flushed tick.</summary>
    public bool ShouldFlush(uint currentTick)
    {
        if (_any && currentTick <= LastTick) return false;
        _any = true;
        LastTick = currentTick;
        return true;
    }

    /// <summary>Phase offset in ticks for a slot, so streams of different players land on different ticks.</summary>
    public static int PhaseForSlot(byte slot, double rateHz, int tickRate = NetClock.DefaultRate)
    {
        int period = Math.Max(1, (int)Math.Round(tickRate / rateHz));
        return slot % period;
    }
}

/// <summary>
/// Per-stream send pacing quantised to ticks (spec 01 §6.1): due whenever <c>floor((tick + phase) · rate / tickRate)</c>
/// changes. Divisors of 30 are evenly spaced; 20 Hz alternates 1 and 2 ticks. A tick jump after a hitch fires once.
/// </summary>
public sealed class StreamPacer
{
    long _last = long.MinValue;

    public StreamPacer(double rateHz, int phaseTicks = 0, int tickRate = NetClock.DefaultRate)
    {
        RateHz = rateHz;
        PhaseTicks = phaseTicks;
        TickRate = tickRate;
    }

    public double RateHz { get; set; }
    public int PhaseTicks { get; }
    public int TickRate { get; }

    public bool Due(uint tick)
    {
        if (RateHz <= 0) return false;
        long idx = (long)Math.Floor((tick + (double)PhaseTicks) * RateHz / TickRate);
        // First call: fire only on a period boundary, so streams with different phases never share their first tick.
        if (_last == long.MinValue) _last = (long)Math.Floor((tick + (double)PhaseTicks - 1) * RateHz / TickRate);
        if (idx == _last) return false;
        _last = idx;
        return true;
    }

    public void Reset() => _last = long.MinValue;
}
