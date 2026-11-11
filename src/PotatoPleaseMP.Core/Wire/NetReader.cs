using System.Buffers.Binary;
using System.Text;

namespace PotatoPleaseMP.Core.Wire;

/// <summary>
/// Bounds-checked little-endian reader over a span (spec 01 §4.1). Every read checks the remaining bytes first and
/// every count is checked against the remaining bytes and a caller-given maximum before anything is allocated, so a
/// hostile length can never make us allocate. Any violation throws <see cref="WireFormatException"/>.
/// </summary>
public ref struct NetReader
{
    readonly ReadOnlySpan<byte> _data;
    int _pos;

    public NetReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public int Position => _pos;
    public int Remaining => _data.Length - _pos;
    public bool IsEnd => _pos >= _data.Length;

    ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || n > _data.Length - _pos)
            throw new WireFormatException($"Read of {n} bytes past the end (pos {_pos}, length {_data.Length}).");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public void Skip(int n) => Take(n);

    /// <summary>Throws unless every byte was consumed (codecs with a fixed layout use it to reject trailing junk).</summary>
    public void ExpectEnd()
    {
        if (_pos != _data.Length) throw new WireFormatException($"{_data.Length - _pos} unexpected trailing bytes.");
    }

    public byte U8() => Take(1)[0];
    public sbyte I8() => (sbyte)Take(1)[0];

    public bool Bool()
    {
        byte b = Take(1)[0];
        if (b > 1) throw new WireFormatException($"Bool byte {b} is not 0 or 1.");
        return b == 1;
    }

    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public uint U24()
    {
        var s = Take(3);
        return (uint)(s[0] | (s[1] << 8) | (s[2] << 16));
    }

    public int I24()
    {
        uint u = U24();
        return (int)(u << 8) >> 8; // sign-extend bit 23
    }

    /// <summary>Half float, any value including NaN (use <see cref="F16"/> where the value must be finite).</summary>
    public Half F16Raw() => BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(Take(2)));
    public float F32Raw() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double F64Raw() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    /// <summary>Finite half float (NaN/infinity throw: spec 20 §4.1).</summary>
    public float F16()
    {
        float v = (float)F16Raw();
        if (!float.IsFinite(v)) throw new WireFormatException("Non-finite f16.");
        return v;
    }

    public float F32()
    {
        float v = F32Raw();
        if (!float.IsFinite(v)) throw new WireFormatException("Non-finite f32.");
        return v;
    }

    public double F64()
    {
        double v = F64Raw();
        if (!double.IsFinite(v)) throw new WireFormatException("Non-finite f64.");
        return v;
    }

    /// <summary>LEB128, at most 5 bytes; a 6th continuation byte or bits above 32 throw.</summary>
    public uint VarU32()
    {
        uint result = 0;
        for (int i = 0; i < 5; i++)
        {
            byte b = U8();
            if (i == 4 && b > 0x0F) throw new WireFormatException("VarU32 overflows 32 bits.");
            result |= (uint)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return result;
        }
        throw new WireFormatException("VarU32 longer than 5 bytes.");
    }

    /// <summary>LEB128, at most 10 bytes.</summary>
    public ulong VarU64()
    {
        ulong result = 0;
        for (int i = 0; i < 10; i++)
        {
            byte b = U8();
            if (i == 9 && b > 0x01) throw new WireFormatException("VarU64 overflows 64 bits.");
            result |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return result;
        }
        throw new WireFormatException("VarU64 longer than 10 bytes.");
    }

    public int VarI32()
    {
        uint u = VarU32();
        return (int)(u >> 1) ^ -(int)(u & 1);
    }

    public long VarI64()
    {
        ulong u = VarU64();
        return (long)(u >> 1) ^ -(long)(u & 1);
    }

    /// <summary>
    /// A varint element count, validated against <paramref name="max"/> and against the bytes left assuming each element
    /// takes at least <paramref name="minBytesEach"/> bytes. Use before allocating a list or array.
    /// </summary>
    public int Count(int max, int minBytesEach = 1)
    {
        uint n = VarU32();
        if (n > (uint)max) throw new WireFormatException($"Count {n} exceeds the limit {max}.");
        if (minBytesEach > 0 && (long)n * minBytesEach > Remaining)
            throw new WireFormatException($"Count {n} cannot fit in the {Remaining} remaining bytes.");
        return (int)n;
    }

    public string Str8(int maxBytes = 255)
    {
        int n = U8();
        if (n > maxBytes) throw new WireFormatException($"String of {n} bytes exceeds the limit {maxBytes}.");
        return Utf8(Take(n));
    }

    public string StrV(int maxBytes)
    {
        uint n = VarU32();
        if (n > (uint)maxBytes) throw new WireFormatException($"String of {n} bytes exceeds the limit {maxBytes}.");
        return Utf8(Take((int)n));
    }

    public ReadOnlySpan<byte> Bytes8() => Take(U8());

    public ReadOnlySpan<byte> BytesV(int maxBytes)
    {
        uint n = VarU32();
        if (n > (uint)maxBytes) throw new WireFormatException($"Byte string of {n} exceeds the limit {maxBytes}.");
        return Take((int)n);
    }

    /// <summary>The next <paramref name="n"/> bytes as a span over the input (no copy).</summary>
    public ReadOnlySpan<byte> Raw(int n) => Take(n);

    /// <summary>Everything not read yet.</summary>
    public ReadOnlySpan<byte> Rest() => Take(Remaining);

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static string Utf8(ReadOnlySpan<byte> s)
    {
        try { return StrictUtf8.GetString(s); }
        catch (DecoderFallbackException e) { throw new WireFormatException("Invalid UTF-8.", e); }
    }
}
