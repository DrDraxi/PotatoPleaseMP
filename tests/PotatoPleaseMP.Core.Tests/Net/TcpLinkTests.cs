using System.Diagnostics;
using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Testing;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Net;

/// <summary>A real client session joins a host over the direct TCP link (two game copies on one PC).</summary>
public class TcpLinkTests
{
    sealed class World : IHostWorld
    {
        public VersionInfo Version { get; } = new(Envelope.ProtocolVersion, "0.0.2", "1.0.1", 1, 44);
        public byte Difficulty => 0;
        public QueueReason? JoinGate() => null;
        public bool IsShiftOver => false;
        public HostSnapshot CaptureSnapshot(SnapshotReason reason) => new() { SaveUtcTicks = 1, SaveVersion = 44, Save = Task.FromResult(TestData.SaveLike(200_000, 3)) };
    }

    [Fact]
    public void Client_joins_over_tcp()
    {
        var sw = Stopwatch.StartNew();
        double Now() => sw.Elapsed.TotalSeconds;
        var hostLink = new TcpLink(UdpIds.Make(0).Value);
        hostLink.Listen(0, any: false);
        var hostT = new P2PTransport(hostLink, Now);
        var world = new World();
        var host = new HostSession(hostT, world, new HostConfig { HostName = "H" }, now: Now());
        host.Start(Now(), worldReady: true, acceptFilter: p => p.IsUdp);

        var clientLink = new TcpLink(UdpIds.Make(1).Value);
        var hostId = clientLink.ConnectTo("127.0.0.1", hostLink.Port);
        Assert.Equal(hostLink.LocalId, hostId);
        var clientT = new P2PTransport(clientLink, Now);
        var cw = new BotClientWorld(world.Version, 0.5);
        var client = new ClientSession(clientT, cw, new ClientConfig { Name = "C" });
        client.Connect(new PeerId(hostId), Now());

        var end = Now() + 15;
        while (Now() < end && !(client.State == ClientState.Playing && host.Players.BySlot(1)?.Phase == PlayerPhase.Playing))
        {
            host.Update(Now());
            client.Update(Now());
            Thread.Sleep(5);
        }
        Assert.Equal(ClientState.Playing, client.State);
        Assert.Equal(PlayerPhase.Playing, host.Players.BySlot(1)?.Phase);
        Assert.Equal(200_000, cw.Snapshot!.Save.Length);
        hostLink.Dispose(); clientLink.Dispose();
    }
}
