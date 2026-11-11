using System.Text;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Wire;

public class BitsAndCompressTests
{
    [Fact]
    public void SortedIndexCodec_round_trip_and_size()
    {
        using var w = new NetWriter();
        var idx = new List<int> { 0, 3, 4, 1000, 105197 };
        SortedIndexCodec.Write(w, idx);
        var r = new NetReader(w.Span);
        Assert.Equal(idx, SortedIndexCodec.Read(ref r, 10, 105198));

        Assert.Throws<ArgumentException>(() => SortedIndexCodec.Write(new NetWriter(), new[] { 3, 2 }));
        Assert.Throws<ArgumentException>(() => SortedIndexCodec.Write(new NetWriter(), new[] { 3, 3 }));

        var rng = new Random(7);
        var set = new SortedSet<int>();
        while (set.Count < 1000) set.Add(rng.Next(105198));
        w.Reset();
        SortedIndexCodec.Write(w, set.ToList());
        Assert.True(w.Length / 1000.0 <= 1.8, $"{w.Length / 1000.0} B/idx");
        r = new NetReader(w.Span);
        Assert.Equal(set.ToList(), SortedIndexCodec.Read(ref r, 2000, 105198));
        Assert.Throws<WireFormatException>(() => { var rr = new NetReader(w.Span); SortedIndexCodec.Read(ref rr, 2000, 50000); });
    }

    static Bitset RoundTrip(Bitset b, out int size)
    {
        using var w = new NetWriter();
        BitsetCodec.Write(w, b);
        size = w.Length;
        var r = new NetReader(w.Span);
        var back = BitsetCodec.Read(ref r);
        Assert.True(r.IsEnd);
        return back;
    }

    [Fact]
    public void BitsetCodec_forms()
    {
        var empty = new Bitset(105_216);
        Assert.Equal(empty, RoundTrip(empty, out int s1));
        Assert.True(s1 <= 4, $"empty = {s1} B");

        var ones = new Bitset(105_216);
        ones.SetAll(true);
        Assert.Equal(ones, RoundTrip(ones, out int s2));
        Assert.True(s2 <= 6);

        var alt = new Bitset(10_000);
        for (int i = 0; i < alt.Count; i += 2) alt[i] = true;
        Assert.Equal(alt, RoundTrip(alt, out int s3));
        Assert.Equal(NetWriter.VarU32Size(10_000 << 1) + 1250, s3); // raw fallback

        var sparse = new Bitset(105_216);
        foreach (var i in new[] { 5, 6, 7, 900, 105_215 }) sparse[i] = true;
        Assert.Equal(sparse, RoundTrip(sparse, out _));
        Assert.Equal(5, sparse.PopCount());

        // Diff rebuilds the target.
        var a = sparse.Clone();
        var b = sparse.Clone();
        b[5] = false; b[42] = true; b[100_000] = true;
        using var w = new NetWriter();
        BitsetCodec.WriteDiff(w, a, b);
        var r = new NetReader(w.Span);
        BitsetCodec.ApplyDiff(ref r, a);
        Assert.Equal(b, a);
    }

    [Fact]
    public void ByteRle_with_deflate_on_synthetic_frost()
    {
        var frost = new byte[105_216];
        var rng = new Random(8);
        for (int i = 0; i < frost.Length; i++) frost[i] = (byte)(i % 3000 < 400 ? rng.Next(200, 256) : 0);
        using var w = new NetWriter();
        ByteRle.Write(w, frost);
        var packed = Compress.Deflate(w.Span);
        Assert.True(packed.Length < frost.Length / 4);
        var raw = Compress.Inflate(packed, 1 << 20);
        var r = new NetReader(raw);
        Assert.Equal(frost, ByteRle.Read(ref r, frost.Length));
        Assert.Throws<WireFormatException>(() => { var rr = new NetReader(raw); ByteRle.Read(ref rr, 100); });
    }

    [Fact]
    public void BitWriter_round_trip()
    {
        using var w = new NetWriter();
        var bw = new BitWriter(w);
        bw.Write(5, 3); bw.WriteBool(true); bw.Write(0xABCDE, 20); bw.Write(uint.MaxValue, 32);
        bw.Flush();
        var br = new BitReader(w.Span);
        Assert.Equal(5u, br.Read(3));
        Assert.True(br.ReadBool());
        Assert.Equal(0xABCDEu, br.Read(20));
        Assert.Equal(uint.MaxValue, br.Read(32));
        Assert.Throws<WireFormatException>(() => { var b2 = new BitReader(new byte[] { 1 }); b2.Read(9); });
    }

    [Fact]
    public void Compress_round_trips_and_caps()
    {
        var data = TestData.SaveLike(200_000, 9);
        foreach (var (pack, unpack) in new (Func<byte[], byte[]>, Func<byte[], int, byte[]>)[]
                 { (d => Compress.Deflate(d), (p, m) => Compress.Inflate(p, m)), (d => Compress.Brotli(d), (p, m) => Compress.Unbrotli(p, m)) })
        {
            var p = pack(data);
            Assert.True(p.Length < data.Length);
            Assert.Equal(data, unpack(p, data.Length));
            Assert.Throws<WireFormatException>(() => unpack(p, data.Length - 1));
        }
        // Garbage only ever surfaces as WireFormatException.
        var rng = new Random(10);
        for (int i = 0; i < 200; i++)
        {
            var junk = TestData.Random(rng.Next(1, 500), i);
            try { Compress.Inflate(junk, 1 << 16); } catch (WireFormatException) { }
            try { Compress.Unbrotli(junk, 1 << 16); } catch (WireFormatException) { }
        }
    }

    [Fact]
    public void Crc32_check_value()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
        var a = Encoding.ASCII.GetBytes("1234");
        var b = Encoding.ASCII.GetBytes("56789");
        Assert.Equal(0xCBF43926u, Crc32.Append(Crc32.Compute(a), b));
        Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));
    }
}
