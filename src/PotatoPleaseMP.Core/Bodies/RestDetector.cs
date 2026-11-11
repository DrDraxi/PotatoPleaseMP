namespace PotatoPleaseMP.Core.Bodies;

/// <summary>What one <see cref="RestDetector.Update"/> found.</summary>
public readonly record struct RestUpdate(bool Awake, bool BecameRest, bool BecameAwake, bool Snap);

/// <summary>
/// Host pose-delta rest test (spec 07 §6.1): a body is awake while it moves more than 2 mm or 1° (basin residents: 5 mm
/// or 3°) away from the pose it had at its last significant move; unchanged for 0.3 s (or reported sleeping) it comes to
/// rest once. A jump over 1 m between two updates flags a snap. Slow creep is caught because the reference pose only
/// moves on a significant move.
/// </summary>
public sealed class RestDetector
{
    sealed class Track
    {
        public Vec3 RefPos, LastPos;
        public Quat RefRot;
        public double LastMove;
        public bool Resting;
    }

    readonly Dictionary<ushort, Track> _tracks = new();

    public float MoveEpsilon { get; set; } = 0.002f;
    public float AngleEpsilonDeg { get; set; } = 1f;
    public float BasinMoveEpsilon { get; set; } = 0.005f;
    public float BasinAngleEpsilonDeg { get; set; } = 3f;
    public double RestSeconds { get; set; } = 0.3;
    public float SnapDistance { get; set; } = 1f;

    public int Count => _tracks.Count;
    public bool IsResting(NetId id) => _tracks.TryGetValue(id.Value, out var t) && t.Resting;

    /// <summary>Starts tracking (a spawn). <paramref name="resting"/> for bodies spawned asleep.</summary>
    public void Add(NetId id, Vec3 pos, Quat rot, double now, bool resting = false)
        => _tracks[id.Value] = new Track { RefPos = pos, LastPos = pos, RefRot = rot, LastMove = now, Resting = resting };

    public void Remove(NetId id) => _tracks.Remove(id.Value);

    /// <summary>Forces the body awake (a release or a host teleport) so the next rest is reported again.</summary>
    public void Wake(NetId id, double now)
    {
        if (_tracks.TryGetValue(id.Value, out var t)) { t.Resting = false; t.LastMove = now; }
    }

    public RestUpdate Update(NetId id, Vec3 pos, Quat rot, double now, bool basin = false, bool sleeping = false)
    {
        if (!_tracks.TryGetValue(id.Value, out var t))
        {
            Add(id, pos, rot, now);
            return new RestUpdate(true, false, true, false);
        }
        bool snap = Vec3.Distance(pos, t.LastPos) > SnapDistance;
        t.LastPos = pos;
        float eps = basin ? BasinMoveEpsilon : MoveEpsilon;
        float ang = basin ? BasinAngleEpsilonDeg : AngleEpsilonDeg;
        bool moved = Vec3.Distance(pos, t.RefPos) > eps || Quat.AngleDeg(rot, t.RefRot) > ang;
        if (moved && !sleeping)
        {
            t.RefPos = pos; t.RefRot = rot; t.LastMove = now;
            bool woke = t.Resting;
            t.Resting = false;
            return new RestUpdate(true, false, woke, snap);
        }
        if (t.Resting) return new RestUpdate(false, false, false, false);
        if (sleeping || now - t.LastMove >= RestSeconds)
        {
            t.Resting = true;
            t.RefPos = pos; t.RefRot = rot;
            return new RestUpdate(false, true, false, snap);
        }
        return new RestUpdate(true, false, false, snap);
    }
}
