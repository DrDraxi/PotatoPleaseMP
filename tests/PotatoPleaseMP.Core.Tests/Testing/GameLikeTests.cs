using System.Diagnostics;
using PotatoPleaseMP.Core;
using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Testing;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Testing;

/// <summary>The plugin's wiring: a real-time clock, bots updated before the host, BotHub defaults.</summary>
public class GameLikeTests
{
    sealed class World : IHostWorld
    {
        public VersionInfo Version { get; } = new(Envelope.ProtocolVersion, "0.0.2", "1.0.1", 26422447, 44);
        public byte Difficulty => 0;
        public QueueReason? JoinGate() => null;
        public bool IsShiftOver => false;
        public HostSnapshot CaptureSnapshot(SnapshotReason reason) => new()
        {
            SaveUtcTicks = 1, SaveVersion = 44, Save = Task.FromResult(TestData.SaveLike(163849, 7)),
        };
    }

    [Fact]
    public void Bot_joins_with_a_realtime_clock()
    {
        var sw = Stopwatch.StartNew();
        double Now() => sw.Elapsed.TotalSeconds;
        var hub = new LoopbackHub(Now);
        var hostId = new PeerId(0x0110000105324FF4UL);
        var world = new World();
        var host = new HostSession(hub.CreateTransport(hostId), world, new HostConfig { HostSteamId = hostId.Value, HostName = "H" }, now: Now());
        var warnings = new List<string>();
        host.Warning += w => warnings.Add(w);
        host.Start(Now(), worldReady: true);
        var bots = new BotHub(hub, hostId, () => new BotOptions { Version = world.Version, LoadSeconds = 1.5 });
        var bot = bots.Add("Ivan", Now());
        var deadline = Now() + 15;
        while (Now() < deadline && host.Players.BySlot(1)?.Phase != PlayerPhase.Playing)
        {
            bots.Update(Now());
            host.Update(Now());
            bots.Prune();
            Thread.Sleep(16);
        }
        var end = Now() + 4;
        bot.Walk(new[] { new Vec3(5, 0, 0), new Vec3(-5, 0, 0), new Vec3(5, 0, 0) });
        while (Now() < end) { bots.Update(Now()); host.Update(Now()); Thread.Sleep(16); }
        var link = host.Net.Link(bot.Id)!;
        Assert.True(link.InCountById[0x10] > 20, $"host got {link.InCountById[0x10]} poses, bot sent {bot.PosesSent}, envIn {link.EnvelopesIn}");
        Assert.True(host.Players.BySlot(1)?.Phase == PlayerPhase.Playing,
            $"host sees {host.Players.BySlot(1)?.Phase}, bot {bot.State}; warnings: {string.Join(" | ", warnings)}");
    }
}
