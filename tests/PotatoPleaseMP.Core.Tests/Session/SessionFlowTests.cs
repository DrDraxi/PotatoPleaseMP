using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Presence;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Testing;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Session;

/// <summary>
/// A batching world-change module on the host (sequence numbers on 0x50) whose state rides in the join extras, plus
/// the client-side mirror that checks every change is applied exactly once and in order after the snapshot.
/// </summary>
sealed class SeqModule : IJoinStateProvider
{
    readonly HostSession _host;
    readonly List<int> _pending = new();
    int _frame;

    public SeqModule(HostSession host, int flushEvery = 3)
    {
        _host = host;
        FlushEvery = flushEvery;
        host.OnBeforeSnapshot += Flush;
        host.Register(this);
    }

    public byte ModuleId => 7;
    public int Applied { get; private set; }
    public int FlushEvery { get; set; }
    public int PerTick { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    /// <summary>One frame: apply changes to the "world" now, broadcast them in batches.</summary>
    public void Tick()
    {
        if (!Enabled) return;
        for (int i = 0; i < PerTick; i++) _pending.Add(++Applied);
        if (++_frame % FlushEvery == 0) Flush();
    }

    public void Flush()
    {
        foreach (var s in _pending) _host.Broadcast(MsgId.Balance, BitConverter.GetBytes(s));
        _pending.Clear();
    }

    public void Capture(BinaryWriter w) => w.Write(Applied);
    public void ApplyBeforeLoad(BinaryReader r) { }
    public void ApplyAfterLoad(BinaryReader r) { }
}

sealed class SeqMirror : IJoinStateProvider
{
    public byte ModuleId => 7;
    public int State = -1;
    public int Errors, Applied, Loads;
    public void Capture(BinaryWriter w) { }
    public void ApplyBeforeLoad(BinaryReader r) { }
    public void ApplyAfterLoad(BinaryReader r) { State = r.ReadInt32(); Loads++; }

    public void Attach(BotClient bot)
    {
        bot.Session.Register(this);
        var inner = bot.Session.MessageHandler;
        bot.Session.MessageHandler = (id, p, d, s) =>
        {
            if (id == MsgId.Balance)
            {
                int seq = BitConverter.ToInt32(p);
                if (seq != State + 1) Errors++;
                State = seq;
                Applied++;
            }
            inner?.Invoke(id, p, d, s);
        };
    }
}

public class SessionFlowTests
{
    [Fact]
    public void Hello_welcome_registry_identical_on_all_peers()
    {
        using var rig = new TestRig();
        var added = new List<PlayerInfo>();
        rig.Host.PlayerAdded += added.Add;
        var a = rig.JoinBot("Alice");
        var b = rig.JoinBot("Bob");
        var c = rig.JoinBot("Alice"); // deduplicated
        rig.Run(1);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, rig.Host.Players.Players.Select(p => p.Slot));
        Assert.Equal("Alice (2)", rig.Host.Players.BySlot(3)!.Name);
        string Sig(IEnumerable<PlayerInfo> ps) => string.Join("|", ps.Select(p => $"{p.Slot}:{p.Name}:{p.SteamId}:{p.Phase}"));
        var hostSig = Sig(rig.Host.Players.Players);
        foreach (var bot in new[] { a, b, c })
        {
            Assert.Equal(hostSig, Sig(bot.Session.Players.Players));
            Assert.True(bot.Session.Players.Local!.IsLocal);
        }
        Assert.Equal(1, a.Session.LocalSlot);
        Assert.Equal(3, added.Count);
        Assert.All(rig.Host.Players.Players.Skip(1), p => Assert.True(p.Flags.HasFlag(PlayerFlags.Bot)));
        Assert.Equal(TestRig.Versions.ManifestHash, a.Session.HostManifest);
        Assert.Equal(rig.World.Difficulty, a.Session.Difficulty);
    }

    [Fact]
    public void Full_reservation_and_reclaim()
    {
        using var rig = new TestRig(c => c.MaxPlayers = 4);
        var bots = Enumerable.Range(0, 3).Select(i => rig.JoinBot($"B{i}", o => o.SteamId = 1000 + (ulong)i)).ToList();
        var fifth = rig.AddBot("Late");
        Assert.True(rig.RunUntil(() => fifth.State == BotState.Ended, 5));
        Assert.Equal(RejectCode.Full, fifth.Session.RejectCode);

        // B0 leaves after playing: its slot is held for it.
        byte slot0 = bots[0].Session.LocalSlot;
        bots[0].Leave();
        rig.Run(1);
        Assert.True(rig.Host.Players.Reservations.ContainsKey(1000));
        var back = rig.JoinBot("B0 again", o => o.SteamId = 1000);
        Assert.Equal(slot0, back.Session.LocalSlot);

        // Reclaim: with every other slot live, a newcomer takes the oldest reservation.
        using var r2 = new TestRig(c => c.MaxPlayers = 3);
        var x = r2.JoinBot("X", o => o.SteamId = 1);
        r2.JoinBot("Y", o => o.SteamId = 2);
        x.Leave();
        r2.Run(1);
        Assert.True(r2.Host.Players.Reservations.ContainsKey(1));
        var z = r2.JoinBot("Z", o => o.SteamId = 3);
        Assert.Equal(1, z.Session.LocalSlot);
        Assert.False(r2.Host.Players.Reservations.ContainsKey(1));
    }

    [Fact]
    public void Gate_queues_then_releases_and_times_out()
    {
        using var rig = new TestRig();
        rig.World.Gate = QueueReason.Cinematic;
        var a = rig.AddBot("Alice");
        rig.Run(3);
        Assert.Equal(ClientState.Queued, a.Session.State);
        Assert.Equal(QueueReason.Cinematic, a.Session.QueueReason);
        Assert.Equal(PlayerPhase.Queued, rig.Host.Players.BySlot(1)!.Phase);
        Assert.Equal(0, rig.World.Captures);
        rig.World.Gate = null;
        Assert.True(rig.RunUntil(() => a.State == BotState.Ready, 10));
        Assert.Equal(1, rig.World.Captures);

        // Module gates count too, and the queue gives up at 180 s.
        var gated = true;
        rig.Host.AddJoinGate(() => gated ? QueueReason.StoryChoice : null);
        var b = rig.AddBot("Bob");
        rig.Run(170, 0.1);
        Assert.Equal(QueueReason.StoryChoice, b.Session.QueueReason);
        Assert.True(rig.RunUntil(() => b.State == BotState.Ended, 15, 0.1));
        Assert.Equal(RejectCode.QueueTimeout, b.Session.RejectCode);
        Assert.Equal(BotState.Ready, a.State);
    }

    [Fact]
    public void Exact_join_world_changes_before_during_and_after_capture()
    {
        using var rig = new TestRig(hub: h => { h.LatencyMs = 50; h.JitterMs = 20; h.UnreliableLossPercent = 10; });
        var mod = new SeqModule(rig.Host, flushEvery: 3);
        rig.Extra.Add(_ => mod.Tick());
        rig.Run(1); // changes before the join
        var mirror = new SeqMirror();
        var bot = rig.AddBot("Alice", o => o.LoadSeconds = 2, mirror.Attach);
        Assert.True(rig.RunUntil(() => bot.State == BotState.Ready, 20));
        Assert.True(bot.Session.LastReplayedCount > 30, $"replayed {bot.Session.LastReplayedCount}");
        rig.Run(2);
        mod.Enabled = false;
        mod.Flush();
        rig.Run(1);
        Assert.Equal(0, mirror.Errors);
        Assert.Equal(mod.Applied, mirror.State);
        Assert.Equal(1, mirror.Loads);
    }

    [Fact]
    public void Resync_drops_changes_before_the_new_gather_point()
    {
        using var rig = new TestRig(hub: h => h.LatencyMs = 40);
        var mod = new SeqModule(rig.Host, flushEvery: 2);
        rig.Extra.Add(_ => mod.Tick());
        var mirror = new SeqMirror();
        var bot = rig.AddBot("Alice", o => o.LoadSeconds = 1, mirror.Attach);
        Assert.True(rig.RunUntil(() => bot.State == BotState.Ready, 20));
        rig.Run(1);
        byte? reqModule = null;
        rig.Host.ResyncRequested += (p, m, d) => reqModule = m;
        Assert.True(bot.Session.RequestResync(5, 42));
        Assert.Equal(ClientState.Resyncing, bot.Session.State);
        rig.Run(0.2); // changes arrive meanwhile: dropped, the snapshot covers them
        Assert.True(rig.RunUntil(() => bot.State == BotState.Ready, 20));
        Assert.Equal((byte)5, reqModule);
        Assert.Equal(2, rig.World.Captures);
        Assert.Equal(SnapshotReason.Resync, rig.World.CaptureReasons[1]);
        Assert.Equal(2, mirror.Loads);
        rig.Run(1);
        mod.Enabled = false;
        mod.Flush();
        rig.Run(1);
        Assert.Equal(0, mirror.Errors);
        Assert.Equal(mod.Applied, mirror.State);
    }

    [Fact]
    public void Buffer_overflow_fell_behind_then_one_retry()
    {
        using var rig = new TestRig();
        var mod = new SeqModule(rig.Host, flushEvery: 1) { PerTick = 3 };
        rig.Extra.Add(_ => mod.Tick());
        int joins = 0;
        rig.Host.PlayerAdded += _ => joins++;
        var bot = rig.AddBot("Alice", o => { o.LoadSeconds = 3; o.Session = new ClientConfig { BufferMaxMessages = 50 }; });
        LeaveReason? ended = null;
        bot.Session.Ended += (r, _) => { ended ??= r; mod.Enabled = false; };
        Assert.True(rig.RunUntil(() => ended != null, 20));
        Assert.Equal(LeaveReason.FellBehind, ended);
        Assert.True(rig.RunUntil(() => bot.State == BotState.Ready, 20)); // rejoined by itself
        Assert.Equal(2, joins);
        Assert.Equal(1, rig.Host.Players.Count - 1);
    }

    [Fact]
    public void Heartbeat_rtt_not_responding_and_timeout()
    {
        using var rig = new TestRig(hub: h => h.LatencyMs = 40);
        var left = new List<(PlayerInfo, LeaveReason)>();
        rig.Host.PlayerLeft += (p, r) => left.Add((p, r));
        var bot = rig.JoinBot("Alice");
        rig.Run(4);
        var row = rig.Host.Players.BySlot(1)!;
        // Application RTT: 2 x 40 ms plus up to a 30 Hz flush wait and a 60 Hz frame on each leg.
        Assert.InRange(row.RttMs, 75, 170);
        Assert.InRange(bot.Session.RttMs, 75, 170);
        Assert.InRange(bot.Session.Players.BySlot(1)!.RttMs, 75, 170); // replicated (refreshed at most every 5 s)

        rig.Hub.SetLinkDown(bot.Id, TestRig.HostId, true);
        rig.Run(11, 0.05);
        Assert.True(row.Flags.HasFlag(PlayerFlags.NotResponding));
        Assert.True(bot.Session.HostNotResponding);
        Assert.Empty(left);
        Assert.True(rig.RunUntil(() => left.Count == 1, 40, 0.05));
        Assert.Equal(LeaveReason.TimedOut, left[0].Item2);
        Assert.True(rig.RunUntil(() => bot.State == BotState.Ended, 5, 0.05));
        Assert.Equal(LeaveReason.HostLost, bot.Session.EndReason);
    }

    [Fact]
    public void Loading_peer_survives_a_120_second_stall()
    {
        using var rig = new TestRig();
        rig.HostTransport.TimeoutSeconds = 300; // the stall is the session's business here, not the transport's
        var removed = new List<LeaveReason>();
        rig.Host.PlayerRemoved += (_, r) => removed.Add(r);
        var bot = rig.AddBot("Alice", o => o.LoadSeconds = 125);
        Assert.True(rig.RunUntil(() => bot.State == BotState.Loading, 10));
        rig.Bots.Remove(bot); // frozen: no updates at all (scene load on the main thread)
        rig.Run(120, 0.1);
        Assert.Empty(removed);
        Assert.Equal(PlayerPhase.Loading, rig.Host.Players.BySlot(1)!.Phase);
        Assert.True(rig.Host.Players.BySlot(1)!.Flags.HasFlag(PlayerFlags.NotResponding));
        rig.Bots.Add(bot);
        Assert.True(rig.RunUntil(() => bot.State == BotState.Ready, 20, 0.05));
        rig.Run(1);
        Assert.False(rig.Host.Players.BySlot(1)!.Flags.HasFlag(PlayerFlags.NotResponding));
        Assert.Empty(removed);
    }

    [Fact]
    public void Duplicate_steam_id_replaces_and_kick_bans_for_ten_minutes()
    {
        using var rig = new TestRig();
        var removed = new List<LeaveReason>();
        rig.Host.PlayerRemoved += (_, r) => removed.Add(r);
        var a = rig.JoinBot("A", o => o.SteamId = 555);
        var b = rig.JoinBot("A2", o => o.SteamId = 555);
        Assert.True(rig.RunUntil(() => a.State == BotState.Ended, 2));
        Assert.Equal(LeaveReason.Replaced, a.Session.EndReason);
        Assert.Equal(LeaveReason.Replaced, removed.Single());
        Assert.Equal(1, b.Session.LocalSlot);

        Assert.True(rig.Host.Kick(b.Session.LocalSlot, "go"));
        Assert.True(rig.RunUntil(() => b.State == BotState.Ended, 2));
        Assert.Equal(LeaveReason.Kicked, b.Session.EndReason);
        var c = rig.AddBot("A3", o => o.SteamId = 555);
        Assert.True(rig.RunUntil(() => c.State == BotState.Ended, 3));
        Assert.Equal(RejectCode.Kicked, c.Session.RejectCode);
        rig.Hub.Advance(601);
        rig.JoinBot("A4", o => o.SteamId = 555);
    }

    [Fact]
    public void Host_closing_says_goodbye_to_everyone_once()
    {
        using var rig = new TestRig();
        var a = rig.JoinBot("A");
        var b = rig.JoinBot("B");
        int ended = 0;
        rig.Host.SessionEnded += _ => ended++;
        rig.Host.Shutdown(LeaveReason.HostLeft);
        rig.Host.Shutdown(LeaveReason.HostLeft);
        Assert.True(rig.RunUntil(() => a.State == BotState.Ended && b.State == BotState.Ended, 2));
        Assert.Equal(LeaveReason.HostLeft, a.Session.EndReason);
        Assert.Equal(LeaveReason.HostLeft, b.Session.EndReason);
        Assert.Equal(1, ended);
        Assert.Equal(SessionPhase.Idle, rig.Host.Phase);
        Assert.Equal(LeaveReason.HostLeft, a.World.EndedWith);
    }

    [Fact]
    public void Leaving_while_downloading_cancels_and_frees_the_slot()
    {
        using var rig = new TestRig(hub: h => { h.SendBufferBytes = 256 * 1024; h.SendRateBytesPerSec = 200 * 1024; });
        rig.World.SaveFactory = _ => TestData.Random(3 << 20, 1);
        var left = 0;
        var removed = new List<LeaveReason>();
        rig.Host.PlayerLeft += (_, _) => left++;
        rig.Host.PlayerRemoved += (_, r) => removed.Add(r);
        var bot = rig.AddBot("Alice");
        Assert.True(rig.RunUntil(() => bot.State == BotState.Downloading && bot.Session.DownloadProgress > 0.05f, 20));
        Assert.True(bot.Session.DownloadProgress < 0.9f);
        bot.Leave();
        rig.Run(2);
        Assert.Equal(LeaveReason.Left, removed.Single());
        Assert.Equal(0, left);
        Assert.Empty(rig.Host.Players.Reservations);
        Assert.Equal(1, rig.Host.Players.Count);
        Assert.Equal(0, rig.Host.ConnectedPeers);
    }

    [Fact]
    public void Late_joiner_gets_player_joined_once_per_player_after_world_ready()
    {
        using var rig = new TestRig();
        var a = rig.JoinBot("A");
        var order = new List<string>();
        var b = rig.AddBot("B", beforeStart: bot =>
        {
            bot.Session.WorldReady += () => order.Add("ready");
            bot.Session.PlayerJoined += p => order.Add($"joined:{p.Slot}");
        });
        Assert.True(rig.RunUntil(() => b.State == BotState.Ready, 10));
        rig.Run(2);
        Assert.Equal(new[] { "ready", "joined:0", "joined:1" }, order);

        // A third player joining later is announced to B once it reaches Playing.
        var joinedOnA = new List<byte>();
        a.Session.PlayerJoined += p => joinedOnA.Add(p.Slot);
        var c = rig.JoinBot("C");
        rig.Run(1);
        Assert.Equal(new[] { "ready", "joined:0", "joined:1", "joined:3" }, order);
        Assert.Equal(new byte[] { 3 }, joinedOnA);
        var leftOnA = new List<(byte, LeaveReason)>();
        a.Session.PlayerLeft += (p, r) => leftOnA.Add((p.Slot, r));
        c.Leave();
        rig.Run(1);
        Assert.Equal((3, LeaveReason.Left), leftOnA.Single());
    }

    [Fact]
    public void Relay_copies_bytes_and_overwrites_only_the_slot()
    {
        using var rig = new TestRig();
        rig.Host.MessageHandler = (from, id, p, d, s) => { if (id == MsgId.PoseUpdate) rig.Host.Relay(from, id, p, PoseUpdate.SlotOffset); };
        var a = rig.JoinBot("A");
        var b = rig.JoinBot("B");
        byte[]? got = null;
        var inner = b.Session.MessageHandler;
        b.Session.MessageHandler = (id, p, d, s) => { if (id == MsgId.PoseUpdate && got == null) got = p.ToArray(); inner?.Invoke(id, p, d, s); };
        var pose = new PoseUpdate { Slot = 3, Seq = 9, Pos = new Vec3(1, 2, 3), YawDeg = 45, Flags = PoseFlags.Grounded };
        var sent = SessionMessages.Encode(pose);
        a.Session.Send(MsgId.PoseUpdate, sent);
        Assert.True(rig.RunUntil(() => got != null, 2));
        Assert.Equal(a.Session.LocalSlot, got![0]); // identity from the connection, never from the payload
        Assert.Equal(sent.Skip(1), got.Skip(1));
        Assert.Single(b.RemotePoses);
    }

    [Fact]
    public void Joiner_sending_world_ids_before_playing_is_dropped()
    {
        using var rig = new TestRig();
        var seen = new List<MsgId>();
        rig.Host.MessageHandler = (from, id, p, d, s) => { if (id != MsgId.PoseUpdate) seen.Add(id); };
        rig.World.Gate = QueueReason.HostLoading;
        var a = rig.AddBot("A");
        rig.Run(1);
        a.Session.Send(MsgId.PurchaseReq, new byte[] { 1, 2, 3 });
        rig.Run(1);
        Assert.Empty(seen);
        Assert.Equal(1, rig.Host.RejectedNonJoinCount(1));
        rig.World.Gate = null;
        Assert.True(rig.RunUntil(() => a.State == BotState.Ready, 10));
        a.Session.Send(MsgId.PurchaseReq, new byte[] { 1, 2, 3 });
        rig.Run(1);
        Assert.Equal(new[] { MsgId.PurchaseReq }, seen);
    }

    [Fact]
    public void Host_reload_resends_the_world_and_keeps_slots()
    {
        using var rig = new TestRig();
        var a = rig.JoinBot("A");
        byte slot = a.Session.LocalSlot;
        rig.Host.BeginHostReload();
        rig.Run(2);
        Assert.Equal(SessionPhase.HostLoading, rig.Host.Phase);
        Assert.Equal(ClientState.Queued, a.Session.State);
        Assert.Equal(QueueReason.HostLoading, a.Session.QueueReason);
        rig.Host.SetWorldReady();
        Assert.True(rig.RunUntil(() => a.State == BotState.Ready, 10));
        Assert.Equal(slot, a.Session.LocalSlot);
        Assert.Equal(SnapshotReason.HostReload, rig.World.CaptureReasons.Last());
        Assert.Equal(2, a.World.Loads);
    }

    [Fact]
    public void Ending_refuses_new_joins()
    {
        using var rig = new TestRig();
        rig.World.Gate = QueueReason.FinalScene;
        var q = rig.AddBot("Queued");
        rig.Run(1);
        Assert.Equal(QueueReason.FinalScene, q.Session.QueueReason);
        rig.Host.ShowEnding();
        Assert.True(rig.RunUntil(() => q.State == BotState.Ended, 2));
        Assert.Equal(RejectCode.ShiftOver, q.Session.RejectCode);
        var late = rig.AddBot("Late");
        Assert.True(rig.RunUntil(() => late.State == BotState.Ended, 2));
        Assert.Equal(RejectCode.ShiftOver, late.Session.RejectCode);
    }

    [Fact]
    public void Manifest_mismatch_at_client_ready_is_refused()
    {
        using var rig = new TestRig();
        var bot = rig.AddBot("A", o => o.Version = TestRig.Versions with { ManifestHash = 0 }); // not cached: passes Hello
        bot.World.ManifestOverride = 0x11111111;                                            // but loads a different scene
        Assert.True(rig.RunUntil(() => bot.State == BotState.Ended, 10));
        Assert.Equal(RejectCode.Manifest, bot.Session.RejectCode);
    }
}
