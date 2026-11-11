using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Wire;

public class SerializerTests
{
    static byte[] W(Action<NetWriter> f)
    {
        using var w = new NetWriter();
        f(w);
        return w.ToArray();
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(127u, 1)]
    [InlineData(128u, 2)]
    [InlineData(16383u, 2)]
    [InlineData(16384u, 3)]
    [InlineData(uint.MaxValue, 5)]
    public void VarU32_sizes_and_round_trip(uint v, int size)
    {
        var b = W(w => w.VarU32(v));
        Assert.Equal(size, b.Length);
        Assert.Equal(size, NetWriter.VarU32Size(v));
        var r = new NetReader(b);
        Assert.Equal(v, r.VarU32());
        Assert.True(r.IsEnd);
    }

    [Fact]
    public void VarU64_max_is_10_bytes()
    {
        var b = W(w => w.VarU64(ulong.MaxValue));
        Assert.Equal(10, b.Length);
        var r = new NetReader(b);
        Assert.Equal(ulong.MaxValue, r.VarU64());
    }

    [Fact]
    public void Zigzag()
    {
        Assert.Equal(new byte[] { 0x01 }, W(w => w.VarI32(-1)));
        Assert.Equal(new byte[] { 0x02 }, W(w => w.VarI32(1)));
        foreach (var v in new[] { int.MinValue, int.MaxValue, -1, 0, 63, -64, 1000000 })
        {
            var r = new NetReader(W(w => w.VarI32(v)));
            Assert.Equal(v, r.VarI32());
        }
        foreach (var v in new[] { long.MinValue, long.MaxValue, -1L })
        {
            var r = new NetReader(W(w => w.VarI64(v)));
            Assert.Equal(v, r.VarI64());
        }
    }

    [Fact]
    public void Truncated_and_overlong_varints_throw()
    {
        Assert.Throws<WireFormatException>(() => { var r = new NetReader(new byte[] { 0x80, 0x80 }); r.VarU32(); });
        Assert.Throws<WireFormatException>(() => { var r = new NetReader(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 }); r.VarU32(); });
        Assert.Throws<WireFormatException>(() => { var r = new NetReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x1F }); r.VarU32(); }); // > 32 bits
        Assert.Throws<WireFormatException>(() => { var r = new NetReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02 }); r.VarU64(); });
    }

    [Fact]
    public void Fixed_width_little_endian()
    {
        Assert.Equal(new byte[] { 0x34, 0x12 }, W(w => w.U16(0x1234)));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, W(w => w.I24(-1)));
        var r = new NetReader(new byte[] { 0xFF, 0xFF, 0xFF });
        Assert.Equal(-1, r.I24());
        r = new NetReader(W(w => w.I24(-8388608)));
        Assert.Equal(-8388608, r.I24());
        r = new NetReader(W(w => w.U24(0xABCDEF)));
        Assert.Equal(0xABCDEFu, r.U24());
        Assert.Throws<ArgumentOutOfRangeException>(() => W(w => w.I24(8388608)));
        r = new NetReader(W(w => { w.U64(0x0102030405060708); w.I64(-5); w.F64(Math.PI); w.F32(1.5f); }));
        Assert.Equal(0x0102030405060708UL, r.U64());
        Assert.Equal(-5, r.I64());
        Assert.Equal(Math.PI, r.F64());
        Assert.Equal(1.5f, r.F32());
    }

    [Fact]
    public void F16_including_NaN()
    {
        var b = W(w => { w.F16((Half)1.5f); w.F16(Half.NaN); w.F16((Half)(-65504f)); });
        var r = new NetReader(b);
        Assert.Equal(1.5f, r.F16());
        var raw = new NetReader(b.AsSpan(2).ToArray());
        Assert.True(Half.IsNaN(raw.F16Raw()));
        var strict = b.AsSpan(2).ToArray();
        Assert.Throws<WireFormatException>(() => { var rr = new NetReader(strict); rr.F16(); });
        Assert.Throws<WireFormatException>(() => { var rr = new NetReader(W(w => w.F32(float.PositiveInfinity))); rr.F32(); });
    }

    [Fact]
    public void Str8_utf8_and_limits()
    {
        var b = W(w => w.Str8("Пётр"));
        Assert.Equal(1 + 8, b.Length);
        var r = new NetReader(b);
        Assert.Equal("Пётр", r.Str8());
        Assert.Throws<ArgumentException>(() => W(w => w.Str8(new string('x', 256))));
        // A length past the buffer throws before allocating.
        Assert.Throws<WireFormatException>(() => { var rr = new NetReader(new byte[] { 200, 65, 66 }); rr.Str8(); });
        Assert.Throws<WireFormatException>(() => { var rr = new NetReader(W(w => w.StrV("hello"))); rr.StrV(3); });
        Assert.Throws<WireFormatException>(() => { var rr = new NetReader(new byte[] { 2, 0xC3, 0x28 }); rr.Str8(); }); // invalid UTF-8
    }

    [Fact]
    public void Count_is_checked_against_remaining_bytes()
    {
        var b = W(w => { w.VarU32(1000); w.U8(1); });
        Assert.Throws<WireFormatException>(() => { var r = new NetReader(b); r.Count(5000, 2); });
        Assert.Throws<WireFormatException>(() => { var r = new NetReader(b); r.Count(10); });
    }

    [Fact]
    public void PatchVarLen_inserts_the_length()
    {
        using var w = new NetWriter();
        w.U8(9);
        int m = w.Mark();
        for (int i = 0; i < 200; i++) w.U8((byte)i);
        w.PatchVarLen(m);
        var r = new NetReader(w.Span);
        Assert.Equal(9, r.U8());
        Assert.Equal(200, r.Count(1000));
        Assert.Equal(199, r.Raw(200)[199]);
    }

    [Fact]
    public void Bool_rejects_other_bytes()
    {
        Assert.Throws<WireFormatException>(() => { var r = new NetReader(new byte[] { 2 }); r.Bool(); });
    }
}
