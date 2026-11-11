using System.Numerics;

namespace PotatoPleaseMP.Core.Wire;

/// <summary>Packs values of 1..32 bits, LSB first, into a <see cref="NetWriter"/>. Call <see cref="Flush"/> at the end.</summary>
public sealed class BitWriter
{
    readonly NetWriter _w;
    ulong _acc;
    int _bits;

    public BitWriter(NetWriter w) => _w = w;

    public void Write(uint value, int bits)
    {
        if (bits < 1 || bits > 32) throw new ArgumentOutOfRangeException(nameof(bits));
        ulong mask = bits == 32 ? 0xFFFFFFFFUL : (1UL << bits) - 1;
        _acc |= (value & mask) << _bits;
        _bits += bits;
        while (_bits >= 8) { _w.U8((byte)_acc); _acc >>= 8; _bits -= 8; }
    }

    public void WriteBool(bool v) => Write(v ? 1u : 0u, 1);

    /// <summary>Writes the last partial byte (zero padded).</summary>
    public void Flush()
    {
        if (_bits > 0) { _w.U8((byte)_acc); _acc = 0; _bits = 0; }
    }
}

/// <summary>Reads what <see cref="BitWriter"/> wrote. Reading past the end throws <see cref="WireFormatException"/>.</summary>
public ref struct BitReader
{
    readonly ReadOnlySpan<byte> _data;
    int _byte;
    ulong _acc;
    int _bits;

    public BitReader(ReadOnlySpan<byte> data)
    {
        _data = data; _byte = 0; _acc = 0; _bits = 0;
    }

    /// <summary>Whole bytes consumed so far (including a partially read one).</summary>
    public int BytesConsumed => _byte;

    public uint Read(int bits)
    {
        if (bits < 1 || bits > 32) throw new ArgumentOutOfRangeException(nameof(bits));
        while (_bits < bits)
        {
            if (_byte >= _data.Length) throw new WireFormatException("Bit read past the end.");
            _acc |= (ulong)_data[_byte++] << _bits;
            _bits += 8;
        }
        ulong mask = bits == 32 ? 0xFFFFFFFFUL : (1UL << bits) - 1;
        uint v = (uint)(_acc & mask);
        _acc >>= bits;
        _bits -= bits;
        return v;
    }

    public bool ReadBool() => Read(1) != 0;
}

/// <summary>Fixed-size bit set over 64-bit words (pile removal sets, masks).</summary>
public sealed class Bitset : IEquatable<Bitset>
{
    readonly ulong[] _words;

    public Bitset(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        Count = count;
        _words = new ulong[(count + 63) / 64];
    }

    public int Count { get; }
    internal ulong[] Words => _words;

    public bool this[int i]
    {
        get { Check(i); return (_words[i >> 6] & (1UL << (i & 63))) != 0; }
        set
        {
            Check(i);
            if (value) _words[i >> 6] |= 1UL << (i & 63);
            else _words[i >> 6] &= ~(1UL << (i & 63));
        }
    }

    void Check(int i)
    {
        if ((uint)i >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(i), i, $"Bit index outside 0..{Count - 1}.");
    }

    public int PopCount()
    {
        int n = 0;
        foreach (var w in _words) n += BitOperations.PopCount(w);
        return n;
    }

    public void SetAll(bool value)
    {
        Array.Fill(_words, value ? ulong.MaxValue : 0UL);
        TrimTail();
    }

    public Bitset Clone()
    {
        var b = new Bitset(Count);
        Array.Copy(_words, b._words, _words.Length);
        return b;
    }

    /// <summary>this ^= other (same size).</summary>
    public void XorWith(Bitset other)
    {
        if (other.Count != Count) throw new ArgumentException("Bitset sizes differ.");
        for (int i = 0; i < _words.Length; i++) _words[i] ^= other._words[i];
    }

    internal void TrimTail()
    {
        int extra = _words.Length * 64 - Count;
        if (extra > 0) _words[^1] &= ulong.MaxValue >> extra;
    }

    public bool Equals(Bitset? other) => other != null && other.Count == Count && _words.AsSpan().SequenceEqual(other._words);
    public override bool Equals(object? obj) => obj is Bitset b && Equals(b);
    public override int GetHashCode() => HashCode.Combine(Count, PopCount());
}

/// <summary>
/// Bitset wire form (spec 01 §4.3). Header varint = bitCount &lt;&lt; 1 | raw. Runs form: varint runCount, then that many
/// alternating run lengths starting with a 0-run; the last run is implicit (the rest of the bits), so the empty set of
/// 105,216 bits costs 4 bytes. Raw form: the words' bytes, used when smaller (noisy sets).
/// </summary>
public static class BitsetCodec
{
    public const int MaxBits = 1 << 26;

    public static void Write(NetWriter w, Bitset set)
    {
        // Count the explicit runs first to pick the smaller form.
        var runs = new List<int>();
        bool cur = false;
        int run = 0;
        for (int i = 0; i < set.Count; i++)
        {
            bool b = set[i];
            if (b == cur) { run++; continue; }
            runs.Add(run);
            cur = b;
            run = 1;
        }
        // The final run is implicit.
        int runBytes = NetWriter.VarU32Size((uint)runs.Count);
        foreach (var r in runs) runBytes += NetWriter.VarU32Size((uint)r);
        int rawBytes = (set.Count + 7) / 8;

        bool raw = rawBytes < runBytes;
        w.VarU32(((uint)set.Count << 1) | (raw ? 1u : 0u));
        if (raw)
        {
            Span<byte> tmp = stackalloc byte[8];
            for (int i = 0, left = rawBytes; left > 0; i++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(tmp, set.Words[i]);
                int n = Math.Min(8, left);
                w.Raw(tmp[..n]);
                left -= n;
            }
            return;
        }
        w.VarU32((uint)runs.Count);
        foreach (var r in runs) w.VarU32((uint)r);
    }

    public static Bitset Read(ref NetReader r, int maxBits = MaxBits)
    {
        uint head = r.VarU32();
        uint count = head >> 1;
        if (count > (uint)maxBits) throw new WireFormatException($"Bitset of {count} bits exceeds {maxBits}.");
        var set = new Bitset((int)count);
        if ((head & 1) != 0)
        {
            var bytes = r.Raw(((int)count + 7) / 8);
            for (int i = 0; i < bytes.Length; i++) set.Words[i >> 3] |= (ulong)bytes[i] << ((i & 7) * 8);
            set.TrimTail();
            return set;
        }
        int runCount = r.Count((int)count + 1);
        long pos = 0;
        bool bit = false;
        for (int k = 0; k < runCount; k++)
        {
            uint len = r.VarU32();
            if (pos + len > count) throw new WireFormatException("Bitset runs exceed the bit count.");
            if (bit) for (long i = pos; i < pos + len; i++) set[(int)i] = true;
            pos += len;
            bit = !bit;
        }
        if (bit) for (long i = pos; i < count; i++) set[(int)i] = true; // implicit final run
        return set;
    }

    /// <summary>Writes the difference a ^ b (apply it with <see cref="ApplyDiff"/>).</summary>
    public static void WriteDiff(NetWriter w, Bitset from, Bitset to)
    {
        var x = from.Clone();
        x.XorWith(to);
        Write(w, x);
    }

    /// <summary>Reads a diff and applies it to <paramref name="target"/> in place.</summary>
    public static void ApplyDiff(ref NetReader r, Bitset target)
    {
        var x = Read(ref r, target.Count);
        if (x.Count != target.Count) throw new WireFormatException($"Diff of {x.Count} bits for a {target.Count}-bit set.");
        target.XorWith(x);
    }
}

/// <summary>Byte run-length coding: varint total length, then (value, varint run) pairs (frost grids).</summary>
public static class ByteRle
{
    public static void Write(NetWriter w, ReadOnlySpan<byte> data)
    {
        w.VarU32((uint)data.Length);
        int i = 0;
        while (i < data.Length)
        {
            byte v = data[i];
            int j = i + 1;
            while (j < data.Length && data[j] == v) j++;
            w.U8(v);
            w.VarU32((uint)(j - i));
            i = j;
        }
    }

    public static byte[] Read(ref NetReader r, int maxLength)
    {
        uint total = r.VarU32();
        if (total > (uint)maxLength) throw new WireFormatException($"RLE length {total} exceeds {maxLength}.");
        var data = new byte[total];
        int pos = 0;
        while (pos < total)
        {
            byte v = r.U8();
            uint run = r.VarU32();
            if (run == 0 || pos + run > total) throw new WireFormatException("Bad RLE run.");
            data.AsSpan(pos, (int)run).Fill(v);
            pos += (int)run;
        }
        return data;
    }
}

/// <summary>
/// Strictly increasing indices: varint count, the first index, then varint (gap - 1) for each next one (~1.5 B per
/// pile index). Unsorted or duplicate input throws on write.
/// </summary>
public static class SortedIndexCodec
{
    public static void Write(NetWriter w, IReadOnlyList<int> sorted)
    {
        w.VarU32((uint)sorted.Count);
        int prev = -1;
        for (int i = 0; i < sorted.Count; i++)
        {
            int v = sorted[i];
            if (v < 0) throw new ArgumentException("Indices must be non-negative.");
            if (v <= prev) throw new ArgumentException($"Indices must be strictly increasing ({prev} then {v}).");
            w.VarU32((uint)(v - prev - 1));
            prev = v;
        }
    }

    public static List<int> Read(ref NetReader r, int maxCount, int maxIndexExclusive)
    {
        int n = r.Count(maxCount);
        var list = new List<int>(n);
        long prev = -1;
        for (int i = 0; i < n; i++)
        {
            long v = prev + 1 + r.VarU32();
            if (v >= maxIndexExclusive) throw new WireFormatException($"Index {v} outside 0..{maxIndexExclusive - 1}.");
            list.Add((int)v);
            prev = v;
        }
        return list;
    }
}
