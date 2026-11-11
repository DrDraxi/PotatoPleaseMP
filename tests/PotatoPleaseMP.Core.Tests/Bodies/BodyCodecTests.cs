using PotatoPleaseMP.Core.Bodies;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Bodies;

public class BodyCodecTests
{
    static float MaxAxisErr(Vec3 a, Vec3 b) => Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));

    static Vec3 InBox(Random r) => new(
        (float)(BodyCodec.BoxMinX + r.NextDouble() * (BodyCodec.BoxMaxX - BodyCodec.BoxMinX)),
        (float)(BodyCodec.BoxMinY + r.NextDouble() * (BodyCodec.BoxMaxY - BodyCodec.BoxMinY)),
        (float)(BodyCodec.BoxMinZ + r.NextDouble() * (BodyCodec.BoxMaxZ - BodyCodec.BoxMinZ)));

    static Quat RandQuat(Random r)
    {
        // Uniform random rotation (Shoemake).
        double u1 = r.NextDouble(), u2 = r.NextDouble() * 2 * Math.PI, u3 = r.NextDouble() * 2 * Math.PI;
        double a = Math.Sqrt(1 - u1), b = Math.Sqrt(u1);
        return new Quat((float)(a * Math.Sin(u2)), (float)(a * Math.Cos(u2)), (float)(b * Math.Sin(u3)), (float)(b * Math.Cos(u3)));
    }

    [Fact]
    public void PosAbs_is_6_bytes_within_half_a_step_per_axis()
    {
        var rng = new Random(11);
        using var w = new NetWriter();
        // Steps: 128/65535 = 1.95 mm, 64/65535 = 0.98 mm, 160/65535 = 2.44 mm.
        float ex = 128f / 65535 / 2 + 1e-5f, ey = 64f / 65535 / 2 + 1e-5f, ez = 160f / 65535 / 2 + 1e-5f;
        for (int i = 0; i < 20_000; i++)
        {
            var p = InBox(rng);
            w.Reset();
            Assert.True(BodyCodec.WritePosAbs(w, p));
            Assert.Equal(6, w.Length);
            var r = new NetReader(w.Span);
            var q = BodyCodec.ReadPosAbs(ref r);
            Assert.True(Math.Abs(p.X - q.X) <= ex && Math.Abs(p.Y - q.Y) <= ey && Math.Abs(p.Z - q.Z) <= ez, $"{p} -> {q}");
            Assert.Equal(q, BodyCodec.QuantizeAbs(p));
        }
    }

    [Fact]
    public void PosAbs_clamps_outside_the_box_and_says_so()
    {
        using var w = new NetWriter();
        Assert.False(BodyCodec.WritePosAbs(w, new Vec3(100, 0, 0)));
        var r = new NetReader(w.Span);
        Assert.Equal(BodyCodec.BoxMaxX, BodyCodec.ReadPosAbs(ref r).X);
    }

    [Fact]
    public void PosRel_within_steps_and_FitsRel_marks_the_escape()
    {
        var rng = new Random(12);
        using var w = new NetWriter();
        var anchor = BodyCodec.QuantizeAnchor(new Vec3(3.217f, 1.5f, -7.77f));
        for (int i = 0; i < 20_000; i++)
        {
            var d = new Vec3((float)(rng.NextDouble() * 32.6 - 16.3), (float)(rng.NextDouble() * 16.3 - 8.15), (float)(rng.NextDouble() * 32.6 - 16.3));
            var p = anchor + d;
            Assert.True(BodyCodec.FitsRel(p, anchor));
            w.Reset();
            BodyCodec.WritePosRel(w, p, anchor);
            Assert.Equal(5, w.Length);
            var r = new NetReader(w.Span);
            var q = BodyCodec.ReadPosRel(ref r, anchor);
            Assert.True(Math.Abs(p.X - q.X) <= 0.001f + 1e-4f && Math.Abs(p.Z - q.Z) <= 0.001f + 1e-4f && Math.Abs(p.Y - q.Y) <= 0.002f + 1e-4f);
        }
        Assert.False(BodyCodec.FitsRel(anchor + new Vec3(16.5f, 0, 0), anchor));
        Assert.False(BodyCodec.FitsRel(anchor + new Vec3(0, -8.3f, 0), anchor));
    }

    [Fact]
    public void QuatS3_round_trip_within_0_2_degrees()
    {
        var rng = new Random(13);
        using var w = new NetWriter();
        float worst = 0;
        for (int i = 0; i < 20_000; i++)
        {
            var q = RandQuat(rng);
            w.Reset();
            BodyCodec.WriteRot(w, q);
            Assert.Equal(4, w.Length);
            var r = new NetReader(w.Span);
            worst = Math.Max(worst, Quat.AngleDeg(q, BodyCodec.ReadRot(ref r)));
        }
        Assert.True(worst <= 0.2f, $"worst {worst}°");
    }

    [Fact]
    public void Velocity_scales()
    {
        var rng = new Random(14);
        using var w = new NetWriter();
        for (int i = 0; i < 5000; i++)
        {
            var v = new Vec3((float)(rng.NextDouble() * 60 - 30), (float)(rng.NextDouble() * 60 - 30), (float)(rng.NextDouble() * 60 - 30));
            var av = new Vec3((float)(rng.NextDouble() * 60 - 30), (float)(rng.NextDouble() * 60 - 30), (float)(rng.NextDouble() * 60 - 30));
            w.Reset();
            BodyCodec.WriteVel(w, v); BodyCodec.WriteAngVel(w, av);
            Assert.Equal(9, w.Length);
            var r = new NetReader(w.Span);
            Assert.True(MaxAxisErr(v, BodyCodec.ReadVel(ref r)) <= 0.0025f + 1e-4f);
            Assert.True(MaxAxisErr(av, BodyCodec.ReadAngVel(ref r)) <= 0.125f + 1e-4f);
        }
    }

    [Fact]
    public void Auth_byte_round_trips_and_rejects_bad_modes()
    {
        foreach (BodyMode m in Enum.GetValues<BodyMode>())
            for (byte o = 0; o < 16; o++)
            {
                var (owner, mode, rest) = BodyCodec.UnpackAuth(BodyCodec.PackAuth(o, m, o % 2 == 0));
                Assert.Equal(o, owner); Assert.Equal(m, mode); Assert.Equal(o % 2 == 0, rest);
            }
        Assert.Throws<WireFormatException>(() => BodyCodec.UnpackAuth(0x70));
    }

    [Fact]
    public void Slerp_endpoints_and_shortest_path()
    {
        var a = Quat.Identity;
        var b = new Quat(0, MathF.Sin(0.5f), 0, MathF.Cos(0.5f)); // 57.3° about Y
        Assert.True(Quat.AngleDeg(BodyCodec.Slerp(a, b, 0), a) < 0.01f);
        Assert.True(Quat.AngleDeg(BodyCodec.Slerp(a, b, 1), b) < 0.01f);
        Assert.True(Math.Abs(Quat.AngleDeg(BodyCodec.Slerp(a, b, 0.5f), a) - 28.65f) < 0.05f);
        var nb = new Quat(-b.X, -b.Y, -b.Z, -b.W);
        Assert.True(Math.Abs(Quat.AngleDeg(BodyCodec.Slerp(a, nb, 0.5f), a) - 28.65f) < 0.05f);
    }
}
