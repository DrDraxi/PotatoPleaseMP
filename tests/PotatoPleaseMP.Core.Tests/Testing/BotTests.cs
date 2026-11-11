using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Presence;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Testing;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Testing;

public class BotTests
{
    [Fact]
    public void Version_mismatch_is_refused_and_never_listed()
    {
        using var rig = new TestRig();
        var bob = rig.AddBot("Bob", o => o.Version = TestRig.Versions with { ModVersion = "0.0.0" });
        Assert.True(rig.RunUntil(() => bob.State == BotState.Ended, 3));
        Assert.Equal(RejectCode.ModVersion, bob.Session.RejectCode);
        Assert.Contains("0.0.0", bob.Session.EndDetail);
        Assert.Equal(1, rig.Host.Players.Count);
    }

    [Fact]
    public void Nobody_hosting_fails_the_connect()
    {
        using var rig = new TestRig(start: false);
        var bot = rig.AddBot("Alice");
        Assert.True(rig.RunUntil(() => bot.State == BotState.Ended, 35, 0.1));
        Assert.Equal(LeaveReason.HostLost, bot.Session.EndReason);
    }

    [Fact]
    public void Graceful_leave_cut_and_host_bye()
    {
        using var rig = new TestRig();
        var left = new List<(byte Slot, LeaveReason R, double At)>();
        rig.Host.PlayerLeft += (p, r) => left.Add((p.Slot, r, rig.Now));
        var a = rig.JoinBot("A");
        var b = rig.JoinBot("B");
        var c = rig.JoinBot("C");

        double t0 = rig.Now;
        a.Leave();
        Assert.True(rig.RunUntil(() => left.Count == 1, 1));
        Assert.Equal(LeaveReason.Left, left[0].R);
        Assert.Equal(LeaveReason.Left, a.Session.EndReason);

        rig.Hub.SetLinkDown(b.Id, TestRig.HostId, true);
        t0 = rig.Now;
        Assert.True(rig.RunUntil(() => left.Count == 2, 50, 0.05));
        Assert.Equal(LeaveReason.TimedOut, left[1].R);
        Assert.InRange(left[1].At - t0, 44, 47);

        rig.Host.Shutdown();
        Assert.True(rig.RunUntil(() => c.State == BotState.Ended, 1));
        Assert.Equal(LeaveReason.HostLeft, c.Session.EndReason);
    }

    [Fact]
    public void Snapshot_bytes_arrive_intact()
    {
        using var rig = new TestRig(hub: h => h.LatencyMs = 30);
        rig.World.SaveFactory = _ => TestData.SaveLike(500_000, 3);
        var bot = rig.JoinBot("Alice");
        Assert.Equal(rig.World.LastSave, bot.World.Snapshot!.Save);
        Assert.Equal(Crc32.Compute(rig.World.LastSave), bot.SnapshotCrc);
        Assert.Equal(500_000, bot.SnapshotBytes);
        Assert.NotNull(bot.JoinMs);
    }

    [Fact]
    public void Corrupt_blob_from_a_rogue_host_ends_the_join()
    {
        // A host speaking the protocol by hand: Welcome, SnapshotInfo, then a blob whose CRC does not match.
        var hub = new LoopbackHub();
        var hostT = hub.CreateTransport(TestRig.HostId);
        hostT.StartHost(4, null);
        var net = new NetLayer(hostT, MessageRegistry.CreateDefault(), isHost: true);
        var bot = new BotClient(hub, 1, TestRig.HostId, new BotOptions { Name = "Victim", Version = TestRig.Versions });
        bot.Start(hub.Now);
        var ev = new List<NetLayerEvent>();
        BlobStatus? reported = null;
        bool hello = false;
        for (int i = 0; i < 300 && bot.State != BotState.Ended; i++)
        {
            hub.Advance(1 / 60.0);
            ev.Clear();
            net.Poll(hub.Now, ev);
            foreach (var e in ev)
            {
                if (e.Kind != NetLayerEventKind.Message) continue;
                if (e.Id == MsgId.Hello && !hello)
                {
                    hello = true;
                    var me = new PlayerInfo { Slot = 0, Name = "Rogue", Phase = PlayerPhase.Playing, Flags = PlayerFlags.Host };
                    var you = new PlayerInfo { Slot = 1, Name = "Victim", Phase = PlayerPhase.Queued };
                    net.Send(e.Peer, new Welcome { SessionId = 1, YourSlot = 1, MaxPlayers = 4, TickRate = 30, Players = new() { me, you } });
                    net.Send(e.Peer, new SnapshotInfo { SnapshotId = 1, SaveBlobId = 1, ExtrasBlobId = 2 });
                    var data = TestData.Random(1000, 1);
                    net.Send(e.Peer, new BlobBegin { BlobId = 1, Kind = BlobKind.WorldSave, Codec = BlobCodec.None, RawLength = 1000, PackedLength = 1000, Crc = Crc32.Compute(data) ^ 0x10 });
                    net.Send(e.Peer, new BlobChunkMsg { BlobId = 1, Offset = 0, Data = data });
                }
                if (e.Id == MsgId.BlobDone) reported = SessionMessages.Decode<BlobDone>(e.Payload).Status;
            }
            net.Flush(hub.Now);
            bot.Update(hub.Now);
        }
        Assert.Equal(BotState.Ended, bot.State);
        Assert.Equal(LeaveReason.LoadFailed, bot.Session.EndReason);
        for (int i = 0; i < 10 && reported == null; i++)
        {
            hub.Advance(1 / 60.0);
            ev.Clear();
            net.Poll(hub.Now, ev);
            foreach (var e in ev)
                if (e.Kind == NetLayerEventKind.Message && e.Id == MsgId.BlobDone) reported = SessionMessages.Decode<BlobDone>(e.Payload).Status;
        }
        Assert.Equal(BlobStatus.Crc, reported); // the bot told the host why before leaving
        bot.Dispose();
    }

    [Fact]
    public void Walker_speed_arrival_and_teleport()
    {
        var w = new BotWalker(new Vec3(0, 0, 0));
        w.SetPath(new[] { new Vec3(0, 0, 7), new Vec3(7, 0, 7) });
        for (int i = 0; i < 60; i++) w.Update(1 / 60.0);
        Assert.Equal(3.5f, w.Position.Z, 2);
        Assert.Equal(0f, w.YawDeg, 1);      // facing +Z
        Assert.Equal(3.5f, w.Velocity.Length, 1);
        for (int i = 0; i < 60; i++) w.Update(1 / 60.0);
        Assert.Equal(90f, w.YawDeg, 1);     // turned toward +X
        for (int i = 0; i < 120; i++) w.Update(1 / 60.0);
        Assert.False(w.Moving);
        Assert.Equal(new Vec3(7, 0, 7), w.Position);
        Assert.Equal(0f, w.Velocity.Length);

        w.SetPath(new[] { new Vec3(100, 0, 0) }, sprint: true);
        w.Update(1);
        Assert.Equal(7.2, w.DistanceWalked - 14, 2);
        w.Teleport(new Vec3(-5, 1, -5), 180);
        Assert.False(w.Moving);
        Assert.Equal(1, w.TeleportSeq);
        w.Update(1);
        Assert.Equal(new Vec3(-5, 1, -5), w.Position);
        Assert.NotEqual(BotWalker.SpreadAround(Vec3.Zero, 0), BotWalker.SpreadAround(Vec3.Zero, 1));
    }

    [Fact]
    public void Bot_hub_adds_cuts_and_prunes()
    {
        using var rig = new TestRig();
        var bots = new BotHub(rig.Hub, TestRig.HostId, () => new BotOptions { Version = TestRig.Versions, LoadSeconds = 0.2 });
        rig.Extra.Add(now => bots.Update(now));
        var a = bots.Add("Alice", rig.Now);
        var b = bots.Add("Bob", rig.Now);
        Assert.True(rig.RunUntil(() => a.State == BotState.Ready && b.State == BotState.Ready, 10));
        Assert.Same(b, bots.Find("bob"));
        Assert.Same(a, bots.Find("@1"));
        bots.Leave(a);
        rig.Run(0.5);
        Assert.Equal(BotState.Ready, b.State);
        Assert.Equal(1, bots.Prune());
        Assert.Single(bots.Bots);
        bots.Cut(b);
        Assert.True(rig.RunUntil(() => b.State == BotState.Ended && rig.Host.Players.Count == 1, 50, 0.1));
        Assert.Equal(1, rig.Host.Players.Count);
        bots.Dispose();
    }

    [Fact]
    public void Poses_are_paced_at_20_hz_moving_and_5_hz_idle()
    {
        using var rig = new TestRig();
        rig.Host.MessageHandler = (from, id, p, d, s) => { if (id == MsgId.PoseUpdate) rig.Host.Relay(from, id, p, PoseUpdate.SlotOffset); };
        int poses = 0;
        var inner = rig.Host.MessageHandler;
        rig.Host.MessageHandler = (from, id, p, d, s) => { if (id == MsgId.PoseUpdate) poses++; inner(from, id, p, d, s); };
        var a = rig.JoinBot("A");
        rig.Run(1);
        poses = 0;
        rig.Run(4);
        Assert.InRange(poses, 18, 22); // idle 5 Hz
        a.Walk(new[] { new Vec3(50, 0, 0) });
        poses = 0;
        rig.Run(4);
        Assert.InRange(poses, 76, 84); // moving 20 Hz
    }
}
