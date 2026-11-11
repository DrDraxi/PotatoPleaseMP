using System.IO.Compression;
using PotatoPleaseMP.Core.Net;

namespace PotatoPleaseMP.Core.Wire;

/// <summary>Envelope kind in header bits 0-1; must match the channel it arrived on.</summary>
public enum EnvelopeKind : byte { Reliable = 1, Unreliable = 2 }

/// <summary>Why an envelope (or a peer) was dropped (spec 01 §3; counted toward spec 20's PeerHealth).</summary>
public enum ProtocolError : byte
{
    None,
    /// <summary>Reliable seq not last+1: restarted session or bug. The peer is dropped as ConnectionLost.</summary>
    Gap,
    BadHeader,
    EpochMismatch,
    KindMismatch,
    LengthPastBody,
    InflateFailed,
}

public struct EnvelopeHeader
{
    public EnvelopeKind Kind;
    public bool HasAck;
    public bool Deflated;
    public byte Epoch;
    /// <summary>Reliable: envelope number. Unreliable: host-tick stamp (low 16 bits).</summary>
    public ushort Seq;
    public ushort AckRel;
    public ushort AckStamp;
}

/// <summary>One message inside a parsed envelope: a slice of <see cref="ParsedEnvelope.Body"/>.</summary>
public readonly record struct RawMessage(byte Id, int Offset, int Length);

public sealed class ParsedEnvelope
{
    public EnvelopeHeader Header;
    public byte[] Body = Array.Empty<byte>();
    public readonly List<RawMessage> Messages = new();
    public ReadOnlySpan<byte> Payload(in RawMessage m) => Body.AsSpan(m.Offset, m.Length);
}

/// <summary>
/// One Steam message (spec 01 §3):
/// <code>
/// u8 hdr   bits 0-1 kind, bit 2 HasAck, bit 3 Deflated (reliable only), bits 4-7 epoch = ProtocolVersion &amp; 0xF
/// u16 seq  reliable envelope number / unreliable stamp
/// [HasAck] u16 ackRel, u16 ackStamp
/// body     { u8 id, varint len, payload[len] }*      (Deflated: varint rawLen + deflate(body))
/// </code>
/// </summary>
public static class Envelope
{
    /// <summary>Wire protocol version (Hello.proto); its low 4 bits are the header epoch.</summary>
    public const ushort ProtocolVersion = 1;
    public const int HeaderSize = 3, AckSize = 4;
    public const int MaxReliable = 64 * 1024;
    public const int MaxUnreliable = 1100;
    public const int MaxInflate = 256 * 1024;
    /// <summary>Reliable bodies above this are tried deflated; kept only if at least 12 % smaller.</summary>
    public const int DeflateThreshold = 512;
    public const double DeflateMinGain = 0.12;

    public static byte Epoch => (byte)(ProtocolVersion & 0xF);

    /// <summary>Bytes the framing of one message adds (id + varint length).</summary>
    public static int FramingSize(int payloadLength) => 1 + NetWriter.VarU32Size((uint)payloadLength);

    public static void WriteHeader(NetWriter w, in EnvelopeHeader h)
    {
        byte b = (byte)((byte)h.Kind & 3);
        if (h.HasAck) b |= 4;
        if (h.Deflated) b |= 8;
        b |= (byte)((h.Epoch & 0xF) << 4);
        w.U8(b);
        w.U16(h.Seq);
        if (h.HasAck) { w.U16(h.AckRel); w.U16(h.AckStamp); }
    }

    /// <summary>
    /// Builds a whole envelope from a header and a raw body (deflating a big reliable body when it pays off).
    /// </summary>
    public static byte[] Build(EnvelopeHeader h, ReadOnlySpan<byte> body, bool allowDeflate = true)
    {
        h.Epoch = Epoch;
        h.Deflated = false;
        byte[]? packed = null;
        if (allowDeflate && h.Kind == EnvelopeKind.Reliable && body.Length > DeflateThreshold)
        {
            var d = Compress.Deflate(body, CompressionLevel.Fastest);
            int packedSize = d.Length + NetWriter.VarU32Size((uint)body.Length);
            if (packedSize <= body.Length * (1 - DeflateMinGain)) { packed = d; h.Deflated = true; }
        }
        using var w = new NetWriter(HeaderSize + AckSize + (packed?.Length ?? body.Length) + 8);
        WriteHeader(w, h);
        if (packed != null) { w.VarU32((uint)body.Length); w.Raw(packed); }
        else w.Raw(body);
        return w.ToArray();
    }

    /// <summary>
    /// Parses and validates a whole envelope. Every message length is checked before anything is delivered, so a bad
    /// envelope is dropped whole. Returns <see cref="ProtocolError.None"/> on success.
    /// </summary>
    public static ProtocolError TryParse(ReadOnlySpan<byte> data, Delivery channel, ParsedEnvelope into)
    {
        into.Messages.Clear();
        if (data.Length < HeaderSize) return ProtocolError.BadHeader;
        byte b = data[0];
        var h = new EnvelopeHeader
        {
            Kind = (EnvelopeKind)(b & 3),
            HasAck = (b & 4) != 0,
            Deflated = (b & 8) != 0,
            Epoch = (byte)(b >> 4),
            Seq = (ushort)(data[1] | (data[2] << 8)),
        };
        if (h.Kind is not (EnvelopeKind.Reliable or EnvelopeKind.Unreliable)) return ProtocolError.BadHeader;
        if (h.Epoch != Epoch) return ProtocolError.EpochMismatch;
        var expected = channel == Delivery.Reliable ? EnvelopeKind.Reliable : EnvelopeKind.Unreliable;
        if (h.Kind != expected) return ProtocolError.KindMismatch;
        if (h.Deflated && h.Kind != EnvelopeKind.Reliable) return ProtocolError.BadHeader;
        int pos = HeaderSize;
        if (h.HasAck)
        {
            if (data.Length < HeaderSize + AckSize) return ProtocolError.BadHeader;
            h.AckRel = (ushort)(data[3] | (data[4] << 8));
            h.AckStamp = (ushort)(data[5] | (data[6] << 8));
            pos += AckSize;
        }
        into.Header = h;

        var rest = data[pos..];
        if (h.Deflated)
        {
            try
            {
                var r = new NetReader(rest);
                uint rawLen = r.VarU32();
                if (rawLen > MaxInflate) return ProtocolError.InflateFailed;
                var body = Compress.Inflate(r.Rest(), (int)rawLen);
                if (body.Length != rawLen) return ProtocolError.InflateFailed;
                into.Body = body;
            }
            catch (WireFormatException) { return ProtocolError.InflateFailed; }
        }
        else into.Body = rest.ToArray();

        // Frame the body: { u8 id, varint len, payload }*
        try
        {
            var r = new NetReader(into.Body);
            while (!r.IsEnd)
            {
                byte id = r.U8();
                uint len = r.VarU32();
                if (len > (uint)r.Remaining) { into.Messages.Clear(); return ProtocolError.LengthPastBody; }
                into.Messages.Add(new RawMessage(id, r.Position, (int)len));
                r.Skip((int)len);
            }
        }
        catch (WireFormatException) { into.Messages.Clear(); return ProtocolError.LengthPastBody; }
        return ProtocolError.None;
    }
}

/// <summary>
/// Accumulates framed messages for one envelope body up to a byte cap. The caller writes the header via
/// <see cref="Envelope.Build"/>.
/// </summary>
public sealed class EnvelopeBody : IDisposable
{
    readonly NetWriter _w = new(1024);
    public int Count { get; private set; }
    public int Length => _w.Length;
    public ReadOnlySpan<byte> Span => _w.Span;

    public void Clear() { _w.Reset(); Count = 0; }

    /// <summary>Appends one message if it fits within <paramref name="cap"/> body bytes.</summary>
    public bool TryAppend(byte id, ReadOnlySpan<byte> payload, int cap)
    {
        if (_w.Length + Envelope.FramingSize(payload.Length) + payload.Length > cap) return false;
        _w.U8(id);
        _w.VarU32((uint)payload.Length);
        _w.Raw(payload);
        Count++;
        return true;
    }

    public void Dispose() => _w.Dispose();
}
