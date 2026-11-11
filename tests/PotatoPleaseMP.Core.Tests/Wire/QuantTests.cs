using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Wire;

public class QuantTests
{
    static Vec3 RandVec(Random r, float range) => new((float)(r.NextDouble() * 2 - 1) * range, (float)(r.NextDouble() * 2 - 1) * range, (float)(r.NextDouble() * 2 - 1) * range);

    static float MaxAxisErr(Vec3 a, Vec3 b) => Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));

    [Fact]
    public void Pos16_within_5mm_and_sentinel_out_of_range()
    {
        var rng = new Random(1);
        using var w = new NetWriter();
        var origin = new Vec3(1, 2, 3);
        for (int i = 0; i < 10_000; i++)
        {
            var p = origin + RandVec(rng, 300);
            w.Reset();
            Assert.True(Quant.WritePos16(w, p, origin));
            var r = new NetReader(w.Span);
            var q = Quant.ReadPos16(ref r, origin);
            Assert.NotNull(q);
            Assert.True(MaxAxisErr(p, q!.Value) <= 0.005f + 1e-4f);
        }
        w.Reset();
        Assert.False(Quant.WritePos16(w, new Vec3(327.7f, 0, 0)));
        var rr = new NetReader(w.Span);
        Assert.Null(Quant.ReadPos16(ref rr));
    }

    [Fact]
    public void Pos24_within_half_mm()
    {
        var rng = new Random(2);
        using var w = new NetWriter();
        for (int i = 0; i < 10_000; i++)
        {
            var p = RandVec(rng, 2000);
            w.Reset();
            Quant.WritePos24(w, p);
            var r = new NetReader(w.Span);
            // float has ~0.12 mm resolution at 2 km; the codec itself is 0.5 mm.
            Assert.True(MaxAxisErr(p, Quant.ReadPos24(ref r)) <= 0.0005f + 0.0003f);
        }
    }

    [Fact]
    public void Rel5_is_5_bytes_and_within_its_steps()
    {
        var rng = new Random(3);
        using var w = new NetWriter();
        var anchor = new Vec3(10, 5, -3);
        for (int i = 0; i < 10_000; i++)
        {
            var d = new Vec3((float)(rng.NextDouble() * 32 - 16), (float)(rng.NextDouble() * 16 - 8), (float)(rng.NextDouble() * 32 - 16));
            w.Reset();
            Quant.WriteRel5(w, anchor + d, anchor);
            Assert.Equal(5, w.Length);
            var r = new NetReader(w.Span);
            var q = Quant.ReadRel5(ref r, anchor);
            var e = q - (anchor + d);
            Assert.True(Math.Abs(e.X) <= 0.001f + 1e-5f && Math.Abs(e.Z) <= 0.001f + 1e-5f, $"xz {e}");
            Assert.True(Math.Abs(e.Y) <= 0.002f + 1e-5f, $"y {e}");
        }
    }

    [Fact]
    public void Local16_within_2_5_micrometres()
    {
        var rng = new Random(4);
        using var w = new NetWriter();
        for (int i = 0; i < 10_000; i++)
        {
            var p = RandVec(rng, 0.16f);
            w.Reset();
            Quant.WriteLocal16(w, p, 0.16f);
            var r = new NetReader(w.Span);
            Assert.True(MaxAxisErr(p, Quant.ReadLocal16(ref r, 0.16f)) <= 2.5e-6f);
        }
    }

    static Quat RandQuat(Random r)
    {
        // Uniform random rotation (Shoemake).
        double u1 = r.NextDouble(), u2 = r.NextDouble() * 2 * Math.PI, u3 = r.NextDouble() * 2 * Math.PI;
        double a = Math.Sqrt(1 - u1), b = Math.Sqrt(u1);
        return new Quat((float)(a * Math.Sin(u2)), (float)(a * Math.Cos(u2)), (float)(b * Math.Sin(u3)), (float)(b * Math.Cos(u3)));
    }

    [Fact]
    public void QuatS3_error_bound_sign_and_special_cases()
    {
        var rng = new Random(5);
        float worst = 0;
        for (int i = 0; i < 10_000; i++)
        {
            var q = RandQuat(rng);
            uint bits = Quant.PackQuat(q);
            Assert.Equal(bits, Quant.PackQuat(new Quat(-q.X, -q.Y, -q.Z, -q.W)));
            worst = Math.Max(worst, Quat.AngleDeg(q, Quant.UnpackQuat(bits)));
        }
        Assert.True(worst <= 0.15f, $"worst {worst}");

        uint id = Quant.PackQuat(Quat.Identity);
        var back = Quant.UnpackQuat(id);
        Assert.Equal(Quat.Identity, back);
        Assert.Equal(id, Quant.PackQuat(back));

        float s = MathF.Sqrt(0.5f);
        var half = new Quat(s, 0, 0, s);
        var hb = Quant.UnpackQuat(Quant.PackQuat(half));
        Assert.True(Math.Abs(hb.X * hb.X + hb.Y * hb.Y + hb.Z * hb.Z + hb.W * hb.W - 1) < 1e-5);
        Assert.True(Quat.AngleDeg(half, hb) < 0.15f);
    }

    [Fact]
    public void Yaw_and_pitch()
    {
        Assert.Equal(0, Quant.Yaw16(0));
        Assert.Equal(0, Quant.Yaw16(360));
        Assert.Equal(Quant.Yaw16(-90), Quant.Yaw16(270));
        Assert.True(Math.Abs(Quant.Yaw16ToDeg(Quant.Yaw16(359.999f)) - 360) < 0.01f || Quant.Yaw16(359.999f) == 0);
        Assert.True(Math.Abs(Quant.Yaw16ToDeg(Quant.Yaw16(123.456f)) - 123.456f) <= 0.0055f);
        Assert.Equal(32767, Quant.Pitch16(120));
        Assert.Equal(-32767, Quant.Pitch16(-91));
        Assert.Equal(90f, Quant.Pitch16ToDeg(Quant.Pitch16(90)));
    }

    [Fact]
    public void Octahedral_error_bounds()
    {
        var rng = new Random(6);
        double worst16 = 0, worst24 = 0;
        using var w = new NetWriter();
        for (int i = 0; i < 10_000; i++)
        {
            var n = RandVec(rng, 1).Normalized;
            if (n.LengthSquared < 0.5f) continue;
            w.Reset();
            Quant.WriteOct16(w, n);
            Quant.WriteOct24(w, n);
            Assert.Equal(5, w.Length);
            var r = new NetReader(w.Span);
            var a = Quant.ReadOct16(ref r);
            var b = Quant.ReadOct24(ref r);
            worst16 = Math.Max(worst16, AngleDeg(n, a));
            worst24 = Math.Max(worst24, AngleDeg(n, b));
        }
        Assert.True(worst16 <= 0.9, $"oct16 worst {worst16}");
        Assert.True(worst24 <= 0.06, $"oct24 worst {worst24}");
    }

    static double AngleDeg(Vec3 a, Vec3 b)
    {
        double d = Math.Clamp((double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z, -1, 1);
        return Math.Acos(d) * 180 / Math.PI;
    }

    [Fact]
    public void Norms_and_ranges_have_exact_endpoints()
    {
        Assert.Equal(0f, Quant.UNorm8(Quant.UNorm8(0f)));
        Assert.Equal(1f, Quant.UNorm8(Quant.UNorm8(1f)));
        Assert.Equal(1f, Quant.UNorm16(Quant.UNorm16(1f)));
        Assert.Equal(-1f, Quant.SNorm8(Quant.SNorm8(-1f)));
        Assert.Equal(1f, Quant.SNorm8(Quant.SNorm8(1f)));
        Assert.Equal(-1f, Quant.SNorm16(Quant.SNorm16(-1f)));
        foreach (int bits in new[] { 4, 10, 16, 32 })
        {
            Assert.Equal(-0.3f, Quant.FromRange(Quant.Range(-0.3f, -0.3f, 0.7f, bits), -0.3f, 0.7f, bits));
            Assert.Equal(0.7f, Quant.FromRange(Quant.Range(0.7f, -0.3f, 0.7f, bits), -0.3f, 0.7f, bits));
            Assert.Equal(0.7f, Quant.FromRange(Quant.Range(5f, -0.3f, 0.7f, bits), -0.3f, 0.7f, bits)); // clamped
        }
        Assert.True(Math.Abs(Quant.FromRange(Quant.Range(0.2f, -0.3f, 0.7f, 10), -0.3f, 0.7f, 10) - 0.2f) <= 0.5f / 1023 + 1e-6f);
    }
}
