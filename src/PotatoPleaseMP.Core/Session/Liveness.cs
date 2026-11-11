namespace PotatoPleaseMP.Core.Session;

/// <summary>
/// Heartbeat schedule and silence tracking for one peer (spec 02 §3.5): 1 Hz heartbeats each way, RTT as an EWMA,
/// silence 10 s = NotResponding, 45 s = TimedOut (the caller picks the limit; Loading peers get 180 s). Any packet
/// resets the silence.
/// </summary>
public sealed class Liveness
{
    double _lastHeard, _nextBeat;
    uint _remoteSendMs;
    double _remoteSendAt = double.NaN;

    public double HeartbeatInterval { get; set; } = 1.0;
    public double RttMs { get; private set; } = -1;
    public double LastHeard => _lastHeard;
    public HeartbeatFlags RemoteFlags { get; private set; }

    public void Start(double now)
    {
        _lastHeard = now;
        _nextBeat = now;
    }

    public void OnPacket(double now) => _lastHeard = Math.Max(_lastHeard, now);
    public double Silence(double now) => now - _lastHeard;

    public static uint Ms(double now) => unchecked((uint)(long)Math.Floor(now * 1000.0) + 1u); // never 0: 0 = "no echo"

    public void OnHeartbeat(in Heartbeat hb, double now)
    {
        OnPacket(now);
        RemoteFlags = hb.Flags;
        _remoteSendMs = hb.SendMs;
        _remoteSendAt = now;
        if (hb.EchoMs != 0)
        {
            uint rtt = unchecked(Ms(now) - hb.EchoMs);
            if (rtt < 60_000) RttMs = RttMs < 0 ? rtt : RttMs + (rtt - RttMs) / 8.0;
        }
    }

    public bool HeartbeatDue(double now) => now >= _nextBeat;

    public Heartbeat Build(double now, HeartbeatFlags flags)
    {
        _nextBeat = now + HeartbeatInterval;
        uint echo = double.IsNaN(_remoteSendAt) ? 0u : unchecked(_remoteSendMs + (uint)Math.Round((now - _remoteSendAt) * 1000.0));
        return new Heartbeat { SendMs = Ms(now), EchoMs = echo, Flags = flags };
    }
}

/// <summary>
/// Ordered buffer of world-change messages a joiner receives between its gather point (SnapshotInfo) and the end of its
/// world load (spec 02 §3.4), replayed through the normal router. Caps: 16 MB / 20,000 messages.
/// </summary>
public sealed class WorldChangeBuffer
{
    public const long DefaultMaxBytes = 16L * 1024 * 1024;
    public const int DefaultMaxMessages = 20_000;

    readonly List<(Wire.MsgId Id, byte[] Payload)> _items = new();

    public long MaxBytes { get; set; } = DefaultMaxBytes;
    public int MaxMessages { get; set; } = DefaultMaxMessages;
    public long Bytes { get; private set; }
    public int Count => _items.Count;
    public bool Overflowed { get; private set; }

    /// <summary>Appends a copy; false (and <see cref="Overflowed"/>) once a cap is exceeded.</summary>
    public bool Add(Wire.MsgId id, ReadOnlySpan<byte> payload)
    {
        if (Overflowed) return false;
        if (_items.Count + 1 > MaxMessages || Bytes + payload.Length > MaxBytes) { Overflowed = true; return false; }
        _items.Add((id, payload.ToArray()));
        Bytes += payload.Length;
        return true;
    }

    /// <summary>Hands every message to <paramref name="apply"/> in arrival order, then empties the buffer.</summary>
    public int Replay(Action<Wire.MsgId, byte[]> apply)
    {
        int n = 0;
        foreach (var (id, p) in _items) { apply(id, p); n++; }
        Clear();
        return n;
    }

    public void Clear()
    {
        _items.Clear();
        Bytes = 0;
        Overflowed = false;
    }
}
