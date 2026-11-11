using System.Buffers.Binary;
using PotatoPleaseMP.Core.Net;

namespace PotatoPleaseMP.Core.Tests.Net;

public class P2PTransportTests
{
    static readonly PeerId H = new(0x0110000100000001), C = new(0x0110000100000002), C2 = new(0x0110000100000003);

    sealed class Net2
    {
        public readonly LoopbackHub Hub = new();
        public readonly P2PTransport Host, Client;
        public readonly List<NetEvent> HostEv = new(), ClientEv = new();

        public Net2(int maxPeers = 4, Func<PeerId, bool>? filter = null, Action<LoopbackHub>? hub = null, bool host = true, bool connect = true)
        {
            hub?.Invoke(Hub);
            Host = Hub.CreateTransport(H);
            Client = Hub.CreateTransport(C);
            if (host) Host.StartHost(maxPeers, filter);
            if (connect) Client.Connect(H);
        }

        public void Step(double dt = 1 / 60.0)
        {
            Hub.Advance(dt);
            Host.Poll(HostEv);
            Client.Poll(ClientEv);
        }

        public void Run(double s, double dt = 1 / 60.0) { for (double t = 0; t < s; t += dt) Step(dt); }

        public bool Connected => ClientEv.Any(e => e.Kind == NetEventKind.Connected) && HostEv.Any(e => e.Kind == NetEventKind.Connected);
    }

    [Fact]
    public void Connect_and_accept()
    {
        var n = new Net2(hub: h => h.LatencyMs = 30);
        n.Run(0.5);
        Assert.True(n.Connected);
        Assert.True(n.Client.Send(H, new byte[] { 1, 2 }, Delivery.Reliable));
        n.Run(0.2);
        var d = n.HostEv.Single(e => e.Kind == NetEventKind.Data);
        Assert.Equal(C, d.Peer);
        Assert.Equal(new byte[] { 1, 2 }, d.Data);
        Assert.Equal(Delivery.Reliable, d.Delivery);
    }

    [Fact]
    public void Lost_accept_is_repeated_for_the_same_token_and_a_new_token_replaces()
    {
        var n = new Net2();
        // The host accepts, but its Accepts vanish for a while: the client repeats Connect at 1 Hz (same token).
        n.Hub.SetPipeDown(H, C, true);
        n.Run(1.5);
        Assert.Single(n.HostEv, e => e.Kind == NetEventKind.Connected);
        Assert.DoesNotContain(n.ClientEv, e => e.Kind == NetEventKind.Connected);
        n.Hub.SetPipeDown(H, C, false);
        n.Run(2);
        Assert.True(n.Connected);
        Assert.Single(n.HostEv, e => e.Kind == NetEventKind.Connected); // same token: one peer, Accept repeated

        // The same client reconnects with a new token (crashed and came back): old peer Replaced, new Connected.
        var again = new Net2(connect: false);
        again.Client.Connect(H);
        again.Run(0.5);
        again.Client.Connect(H); // new token, same id
        again.Run(0.5);
        Assert.Contains(again.HostEv, e => e.Kind == NetEventKind.Disconnected && e.Reason == DisconnectReason.Replaced);
        Assert.Equal(2, again.HostEv.Count(e => e.Kind == NetEventKind.Connected));
    }

    [Fact]
    public void Rejects_full_filter_version_and_not_hosting()
    {
        var full = new Net2(maxPeers: 0);
        full.Run(0.5);
        var d = full.ClientEv.Single(e => e.Kind == NetEventKind.Disconnected);
        Assert.Equal(DisconnectReason.Rejected, d.Reason);
        Assert.Equal((byte)RejectReason.Full, d.Code);

        var filt = new Net2(filter: _ => false);
        filt.Run(0.5);
        Assert.Equal((byte)RejectReason.NotAllowed, filt.ClientEv.Single(e => e.Kind == NetEventKind.Disconnected).Code);

        var nh = new Net2(host: false);
        nh.Host.Connect(new PeerId(0x0110000100000099)); // running, but not hosting
        nh.Run(0.5);
        Assert.Equal((byte)RejectReason.NotHosting, nh.ClientEv.Single(e => e.Kind == NetEventKind.Disconnected).Code);

        // linkVer 2: a raw Connect frame from a foreign link.
        var hub = new LoopbackHub();
        var host = hub.CreateTransport(H);
        host.StartHost(4, null);
        var raw = hub.CreateLink(C);
        var frame = new byte[15];
        frame[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), P2PTransport.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(7), 77);
        raw.Send(H.Value, frame, 0, LinkFlags.Reliable);
        hub.Advance(0.1);
        host.Poll(new List<NetEvent>());
        hub.Advance(0.1);
        var got = new List<(ulong, byte[])>();
        raw.Receive(0, got);
        Assert.Equal(3, got.Single().Item2[0]);                    // Reject
        Assert.Equal((byte)RejectReason.Version, got.Single().Item2[9]);
    }

    [Fact]
    public void Unreachable_host_fails_at_30_seconds()
    {
        var n = new Net2(host: false, connect: false);
        n.Client.Connect(new PeerId(0x0110000100000777)); // nobody there
        n.Run(29.5, 0.25);
        Assert.DoesNotContain(n.ClientEv, e => e.Kind == NetEventKind.Disconnected);
        n.Run(1, 0.25);
        Assert.Equal(DisconnectReason.ConnectFailed, n.ClientEv.Single(e => e.Kind == NetEventKind.Disconnected).Reason);
    }

    [Fact]
    public void Busy_link_queues_reliable_in_order_until_drained()
    {
        var n = new Net2(hub: h => { h.SendBufferBytes = 64 * 1024; h.SendRateBytesPerSec = 100 * 1024; });
        n.Run(0.5);
        Assert.True(n.Connected);
        int count = 2 * 1024 * 1024 / 4096;
        for (int i = 0; i < count; i++)
        {
            var m = new byte[4096];
            BinaryPrimitives.WriteInt32LittleEndian(m, i);
            Assert.True(n.Client.Send(H, m, Delivery.Reliable));
        }
        Assert.True(n.Client.PendingReliableBytes(H) > 1_000_000);
        n.Run(25, 0.05);
        var got = n.HostEv.Where(e => e.Kind == NetEventKind.Data).Select(e => BinaryPrimitives.ReadInt32LittleEndian(e.Data)).ToList();
        Assert.Equal(Enumerable.Range(0, count), got);
        Assert.True(n.Client.TryGetStats(H, out var st));
        Assert.True(st.BusyCount > 0);
        n.Run(0.5);
        Assert.Equal(0, n.Client.PendingReliableBytes(H));
    }

    [Fact]
    public void Queue_overflow_loses_the_connection()
    {
        var n = new Net2(hub: h => { h.SendBufferBytes = 16 * 1024; h.SendRateBytesPerSec = 1024; });
        n.Run(0.5);
        var big = new byte[512 * 1024];
        for (int i = 0; i < 66; i++) n.Client.Send(H, big, Delivery.Reliable); // > 32 MB
        n.Step();
        var d = n.ClientEv.Single(e => e.Kind == NetEventKind.Disconnected);
        Assert.Equal(DisconnectReason.ConnectionLost, d.Reason);
        Assert.Equal("SendOverflow", d.Detail);
    }

    [Fact]
    public void Failed_send_ends_the_connection_on_the_next_poll()
    {
        var n = new Net2();
        n.Run(0.5);
        n.Hub.SetFailSends(C, H, true);
        Assert.False(n.Client.Send(H, new byte[] { 1 }, Delivery.Reliable));
        n.Step();
        Assert.Equal(DisconnectReason.ConnectionLost, n.ClientEv.Single(e => e.Kind == NetEventKind.Disconnected).Reason);
    }

    [Fact]
    public void Timeout_without_traffic_pings_keep_alive_and_measure_rtt()
    {
        var n = new Net2(hub: h => h.LatencyMs = 50);
        n.Run(60, 0.05); // idle but pinging
        Assert.DoesNotContain(n.ClientEv, e => e.Kind == NetEventKind.Disconnected);
        Assert.True(n.Client.TryGetStats(H, out var st));
        Assert.InRange(st.RttMs, 95, 110);

        n.Hub.SetLinkDown(H, C, true);
        n.Run(43, 0.25);
        Assert.DoesNotContain(n.ClientEv, e => e.Kind == NetEventKind.Disconnected);
        n.Run(3, 0.25);
        Assert.Equal(DisconnectReason.Timeout, n.ClientEv.Single(e => e.Kind == NetEventKind.Disconnected).Reason);
        Assert.Equal(DisconnectReason.Timeout, n.HostEv.Single(e => e.Kind == NetEventKind.Disconnected).Reason);
    }

    [Fact]
    public void Bye_after_data_in_the_same_poll_is_applied_after_the_data()
    {
        var n = new Net2();
        n.Run(0.5);
        for (byte i = 0; i < 3; i++) n.Host.Send(C, new[] { i }, Delivery.Reliable);
        n.Host.Disconnect(C, 7, new byte[] { 0xAA, 0xBB });
        n.Step();
        var ev = n.ClientEv.Where(e => e.Kind != NetEventKind.Connected).ToList();
        Assert.Equal(4, ev.Count);
        Assert.All(ev.Take(3), e => Assert.Equal(NetEventKind.Data, e.Kind));
        var bye = ev[3];
        Assert.Equal(DisconnectReason.RemoteBye, bye.Reason);
        Assert.Equal(7, bye.Code);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, bye.Data);
        Assert.Equal(DisconnectReason.Local, n.HostEv.Last().Reason);
    }

    [Fact]
    public void Unreliable_channel_size_and_loss()
    {
        var n = new Net2(hub: h => { h.UnreliableLossPercent = 30; h.JitterMs = 40; });
        n.Run(1);
        Assert.False(n.Client.Send(H, new byte[1101], Delivery.Unreliable));
        Assert.True(n.Client.TryGetStats(H, out var st0) && st0.Oversize == 1);
        for (int i = 0; i < 500; i++)
        {
            n.Client.Send(H, BitConverter.GetBytes(i), Delivery.Unreliable);
            n.Client.Send(H, BitConverter.GetBytes(i), Delivery.Reliable);
            n.Step(0.01);
        }
        n.Run(1);
        var data = n.HostEv.Where(e => e.Kind == NetEventKind.Data).ToList();
        var rel = data.Where(e => e.Delivery == Delivery.Reliable).Select(e => BitConverter.ToInt32(e.Data)).ToList();
        var unrel = data.Where(e => e.Delivery == Delivery.Unreliable).ToList();
        Assert.Equal(Enumerable.Range(0, 500), rel);  // jitter never reorders reliable
        Assert.InRange(unrel.Count, 280, 420);         // loss hits unreliable only
    }

    [Fact]
    public void Expect_and_hello_release_a_held_connect()
    {
        var n = new Net2(hub: h => h.RequireAccept = true);
        n.Run(2);
        Assert.False(n.Connected); // held: the host never accepted the session
        n.Host.Expect(C);
        n.Run(1);
        Assert.True(n.Connected);

        // A stray (data from a peer without a connection) is counted, not delivered.
        var hub = new LoopbackHub();
        var host = hub.CreateTransport(H);
        host.StartHost(4, null);
        var raw = hub.CreateLink(C2);
        raw.Send(H.Value, new byte[] { 1, 2, 3 }, 1, LinkFlags.Reliable);
        hub.Advance(0.1);
        var ev = new List<NetEvent>();
        host.Poll(ev);
        Assert.Empty(ev);
        Assert.Equal(1, host.Strays);
    }

    [Fact]
    public void Deferred_close_after_linger_and_cancelled_by_a_new_connect()
    {
        var n = new Net2();
        n.Run(0.5);
        n.Host.Disconnect(C);
        Assert.Equal(1, n.Host.DeferredCloses);
        n.Run(5);
        Assert.Equal(1, n.Host.DeferredCloses); // still lingering (13 s)
        n.Client.Connect(H);                     // the same id comes back before the close
        n.Run(1);
        Assert.Equal(0, n.Host.DeferredCloses);
        Assert.Equal(2, n.HostEv.Count(e => e.Kind == NetEventKind.Connected));
        n.Run(15);
        Assert.Equal(1, n.HostEv.Count(e => e.Kind == NetEventKind.Disconnected)); // the new connection survived
        n.Host.Disconnect(C);
        n.Run(14, 0.25);
        Assert.Equal(0, n.Host.DeferredCloses);

        // Stop defers its closes to the static pump.
        var m = new Net2();
        m.Run(0.5);
        int before = P2PTransport.PendingDeferredCloses;
        m.Host.Stop(9);
        Assert.False(m.Host.IsRunning);
        Assert.True(P2PTransport.PendingDeferredCloses >= before + 1);
        m.Run(0.2);
        Assert.Equal(9, m.ClientEv.Single(e => e.Kind == NetEventKind.Disconnected).Code);
        m.Hub.Advance(14);
        P2PTransport.PumpDeferredCloses();
    }

    [Fact]
    public void Self_connect_is_refused()
    {
        var hub = new LoopbackHub();
        var t = hub.CreateTransport(H);
        t.Connect(H);
        var ev = new List<NetEvent>();
        t.Poll(ev);
        Assert.Equal(DisconnectReason.ConnectFailed, ev.Single().Reason);
    }

    [Fact]
    public void Pop_ids_decode()
    {
        uint Pop(string s) => (uint)((s.Length > 3 ? s[3] << 24 : 0) | (s[0] << 16) | (s[1] << 8) | s[2]);
        Assert.Equal("fra", PopId.Decode(Pop("fra")));
        Assert.Equal("iad", PopId.Decode(Pop("iad")));
        Assert.Equal("sto2", PopId.Decode(Pop("sto2")));
        Assert.Null(PopId.Decode(0));
    }
}

public class LoopbackHubTests
{
    static readonly PeerId A = new(1), B = new(2);

    [Fact]
    public void Latency_jitter_order_and_loss()
    {
        var hub = new LoopbackHub { LatencyMs = 100, JitterMs = 50, UnreliableLossPercent = 50 };
        var a = hub.CreateLink(A);
        var b = hub.CreateLink(B);
        for (int i = 0; i < 200; i++)
        {
            a.Send(B.Value, BitConverter.GetBytes(i), i % 2 == 0 ? 1 : 0, LinkFlags.Reliable); // reliable on two channels
            a.Send(B.Value, BitConverter.GetBytes(i), 2, LinkFlags.Unreliable);
        }
        var got = new List<(ulong, byte[])>();
        hub.Advance(0.099);
        b.Receive(1, got);
        Assert.Empty(got); // nothing before the latency
        hub.Advance(0.1);
        var rel = new List<(ulong, byte[])>();
        b.Receive(1, rel);
        b.Receive(0, rel);
        var un = new List<(ulong, byte[])>();
        b.Receive(2, un);
        Assert.Equal(200, rel.Count);
        Assert.Equal(Enumerable.Range(0, 100).Select(i => i * 2), rel.Take(100).Select(x => BitConverter.ToInt32(x.Item2)));
        Assert.InRange(un.Count, 60, 140);
    }

    [Fact]
    public void Link_down_and_up()
    {
        var hub = new LoopbackHub();
        var a = hub.CreateLink(A);
        var b = hub.CreateLink(B);
        hub.SetLinkDown(A, B, true);
        a.Send(B.Value, new byte[] { 1 }, 1, LinkFlags.Reliable);
        hub.SetLinkDown(A, B, false);
        a.Send(B.Value, new byte[] { 2 }, 1, LinkFlags.Reliable);
        var got = new List<(ulong, byte[])>();
        b.Receive(1, got);
        Assert.Equal(new byte[] { 2 }, got.Single().Item2);
        Assert.Equal(1, hub.PacketsDropped);
    }

    [Fact]
    public void Closed_session_fails_sends_until_restarted()
    {
        var hub = new LoopbackHub();
        var a = hub.CreateLink(A);
        var b = hub.CreateLink(B);
        a.Send(B.Value, new byte[] { 1 }, 1, LinkFlags.Reliable);
        b.Close(A.Value);
        Assert.Equal(LinkState.ClosedByPeer, a.Status(B.Value).State);
        Assert.Equal(LinkSend.Failed, a.Send(B.Value, new byte[] { 2 }, 1, LinkFlags.Reliable));
        Assert.Equal(LinkSend.Ok, a.Send(B.Value, new byte[] { 3 }, 0, LinkFlags.Reliable | LinkFlags.RestartBroken));
        var got = new List<(ulong, byte[])>();
        b.Receive(0, got);
        Assert.Single(got);
    }
}

public class CompositeTransportTests
{
    static readonly PeerId Host = new(0x0110000100000001), Human = new(0x0110000100000002);

    [Fact]
    public void Bot_ids_go_to_the_hub_others_to_the_primary()
    {
        var steam = new LoopbackHub();   // stands in for Steam
        var bots = new LoopbackHub();
        var primary = steam.CreateTransport(Host);
        var comp = new CompositeTransport(primary, bots.CreateTransport(Host)) { LoopbackEnabled = true };
        comp.StartHost(4, null);
        var human = steam.CreateTransport(Human);
        var bot = bots.CreateTransport(BotIds.Make(1));
        human.Connect(Host);
        bot.Connect(Host);
        var ev = new List<NetEvent>();
        var tmp = new List<NetEvent>();
        for (int i = 0; i < 10; i++)
        {
            steam.Advance(0.05); bots.Advance(0.05);
            comp.Poll(ev); human.Poll(tmp); bot.Poll(tmp);
        }
        Assert.Equal(2, ev.Count(e => e.Kind == NetEventKind.Connected));
        var peers = new List<PeerId>();
        comp.CollectPeers(peers);
        Assert.Equal(2, peers.Count);
        Assert.True(comp.Send(BotIds.Make(1), new byte[] { 1 }, Delivery.Reliable));
        Assert.True(comp.Send(Human, new byte[] { 2 }, Delivery.Reliable));
        Assert.Equal(TransportKind.Composite, comp.Kind);

        comp.LoopbackEnabled = false; // not hosting any more: bot traffic is dropped
        Assert.False(comp.Send(BotIds.Make(1), new byte[] { 3 }, Delivery.Reliable));
        Assert.True(comp.LoopbackDropped > 0);

        comp.Dispose(); // keeps the primary
        Assert.True(primary.IsRunning);
        Assert.True(comp.Send(Human, new byte[] { 4 }, Delivery.Reliable));
        Assert.Throws<ArgumentException>(() => new CompositeTransport(primary, bots.CreateTransport(new PeerId(55))));
    }

    [Fact]
    public void Bot_ids_never_collide_with_steam_ids()
    {
        for (int i = 1; i <= BotIds.MaxIndex; i++)
        {
            var id = BotIds.Make(i);
            Assert.True(id.IsBot);
            Assert.True(id.IsUniverseZero);
            Assert.Equal(i, BotIds.IndexOf(id));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => BotIds.Make(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BotIds.Make(16));
        Assert.False(new PeerId(0x0110000100000001).IsBot);
        Assert.False(new PeerId(0x0110000100000001).IsUniverseZero);
        Assert.False(new PeerId((BotIds.Prefix << 32) | 16).IsBot);
        Assert.True(UdpIds.Make(0).IsUdp);
        Assert.False(UdpIds.Make(0).IsBot);
    }
}
