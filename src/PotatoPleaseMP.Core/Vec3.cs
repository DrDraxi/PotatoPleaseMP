namespace PotatoPleaseMP.Core;

/// <summary>Unity-free 3D vector (world metres). The plugin converts to/from <c>UnityEngine.Vector3</c> at the edge.</summary>
public readonly record struct Vec3(float X, float Y, float Z)
{
    public static readonly Vec3 Zero = default;

    // Explicit: the record's generated ToString would print Normalized, which recurses forever.
    public override string ToString() => FormattableString.Invariant($"({X:0.###}, {Y:0.###}, {Z:0.###})");

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator /(Vec3 a, float s) => new(a.X / s, a.Y / s, a.Z / s);

    public float Length => MathF.Sqrt(X * X + Y * Y + Z * Z);
    public float LengthSquared => X * X + Y * Y + Z * Z;
    public Vec3 Normalized { get { float l = Length; return l > 1e-12f ? this / l : Zero; } }

    public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static float Distance(Vec3 a, Vec3 b) => (a - b).Length;
    public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
}

/// <summary>Unity-free quaternion (x, y, z, w), Unity's component order.</summary>
public readonly record struct Quat(float X, float Y, float Z, float W)
{
    public static readonly Quat Identity = new(0, 0, 0, 1);

    public override string ToString() => FormattableString.Invariant($"({X:0.####}, {Y:0.####}, {Z:0.####}, {W:0.####})");

    public static float Dot(Quat a, Quat b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

    public Quat Normalized
    {
        get
        {
            float l = MathF.Sqrt(X * X + Y * Y + Z * Z + W * W);
            return l > 1e-12f ? new Quat(X / l, Y / l, Z / l, W / l) : Identity;
        }
    }

    /// <summary>Angle in degrees between two unit rotations (q and -q are the same rotation).</summary>
    public static float AngleDeg(Quat a, Quat b)
    {
        // Double precision: acos near 1 in float has a ~0.05 degree floor, too coarse for codec error bounds.
        double la = Math.Sqrt((double)a.X * a.X + (double)a.Y * a.Y + (double)a.Z * a.Z + (double)a.W * a.W);
        double lb = Math.Sqrt((double)b.X * b.X + (double)b.Y * b.Y + (double)b.Z * b.Z + (double)b.W * b.W);
        double d = ((double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z + (double)a.W * b.W) / (la * lb);
        d = Math.Min(1.0, Math.Abs(d));
        return (float)(2.0 * Math.Acos(d) * (180.0 / Math.PI));
    }
}
