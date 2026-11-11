using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Wire;

public class RegistryTests
{
    [Fact]
    public void Default_registry_passes_the_self_check()
    {
        var g = MessageRegistry.CreateDefault();
        var problems = g.SelfCheck(i => (byte)i.Id <= 0x0E || ((byte)i.Id >= 0xE0 && (byte)i.Id <= 0xE3) || i.Id == MsgId.PoseUpdate);
        Assert.Empty(problems);
        // Every enum value is described, owners match the roadmap ranges.
        foreach (MsgId id in Enum.GetValues<MsgId>()) Assert.NotNull(g[id]);
        Assert.Equal(19, g[MsgId.EndState]!.Owner);
        Assert.Equal(20, g[MsgId.PeerError]!.Owner);
        Assert.Equal(18, g[MsgId.TestMark]!.Owner);
        Assert.Equal(9, g[MsgId.PaLine]!.Owner);
        Assert.Null(g[(MsgId)0x2A]); // ribbons moved to 0x86
    }

    [Fact]
    public void Self_check_finds_problems()
    {
        var g = new MessageRegistry();
        g.Register(MsgId.Balance, MsgDir.HostToClient, DeliveryClass.R, owner: 9);
        g.Register(MsgId.Counters, MsgDir.HostToClient, DeliveryClass.R, name: "Balance");
        g.Register(MsgId.Quota, MsgDir.HostToClient, DeliveryClass.R, 70 * 1024);
        g.Register(MsgId.PileDigests, MsgDir.HostToClient, DeliveryClass.U, 5000);
        var p = g.SelfCheck(_ => true);
        Assert.Contains(p, s => s.Contains("owner"));
        Assert.Contains(p, s => s.Contains("duplicate name"));
        Assert.Contains(p, s => s.Contains("MaxLen"));
        Assert.Contains(p, s => s.Contains("no codec"));
    }

    [Fact]
    public void Default_classes()
    {
        var g = MessageRegistry.CreateDefault();
        Assert.Equal(SessionClass.Session, g[MsgId.Hello]!.Class);
        Assert.Equal(SessionClass.Session, g[MsgId.BlobChunk]!.Class);
        Assert.Equal(SessionClass.WorldChange, g[MsgId.Balance]!.Class);
        Assert.Equal(SessionClass.State, g[MsgId.PoseUpdate]!.Class);
        Assert.Equal(SessionClass.State, g[MsgId.PurchaseReq]!.Class);
        Assert.Equal(SessionClass.Session, g[MsgId.WorldManifest]!.Class);
    }
}

public class NetLayerTests
{
    sealed class Pair : IDisposable
    {
        public readonly LoopbackHub Hub = new();
        public readonly NetLayer Host, Client;
        public readonly PeerId HostId = new(0x0110000100000001), ClientId = new(0x0110000100000002);
        public readonly List<NetLayerEvent> HostEv = new(), ClientEv = new();

        public Pair(double lat = 0, double jit = 0, double loss = 0)
        {
            Hub.LatencyMs = lat; Hub.JitterMs = jit; Hub.UnreliableLossPercent = loss;
            var ht = Hub.CreateTransport(HostId);
            var ct = Hub.CreateTransport(ClientId);
            Host = new NetLayer(ht, MessageRegistry.CreateDefault(), true) { StampSource = t => (ushort)(t * 30) };
            Client = new NetLayer(ct, MessageRegistry.CreateDefault(), false);
            ht.StartHost(4, null);
            ct.Connect(HostId);
            for (int i = 0; i < 200 && !(Host.HasPeer(ClientId) && Client.HasPeer(HostId)); i++) Step();
            Assert.True(Host.HasPeer(ClientId) && Client.HasPeer(HostId));
        }

        public void Step(double dt = 1 / 30.0)
        {
            Hub.Advance(dt);
            Host.Poll(Hub.Now, HostEv);
            Client.Poll(Hub.Now, ClientEv);
            Host.Flush(Hub.Now);
            Client.Flush(Hub.Now);
        }

        public void Dispose() { Host.Dispose(); Client.Dispose(); }
    }

    [Fact]
    public void Wrong_direction_and_oversized_messages_are_dropped_and_counted()
    {
        using var p = new Pair();
        p.Client.Send(p.HostId, MsgId.Balance, new byte[] { 1 });      // H->C id sent by a client
        p.Client.Send(p.HostId, MsgId.LoadProgress, new byte[40]);    // MaxLen 8
        p.Client.Send(p.HostId, MsgId.LoadProgress, new byte[] { 3, 50 });
        p.Client.SendUnreliable(p.HostId, MsgId.ClientReady, new byte[10]); // reliable-only id on the unreliable channel
        for (int i = 0; i < 5; i++) p.Step();
        var msgs = p.HostEv.Where(e => e.Kind == NetLayerEventKind.Message).ToList();
        Assert.Single(msgs);
        Assert.Equal(MsgId.LoadProgress, msgs[0].Id);
        Assert.Equal(3, p.Host.Link(p.ClientId)!.RejectedMessages);
    }

    [Fact]
    public void Loopback_end_to_end_with_latency_and_loss()
    {
        using var p = new Pair(lat: 120, jit: 20, loss: 10);
        int sent = 0;
        for (int t = 0; t < 300; t++)
        {
            for (int k = 0; k < 3; k++) p.Host.Send(p.ClientId, MsgId.Balance, BitConverter.GetBytes(sent++));
            p.Host.SendUnreliable(p.ClientId, MsgId.PileDigests, BitConverter.GetBytes(t));
            p.Step();
        }
        for (int t = 0; t < 30; t++) p.Step();
        var rel = p.ClientEv.Where(e => e.Kind == NetLayerEventKind.Message && e.Id == MsgId.Balance).Select(e => BitConverter.ToInt32(e.Payload)).ToList();
        Assert.Equal(Enumerable.Range(0, sent), rel); // complete and ordered
        int unrel = p.ClientEv.Count(e => e.Kind == NetLayerEventKind.Message && e.Id == MsgId.PileDigests);
        Assert.InRange(unrel, 230, 295); // ~10 % lost
        Assert.Equal(0, p.Host.Link(p.ClientId)!.UnackedBytes > 64 * 1024 ? 1 : 0);
    }

    [Fact]
    public void One_lost_reliable_envelope_drops_the_peer_as_connection_lost()
    {
        using var p = new Pair();
        p.Host.Send(p.ClientId, MsgId.Balance, new byte[] { 1 });
        p.Step();
        p.Hub.DropNextReliable(p.HostId, p.ClientId);
        p.Host.Send(p.ClientId, MsgId.Balance, new byte[] { 2 });
        p.Step();
        p.Host.Send(p.ClientId, MsgId.Balance, new byte[] { 3 });
        p.Step();
        p.Step(); // flushed at the end of the previous step, received in this one
        var d = p.ClientEv.Single(e => e.Kind == NetLayerEventKind.Disconnected);
        Assert.Equal(DisconnectReason.ConnectionLost, d.Reason);
        Assert.False(p.Client.HasPeer(p.HostId));
    }

    [Fact]
    public void Final_message_travels_with_the_bye()
    {
        using var p = new Pair();
        p.Host.Send(p.ClientId, MsgId.Balance, new byte[] { 1 });
        p.Host.Disconnect(p.ClientId, p.Hub.Now, new Goodbye { Reason = LeaveReason.HostLeft, Text = "bye" });
        p.Step();
        p.Step();
        Assert.Contains(p.ClientEv, e => e.Kind == NetLayerEventKind.Message && e.Id == MsgId.Balance);
        var d = p.ClientEv.Single(e => e.Kind == NetLayerEventKind.Disconnected);
        Assert.Equal(MsgId.Goodbye, d.FinalId);
        var g = SessionMessages.Decode<Goodbye>(d.Payload);
        Assert.Equal(LeaveReason.HostLeft, g.Reason);
        Assert.Equal("bye", g.Text);
    }

    [Fact]
    public void Fuzz_envelopes_and_codecs_only_throw_wire_format_exceptions()
    {
        var g = MessageRegistry.CreateDefault();
        var rng = new Random(11);
        var seeds = new List<byte[]>();
        using (var w = new NetWriter())
        {
            foreach (var info in g.All.Where(i => i.Codec != null))
            {
                var body = new EnvelopeBody();
                body.TryAppend((byte)info.Id, TestData.Random(rng.Next(0, 40), rng.Next()), int.MaxValue);
                seeds.Add(Envelope.Build(new EnvelopeHeader { Kind = EnvelopeKind.Reliable, Seq = 1, HasAck = true }, body.Span));
            }
        }
        var parsed = new ParsedEnvelope();
        for (int i = 0; i < 100_000; i++)
        {
            byte[] data;
            if (i % 3 == 0) data = TestData.Random(rng.Next(0, 80), i);
            else
            {
                data = (byte[])seeds[rng.Next(seeds.Count)].Clone();
                for (int k = rng.Next(1, 4); k > 0; k--) data[rng.Next(data.Length)] ^= (byte)rng.Next(1, 256);
                if (rng.Next(4) == 0) Array.Resize(ref data, rng.Next(data.Length + 1));
            }
            var err = Envelope.TryParse(data, i % 2 == 0 ? Delivery.Reliable : Delivery.Unreliable, parsed);
            if (err != ProtocolError.None) continue;
            foreach (var m in parsed.Messages)
            {
                if (!g.TryGet(m.Id, out var info) || info.Codec == null) continue;
                var codec = info.Codec();
                try
                {
                    var r = new NetReader(parsed.Payload(m));
                    codec.Read(ref r);
                }
                catch (WireFormatException) { }
            }
        }
    }
}
