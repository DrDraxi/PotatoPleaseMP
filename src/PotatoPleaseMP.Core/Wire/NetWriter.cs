using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace PotatoPleaseMP.Core.Wire;

/// <summary>
/// Growable little-endian byte writer (spec 01 §4.1). The buffer is rented from <see cref="ArrayPool{T}"/>; call
/// <see cref="Dispose"/> (or reuse with <see cref="Reset"/>) when done. Writer misuse (a string too long for its prefix,
/// a negative count) is a programming error and throws <see cref="ArgumentException"/>; reading bad bytes is the
/// reader's <see cref="WireFormatException"/>.
/// </summary>
public sealed class NetWriter : IDisposable
{
    byte[] _buf;
    int _pos;

    public NetWriter(int initialCapacity = 256)
    {
        _buf = ArrayPool<byte>.Shared.Rent(Math.Max(16, initialCapacity));
    }

    public int Length => _pos;
    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, _pos);
    public ReadOnlyMemory<byte> Memory => _buf.AsMemory(0, _pos);
    public byte[] ToArray() => Span.ToArray();
    public void Reset() => _pos = 0;

    /// <summary>Shrinks the written length back to <paramref name="length"/> (undo a partial write).</summary>
    public void Truncate(int length)
    {
        if ((uint)length > (uint)_pos) throw new ArgumentOutOfRangeException(nameof(length));
        _pos = length;
    }

    public void Dispose()
    {
        var b = _buf;
        _buf = Array.Empty<byte>();
        _pos = 0;
        if (b.Length > 0) ArrayPool<byte>.Shared.Return(b);
    }

    Span<byte> Grab(int n)
    {
        if (_pos + n > _buf.Length)
        {
            int size = Math.Max(_buf.Length * 2, _pos + n);
            var nb = ArrayPool<byte>.Shared.Rent(size);
            Buffer.BlockCopy(_buf, 0, nb, 0, _pos);
            if (_buf.Length > 0) ArrayPool<byte>.Shared.Return(_buf);
            _buf = nb;
        }
        var s = _buf.AsSpan(_pos, n);
        _pos += n;
        return s;
    }

    // ---- fixed width ----
    public void U8(byte v) => Grab(1)[0] = v;
    public void I8(sbyte v) => Grab(1)[0] = (byte)v;
    public void Bool(bool v) => Grab(1)[0] = v ? (byte)1 : (byte)0;
    public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Grab(2), v);
    public void I16(short v) => BinaryPrimitives.WriteInt16LittleEndian(Grab(2), v);
    public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Grab(4), v);
    public void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Grab(4), v);
    public void U64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Grab(8), v);
    public void I64(long v) => BinaryPrimitives.WriteInt64LittleEndian(Grab(8), v);
    public void F16(Half v) => BinaryPrimitives.WriteUInt16LittleEndian(Grab(2), BitConverter.HalfToUInt16Bits(v));
    public void F16(float v) => F16((Half)v);
    public void F32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Grab(4), v);
    public void F64(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Grab(8), v);

    /// <summary>Unsigned 24-bit; values above 0xFFFFFF throw.</summary>
    public void U24(uint v)
    {
        if (v > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(v), v, "U24 holds 0..16777215.");
        var s = Grab(3);
        s[0] = (byte)v; s[1] = (byte)(v >> 8); s[2] = (byte)(v >> 16);
    }

    /// <summary>Signed 24-bit two's complement; outside -8388608..8388607 throws.</summary>
    public void I24(int v)
    {
        if (v < -0x800000 || v > 0x7FFFFF) throw new ArgumentOutOfRangeException(nameof(v), v, "I24 holds -8388608..8388607.");
        uint u = (uint)v & 0xFFFFFF;
        var s = Grab(3);
        s[0] = (byte)u; s[1] = (byte)(u >> 8); s[2] = (byte)(u >> 16);
    }

    // ---- varints (LEB128, zigzag for signed) ----
    public void VarU32(uint v)
    {
        while (v >= 0x80) { U8((byte)(v | 0x80)); v >>= 7; }
        U8((byte)v);
    }

    public void VarU64(ulong v)
    {
        while (v >= 0x80) { U8((byte)(v | 0x80)); v >>= 7; }
        U8((byte)v);
    }

    public void VarI32(int v) => VarU32((uint)((v << 1) ^ (v >> 31)));
    public void VarI64(long v) => VarU64((ulong)((v << 1) ^ (v >> 63)));

    public static int VarU32Size(uint v)
    {
        int n = 1;
        while (v >= 0x80) { v >>= 7; n++; }
        return n;
    }

    // ---- strings and bytes ----
    /// <summary>u8 byte length + UTF-8. More than 255 bytes throws.</summary>
    public void Str8(string s)
    {
        int n = Encoding.UTF8.GetByteCount(s);
        if (n > 255) throw new ArgumentException($"Str8 is limited to 255 UTF-8 bytes, got {n}.", nameof(s));
        U8((byte)n);
        Encoding.UTF8.GetBytes(s, Grab(n));
    }

    /// <summary>varint byte length + UTF-8, at most <paramref name="maxBytes"/>.</summary>
    public void StrV(string s, int maxBytes = 64 * 1024)
    {
        int n = Encoding.UTF8.GetByteCount(s);
        if (n > maxBytes) throw new ArgumentException($"StrV is limited to {maxBytes} bytes, got {n}.", nameof(s));
        VarU32((uint)n);
        Encoding.UTF8.GetBytes(s, Grab(n));
    }

    public void Bytes8(ReadOnlySpan<byte> b)
    {
        if (b.Length > 255) throw new ArgumentException($"Bytes8 is limited to 255 bytes, got {b.Length}.", nameof(b));
        U8((byte)b.Length);
        Raw(b);
    }

    public void BytesV(ReadOnlySpan<byte> b)
    {
        VarU32((uint)b.Length);
        Raw(b);
    }

    public void Raw(ReadOnlySpan<byte> b)
    {
        if (b.Length > 0) b.CopyTo(Grab(b.Length));
    }

    // ---- length patching ----
    /// <summary>Remembers the current position; write a payload, then <see cref="PatchVarLen"/> inserts its length here.</summary>
    public int Mark() => _pos;

    /// <summary>Inserts the varint byte count of everything written since <paramref name="mark"/> at the mark.</summary>
    public void PatchVarLen(int mark)
    {
        if ((uint)mark > (uint)_pos) throw new ArgumentOutOfRangeException(nameof(mark));
        int len = _pos - mark;
        int n = VarU32Size((uint)len);
        Grab(n);
        Buffer.BlockCopy(_buf, mark, _buf, mark + n, len);
        uint v = (uint)len;
        int i = mark;
        while (v >= 0x80) { _buf[i++] = (byte)(v | 0x80); v >>= 7; }
        _buf[i] = (byte)v;
    }

    /// <summary>Overwrites one byte that was already written (e.g. a relayed message's slot field).</summary>
    public void PatchU8(int at, byte v)
    {
        if ((uint)at >= (uint)_pos) throw new ArgumentOutOfRangeException(nameof(at));
        _buf[at] = v;
    }
}

/// <summary>
/// A message codec: a struct that knows its id and writes/reads its payload (spec 01 §2). The envelope adds id and length.
/// </summary>
public interface IWireMessage
{
    MsgId Id { get; }
    void Write(NetWriter w);
    void Read(ref NetReader r);
}

/// <summary>Malformed bytes from the wire. Always caught by the receiving layer and counted, never a crash.</summary>
public sealed class WireFormatException : Exception
{
    public WireFormatException(string message) : base(message) { }
    public WireFormatException(string message, Exception inner) : base(message, inner) { }
}
