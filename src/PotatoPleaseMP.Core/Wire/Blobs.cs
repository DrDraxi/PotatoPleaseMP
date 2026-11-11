namespace PotatoPleaseMP.Core.Wire;

/// <summary>Blob kinds (spec 01 §5.4).</summary>
public enum BlobKind : byte
{
    WorldSave = 1, JoinExtras = 2, CarveSnapshot = 3, PileFull = 4, PileFrost = 5, DustRepair = 6,
    DeviceSnapshot = 7, ClawShells = 8, Resync = 9, Generic = 10,
}

public enum BlobCodec : byte { None = 0, Deflate = 1, Brotli = 2 }
public enum BlobStatus : byte { Ok = 0, Crc = 1, Size = 2, Cancelled = 3 }

/// <summary>0xE0: u16 blobId, u8 kind, u32 context, u8 codec, u32 rawLen, u32 packedLen, u32 crc32(raw) = 20 B.</summary>
public struct BlobBegin : IWireMessage
{
    public ushort BlobId; public BlobKind Kind; public uint Context; public BlobCodec Codec;
    public uint RawLength, PackedLength, Crc;
    public MsgId Id => MsgId.BlobBegin;

    public void Write(NetWriter w)
    {
        w.U16(BlobId); w.U8((byte)Kind); w.U32(Context); w.U8((byte)Codec);
        w.U32(RawLength); w.U32(PackedLength); w.U32(Crc);
    }

    public void Read(ref NetReader r)
    {
        BlobId = r.U16(); Kind = (BlobKind)r.U8(); Context = r.U32(); Codec = (BlobCodec)r.U8();
        RawLength = r.U32(); PackedLength = r.U32(); Crc = r.U32();
        r.ExpectEnd();
    }
}

/// <summary>0xE1: u16 blobId, varint offset, data (&lt;= 32 KB, the rest of the payload).</summary>
public struct BlobChunkMsg : IWireMessage
{
    public ushort BlobId; public uint Offset; public byte[] Data;
    public MsgId Id => MsgId.BlobChunk;

    public void Write(NetWriter w) { w.U16(BlobId); w.VarU32(Offset); w.Raw(Data); }

    public void Read(ref NetReader r)
    {
        BlobId = r.U16(); Offset = r.VarU32();
        if (r.Remaining > BlobSender.ChunkSize) throw new WireFormatException($"Blob chunk of {r.Remaining} B exceeds {BlobSender.ChunkSize}.");
        Data = r.Rest().ToArray();
    }

    /// <summary>Zero-copy parse used by the receiver.</summary>
    public static ReadOnlySpan<byte> Parse(ReadOnlySpan<byte> payload, out ushort blobId, out uint offset)
    {
        var r = new NetReader(payload);
        blobId = r.U16();
        offset = r.VarU32();
        if (r.Remaining > BlobSender.ChunkSize) throw new WireFormatException($"Blob chunk of {r.Remaining} B exceeds {BlobSender.ChunkSize}.");
        return r.Rest();
    }
}

/// <summary>0xE2: u16 blobId, u8 status (receiver -> sender).</summary>
public struct BlobDone : IWireMessage
{
    public ushort BlobId; public BlobStatus Status;
    public MsgId Id => MsgId.BlobDone;
    public void Write(NetWriter w) { w.U16(BlobId); w.U8((byte)Status); }
    public void Read(ref NetReader r) { BlobId = r.U16(); Status = (BlobStatus)r.U8(); r.ExpectEnd(); }
}

/// <summary>0xE3: u16 blobId, u8 reason (either side).</summary>
public struct BlobCancel : IWireMessage
{
    public ushort BlobId; public byte Reason;
    public MsgId Id => MsgId.BlobCancel;
    public void Write(NetWriter w) { w.U16(BlobId); w.U8(Reason); }
    public void Read(ref NetReader r) { BlobId = r.U16(); Reason = r.U8(); r.ExpectEnd(); }
}

/// <summary>
/// A blob ready to send: raw CRC and the packed bytes. <see cref="Prepare"/> is pure and thread-safe, so Brotli of a
/// join save can run on a worker; enqueueing happens on the main thread.
/// </summary>
public sealed class PreparedBlob
{
    public BlobKind Kind { get; init; }
    public uint Context { get; init; }
    public BlobCodec Codec { get; init; }
    public uint RawLength { get; init; }
    public uint Crc { get; init; }
    public byte[] Packed { get; init; } = Array.Empty<byte>();

    public static PreparedBlob Prepare(BlobKind kind, uint context, ReadOnlySpan<byte> raw, BlobCodec codec)
    {
        byte[] packed = codec switch
        {
            BlobCodec.None => raw.ToArray(),
            BlobCodec.Deflate => Compress.Deflate(raw),
            BlobCodec.Brotli => Compress.Brotli(raw),
            _ => throw new ArgumentOutOfRangeException(nameof(codec)),
        };
        return new PreparedBlob { Kind = kind, Context = context, Codec = codec, RawLength = (uint)raw.Length, Crc = Crc32.Compute(raw), Packed = packed };
    }
}

/// <summary>Where a <see cref="BlobSender"/> or <see cref="BlobReceiver"/> sends its reliable messages.</summary>
public delegate void BlobSend(MsgId id, ReadOnlySpan<byte> payload);

/// <summary>
/// Sends blobs to one peer (spec 01 §5.4): BlobBegin, then 32 KB chunks round-robin over open blobs by priority
/// (CarveSnapshot &gt; Resync &gt; JoinExtras &gt; other module blobs &gt; WorldSave), only while the peer's pending reliable bytes
/// stay under 256 KB, our unacknowledged envelope bytes under 512 KB, and the rate under 512 KB/s. A joiner therefore
/// never starves live traffic and a Bye never waits behind megabytes.
/// </summary>
public sealed class BlobSender
{
    public const int ChunkSize = 32 * 1024;

    sealed class Out
    {
        public ushort Id;
        public PreparedBlob Blob = null!;
        public int Sent;
        public bool BeginSent;
        public int Priority;
    }

    readonly List<Out> _open = new();
    readonly HashSet<ushort> _reserved = new();
    readonly List<(ushort Id, BlobStatus Status)> _finished = new();
    ushort _nextId = 1;
    double _tokens;
    double _lastPump = double.NaN;
    int _rr;

    public int MaxPendingReliable { get; set; } = 256 * 1024;
    public long MaxUnacked { get; set; } = 512 * 1024;
    public int RateBytesPerSec { get; set; } = 512 * 1024;
    public int BurstBytes { get; set; } = 64 * 1024;

    public int OpenCount => _open.Count;
    public bool IsIdle => _open.All(o => o.BeginSent && o.Sent >= o.Blob.Packed.Length);
    public long BytesSent { get; private set; }
    public double LastChunkAt { get; private set; } = double.NegativeInfinity;

    public static int PriorityOf(BlobKind k) => k switch
    {
        BlobKind.CarveSnapshot => 5,
        BlobKind.Resync => 4,
        BlobKind.JoinExtras => 3,
        BlobKind.WorldSave => 1,
        _ => 2,
    };

    /// <summary>Reserves an id now (it can be announced, e.g. in SnapshotInfo) for a blob enqueued later.</summary>
    public ushort ReserveId()
    {
        ushort id;
        do id = _nextId++; while (id == 0 || _reserved.Contains(id) || _open.Any(o => o.Id == id));
        _reserved.Add(id);
        return id;
    }

    public ushort Enqueue(PreparedBlob blob, ushort reservedId = 0)
    {
        ushort id = reservedId != 0 ? reservedId : ReserveId();
        _reserved.Remove(id);
        _open.Add(new Out { Id = id, Blob = blob, Priority = PriorityOf(blob.Kind) });
        return id;
    }

    /// <summary>Fraction of a blob's packed bytes handed to the transport (1 when unknown or done).</summary>
    public float Progress(ushort id)
    {
        var o = _open.FirstOrDefault(x => x.Id == id);
        return o == null || o.Blob.Packed.Length == 0 ? 1f : (float)o.Sent / o.Blob.Packed.Length;
    }

    public bool IsOpen(ushort id) => _open.Any(o => o.Id == id) || _reserved.Contains(id);

    /// <summary>Blobs the receiver confirmed (or refused) since the last call.</summary>
    public List<(ushort Id, BlobStatus Status)> TakeFinished()
    {
        var l = new List<(ushort, BlobStatus)>(_finished);
        _finished.Clear();
        return l;
    }

    /// <summary>Receiver answered with BlobDone.</summary>
    public void OnDone(ushort id, BlobStatus status)
    {
        int n = _open.RemoveAll(o => o.Id == id);
        if (n > 0) _finished.Add((id, status));
    }

    /// <summary>Receiver cancelled.</summary>
    public void OnCancel(ushort id)
    {
        int n = _open.RemoveAll(o => o.Id == id);
        _reserved.Remove(id);
        if (n > 0) _finished.Add((id, BlobStatus.Cancelled));
    }

    /// <summary>Cancels a blob we are sending (tells the receiver if it was announced).</summary>
    public void Cancel(ushort id, byte reason, BlobSend send)
    {
        var o = _open.FirstOrDefault(x => x.Id == id);
        _reserved.Remove(id);
        if (o == null) return;
        _open.Remove(o);
        if (o.BeginSent) Send(send, new BlobCancel { BlobId = id, Reason = reason });
    }

    /// <summary>Forgets everything (peer gone): every buffer is freed.</summary>
    public void Clear()
    {
        _open.Clear();
        _reserved.Clear();
    }

    /// <summary>
    /// Hands out what flow control allows this frame. <paramref name="pendingReliable"/>: transport + batcher bytes not
    /// yet on the wire; <paramref name="unacked"/>: envelope bytes the peer has not acknowledged.
    /// </summary>
    public void Pump(double now, int pendingReliable, long unacked, BlobSend send)
    {
        double dt = double.IsNaN(_lastPump) ? 0 : Math.Max(0, now - _lastPump);
        if (double.IsNaN(_lastPump)) _tokens = BurstBytes;
        _lastPump = now;
        _tokens = Math.Min(BurstBytes, _tokens + dt * RateBytesPerSec);

        foreach (var o in _open)
        {
            if (o.BeginSent) continue;
            o.BeginSent = true;
            Send(send, new BlobBegin
            {
                BlobId = o.Id, Kind = o.Blob.Kind, Context = o.Blob.Context, Codec = o.Blob.Codec,
                RawLength = o.Blob.RawLength, PackedLength = (uint)o.Blob.Packed.Length, Crc = o.Blob.Crc,
            });
        }

        long pending = Math.Max(0, pendingReliable);
        long inFlight = Math.Max(0, unacked);
        while (_tokens > 0)
        {
            var o = Next();
            if (o == null) break;
            int len = Math.Min(ChunkSize, o.Blob.Packed.Length - o.Sent);
            int cost = len + 8;
            if (pending + cost > MaxPendingReliable || inFlight + cost > MaxUnacked) break;
            using (var w = new NetWriter(len + 8))
            {
                w.U16(o.Id);
                w.VarU32((uint)o.Sent);
                w.Raw(o.Blob.Packed.AsSpan(o.Sent, len));
                send(MsgId.BlobChunk, w.Span);
            }
            o.Sent += len;
            pending += cost;
            inFlight += cost;
            _tokens -= len;
            BytesSent += len;
            LastChunkAt = now;
        }
    }

    /// <summary>Highest priority blob with bytes left; round-robin among equals.</summary>
    Out? Next()
    {
        int best = int.MinValue;
        foreach (var o in _open)
            if (o.Sent < o.Blob.Packed.Length && o.Priority > best) best = o.Priority;
        if (best == int.MinValue) return null;
        for (int k = 0; k < _open.Count; k++)
        {
            var o = _open[(_rr + k) % _open.Count];
            if (o.Priority == best && o.Sent < o.Blob.Packed.Length)
            {
                _rr = (_rr + k + 1) % _open.Count;
                return o;
            }
        }
        return null;
    }

    static void Send<T>(BlobSend send, T msg) where T : IWireMessage
    {
        using var w = new NetWriter(32);
        msg.Write(w);
        send(msg.Id, w.Span);
    }
}

/// <summary>A finished incoming blob: completed (data) or failed (status and reason).</summary>
public sealed record BlobResult(ushort Id, BlobKind Kind, uint Context, BlobStatus Status, byte[]? Data, string? Error)
{
    public bool Ok => Status == BlobStatus.Ok && Data != null;
}

/// <summary>
/// Receives blobs from one peer (spec 01 §5.4). Before allocating: packedLen &lt;= 64 MB, rawLen &lt;= 256 MB, kind allowed
/// for the direction, at most 4 open blobs. Chunks must arrive at <c>offset == received</c> and end at packedLen; then
/// the data is inflated with <c>maxOut = rawLen</c> and CRC-checked. The caller answers <see cref="BlobDone"/> with the
/// result's status; a failure means PeerError and disconnect. The packed buffer grows with the data, so a hostile
/// announcement cannot make us allocate 64 MB up front.
/// </summary>
public sealed class BlobReceiver
{
    public const long MaxPacked = 64L * 1024 * 1024;
    public const long MaxRaw = 256L * 1024 * 1024;
    public const int MaxOpen = 4;

    sealed class In
    {
        public BlobBegin Begin;
        public byte[] Buffer = Array.Empty<byte>();
        public int Received;
    }

    readonly Dictionary<ushort, In> _open = new();

    /// <summary>Kinds this side accepts (direction rule); null accepts every known kind.</summary>
    public Func<BlobKind, bool>? AllowKind { get; set; }

    public int OpenCount => _open.Count;
    public long BytesReceived { get; private set; }

    public float Progress(ushort id)
        => _open.TryGetValue(id, out var b) ? (b.Begin.PackedLength == 0 ? 1f : (float)b.Received / b.Begin.PackedLength) : 0f;

    public bool IsOpen(ushort id) => _open.ContainsKey(id);

    /// <summary>Handles BlobBegin. Returns a result for a zero-length blob or a refusal, else null.</summary>
    public BlobResult? OnBegin(in BlobBegin b)
    {
        if (!Enum.IsDefined(b.Kind) || (AllowKind != null && !AllowKind(b.Kind)))
            return Fail(b, BlobStatus.Size, $"blob kind {b.Kind} not allowed");
        if (!Enum.IsDefined(b.Codec)) return Fail(b, BlobStatus.Size, $"unknown codec {b.Codec}");
        if (b.PackedLength > MaxPacked) return Fail(b, BlobStatus.Size, $"packed length {b.PackedLength} > {MaxPacked}");
        if (b.RawLength > MaxRaw) return Fail(b, BlobStatus.Size, $"raw length {b.RawLength} > {MaxRaw}");
        if (b.Codec == BlobCodec.None && b.RawLength != b.PackedLength) return Fail(b, BlobStatus.Size, "raw and packed lengths differ");
        if (_open.ContainsKey(b.BlobId)) return Fail(b, BlobStatus.Size, $"blob {b.BlobId} already open");
        if (_open.Count >= MaxOpen) return Fail(b, BlobStatus.Size, $"more than {MaxOpen} open blobs");
        var inb = new In { Begin = b, Buffer = new byte[(int)Math.Min(b.PackedLength, 1 << 20)] };
        _open[b.BlobId] = inb;
        return b.PackedLength == 0 ? Finish(inb) : null;
    }

    /// <summary>Handles BlobChunk. Returns the result when the blob completes or fails, else null.</summary>
    public BlobResult? OnChunk(ushort id, uint offset, ReadOnlySpan<byte> data)
    {
        if (!_open.TryGetValue(id, out var b))
            return new BlobResult(id, 0, 0, BlobStatus.Size, null, $"chunk for unknown blob {id}");
        if (offset != (uint)b.Received)
            return Abort(b, BlobStatus.Size, $"chunk at offset {offset}, expected {b.Received}");
        if (data.Length == 0) return Abort(b, BlobStatus.Size, "empty chunk");
        if ((long)b.Received + data.Length > b.Begin.PackedLength)
            return Abort(b, BlobStatus.Size, $"chunk past packed length {b.Begin.PackedLength}");
        if (b.Received + data.Length > b.Buffer.Length)
        {
            long size = Math.Min(b.Begin.PackedLength, Math.Max((long)b.Buffer.Length * 2, b.Received + data.Length));
            Array.Resize(ref b.Buffer, (int)size);
        }
        data.CopyTo(b.Buffer.AsSpan(b.Received));
        b.Received += data.Length;
        BytesReceived += data.Length;
        return b.Received == b.Begin.PackedLength ? Finish(b) : null;
    }

    /// <summary>Sender cancelled: the buffer is freed.</summary>
    public void OnCancel(ushort id) => _open.Remove(id);

    /// <summary>Cancels and forgets one blob (we no longer want it); returns whether it was open.</summary>
    public bool Drop(ushort id) => _open.Remove(id);

    /// <summary>Frees every buffer (peer left mid-blob).</summary>
    public void Clear() => _open.Clear();

    BlobResult Finish(In b)
    {
        _open.Remove(b.Begin.BlobId);
        var packed = b.Buffer.AsSpan(0, b.Received);
        byte[] raw;
        try
        {
            raw = b.Begin.Codec switch
            {
                BlobCodec.None => packed.ToArray(),
                BlobCodec.Deflate => Compress.Inflate(packed, (int)b.Begin.RawLength),
                BlobCodec.Brotli => Compress.Unbrotli(packed, (int)b.Begin.RawLength),
                _ => throw new WireFormatException("unknown codec"),
            };
        }
        catch (WireFormatException e) { return Fail(b.Begin, BlobStatus.Size, e.Message); }
        if (raw.Length != b.Begin.RawLength) return Fail(b.Begin, BlobStatus.Size, $"inflated to {raw.Length} B, announced {b.Begin.RawLength}");
        if (Crc32.Compute(raw) != b.Begin.Crc) return Fail(b.Begin, BlobStatus.Crc, "CRC mismatch");
        return new BlobResult(b.Begin.BlobId, b.Begin.Kind, b.Begin.Context, BlobStatus.Ok, raw, null);
    }

    BlobResult Abort(In b, BlobStatus s, string why)
    {
        _open.Remove(b.Begin.BlobId);
        return Fail(b.Begin, s, why);
    }

    static BlobResult Fail(in BlobBegin b, BlobStatus s, string why) => new(b.BlobId, b.Kind, b.Context, s, null, why);
}
