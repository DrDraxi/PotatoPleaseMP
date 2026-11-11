using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Session;

public class SessionCodecTests
{
    static T RoundTrip<T>(T m) where T : struct, IWireMessage => SessionMessages.Decode<T>(SessionMessages.Encode(m));

    [Fact]
    public void Every_session_message_round_trips()
    {
        var p = new PlayerInfo { Slot = 2, SteamId = 76561198000000001, Name = "Пётр", Phase = PlayerPhase.Loading, Flags = PlayerFlags.Bot | PlayerFlags.Ready, StationIndex = 2 };
        var h = RoundTrip(new Hello { Proto = 1, ModVersion = "0.1.0", GameVersion = "1.0.1", SteamBuildId = 26422447, SaveVersion = 44, SteamId = 7, Name = "Alice", ManifestHash = 0xDEADBEEF, Flags = HelloFlags.Bot | HelloFlags.TestHarness });
        Assert.Equal("Alice", h.Name); Assert.Equal(0xDEADBEEF, h.ManifestHash); Assert.Equal(HelloFlags.Bot | HelloFlags.TestHarness, h.Flags);
        var w = RoundTrip(new Welcome { SessionId = 9, YourSlot = 2, MaxPlayers = 4, TickRate = 30, ManifestHash = 5, Phase = SessionPhase.Hosting, Difficulty = 1, Players = new() { p }, HostTick = 12345 });
        Assert.Equal(12345u, w.HostTick); Assert.Equal("Пётр", w.Players.Single().Name); Assert.Equal(PlayerPhase.Loading, w.Players[0].Phase);
        Assert.Equal(RejectCode.Full, RoundTrip(new Reject { Code = RejectCode.Full, Detail = "x" }).Code);
        Assert.Equal(QueueReason.Cinematic, RoundTrip(new Queued { Reason = QueueReason.Cinematic, Position = 2, WaitedSec = 30 }).Reason);
        var si = RoundTrip(new SnapshotInfo { SnapshotId = 3, Reason = SnapshotReason.Resync, SaveUtcTicks = 99, SaveVersion = 44, SaveBlobId = 5, ExtrasBlobId = 6 });
        Assert.Equal((5, 6), (si.SaveBlobId, si.ExtrasBlobId));
        Assert.Equal(50, RoundTrip(new LoadProgress { Phase = PlayerPhase.Loading, Percent = 50 }).Percent);
        Assert.Equal(7, RoundTrip(new ClientReady { SnapshotId = 1, ManifestHash = 2, Replayed = 7 }).Replayed);
        Assert.Equal(2, RoundTrip(new PlayerAdded { Player = p }).Player.Slot);
        Assert.Equal(LeaveReason.TimedOut, RoundTrip(new PlayerRemoved { Slot = 1, Reason = LeaveReason.TimedOut }).Reason);
        Assert.Equal(123, RoundTrip(new PlayerUpdate { Slot = 1, Phase = PlayerPhase.Playing, RttMs = 123, StationIndex = 1 }).RttMs);
        Assert.Equal(55u, RoundTrip(new Heartbeat { SendMs = 1, EchoMs = 55 }).EchoMs);
        Assert.Equal("bye", RoundTrip(new Goodbye { Reason = LeaveReason.Left, Text = "bye" }).Text);
        Assert.Equal(3, RoundTrip(new SessionStateMsg { Phase = SessionPhase.Hosting, Count = 3, MaxPlayers = 4 }).Count);
        Assert.True(RoundTrip(new SetReady { Context = 1, Ready = true }).Ready);
        var rq = RoundTrip(new ResyncMsg { IsRequest = true, ModuleId = 6, Detail = 77 });
        Assert.True(rq.IsRequest); Assert.Equal(77u, rq.Detail);
        Assert.False(RoundTrip(new ResyncMsg { IsRequest = false }).IsRequest);
        Assert.Empty(SessionMessages.Encode(new ResyncMsg()));
    }

    [Fact]
    public void Truncated_and_oversized_payloads_throw()
    {
        var full = SessionMessages.Encode(new Hello { Proto = 1, ModVersion = "0.1.0", GameVersion = "1.0.1", Name = "Alice" });
        for (int n = 0; n < full.Length; n++)
            Assert.Throws<WireFormatException>(() => SessionMessages.Decode<Hello>(full.AsSpan(0, n)));
        Assert.Throws<WireFormatException>(() => SessionMessages.Decode<Hello>(full.Concat(new byte[] { 0 }).ToArray())); // trailing junk

        using var w = new NetWriter();
        new Welcome { Players = new() }.Write(w);
        var bytes = w.ToArray();
        bytes[14] = 200; // player count
        Assert.Throws<WireFormatException>(() => SessionMessages.Decode<Welcome>(bytes));
        Assert.Throws<WireFormatException>(() => SessionMessages.Decode<LoadProgress>(new byte[] { 3, 101 }));
        Assert.Throws<WireFormatException>(() => SessionMessages.Decode<PlayerUpdate>(new byte[] { 1, 99, 0, 0, 0, 0 }));
    }

    sealed class Prov : IJoinStateProvider
    {
        public byte ModuleId { get; init; }
        public int Value;
        public int Before = -1, After = -1;
        public void Capture(BinaryWriter w) => w.Write(Value);
        public void ApplyBeforeLoad(BinaryReader r) => Before = r.ReadInt32();
        public void ApplyAfterLoad(BinaryReader r) => After = r.ReadInt32();
    }

    [Fact]
    public void Join_extras_skip_unknown_modules_by_length()
    {
        var bytes = JoinExtras.Build(new IJoinStateProvider[] { new Prov { ModuleId = 3, Value = 30 }, new Prov { ModuleId = 9, Value = 90 } });
        var parts = JoinExtras.Parse(bytes);
        Assert.Equal(2, parts.Count);
        Assert.Equal(90, BitConverter.ToInt32(parts[9]));
        // A client that only knows module 9 still finds it.
        Assert.Throws<WireFormatException>(() => JoinExtras.Parse(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Empty(JoinExtras.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Handshake_validator_paths()
    {
        var host = TestRig.Versions;
        Hello Ok() => new() { Proto = host.Proto, ModVersion = host.ModVersion, GameVersion = host.GameVersion, SteamBuildId = host.SteamBuildId, SaveVersion = host.SaveVersion, ManifestHash = host.ManifestHash };
        Assert.Null(HandshakeValidator.Validate(host, Ok(), false, false));
        var h = Ok(); h.Proto = 9;
        Assert.Equal(RejectCode.Protocol, HandshakeValidator.Validate(host, h, false, false)!.Value.Code);
        h = Ok(); h.ModVersion = "0.0.9";
        var v = HandshakeValidator.Validate(host, h, false, false)!.Value;
        Assert.Equal(RejectCode.ModVersion, v.Code);
        Assert.Contains("0.0.9", v.Detail); Assert.Contains(host.ModVersion, v.Detail);
        h = Ok(); h.GameVersion = "1.0.0";
        Assert.Equal(RejectCode.GameBuild, HandshakeValidator.Validate(host, h, false, false)!.Value.Code);
        h = Ok(); h.SaveVersion = 43;
        Assert.Equal(RejectCode.SaveVersion, HandshakeValidator.Validate(host, h, false, false)!.Value.Code);
        h = Ok(); h.SteamBuildId = 1;
        Assert.Equal(RejectCode.GameBuild, HandshakeValidator.Validate(host, h, false, false)!.Value.Code);
        h = Ok(); h.SteamBuildId = 0; // unknown on one side: skipped
        Assert.Null(HandshakeValidator.Validate(host, h, false, false));
        h = Ok(); h.ManifestHash = 1;
        Assert.Equal(RejectCode.Manifest, HandshakeValidator.Validate(host, h, false, false)!.Value.Code);
        h = Ok(); h.ManifestHash = 0; // not cached
        Assert.Null(HandshakeValidator.Validate(host, h, false, false));
        Assert.Equal(RejectCode.Kicked, HandshakeValidator.Validate(host, Ok(), true, true)!.Value.Code);
        Assert.Equal(RejectCode.Full, HandshakeValidator.Validate(host, Ok(), true, false)!.Value.Code);
        h = Ok(); h.Proto = 9; // first failure wins
        Assert.Equal(RejectCode.Protocol, HandshakeValidator.Validate(host, h, true, true)!.Value.Code);
    }

    [Fact]
    public void Names_are_sanitised_and_deduplicated()
    {
        Assert.Equal("Bob", PlayerRegistry.SanitizeName(" B\u0007ob\n "));
        Assert.Equal("Player", PlayerRegistry.SanitizeName("\u0001\u0002"));
        Assert.Equal(24, PlayerRegistry.SanitizeName(new string('x', 40)).Length);
        var reg = new PlayerRegistry();
        reg.Add(new PlayerInfo { Slot = 0, Name = "Alice" });
        Assert.Equal("alice (2)", reg.UniqueName("alice")); // case-insensitive clash, keeps the requested spelling
        reg.Add(new PlayerInfo { Slot = 1, Name = "Alice (2)" });
        Assert.Equal("Alice (3)", reg.UniqueName("Alice"));
        Assert.True(reg.UniqueName(new string('y', 24)).Length <= 24);
    }

    [Fact]
    public void Slot_allocation_and_reservations()
    {
        var reg = new PlayerRegistry { MaxPlayers = 4 };
        reg.Add(new PlayerInfo { Slot = 0, Name = "Host" });
        Assert.Equal((byte)1, reg.AllocateSlot(100, 0));
        reg.Add(new PlayerInfo { Slot = 1, SteamId = 100 });
        reg.Add(new PlayerInfo { Slot = 2, SteamId = 200 });
        reg.Remove(1);
        reg.Reserve(100, 1, 0, 300);
        Assert.Equal((byte)3, reg.AllocateSlot(300, 10));   // slot 1 is reserved for 100
        Assert.Equal((byte)1, reg.AllocateSlot(100, 10));   // 100 gets its own slot back
        reg.Add(new PlayerInfo { Slot = 3, SteamId = 300 });
        Assert.Equal((byte)1, reg.AllocateSlot(400, 10));   // nothing else free: the reservation is reclaimed
        Assert.Empty(reg.Reservations);
        reg.Reserve(100, 1, 0, 300);
        Assert.Equal((byte)1, reg.AllocateSlot(400, 301));  // expired
    }
}
