namespace PotatoPleaseMP.Core.Time;

/// <summary>
/// Snapshot interpolation buffer for one remote stream (spec 01 §6.2, SoD <c>Interpolator</c> port). Samples are
/// stamped in host time; render at <c>estHostTime - Delay</c> with
/// <c>Delay = clamp(1.5 · interval + 2 · jitter, 80, 250) ms</c> (20 Hz poses give ~100 ms). Past the newest sample it
/// extrapolates at most 100 ms; a gap of more than 3 intervals holds the newest pose, and the first sample after the gap
/// starts from rest instead of being stretched across the gap. Each sample carries a tag (flags) read at the same time.
/// </summary>
public sealed class Interpolator
{
    public readonly record struct Pose(Vec3 Pos, float Yaw, float Pitch, uint Tag);

    readonly record struct Entry(double Time, Pose Pose);

    const int Capacity = 32;
    readonly Entry[] _ring = new Entry[Capacity];
    int _count, _head;

    public Interpolator(double intervalMs = 50) => IntervalMs = intervalMs;

    /// <summary>Nominal sample interval of the stream (50 ms for 20 Hz poses).</summary>
    public double IntervalMs { get; set; }
    public double JitterMs { get; set; }
    public double MaxExtrapolationMs { get; set; } = 100;

    public static double ComputeDelayMs(double intervalMs, double jitterMs) => Math.Clamp(1.5 * intervalMs + 2 * jitterMs, 80, 250);
    public double DelaySeconds => ComputeDelayMs(IntervalMs, JitterMs) / 1000.0;

    public int Count => _count;
    public bool HasData => _count > 0;
    public double NewestTime => _count == 0 ? double.NegativeInfinity : _ring[_head].Time;
    /// <summary>True when the last <see cref="Sample"/> found the stream silent for more than 3 intervals.</summary>
    public bool Holding { get; private set; }

    public void Clear() { _count = 0; Holding = false; }

    /// <summary>Adds a sample; out-of-order samples are ignored, a repeated time replaces the newest.</summary>
    public void Push(double time, Pose pose)
    {
        if (_count > 0)
        {
            var newest = _ring[_head];
            if (time < newest.Time) return;
            if (time == newest.Time) { _ring[_head] = new Entry(time, pose); return; }
            // Resuming after a gap: re-stamp the last pose one interval before the new one so motion starts from rest.
            double gap = time - newest.Time;
            if (gap > 3 * IntervalMs / 1000.0) Add(new Entry(time - IntervalMs / 1000.0, newest.Pose));
        }
        Add(new Entry(time, pose));
    }

    void Add(Entry e)
    {
        _head = (_head + 1) % Capacity;
        _ring[_head] = e;
        if (_count < Capacity) _count++;
    }

    Entry At(int back) => _ring[((_head - back) % Capacity + Capacity) % Capacity];

    /// <summary>Pose at host time <paramref name="hostNow"/> minus the delay.</summary>
    public Pose Sample(double hostNow) => SampleAt(hostNow - DelaySeconds);

    /// <summary>Pose at render time <paramref name="t"/> (already delayed).</summary>
    public Pose SampleAt(double t)
    {
        Holding = false;
        if (_count == 0) return default;
        var newest = At(0);
        if (t >= newest.Time)
        {
            double ahead = t - newest.Time;
            if (ahead > 3 * IntervalMs / 1000.0) { Holding = true; return newest.Pose; }
            if (_count < 2) return newest.Pose;
            var prev = At(1);
            double dtS = newest.Time - prev.Time;
            if (dtS <= 0) return newest.Pose;
            double ext = Math.Min(ahead, MaxExtrapolationMs / 1000.0);
            float f = (float)(ext / dtS);
            var vel = (newest.Pose.Pos - prev.Pose.Pos);
            return newest.Pose with { Pos = newest.Pose.Pos + vel * f };
        }
        for (int i = 0; i < _count - 1; i++)
        {
            var b = At(i);
            var a = At(i + 1);
            if (t >= a.Time)
            {
                float f = (float)((t - a.Time) / (b.Time - a.Time));
                return new Pose(Vec3.Lerp(a.Pose.Pos, b.Pose.Pos, f), LerpAngle(a.Pose.Yaw, b.Pose.Yaw, f),
                    a.Pose.Pitch + (b.Pose.Pitch - a.Pose.Pitch) * f, f < 1 ? a.Pose.Tag : b.Pose.Tag);
            }
        }
        return At(_count - 1).Pose;
    }

    /// <summary>Interpolates angles in degrees the short way round.</summary>
    public static float LerpAngle(float a, float b, float t)
    {
        float delta = ((b - a) % 360f + 540f) % 360f - 180f;
        return a + delta * t;
    }
}
