namespace PotatoPleaseMP.Core.Time;

/// <summary>
/// Client estimate of host time (spec 01 §6.2), with no message of its own: every host unreliable envelope carries the
/// host tick as its stamp, giving a sample <c>s = hostTime - now</c> (= offset minus that packet's one-way delay).
/// The offset target is <c>max(s over 2 s) + rttMin/2</c>, i.e. the least-delayed sample corrected by half the best
/// round trip. The applied offset slews at most 5 ms/s toward it and snaps when more than 250 ms away. A constant bias
/// only shifts the interpolation delay, so this is enough for rendering remote players.
/// </summary>
public sealed class ClockSync
{
    readonly Queue<(double At, double S)> _samples = new();
    readonly Queue<(double At, double Rtt)> _rtts = new();
    double _lastUpdate = double.NaN;
    double _target;
    bool _hasTarget;

    public double WindowSeconds { get; set; } = 2;
    public double RttWindowSeconds { get; set; } = 10;
    public double MaxSlewPerSecond { get; set; } = 0.005;
    public double SnapThresholdSeconds { get; set; } = 0.25;

    public bool HasOffset { get; private set; }
    /// <summary>Host time = local time + Offset (seconds).</summary>
    public double Offset { get; private set; }
    public double TargetOffset => _target;
    /// <summary>EWMA of how far samples lag the best one, in ms.</summary>
    public double JitterMs { get; private set; }
    public double RttMinMs { get; private set; } = -1;
    public int Snaps { get; private set; }

    /// <summary>Feeds a round-trip measurement (transport ping or heartbeat).</summary>
    public void AddRtt(double rttMs, double now)
    {
        if (!(rttMs >= 0) || double.IsInfinity(rttMs)) return;
        _rtts.Enqueue((now, rttMs));
        while (_rtts.Count > 1 && now - _rtts.Peek().At > RttWindowSeconds) _rtts.Dequeue();
        RttMinMs = _rtts.Min(x => x.Rtt);
    }

    /// <summary>Feeds one host time sample received at local time <paramref name="now"/>.</summary>
    public void AddSample(double hostTimeSeconds, double now)
    {
        double s = hostTimeSeconds - now;
        _samples.Enqueue((now, s));
        while (_samples.Count > 1 && now - _samples.Peek().At > WindowSeconds) _samples.Dequeue();
        double max = double.NegativeInfinity;
        foreach (var x in _samples) if (x.S > max) max = x.S;
        double lag = (max - s) * 1000.0;
        JitterMs = _hasTarget ? JitterMs + (lag - JitterMs) / 16.0 : 0;
        _target = max + Math.Max(0, RttMinMs) / 2000.0;
        _hasTarget = true;
        Update(now);
    }

    /// <summary>Moves the applied offset toward the target (call every frame; also done on every sample).</summary>
    public void Update(double now)
    {
        if (!_hasTarget) return;
        double dt = double.IsNaN(_lastUpdate) ? 0 : Math.Max(0, now - _lastUpdate);
        _lastUpdate = now;
        double diff = _target - Offset;
        if (!HasOffset || Math.Abs(diff) > SnapThresholdSeconds)
        {
            if (HasOffset) Snaps++;
            Offset = _target;
            HasOffset = true;
            return;
        }
        double step = MaxSlewPerSecond * dt;
        Offset += Math.Clamp(diff, -step, step);
    }

    /// <summary>Snaps to a known host time (e.g. Welcome's host tick plus half the RTT).</summary>
    public void Reset(double hostTimeSeconds, double now)
    {
        _samples.Clear();
        Offset = _target = hostTimeSeconds - now;
        _hasTarget = HasOffset = true;
        _lastUpdate = now;
    }

    public double HostTime(double now) => now + Offset;

    public uint EstHostTick(double now, int rate = NetClock.DefaultRate)
    {
        double t = HostTime(now);
        return t <= 0 ? 0u : (uint)Math.Floor(t * rate);
    }
}
