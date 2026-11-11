using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Wire;

public class BlobTests
{
    /// <summary>Moves every message from a sender to a receiver immediately (no flow-control pressure).</summary>
    static List<BlobResult> Transfer(BlobSender s, BlobReceiver r, int maxPumps = 100_000)
    {
        var results = new List<BlobResult>();
        double now = 0;
        for (int i = 0; i < maxPumps && !s.IsIdle; i++)
        {
            now += 0.1;
            s.Pump(now, 0, 0, (id, payload) =>
            {
                var res = Deliver(r, id, payload);
                if (res != null) results.Add(res);
            });
        }
        return results;
    }

    static BlobResult? Deliver(BlobReceiver r, MsgId id, ReadOnlySpan<byte> payload)
    {
        switch (id)
        {
            case MsgId.BlobBegin: return r.OnBegin(SessionMessages.Decode<BlobBegin>(payload));
            case MsgId.BlobChunk:
                var data = BlobChunkMsg.Parse(payload, out var bid, out var off);
                return r.OnChunk(bid, off, data);
            case MsgId.BlobCancel: r.OnCancel(SessionMessages.Decode<BlobCancel>(payload).BlobId); return null;
        }
        return null;
    }

    [Theory]
    [InlineData(BlobCodec.None)]
    [InlineData(BlobCodec.Deflate)]
    [InlineData(BlobCodec.Brotli)]
    public void Three_megabytes_round_trip(BlobCodec codec)
    {
        var random = TestData.Random(3 << 20, 1);
        var saveLike = TestData.SaveLike(3 << 20, 2);
        var s = new BlobSender();
        var r = new BlobReceiver();
        ushort a = s.Enqueue(PreparedBlob.Prepare(BlobKind.WorldSave, 7, random, codec));
        ushort b = s.Enqueue(PreparedBlob.Prepare(BlobKind.JoinExtras, 8, saveLike, codec)); // interleaved with the first
        var res = Transfer(s, r);
        Assert.Equal(2, res.Count);
        Assert.All(res, x => Assert.True(x.Ok, x.Error));
        Assert.Equal(random, res.Single(x => x.Id == a).Data);
        Assert.Equal(saveLike, res.Single(x => x.Id == b).Data);
        Assert.Equal(8u, res.Single(x => x.Id == b).Context);
        // JoinExtras outranks WorldSave, so it finished first.
        Assert.Equal(b, res[0].Id);
        Assert.Equal(0, r.OpenCount);
    }

    [Fact]
    public void Chunk_order_violations_fail()
    {
        var data = TestData.Random(100_000, 3);
        var p = PreparedBlob.Prepare(BlobKind.WorldSave, 0, data, BlobCodec.None);
        BlobReceiver Fresh()
        {
            var r = new BlobReceiver();
            Assert.Null(r.OnBegin(new BlobBegin { BlobId = 1, Kind = BlobKind.WorldSave, Codec = BlobCodec.None, RawLength = (uint)data.Length, PackedLength = (uint)data.Length, Crc = p.Crc }));
            return r;
        }
        var r1 = Fresh();
        Assert.Equal(BlobStatus.Size, r1.OnChunk(1, 1000, data.AsSpan(0, 100))!.Status);          // offset jump
        var r2 = Fresh();
        Assert.Null(r2.OnChunk(1, 0, data.AsSpan(0, 1000)));
        Assert.Equal(BlobStatus.Size, r2.OnChunk(1, 0, data.AsSpan(0, 1000))!.Status);             // duplicate
        var r3 = Fresh();
        Assert.Null(r3.OnChunk(1, 0, data.AsSpan(0, 99_000)));
        Assert.Equal(BlobStatus.Size, r3.OnChunk(1, 99_000, new byte[2000])!.Status);              // past packedLen
        Assert.Equal(0, r3.OpenCount);
    }

    [Fact]
    public void Oversized_announcements_refused_before_allocating()
    {
        var r = new BlobReceiver();
        Assert.Equal(BlobStatus.Size, r.OnBegin(new BlobBegin { BlobId = 1, Kind = BlobKind.WorldSave, Codec = BlobCodec.Brotli, RawLength = 1000, PackedLength = 65u << 20 })!.Status);
        Assert.Equal(BlobStatus.Size, r.OnBegin(new BlobBegin { BlobId = 2, Kind = BlobKind.WorldSave, Codec = BlobCodec.Brotli, RawLength = 300u << 20, PackedLength = 1000 })!.Status);
        r.AllowKind = k => k != BlobKind.WorldSave;
        Assert.Equal(BlobStatus.Size, r.OnBegin(new BlobBegin { BlobId = 3, Kind = BlobKind.WorldSave, Codec = BlobCodec.None })!.Status);
        r.AllowKind = null;
        for (ushort i = 10; i < 14; i++) Assert.Null(r.OnBegin(new BlobBegin { BlobId = i, Kind = BlobKind.Generic, RawLength = 10, PackedLength = 10 }));
        Assert.Equal(BlobStatus.Size, r.OnBegin(new BlobBegin { BlobId = 20, Kind = BlobKind.Generic, RawLength = 10, PackedLength = 10 })!.Status); // 5th open
        r.OnCancel(10);
        Assert.Equal(3, r.OpenCount);
        r.Clear();
        Assert.Equal(0, r.OpenCount);
    }

    [Fact]
    public void Crc_flip_reports_status_1()
    {
        var data = TestData.SaveLike(50_000, 4);
        var p = PreparedBlob.Prepare(BlobKind.Generic, 0, data, BlobCodec.Deflate);
        var r = new BlobReceiver();
        r.OnBegin(new BlobBegin { BlobId = 5, Kind = BlobKind.Generic, Codec = BlobCodec.Deflate, RawLength = p.RawLength, PackedLength = (uint)p.Packed.Length, Crc = p.Crc ^ 1 });
        var res = r.OnChunk(5, 0, p.Packed);
        Assert.Equal(BlobStatus.Crc, res!.Status);
        Assert.Null(res.Data);
    }

    [Fact]
    public void Empty_blob_completes_at_begin()
    {
        var s = new BlobSender();
        var r = new BlobReceiver();
        s.Enqueue(PreparedBlob.Prepare(BlobKind.JoinExtras, 0, Array.Empty<byte>(), BlobCodec.None));
        var res = Transfer(s, r);
        Assert.Single(res);
        Assert.True(res[0].Ok);
        Assert.Empty(res[0].Data!);
    }

    [Fact]
    public void Cancel_frees_and_tells_the_receiver()
    {
        var s = new BlobSender();
        var r = new BlobReceiver();
        ushort id = s.Enqueue(PreparedBlob.Prepare(BlobKind.WorldSave, 0, TestData.Random(500_000, 5), BlobCodec.None));
        s.Pump(0, 0, 0, (m, pl) => Deliver(r, m, pl));
        Assert.Equal(1, r.OpenCount);
        s.Cancel(id, 3, (m, pl) => Deliver(r, m, pl));
        Assert.Equal(0, s.OpenCount);
        Assert.Equal(0, r.OpenCount);
    }

    [Fact]
    public void Pump_respects_pending_unacked_and_rate()
    {
        var s = new BlobSender();
        s.Enqueue(PreparedBlob.Prepare(BlobKind.WorldSave, 0, TestData.Random(8 << 20, 6), BlobCodec.None));
        // Simulated link: pending drains at 2 MB/s (faster than the cap), acks arrive 100 ms after send.
        double now = 0;
        long pending = 0, unacked = 0, total = 0;
        var inflight = new Queue<(double At, int Bytes)>();
        var sentAt = new List<(double At, int Bytes)>();
        for (int f = 0; f < 60 * 10; f++)
        {
            now += 1 / 60.0;
            pending = Math.Max(0, pending - (long)(2_000_000 / 60.0));
            while (inflight.Count > 0 && inflight.Peek().At <= now) unacked -= inflight.Dequeue().Bytes;
            s.Pump(now, (int)pending, unacked, (m, pl) =>
            {
                if (m != MsgId.BlobChunk) return;
                pending += pl.Length;
                unacked += pl.Length;
                total += pl.Length;
                inflight.Enqueue((now + 0.1, pl.Length));
                sentAt.Add((now, pl.Length));
                Assert.True(pending <= 256 * 1024, $"pending {pending}");
                Assert.True(unacked <= 512 * 1024, $"unacked {unacked}");
            });
        }
        // Rate: no 1-second window carries more than 512 KB plus the 64 KB burst.
        for (int i = 0; i < sentAt.Count; i++)
        {
            long win = sentAt.Skip(i).TakeWhile(x => x.At < sentAt[i].At + 1.0).Sum(x => (long)x.Bytes);
            Assert.True(win <= 512 * 1024 + 64 * 1024 + 33 * 1024, $"window at {sentAt[i].At:F2}: {win}");
        }
        Assert.True(total > 4 << 20, $"only {total} B in 10 s");
    }

    [Fact]
    public void Priority_order_carve_before_world_save()
    {
        var s = new BlobSender();
        s.Enqueue(PreparedBlob.Prepare(BlobKind.WorldSave, 0, new byte[100_000], BlobCodec.None));
        s.Enqueue(PreparedBlob.Prepare(BlobKind.CarveSnapshot, 0, new byte[100_000], BlobCodec.None));
        var order = new List<ushort>();
        s.Pump(0, 0, 0, (m, pl) => { if (m == MsgId.BlobChunk) order.Add(BitConverter.ToUInt16(pl)); });
        Assert.All(order, id => Assert.Equal(2, id));
    }
}
