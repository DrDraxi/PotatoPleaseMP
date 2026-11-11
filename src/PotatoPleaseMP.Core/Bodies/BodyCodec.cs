using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Bodies;

/// <summary>
/// Field codecs of the body stream (spec 07 §5). Positions, rotations and the relative form reuse <see cref="Quant"/>;
/// this class adds the level box, the body velocity scales and the auth byte.
/// <list type="bullet">
/// <item><c>PosAbs</c>: 3 x u16 over the level box x[-64,64] y[-16,48] z[-64,96] (1.95 / 0.98 / 2.44 mm steps).</item>
/// <item><c>PosRel</c>: <see cref="Quant.WriteRel5"/> (5 B, x/z ±16.38 m @ 2 mm, y ±8.19 m @ 4 mm).</item>
/// <item><c>QuatS3</c>: <see cref="Quant.WriteQuat"/> (smallest three, 2 + 3 x 10 bit).</item>
/// <item><c>Vel16</c>: 3 x i16 at 0.5 cm/s (±163 m/s). <c>AngVel8</c>: 3 x i8 at 0.25 rad/s (±31.75 rad/s).</item>
/// </list>
/// </summary>
public static class BodyCodec
{
    public const float BoxMinX = -64, BoxMaxX = 64, BoxMinY = -16, BoxMaxY = 48, BoxMinZ = -64, BoxMaxZ = 96;
    public const int PosAbsSize = 6, PosRelSize = 5, RotSize = 4, VelSize = 6, AngVelSize = 3;

    /// <summary>Largest relative offset per axis (x/z, y) that <see cref="Quant.WriteRel5"/> holds exactly.</summary>
    public const float RelMaxXZ = 8191 * 0.002f, RelMaxY = 2047 * 0.004f;

    public const float VelStep = 0.005f, AngVelStep = 0.25f;

    /// <summary>Host owner nibble in the auth byte.</summary>
    public const byte HostOwner = 15;

    public static bool InLevelBox(Vec3 p) =>
        p.X >= BoxMinX && p.X <= BoxMaxX && p.Y >= BoxMinY && p.Y <= BoxMaxY && p.Z >= BoxMinZ && p.Z <= BoxMaxZ;

    public static Vec3 ClampToBox(Vec3 p) => new(
        Math.Clamp(Finite(p.X), BoxMinX, BoxMaxX), Math.Clamp(Finite(p.Y), BoxMinY, BoxMaxY), Math.Clamp(Finite(p.Z), BoxMinZ, BoxMaxZ));

    static float Finite(float v) => float.IsFinite(v) ? v : 0f;

    // ---------------------------------------------------------------- positions

    /// <summary>Writes the level-box form (clamped). Returns false when <paramref name="p"/> was outside the box.</summary>
    public static bool WritePosAbs(NetWriter w, Vec3 p)
    {
        w.U16((ushort)Quant.Range(p.X, BoxMinX, BoxMaxX, 16));
        w.U16((ushort)Quant.Range(p.Y, BoxMinY, BoxMaxY, 16));
        w.U16((ushort)Quant.Range(p.Z, BoxMinZ, BoxMaxZ, 16));
        return InLevelBox(p);
    }

    public static Vec3 ReadPosAbs(ref NetReader r) => new(
        Quant.FromRange(r.U16(), BoxMinX, BoxMaxX, 16), Quant.FromRange(r.U16(), BoxMinY, BoxMaxY, 16),
        Quant.FromRange(r.U16(), BoxMinZ, BoxMaxZ, 16));

    /// <summary>The pose a receiver decodes from <see cref="WritePosAbs"/> (host keeps it as the "last sent" pose).</summary>
    public static Vec3 QuantizeAbs(Vec3 p) => new(
        Quant.FromRange(Quant.Range(p.X, BoxMinX, BoxMaxX, 16), BoxMinX, BoxMaxX, 16),
        Quant.FromRange(Quant.Range(p.Y, BoxMinY, BoxMaxY, 16), BoxMinY, BoxMaxY, 16),
        Quant.FromRange(Quant.Range(p.Z, BoxMinZ, BoxMaxZ, 16), BoxMinZ, BoxMaxZ, 16));

    /// <summary>True when <paramref name="p"/> fits the 5-byte relative form around <paramref name="anchor"/>.</summary>
    public static bool FitsRel(Vec3 p, Vec3 anchor)
    {
        var d = p - anchor;
        return MathF.Abs(d.X) <= RelMaxXZ && MathF.Abs(d.Z) <= RelMaxXZ && MathF.Abs(d.Y) <= RelMaxY;
    }

    public static void WritePosRel(NetWriter w, Vec3 p, Vec3 anchor) => Quant.WriteRel5(w, p, anchor);
    public static Vec3 ReadPosRel(ref NetReader r, Vec3 anchor) => Quant.ReadRel5(ref r, anchor);

    /// <summary>Anchor as 3 x i16 cm (the <c>BodyState</c> header). Writers use <see cref="QuantizeAnchor"/> for offsets.</summary>
    public static void WriteAnchor(NetWriter w, Vec3 anchor)
    {
        var q = QuantizeAnchor(anchor);
        w.I16((short)MathF.Round(q.X * 100f)); w.I16((short)MathF.Round(q.Y * 100f)); w.I16((short)MathF.Round(q.Z * 100f));
    }

    public static Vec3 ReadAnchor(ref NetReader r) => new(r.I16() * 0.01f, r.I16() * 0.01f, r.I16() * 0.01f);

    public static Vec3 QuantizeAnchor(Vec3 a) => new(Cm(a.X) * 0.01f, Cm(a.Y) * 0.01f, Cm(a.Z) * 0.01f);
    static int Cm(float v) => float.IsFinite(v) ? (int)Math.Clamp(MathF.Round(v * 100f), -32767, 32767) : 0;

    // ---------------------------------------------------------------- rotations

    public static void WriteRot(NetWriter w, Quat q) => Quant.WriteQuat(w, q);
    public static Quat ReadRot(ref NetReader r) => Quant.ReadQuat(ref r);
    public static Quat QuantizeRot(Quat q) => Quant.UnpackQuat(Quant.PackQuat(q));

    /// <summary>Float pose fallback (out of the level box): 3 x f32 + 4 x f32 = 28 B.</summary>
    public static void WritePoseF32(NetWriter w, Vec3 p, Quat q)
    {
        w.F32(Finite(p.X)); w.F32(Finite(p.Y)); w.F32(Finite(p.Z));
        var n = q.Normalized;
        w.F32(n.X); w.F32(n.Y); w.F32(n.Z); w.F32(n.W);
    }

    public static (Vec3 Pos, Quat Rot) ReadPoseF32(ref NetReader r)
        => (new Vec3(r.F32(), r.F32(), r.F32()), new Quat(r.F32(), r.F32(), r.F32(), r.F32()).Normalized);

    // ---------------------------------------------------------------- velocities

    public static void WriteVel(NetWriter w, Vec3 v)
    {
        w.I16(Q16(v.X / VelStep)); w.I16(Q16(v.Y / VelStep)); w.I16(Q16(v.Z / VelStep));
    }

    public static Vec3 ReadVel(ref NetReader r) => new(r.I16() * VelStep, r.I16() * VelStep, r.I16() * VelStep);

    public static void WriteAngVel(NetWriter w, Vec3 v)
    {
        w.I8(Q8(v.X / AngVelStep)); w.I8(Q8(v.Y / AngVelStep)); w.I8(Q8(v.Z / AngVelStep));
    }

    public static Vec3 ReadAngVel(ref NetReader r) => new(r.I8() * AngVelStep, r.I8() * AngVelStep, r.I8() * AngVelStep);

    static short Q16(float v) => float.IsFinite(v) ? (short)Math.Clamp(MathF.Round(v), -32767, 32767) : (short)0;
    static sbyte Q8(float v) => float.IsFinite(v) ? (sbyte)Math.Clamp(MathF.Round(v), -127, 127) : (sbyte)0;

    // ---------------------------------------------------------------- scalars

    public static void WriteHalf(NetWriter w, float v) => w.F16(float.IsFinite(v) ? Math.Clamp(v, -65504f, 65504f) : 0f);
    public static float ReadHalf(ref NetReader r) => r.F16();

    /// <summary>Auth byte: owner in bits 0-3 (15 = host), mode in bits 4-6, bit 7 resting.</summary>
    public static byte PackAuth(byte owner, BodyMode mode, bool resting = false)
        => (byte)((owner & 0xF) | (((byte)mode & 0x7) << 4) | (resting ? 0x80 : 0));

    public static (byte Owner, BodyMode Mode, bool Resting) UnpackAuth(byte b)
    {
        int mode = (b >> 4) & 0x7;
        if (mode > (int)BodyMode.PeelHeld) throw new WireFormatException($"Bad body mode {mode}.");
        return ((byte)(b & 0xF), (BodyMode)mode, (b & 0x80) != 0);
    }

    // ---------------------------------------------------------------- quaternion helpers

    /// <summary>Shortest-path spherical interpolation.</summary>
    public static Quat Slerp(Quat a, Quat b, float t)
    {
        float dot = Quat.Dot(a, b);
        if (dot < 0) { b = new Quat(-b.X, -b.Y, -b.Z, -b.W); dot = -dot; }
        if (dot > 0.9995f)
            return new Quat(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t).Normalized;
        float theta = MathF.Acos(Math.Clamp(dot, -1f, 1f));
        float s = MathF.Sin(theta);
        float wa = MathF.Sin((1 - t) * theta) / s, wb = MathF.Sin(t * theta) / s;
        return new Quat(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb, a.W * wa + b.W * wb).Normalized;
    }
}

/// <summary>Body mode in the auth byte (spec 07 §4). Rest is a separate flag.</summary>
public enum BodyMode : byte { HostSim = 0, ClientOwned = 1, Contained = 2, PeelHeld = 3 }
