using PotatoPleaseMP.Core.Presence;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Testing;
using PotatoPleaseMP.Core.Time;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Testing;

/// <summary>
/// Headless end to end: a host session and two bots over the loopback hub with latency, jitter and unreliable loss.
/// Both join (handshake, gather point, 300 KB world blob, simulated load), exchange poses through the host relay, then
/// one leaves cleanly and the other's link is cut until the host times it out.
/// </summary>
public class EndToEndTests
{
    [Fact]
    public void Host_and_two_bots_join_exchange_poses_leave_and_time_out()
    {
        using var rig = new TestRig(
            configure: c => { c.MaxPlayers = 4; c.PrepareBlobsOnWorker = true; },
            hub: h => { h.LatencyMs = 60; h.JitterMs = 25; h.UnreliableLossPercent = 10; });
        var world = TestData.SaveLike(300 * 1024, 42);
        rig.World.SaveFactory = _ => world;

        // Host side: presence relay (spec 01 §5.2, slot overwritten from the sender) and the host's own 20 Hz pose.
        var hostSeen = new Dictionary<byte, int>();
        rig.Host.MessageHandler = (from, id, payload, d, stamp) =>
        {
            if (id != MsgId.PoseUpdate) return;
            hostSeen[from.Slot] = hostSeen.GetValueOrDefault(from.Slot) + 1;
            rig.Host.Relay(from, id, payload, PoseUpdate.SlotOffset);
        };
        var hostPacer = new StreamPacer(20);
        byte hostSeq = 0;
        rig.Extra.Add(now =>
        {
            if (!hostPacer.Due(rig.Host.CurrentTick)) return;
            float a = (float)now;
            rig.Host.Broadcast(new PoseUpdate { Slot = 0, Seq = ++hostSeq, Pos = new Vec3(MathF.Cos(a) * 3, 0, MathF.Sin(a) * 3), YawDeg = a * 30, Flags = PoseFlags.Grounded });
        });

        var joined = new List<byte>();
        var left = new List<(byte Slot, LeaveReason Reason)>();
        rig.Host.PlayerJoined += p => joined.Add(p.Slot);
        rig.Host.PlayerLeft += (p, r) => left.Add((p.Slot, r));

        var alice = rig.AddBot("Alice", o => { o.LoadSeconds = 1.5; o.Spawn = new Vec3(2, 0, 0); });
        var bob = rig.AddBot("Bob", o => { o.LoadSeconds = 2.5; o.Spawn = new Vec3(-2, 0, 0); });

        // --- both reach Playing with the world intact
        Assert.True(rig.RunUntil(() => alice.State == BotState.Ready && bob.State == BotState.Ready && joined.Count == 2, 30), $"{alice.State} {bob.State}");
        Assert.Equal(new byte[] { 1, 2 }, joined.OrderBy(s => s));
        foreach (var bot in new[] { alice, bob })
        {
            Assert.Equal(world, bot.World.Snapshot!.Save);
            Assert.Equal(Crc32.Compute(world), bot.SnapshotCrc);
            Assert.Equal(1, bot.World.Loads);
            Assert.Equal(PlayerPhase.Playing, rig.Host.Players.BySlot(bot.Session.LocalSlot)!.Phase);
            Assert.True(bot.Session.Clock.HasOffset);
            Assert.True(Math.Abs(bot.Session.Clock.HostTime(rig.Now) - rig.Now) < 0.1, $"clock offset {bot.Session.Clock.Offset} target {bot.Session.Clock.TargetOffset} rttMin {bot.Session.Clock.RttMinMs}");
        }
        Assert.Equal(1, rig.World.Captures); // both were ready to gather in the same frame: one shared capture

        // --- exchange poses while walking
        alice.Walk(new[] { new Vec3(2, 0, 10), new Vec3(10, 0, 10) });
        bob.Walk(new[] { new Vec3(-2, 0, -45) }, sprint: true); // 6 s of sprinting
        rig.Run(5);
        byte sa = alice.Session.LocalSlot, sb = bob.Session.LocalSlot;
        Assert.True(hostSeen.GetValueOrDefault(sa) > 60, $"host got {hostSeen.GetValueOrDefault(sa)} from Alice");
        Assert.True(hostSeen.GetValueOrDefault(sb) > 60, $"host got {hostSeen.GetValueOrDefault(sb)} from Bob");
        Assert.True(alice.RemotePoses.ContainsKey(sb) && alice.RemotePoses.ContainsKey(0));
        Assert.True(bob.RemotePoses.ContainsKey(sa) && bob.RemotePoses.ContainsKey(0));
        Assert.False(alice.RemotePoses.ContainsKey(sa)); // no echo of our own pose
        // The relayed pose matches where the walker is: two hops of 60-85 ms, flush waits, a lost sample or two at 7.2 m/s.
        var seenBob = alice.RemotePoses[sb];
        Assert.True(Vec3.Distance(seenBob.Pos, bob.Walker.Position) < 7.2f * 0.5f, $"{seenBob.Pos} vs {bob.Walker.Position}");
        Assert.True(seenBob.Flags.HasFlag(PoseFlags.Sprint));
        Assert.True(alice.PosesReceived > 100);

        // --- Alice leaves cleanly
        alice.Leave();
        Assert.True(rig.RunUntil(() => left.Count == 1, 2));
        Assert.Equal((sa, LeaveReason.Left), left[0]);
        Assert.True(rig.RunUntil(() => bob.Session.Players.BySlot(sa) == null, 2));
        Assert.True(rig.Host.Players.Reservations.ContainsKey(alice.Id.Value)); // her slot is held for a rejoin

        // --- Bob's cable is cut: the host times him out, Bob notices the host is gone
        rig.Hub.SetLinkDown(bob.Id, TestRig.HostId, true);
        double cut = rig.Now;
        Assert.True(rig.RunUntil(() => left.Count == 2, 60, 0.05));
        Assert.Equal((sb, LeaveReason.TimedOut), left[1]);
        Assert.InRange(rig.Now - cut, 44, 47);
        Assert.True(rig.RunUntil(() => bob.State == BotState.Ended, 5, 0.05));
        Assert.Equal(LeaveReason.HostLost, bob.Session.EndReason);
        Assert.Equal(1, rig.Host.Players.Count);
        Assert.Equal(SessionPhase.Hosting, rig.Host.Phase);
    }
}
