namespace PotatoPleaseMP.Core.Wire;

/// <summary>
/// Quantisation helpers (spec 01 §4.2). Writers clamp out-of-range values and count it in <see cref="Clamps"/> (a dev
/// stat), except <c>QPos16</c>, which writes the "no value" sentinel instead (a pose 300+ m away is not a pose).
/// Error bounds are unit-tested.
/// </summary>
public static class Quant
{
    static long _clamps;

    /// <summary>Number of values clamped by any writer since start (dev stat).</summary>
    public static long Clamps => Interlocked.Read(ref _clamps);

    static void Clamped() => Interlocked.Increment(ref _clamps);

    // ---------------------------------------------------------------- positions

    /// <summary>Axis value meaning "no value" in <see cref="WritePos16"/>.</summary>
    public const short NoValue = short.MinValue;

    /// <summary>3 x I16 centimetres from <paramref name="origin"/> (±327.67 m). Returns false and writes the sentinel when out of range.</summary>
    public static bool WritePos16(NetWriter w, Vec3 p, Vec3 origin = default)
    {
        var d = p - origin;
        if (!TryCm16(d.X, out short x) || !TryCm16(d.Y, out short y) || !TryCm16(d.Z, out short z))
        {
            w.I16(NoValue); w.I16(NoValue); w.I16(NoValue);
            return false;
        }
        w.I16(x); w.I16(y); w.I16(z);
        return true;
    }

    /// <summary>Reads <see cref="WritePos16"/>; null for the sentinel.</summary>
    public static Vec3? ReadPos16(ref NetReader r, Vec3 origin = default)
    {
        short x = r.I16(), y = r.I16(), z = r.I16();
        if (x == NoValue || y == NoValue || z == NoValue) return null;
        return origin + new Vec3(x * 0.01f, y * 0.01f, z * 0.01f);
    }

    static bool TryCm16(float v, out short q)
    {
        q = 0;
        if (!float.IsFinite(v)) return false;
        double cm = Math.Round(v * 100.0);
        if (cm < -32767 || cm > 32767) return false;
        q = (short)cm;
        return true;
    }

    /// <summary>3 x I24 millimetres from <paramref name="origin"/> (±8.38 km).</summary>
    public static void WritePos24(NetWriter w, Vec3 p, Vec3 origin = default)
    {
        var d = p - origin;
        w.I24(ClampInt(d.X * 1000.0, 0x7FFFFF));
        w.I24(ClampInt(d.Y * 1000.0, 0x7FFFFF));
        w.I24(ClampInt(d.Z * 1000.0, 0x7FFFFF));
    }

    public static Vec3 ReadPos24(ref NetReader r, Vec3 origin = default)
        => origin + new Vec3(r.I24() * 0.001f, r.I24() * 0.001f, r.I24() * 0.001f);

    /// <summary>
    /// 5 bytes relative to a per-datagram anchor: x and z 14 bit at 2 mm (±16.38 m), y 12 bit at 4 mm (±8.19 m).
    /// Packed little-endian as x | z &lt;&lt; 14 | y &lt;&lt; 28.
    /// </summary>
    public static void WriteRel5(NetWriter w, Vec3 p, Vec3 anchor)
    {
        var d = p - anchor;
        int x = ClampInt(d.X / 0.002, 8191);
        int z = ClampInt(d.Z / 0.002, 8191);
        int y = ClampInt(d.Y / 0.004, 2047);
        ulong packed = ((ulong)(uint)x & 0x3FFF) | (((ulong)(uint)z & 0x3FFF) << 14) | (((ulong)(uint)y & 0xFFF) << 28);
        w.U32((uint)packed);
        w.U8((byte)(packed >> 32));
    }

    public static Vec3 ReadRel5(ref NetReader r, Vec3 anchor)
    {
        ulong packed = r.U32() | ((ulong)r.U8() << 32);
        int x = SignExtend((int)(packed & 0x3FFF), 14);
        int z = SignExtend((int)((packed >> 14) & 0x3FFF), 14);
        int y = SignExtend((int)((packed >> 28) & 0xFFF), 12);
        return anchor + new Vec3(x * 0.002f, y * 0.004f, z * 0.002f);
    }

    /// <summary>3 x I16 over ±<paramref name="halfExtent"/> (potato-local h = 0.16 m gives 4.9 µm steps).</summary>
    public static void WriteLocal16(NetWriter w, Vec3 p, float halfExtent)
    {
        w.I16((short)ClampInt(p.X / halfExtent * 32767.0, 32767));
        w.I16((short)ClampInt(p.Y / halfExtent * 32767.0, 32767));
        w.I16((short)ClampInt(p.Z / halfExtent * 32767.0, 32767));
    }

    public static Vec3 ReadLocal16(ref NetReader r, float halfExtent)
    {
        float s = halfExtent / 32767f;
        return new Vec3(r.I16() * s, r.I16() * s, r.I16() * s);
    }

    /// <summary>2 x I16 centimetres (x, z) from a floor rect minimum (mop strokes, sweeps).</summary>
    public static void WriteFloor16(NetWriter w, float x, float z, float minX, float minZ)
    {
        w.I16((short)ClampInt((x - minX) * 100.0, 32767));
        w.I16((short)ClampInt((z - minZ) * 100.0, 32767));
    }

    public static (float X, float Z) ReadFloor16(ref NetReader r, float minX, float minZ)
        => (minX + r.I16() * 0.01f, minZ + r.I16() * 0.01f);

    /// <summary>3 x I16 centimetres per second (±327 m/s).</summary>
    public static void WriteVel16(NetWriter w, Vec3 v)
    {
        w.I16((short)ClampInt(v.X * 100.0, 32767));
        w.I16((short)ClampInt(v.Y * 100.0, 32767));
        w.I16((short)ClampInt(v.Z * 100.0, 32767));
    }

    public static Vec3 ReadVel16(ref NetReader r) => new(r.I16() * 0.01f, r.I16() * 0.01f, r.I16() * 0.01f);

    // ---------------------------------------------------------------- rotations

    const float InvSqrt2 = 0.70710678f;
    const int S3Max = 511; // 10 bits hold -511..511 (offset 511), so 0 and ±1/√2 are exact

    /// <summary>
    /// Smallest-three quaternion in a U32: 2-bit index of the largest component, then the other three as 10-bit values
    /// over ±1/√2. The sign is flipped so the dropped component is >= 0, so q and -q give the same bits.
    /// </summary>
    public static uint PackQuat(Quat q)
    {
        q = q.Normalized;
        Span<float> c = stackalloc float[4] { q.X, q.Y, q.Z, q.W };
        int largest = 0;
        float best = MathF.Abs(c[0]);
        for (int i = 1; i < 4; i++)
        {
            float a = MathF.Abs(c[i]);
            if (a > best + 1e-7f) { best = a; largest = i; }
        }
        float sign = c[largest] < 0 ? -1f : 1f;
        Span<float> v = stackalloc float[3];
        for (int i = 0, k = 0; i < 4; i++)
            if (i != largest) v[k++] = Math.Clamp(c[i] * sign / InvSqrt2, -1f, 1f) * S3Max;
        // The dropped component is rebuilt from the other three, so its error adds to theirs. Rounding each one alone is
        // not the best choice: try floor/ceil of all three and keep the code whose decoded rotation is closest.
        uint bestBits = 0;
        double bestDot = double.NegativeInfinity;
        int f0 = (int)MathF.Floor(v[0]), f1 = (int)MathF.Floor(v[1]), f2 = (int)MathF.Floor(v[2]);
        for (int m = 0; m < 8; m++)
        {
            int a = Math.Clamp(f0 + (m & 1), -S3Max, S3Max);
            int b = Math.Clamp(f1 + ((m >> 1) & 1), -S3Max, S3Max);
            int d = Math.Clamp(f2 + ((m >> 2) & 1), -S3Max, S3Max);
            uint bits = ((uint)largest << 30) | ((uint)(a + S3Max) << 20) | ((uint)(b + S3Max) << 10) | (uint)(d + S3Max);
            var u = UnpackQuat(bits);
            double dot = Math.Abs((double)u.X * q.X + (double)u.Y * q.Y + (double)u.Z * q.Z + (double)u.W * q.W);
            if (dot > bestDot) { bestDot = dot; bestBits = bits; }
        }
        return bestBits;
    }

    public static Quat UnpackQuat(uint bits)
    {
        int largest = (int)(bits >> 30);
        Span<float> c = stackalloc float[4];
        int shift = 20;
        float sum = 0;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            int qv = (int)((bits >> shift) & 0x3FF);
            if (qv > 2 * S3Max) qv = 2 * S3Max; // 1023 is not produced by the writer; treat as the maximum
            float v = (qv - S3Max) / (float)S3Max * InvSqrt2;
            c[i] = v;
            sum += v * v;
            shift -= 10;
        }
        c[largest] = MathF.Sqrt(MathF.Max(0f, 1f - sum));
        return new Quat(c[0], c[1], c[2], c[3]).Normalized;
    }

    public static void WriteQuat(NetWriter w, Quat q) => w.U32(PackQuat(q));
    public static Quat ReadQuat(ref NetReader r) => UnpackQuat(r.U32());

    /// <summary>U16 yaw: degrees/360 x 65536, wrapping.</summary>
    public static ushort Yaw16(float yawDeg)
    {
        if (!float.IsFinite(yawDeg)) { Clamped(); return 0; }
        double turns = yawDeg / 360.0;
        turns -= Math.Floor(turns);
        return (ushort)((int)Math.Round(turns * 65536.0) & 0xFFFF);
    }

    public static float Yaw16ToDeg(ushort q) => q * (360f / 65536f);

    /// <summary>I16 pitch: clamp(±90)/90 x 32767.</summary>
    public static short Pitch16(float pitchDeg)
    {
        if (!float.IsFinite(pitchDeg)) { Clamped(); return 0; }
        if (pitchDeg < -90 || pitchDeg > 90) Clamped();
        return (short)Math.Round(Math.Clamp(pitchDeg, -90f, 90f) / 90.0 * 32767.0);
    }

    public static float Pitch16ToDeg(short q) => q / 32767f * 90f;

    // ---------------------------------------------------------------- unit vectors (octahedral)

    /// <summary>Unit vector as 2 x I8 octahedral (<= 0.9 degrees).</summary>
    public static void WriteOct16(NetWriter w, Vec3 n)
    {
        var (u, v) = OctEncode(n, 127);
        w.I8((sbyte)u); w.I8((sbyte)v);
    }

    public static Vec3 ReadOct16(ref NetReader r) => OctDecode(r.I8(), r.I8(), 127);

    /// <summary>Unit vector as 2 x I12 octahedral in 3 bytes (<= 0.06 degrees).</summary>
    public static void WriteOct24(NetWriter w, Vec3 n)
    {
        var (u, v) = OctEncode(n, 2047);
        w.U24(((uint)u & 0xFFF) | (((uint)v & 0xFFF) << 12));
    }

    public static Vec3 ReadOct24(ref NetReader r)
    {
        uint p = r.U24();
        return OctDecode(SignExtend((int)(p & 0xFFF), 12), SignExtend((int)(p >> 12), 12), 2047);
    }

    static (int U, int V) OctEncode(Vec3 n, int scale)
    {
        float l1 = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
        if (!(l1 > 1e-12f)) { Clamped(); return (0, scale); } // degenerate: +Y
        float x = n.X / l1, y = n.Y / l1;
        if (n.Z < 0)
        {
            float ox = (1 - MathF.Abs(y)) * SignNz(x);
            float oy = (1 - MathF.Abs(x)) * SignNz(y);
            x = ox; y = oy;
        }
        // "Precise" variant: try the four neighbouring grid points and keep the one closest to n.
        var target = n.Normalized;
        int bu = 0, bv = 0;
        float bestDot = float.NegativeInfinity;
        int fu = (int)MathF.Floor(x * scale), fv = (int)MathF.Floor(y * scale);
        for (int du = 0; du <= 1; du++)
            for (int dv = 0; dv <= 1; dv++)
            {
                int cu = Math.Clamp(fu + du, -scale, scale), cv = Math.Clamp(fv + dv, -scale, scale);
                float d = Vec3.Dot(OctDecode(cu, cv, scale), target);
                if (d > bestDot) { bestDot = d; bu = cu; bv = cv; }
            }
        return (bu, bv);
    }

    static Vec3 OctDecode(int qu, int qv, int scale)
    {
        float x = Math.Clamp(qu / (float)scale, -1f, 1f), y = Math.Clamp(qv / (float)scale, -1f, 1f);
        float z = 1 - MathF.Abs(x) - MathF.Abs(y);
        if (z < 0)
        {
            float ox = (1 - MathF.Abs(y)) * SignNz(x);
            float oy = (1 - MathF.Abs(x)) * SignNz(y);
            x = ox; y = oy;
        }
        return new Vec3(x, y, z).Normalized;
    }

    static float SignNz(float v) => v < 0 ? -1f : 1f;

    // ---------------------------------------------------------------- scalars

    public static byte UNorm8(float v) => (byte)ClampUInt(v * 255.0, 255);
    public static float UNorm8(byte q) => q / 255f;
    public static ushort UNorm16(float v) => (ushort)ClampUInt(v * 65535.0, 65535);
    public static float UNorm16(ushort q) => q / 65535f;
    public static sbyte SNorm8(float v) => (sbyte)ClampInt(v * 127.0, 127);
    public static float SNorm8(sbyte q) => Math.Max(-1f, q / 127f);
    public static short SNorm16(float v) => (short)ClampInt(v * 32767.0, 32767);
    public static float SNorm16(short q) => Math.Max(-1f, q / 32767f);

    /// <summary>Maps [min, max] onto 0..2^bits-1 (bits 1..32). Both endpoints round-trip exactly.</summary>
    public static uint Range(float v, float min, float max, int bits)
    {
        if (bits < 1 || bits > 32) throw new ArgumentOutOfRangeException(nameof(bits));
        if (!(max > min)) throw new ArgumentException("max must be greater than min.");
        uint top = bits == 32 ? uint.MaxValue : (1u << bits) - 1;
        if (!float.IsFinite(v)) { Clamped(); return 0; }
        if (v <= min) { if (v < min) Clamped(); return 0; }
        if (v >= max) { if (v > max) Clamped(); return top; }
        return (uint)Math.Round((v - (double)min) / ((double)max - min) * top);
    }

    public static float FromRange(uint q, float min, float max, int bits)
    {
        uint top = bits == 32 ? uint.MaxValue : (1u << bits) - 1;
        if (q >= top) return max;
        if (q == 0) return min;
        return (float)(min + q / (double)top * ((double)max - min));
    }

    // ---------------------------------------------------------------- helpers

    static int ClampInt(double v, int limit)
    {
        if (double.IsNaN(v)) { Clamped(); return 0; }
        double r = Math.Round(v);
        if (r > limit) { Clamped(); return limit; }
        if (r < -limit) { Clamped(); return -limit; }
        return (int)r;
    }

    static uint ClampUInt(double v, uint limit)
    {
        if (double.IsNaN(v)) { Clamped(); return 0; }
        double r = Math.Round(v);
        if (r > limit) { Clamped(); return limit; }
        if (r < 0) { Clamped(); return 0; }
        return (uint)r;
    }

    static int SignExtend(int v, int bits)
    {
        int shift = 32 - bits;
        return (v << shift) >> shift;
    }
}
