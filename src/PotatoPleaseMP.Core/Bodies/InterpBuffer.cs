namespace PotatoPleaseMP.Core.Bodies;

/// <summary>How <see cref="InterpBuffer.Sample"/> produced its pose.</summary>
public enum InterpState : byte { Empty, Interpolating, Extrapolating, Holding, AtRest }

public readonly record struct InterpSample(Vec3 Pos, Quat Rot, InterpState State)
{
    public bool HasPose => State != InterpState.Empty;
}

/// <summary>
/// Client proxy buffer for one body (spec 07 §6.3): ring of 8 <c>(tick, pos, rot)</c> in host ticks. Sample at
/// <c>renderTime = hostTime − delay</c>; past the newest sample it extrapolates linearly for at most
/// <see cref="MaxExtrapolationSeconds"/> (200 ms), then holds. A snap sample clears the history so nothing is blended
/// across a teleport. A rest sample is the final pose: once playback reaches it the pose is exact and
/// <see cref="InterpState.AtRest"/>.
/// </summary>
public sealed class InterpBuffer
{
    public const int Capacity = 8;

    readonly record struct Entry(uint Tick, Vec3 Pos, Quat Rot, bool Rest);

    readonly Entry[] _ring = new Entry[Capacity];
    int _head = -1, _count;

    public InterpBuffer(double tickRate = 30) => TickSeconds = 1.0 / tickRate;

    public double TickSeconds { get; }
    public double MaxExtrapolationSeconds { get; set; } = 0.2;

    public int Count => _count;
    public bool IsEmpty => _count == 0;
    public uint NewestTick => _count == 0 ? 0 : _ring[_head].Tick;
    /// <summary>The newest sample is a rest pose.</summary>
    public bool EndsAtRest => _count > 0 && _ring[_head].Rest;
    public Vec3 NewestPos => _count == 0 ? default : _ring[_head].Pos;
    public Quat NewestRot => _count == 0 ? Quat.Identity : _ring[_head].Rot;

    /// <summary>Proxy delay: clamp(1.5 · interval + 2 · jitter, 66, 200) ms (100 ms at 15 Hz without jitter).</summary>
    public static double ComputeDelaySeconds(double intervalSeconds, double jitterSeconds)
        => Math.Clamp(1.5 * intervalSeconds + 2 * jitterSeconds, 0.066, 0.2);

    public void Clear() { _head = -1; _count = 0; }

    /// <summary>
    /// Adds a motion sample. Older ticks than the newest are dropped (unreliable reordering); the same tick replaces.
    /// A sample older than a buffered rest pose is dropped too (the rest is the final truth).
    /// Returns false when dropped.
    /// </summary>
    public bool Push(uint tick, Vec3 pos, Quat rot, bool snap = false)
    {
        if (snap) { Clear(); Add(new Entry(tick, pos, rot, false)); return true; }
        return Insert(new Entry(tick, pos, rot, false));
    }

    /// <summary>Appends the final rest pose (reliable <c>BodyRest</c>, stamped with the host tick it was sent on).</summary>
    public void PushRest(uint tick, Vec3 pos, Quat rot)
    {
        if (_count > 0 && tick < _ring[_head].Tick) tick = _ring[_head].Tick; // reliable can trail unreliable
        if (_count > 0 && tick == _ring[_head].Tick) { _ring[_head] = new Entry(tick, pos, rot, true); return; }
        Add(new Entry(tick, pos, rot, true));
    }

    /// <summary>Sets the pose with no history (spawn, join).</summary>
    public void Reset(uint tick, Vec3 pos, Quat rot, bool rest)
    {
        Clear();
        Add(new Entry(tick, pos, rot, rest));
    }

    bool Insert(Entry e)
    {
        if (_count > 0)
        {
            var newest = _ring[_head];
            if (e.Tick < newest.Tick) return false;
            if (e.Tick == newest.Tick)
            {
                if (newest.Rest) return false;
                _ring[_head] = e;
                return true;
            }
            // Waking from rest after a while: start the motion from the rest pose two ticks before the new sample,
            // instead of stretching one blend across the whole rest period.
            if (newest.Rest && e.Tick - newest.Tick > 2) Add(new Entry(e.Tick - 2, newest.Pos, newest.Rot, false));
        }
        Add(e);
        return true;
    }

    void Add(Entry e)
    {
        _head = (_head + 1) % Capacity;
        _ring[_head] = e;
        if (_count < Capacity) _count++;
    }

    Entry At(int back) => _ring[((_head - back) % Capacity + Capacity) % Capacity];

    /// <summary>Pose at <paramref name="renderTick"/> (fractional host ticks, already delayed).</summary>
    public InterpSample Sample(double renderTick)
    {
        if (_count == 0) return new InterpSample(default, Quat.Identity, InterpState.Empty);
        var newest = At(0);
        if (renderTick >= newest.Tick)
        {
            if (newest.Rest) return new InterpSample(newest.Pos, newest.Rot, InterpState.AtRest);
            if (_count < 2) return new InterpSample(newest.Pos, newest.Rot, InterpState.Holding);
            var prev = At(1);
            if (prev.Rest) return new InterpSample(newest.Pos, newest.Rot, InterpState.Holding);
            double ahead = (renderTick - newest.Tick) * TickSeconds;
            double span = (newest.Tick - prev.Tick) * TickSeconds;
            if (span <= 0 || span > 0.5) return new InterpSample(newest.Pos, newest.Rot, InterpState.Holding);
            bool capped = ahead > MaxExtrapolationSeconds;
            float f = (float)(Math.Min(ahead, MaxExtrapolationSeconds) / span);
            var pos = newest.Pos + (newest.Pos - prev.Pos) * f;
            return new InterpSample(pos, newest.Rot, capped ? InterpState.Holding : InterpState.Extrapolating);
        }
        for (int i = 0; i < _count - 1; i++)
        {
            var b = At(i);
            var a = At(i + 1);
            if (renderTick >= a.Tick)
            {
                float t = (float)((renderTick - a.Tick) / (b.Tick - a.Tick));
                return new InterpSample(Vec3.Lerp(a.Pos, b.Pos, t), BodyCodec.Slerp(a.Rot, b.Rot, t), InterpState.Interpolating);
            }
        }
        var oldest = At(_count - 1);
        return new InterpSample(oldest.Pos, oldest.Rot, InterpState.Holding);
    }

    /// <summary>Pose at host time <paramref name="hostSeconds"/> minus <paramref name="delaySeconds"/>.</summary>
    public InterpSample SampleAtTime(double hostSeconds, double delaySeconds)
        => Sample((hostSeconds - delaySeconds) / TickSeconds);
}
