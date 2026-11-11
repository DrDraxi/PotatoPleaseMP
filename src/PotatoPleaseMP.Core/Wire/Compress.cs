using System.IO.Compression;

namespace PotatoPleaseMP.Core.Wire;

/// <summary>
/// Deflate (live data, envelopes) and Brotli (join blobs) with an output cap on every inflate (spec 01 §4.3). Bad
/// input of any kind surfaces as <see cref="WireFormatException"/> only. Thread-safe (no shared state): Brotli for a
/// join blob is meant to run on a worker.
/// </summary>
public static class Compress
{
    public const int BrotliQuality = 4;
    const int BrotliWindow = 22;

    public static byte[] Deflate(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Fastest)
    {
        using var ms = new MemoryStream(data.Length / 2 + 64);
        using (var ds = new DeflateStream(ms, level, leaveOpen: true)) ds.Write(data);
        return ms.ToArray();
    }

    /// <summary>Inflates at most <paramref name="maxOut"/> bytes; more, or corrupt input, throws.</summary>
    public static byte[] Inflate(ReadOnlySpan<byte> packed, int maxOut)
    {
        try
        {
            using var input = new MemoryStream(packed.ToArray(), writable: false);
            using var ds = new DeflateStream(input, CompressionMode.Decompress);
            return ReadCapped(ds, maxOut);
        }
        catch (WireFormatException) { throw; }
        catch (Exception e) { throw new WireFormatException("Corrupt deflate data: " + e.Message, e); }
    }

    public static byte[] Brotli(ReadOnlySpan<byte> data, int quality = BrotliQuality)
    {
        var dest = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        if (!BrotliEncoder.TryCompress(data, dest, out int written, quality, BrotliWindow))
            throw new InvalidOperationException("Brotli compression failed.");
        Array.Resize(ref dest, written);
        return dest;
    }

    public static byte[] Unbrotli(ReadOnlySpan<byte> packed, int maxOut)
    {
        try
        {
            using var input = new MemoryStream(packed.ToArray(), writable: false);
            using var bs = new BrotliStream(input, CompressionMode.Decompress);
            return ReadCapped(bs, maxOut);
        }
        catch (WireFormatException) { throw; }
        catch (Exception e) { throw new WireFormatException("Corrupt brotli data: " + e.Message, e); }
    }

    static byte[] ReadCapped(Stream s, int maxOut)
    {
        using var output = new MemoryStream();
        var buf = new byte[Math.Clamp(maxOut + 1, 256, 81920)];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (output.Length + n > maxOut) throw new WireFormatException($"Inflated data exceeds the {maxOut}-byte limit.");
            output.Write(buf, 0, n);
        }
        return output.ToArray();
    }
}

/// <summary>IEEE 802.3 CRC-32 (reflected, poly 0xEDB88320), as zlib. <c>Compute("123456789") == 0xCBF43926</c>.</summary>
public static class Crc32
{
    static readonly uint[] Table = BuildTable();

    static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    /// <summary>Continues a CRC: <c>Append(Compute(a), b) == Compute(a + b)</c>.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = ~crc;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }
}
