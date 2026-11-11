namespace PotatoPleaseMP.Core.Testing;

/// <summary>Ground height under a point (the harness implements it with a physics raycast on the host).</summary>
public interface IGroundProbe
{
    float? GroundY(float x, float z);
}

/// <summary>
/// Walks a bot along waypoints at the game's speeds (spec 18 §2: <c>PlayerMove.moveSpeed</c> 3.5 m/s,
/// <c>sprintSpeed</c> 7.2 m/s), facing the direction of travel. A host teleport cancels the path.
/// </summary>
public sealed class BotWalker
{
    public const float WalkSpeed = 3.5f;
    public const float SprintSpeed = 7.2f;

    readonly Queue<Vec3> _path = new();

    public BotWalker(Vec3 start, float yawDeg = 0)
    {
        Position = start;
        YawDeg = yawDeg;
    }

    public Vec3 Position { get; private set; }
    public Vec3 Velocity { get; private set; }
    public float YawDeg { get; private set; }
    public float PitchDeg { get; set; }
    public bool Sprint { get; set; }
    public bool Crouch { get; set; }
    public IGroundProbe? Ground { get; set; }
    public bool Moving => _path.Count > 0;
    public int WaypointsLeft => _path.Count;
    public double DistanceWalked { get; private set; }
    /// <summary>Bumped on every teleport (PoseUpdate.teleportSeq, 2 bits on the wire).</summary>
    public byte TeleportSeq { get; private set; }

    public void SetPath(IEnumerable<Vec3> waypoints, bool sprint = false)
    {
        _path.Clear();
        foreach (var w in waypoints) _path.Enqueue(w);
        Sprint = sprint;
    }

    public void Stop()
    {
        _path.Clear();
        Velocity = Vec3.Zero;
    }

    /// <summary>The host moved us (respawn, ForceTeleport): jump there, forget the path.</summary>
    public void Teleport(Vec3 pos, float? yawDeg = null)
    {
        Stop();
        Position = pos;
        if (yawDeg is { } y) YawDeg = y;
        TeleportSeq = (byte)((TeleportSeq + 1) & 3);
    }

    public void Update(double dt)
    {
        if (dt <= 0) return;
        float budget = (float)(dt * (Sprint ? SprintSpeed : WalkSpeed));
        var start = Position;
        while (budget > 0 && _path.Count > 0)
        {
            var target = _path.Peek();
            var d = new Vec3(target.X - Position.X, 0, target.Z - Position.Z);
            float dist = d.Length;
            if (dist > 1e-4f) YawDeg = MathF.Atan2(d.X, d.Z) * (180f / MathF.PI); // Unity yaw: 0 = +Z, 90 = +X
            if (dist <= budget)
            {
                Position = target;
                budget -= dist;
                _path.Dequeue();
            }
            else
            {
                Position += d * (budget / dist);
                budget = 0;
            }
        }
        if (Ground?.GroundY(Position.X, Position.Z) is { } gy) Position = Position with { Y = gy };
        var moved = Position - start;
        DistanceWalked += new Vec3(moved.X, 0, moved.Z).Length;
        Velocity = moved / (float)dt;
        if (_path.Count == 0 && budget > 0) Velocity = Velocity with { X = 0, Z = 0 };
        if (YawDeg < 0) YawDeg += 360f;
    }

    /// <summary>Golden-angle spread around a point, so bots added one after another do not stand inside each other.</summary>
    public static Vec3 SpreadAround(Vec3 centre, int index, float spacing = 1.2f)
    {
        const double golden = 2.399963229728653; // radians
        double r = spacing * Math.Sqrt(index + 0.5);
        double a = index * golden;
        return new Vec3(centre.X + (float)(r * Math.Sin(a)), centre.Y, centre.Z + (float)(r * Math.Cos(a)));
    }
}
