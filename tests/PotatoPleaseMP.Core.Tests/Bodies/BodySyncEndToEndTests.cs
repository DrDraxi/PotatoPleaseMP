using System.Buffers.Binary;
using PotatoPleaseMP.Core.Bodies;
using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Wire;
using Xunit.Abstractions;

namespace PotatoPleaseMP.Core.Tests.Bodies;

/// <summary>
/// Headless host + 3 clients over the loopback hub (latency, jitter, unreliable loss): HostBodySync streams simulated
/// bodies, ClientBodyMirror applies them. Frames are <c>[u8 id][u32 hostTick][payload]</c>, reliable on channel 1 and
/// unreliable on channel 2 (the envelope layer is tested elsewhere).
/// </summary>
public class BodySyncEndToEndTests
{
    readonly ITestOutputHelper _log;
    public BodySyncEndToEndTests(ITestOutputHelper log) => _log = log;

    const double TickRate = 30, Dt = 1 / TickRate;

    sealed class Net
    {
        public readonly LoopbackHub Hub;
        public readonly LoopbackLink Host;
        public readonly Dictionary<byte, LoopbackLink> Clients = new();
        readonly List<(ulong From, byte[] Data)> _buf = new();

        public Net(double latencyMs, double jitterMs, double loss, IEnumerable<byte> slots)
        {
            Hub = new LoopbackHub(seed: 4242) { LatencyMs = latencyMs, JitterMs = jitterMs, UnreliableLossPercent = loss };
            Host = Hub.CreateLink(new PeerId(1));
            foreach (var s in slots) Clients[s] = Hub.CreateLink(new PeerId(100UL + s));
        }

        public static byte[] Frame(MsgId id, uint tick, ReadOnlySpan<byte> payload)
        {
            var b = new byte[5 + payload.Length];
            b[0] = (byte)id;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), tick);
            payload.CopyTo(b.AsSpan(5));
            return b;
        }

        public void SendToClient(byte slot, MsgId id, uint tick, byte[] payload, bool reliable)
            => Host.Send(100UL + slot, Frame(id, tick, payload), reliable ? 1 : 2, reliable ? LinkFlags.Reliable : LinkFlags.Unreliable);

        public void SendToHost(byte slot, MsgId id, uint tick, byte[] payload, bool reliable)
            => Clients[slot].Send(1, Frame(id, tick, payload), reliable ? 1 : 2, reliable ? LinkFlags.Reliable : LinkFlags.Unreliable);

        public IEnumerable<(ulong From, MsgId Id, uint Tick, byte[] Payload)> Drain(LoopbackLink link)
        {
            var all = new List<(ulong, MsgId, uint, byte[])>();
            foreach (int ch in new[] { 1, 2 })
            {
                _buf.Clear();
                link.Receive(ch, _buf);
                foreach (var (from, data) in _buf)
                    all.Add((from, (MsgId)data[0], BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1)), data[5..]));
            }
            return all;
        }
    }

    [Fact]
    public void Two_hundred_bodies_sixty_awake_three_clients_stay_in_budget_and_converge()
    {
        const int N = 200, Awake = 60;
        const double MoveStart = 0.5, MoveEnd = 5.5, End = 8.0, MeasureFrom = 1.0, MeasureTo = 5.5;
        // Three players a few metres to ~25 m apart: most movers are in everyone's interest, some only in one's.
        var eyes = new Dictionary<byte, Vec3> { [1] = new(0, 1.6f, 0), [2] = new(14, 1.6f, 8), [3] = new(-6, 1.6f, 18) };
        var net = new Net(latencyMs: 40, jitterMs: 20, loss: 3, eyes.Keys);
        var host = new HostBodySync();
        var mirrors = eyes.Keys.ToDictionary(s => s, s => new ClientBodyMirror(s));

        // 200 resting bodies on a grid over the level; every 3rd-ish one (60) will move. Most movers sit near a client.
        var rng = new Random(7);
        var basePos = new Vec3[N];
        var ids = new NetId[N];
        for (int i = 0; i < N; i++)
        {
            var near = eyes[(byte)(1 + i % 3)];
            basePos[i] = i < Awake
                ? near + new Vec3((float)rng.NextDouble() * 24 - 12, -1.1f, (float)rng.NextDouble() * 24 - 12)
                : new Vec3((float)rng.NextDouble() * 120 - 60, 0.5f, (float)rng.NextDouble() * 150 - 60);
            ids[i] = host.Spawn(basePos[i], Quat.Identity, 0, resting: true);
        }
        foreach (var (s, eye) in eyes) host.AddClient(s, new ReceiverView(s, eye, YawDeg: s * 100));

        var radius = Enumerable.Range(0, Awake).Select(_ => 0.5f + (float)rng.NextDouble() * 1.5f).ToArray();
        var omega = Enumerable.Range(0, Awake).Select(_ => 1.5f + (float)rng.NextDouble() * 2.5f).ToArray();
        Vec3 PoseAt(int i, double t)
        {
            if (i >= Awake || t < MoveStart) return basePos[i];
            double tt = Math.Min(t, MoveEnd) - MoveStart;
            float a = (float)(tt * omega[i]);
            return basePos[i] + new Vec3(radius[i] * (MathF.Cos(a) - 1), 0.3f * MathF.Sin(a * 0.7f), radius[i] * MathF.Sin(a));
        }
        Quat RotAt(int i, double t)
        {
            if (i >= Awake || t < MoveStart) return Quat.Identity;
            float a = (float)((Math.Min(t, MoveEnd) - MoveStart) * omega[i]) * 0.5f;
            return new Quat(0, MathF.Sin(a), 0, MathF.Cos(a));
        }

        var bytesAtStart = new Dictionary<byte, long>();
        var bytesAtEnd = new Dictionary<byte, long>();
        var errs = new List<float>();
        int ticks = (int)(End * TickRate);
        for (int k = 0; k <= ticks; k++)
        {
            double now = k * Dt;
            if (k > 0) net.Hub.Advance(Dt);
            for (int i = 0; i < Awake; i++) host.SetPose(ids[i], PoseAt(i, now), RotAt(i, now));

            foreach (var o in host.Tick(now)) net.SendToClient(o.Slot, o.Id, (uint)k, o.Payload, o.Reliable);
            if (Math.Abs(now - MeasureFrom) < Dt / 2) foreach (var s in eyes.Keys) bytesAtStart[s] = host.BytesTo(s);
            if (Math.Abs(now - MeasureTo) < Dt / 2) foreach (var s in eyes.Keys) bytesAtEnd[s] = host.BytesTo(s);

            foreach (var (slot, link) in net.Clients)
            {
                var m = mirrors[slot];
                foreach (var (_, id, tick, payload) in net.Drain(link)) m.Receive(id, payload, tick, net.Hub.Now);
                if (m.TakeNack() is { } nack) net.SendToHost(slot, MsgId.BodyAudit, (uint)k, BodyMessages.Encode(nack), true);

                // Proxy accuracy while moving: the client's pose at its render time vs the host's pose at that time.
                if (now >= 1.5 && now <= MoveEnd - 0.3)
                {
                    double hostEst = m.EstimatedHostSeconds(net.Hub.Now), rt = hostEst - m.DelaySeconds;
                    for (int i = 0; i < Awake; i++)
                    {
                        if (Vec3.Distance(PoseAt(i, now), eyes[slot]) > 25) continue;
                        var s = m.Sample(ids[i], hostEst);
                        if (s.HasPose) errs.Add(Vec3.Distance(s.Pos, PoseAt(i, rt)));
                    }
                }
            }
            foreach (var (from, id, _, payload) in net.Drain(net.Host)) host.Receive((byte)(from - 100), id, payload, now);
        }

        double window = MeasureTo - MeasureFrom;
        foreach (var s in eyes.Keys)
        {
            double rate = (bytesAtEnd[s] - bytesAtStart[s]) / window;
            _log.WriteLine($"client {s}: {rate:0} B/s body traffic in the motion window, {host.StateBytesTo(s)} B of BodyState in total, delay {mirrors[s].DelaySeconds * 1000:0} ms");
            Assert.True(rate <= 16 * 1024, $"client {s}: {rate:0} B/s > 16 KB/s");
            Assert.True(rate >= 2 * 1024, $"client {s}: only {rate:0} B/s, the test is not loading the stream");
        }

        errs.Sort();
        float mean = errs.Average(), p95 = errs[(int)(errs.Count * 0.95)], max = errs[^1];
        _log.WriteLine($"in-motion proxy error over {errs.Count} samples: mean {mean * 100:0.0} cm, p95 {p95 * 100:0.0} cm, max {max * 100:0.0} cm");
        Assert.True(mean < 0.05f, $"mean in-motion error {mean * 100:0.0} cm");
        Assert.True(p95 < 0.15f, $"p95 in-motion error {p95 * 100:0.0} cm");

        // After rest every client knows every body and holds it within 5 cm of the host (exact quantised rest pose).
        foreach (var (slot, m) in mirrors)
        {
            Assert.Equal(N, m.Count);
            double hostEst = m.EstimatedHostSeconds(net.Hub.Now);
            float worst = 0;
            for (int i = 0; i < N; i++)
            {
                var s = m.Sample(ids[i], hostEst);
                Assert.Equal(InterpState.AtRest, s.State);
                var (hp, hq) = host.PoseOf(ids[i]);
                worst = Math.Max(worst, Vec3.Distance(s.Pos, hp));
                Assert.True(Quat.AngleDeg(s.Rot, hq) < 0.5f);
            }
            _log.WriteLine($"client {slot}: worst rest error {worst * 1000:0.0} mm");
            Assert.True(worst <= 0.05f, $"client {slot}: rest error {worst * 100:0.0} cm");
        }
    }

    [Fact]
    public void Two_clients_claim_one_body_owner_streams_and_releases()
    {
        var host = new HostBodySync();
        var a = new ClientBodyMirror(2);
        var b = new ClientBodyMirror(3);
        var id = host.Spawn(new Vec3(1, 0.5f, 1), Quat.Identity, 0, resting: true);
        host.AddClient(2, new ReceiverView(2, new Vec3(0, 1.6f, 0)));
        host.AddClient(3, new ReceiverView(3, new Vec3(2, 1.6f, 2)));
        uint tick = 0;
        double now = 0;
        void Pump()
        {
            foreach (var o in host.Tick(now)) (o.Slot == 2 ? a : b).Receive(o.Id, o.Payload, tick, now);
            tick++; now += Dt;
        }
        Pump();
        Assert.Equal(1, a.Count);

        var ca = a.Claim(id, ClaimReason.Hand);
        var cb = b.Claim(id, ClaimReason.Hand);
        host.Receive(2, MsgId.BodyClaim, BodyMessages.Encode(new BodyClaim { Entries = new() { ca } }), now);
        host.Receive(3, MsgId.BodyClaim, BodyMessages.Encode(new BodyClaim { Entries = new() { cb } }), now);
        var results = new List<(byte Slot, AuthEntry E)>();
        a.AuthChanged += e => results.Add((2, e));
        b.AuthChanged += e => results.Add((3, e));
        Pump();
        Assert.Contains(results, r => r.Slot == 2 && r.E.Result == AuthResult.Granted && r.E.Owner == 2 && r.E.ClaimSeq == ca.ClaimSeq);
        Assert.Contains(results, r => r.Slot == 3 && r.E.Result == AuthResult.Denied && r.E.Owner == 2);
        Assert.True(a.OwnsLocally(id));
        Assert.False(b.OwnsLocally(id));
        Assert.False(a.IsClaimPending(id));
        Assert.True(host.Authority.IsOwnedBy(id, 2));

        // The owner streams; the host follows and relays to the other client, not back to the owner.
        for (int k = 0; k < 15; k++)
        {
            var p = new Vec3(1 + k * 0.1f, 1.2f, 1);
            host.Receive(2, MsgId.BodyOwnedState, BodyMessages.Encode(new BodyOwnedState { Entries = new() { new(id, p, Quat.Identity, new Vec3(3, 0, 0)) } }), now);
            foreach (var o in host.Tick(now))
            {
                Assert.False(o.Slot == 2 && o.Id == MsgId.BodyState, "owner got its own body back");
                (o.Slot == 2 ? a : b).Receive(o.Id, o.Payload, tick, now);
            }
            tick++; now += Dt;
        }
        Assert.Equal(2.4f, host.PoseOf(id).Pos.X, 2);
        Assert.Equal(2.4f, b.Get(id)!.Buffer.NewestPos.X, 2);
        // Samples from a non-owner are ignored.
        host.Receive(3, MsgId.BodyOwnedState, BodyMessages.Encode(new BodyOwnedState { Entries = new() { new(id, new Vec3(9, 9, 9), Quat.Identity, default) } }), now);
        Assert.Equal(2.4f, host.PoseOf(id).Pos.X, 2);

        // Release with a throw: host takes over, everyone sees Returned, body flies on the host and comes to rest.
        host.Receive(2, MsgId.BodyRelease, BodyMessages.Encode(new BodyRelease { Body = id, Reason = ReleaseReason.Throw, Pos = new Vec3(2.5f, 1.2f, 1), Rot = Quat.Identity, Vel = new Vec3(4, 2, 0) }), now);
        results.Clear();
        Pump();
        Assert.Equal(2, results.Count(r => r.E.Result == AuthResult.Returned && r.E.Owner == BodyCodec.HostOwner));
        Assert.False(a.OwnsLocally(id));
        for (int k = 0; k < 20; k++) { host.SetPose(id, new Vec3(2.5f + k * 0.1f, 1.2f, 1), Quat.Identity); Pump(); }
        for (int k = 0; k < 20; k++) Pump(); // still → BodyRest
        Assert.True(a.Get(id)!.Buffer.EndsAtRest);
        Assert.Equal(4.4f, a.Get(id)!.Buffer.NewestPos.X, 2);
        Assert.Equal(4.4f, b.Get(id)!.Buffer.NewestPos.X, 2);
    }

    [Fact]
    public void Owner_silence_and_leaving_return_bodies()
    {
        var host = new HostBodySync();
        var id1 = host.Spawn(new Vec3(0, 0.5f, 1), Quat.Identity, 0);
        var id2 = host.Spawn(new Vec3(0, 0.5f, 2), Quat.Identity, 0);
        host.AddClient(2, new ReceiverView(2, new Vec3(0, 1.6f, 0)));
        host.AddClient(3, new ReceiverView(3, new Vec3(0, 1.6f, 0)));
        host.Receive(2, MsgId.BodyClaim, BodyMessages.Encode(new BodyClaim { Entries = new() { new(id1, ClaimReason.Hand, 1), new(id2, ClaimReason.Hand, 2) } }), 0);
        host.Tick(0);
        Assert.True(host.Authority.IsOwnedBy(id1, 2));
        // id2 keeps getting samples, id1 does not.
        for (double t = 0.1; t < 2.2; t += 0.1)
            host.Receive(2, MsgId.BodyOwnedState, BodyMessages.Encode(new BodyOwnedState { Entries = new() { new(id2, new Vec3(0, 0.5f, 2), Quat.Identity, default) } }), t);
        var outs = host.Tick(2.2);
        var auth = BodyMessages.Decode<BodyAuth>(outs.First(o => o.Id == MsgId.BodyAuth && o.Slot == 3).Payload);
        Assert.Equal(id1, Assert.Single(auth.Entries).Id);
        Assert.True(host.Authority.IsOwnedBy(id2, 2));
        host.RemoveClient(2, 2.3);
        Assert.False(host.Authority.IsOwnedBy(id2, 2));
        var outs2 = host.Tick(2.3);
        Assert.Contains(BodyMessages.Decode<BodyAuth>(outs2.First(o => o.Id == MsgId.BodyAuth).Payload).Entries, e => e.Id == id2 && e.Result == AuthResult.Returned);
        Assert.DoesNotContain(outs2, o => o.Slot == 2);
    }

    [Fact]
    public void Predicted_spill_is_adopted_and_predicted_swallow_is_validated()
    {
        var host = new HostBodySync();
        var c = new ClientBodyMirror(2);
        var other = new ClientBodyMirror(3);
        host.AddClient(2, new ReceiverView(2, new Vec3(0, 1.6f, 0)));
        host.AddClient(3, new ReceiverView(3, new Vec3(0, 1.6f, 0)));
        var near = host.Spawn(new Vec3(1, 0.5f, 1), Quat.Identity, 0);
        var far = host.Spawn(new Vec3(20, 0.5f, 1), Quat.Identity, 0);
        uint tick = 0;
        void Pump(double now) { foreach (var o in host.Tick(now)) (o.Slot == 2 ? c : other).Receive(o.Id, o.Payload, tick, now); tick++; }
        Pump(0);

        // Swallow: the near free body is accepted, the far one is not.
        host.Receive(2, MsgId.BodyConsume, BodyMessages.Encode(new BodyConsume { Kind = ConsumeKind.Vacuum, Holder = 2, Ids = new() { near, far } }), 0.1);
        Pump(0.1);
        Assert.Null(c.Get(near));
        Assert.Null(other.Get(near));
        Assert.NotNull(c.Get(far));
        Assert.Equal(1, c.Tank(ContainerKind.Vacuum, 2)!.Value.Count);
        Assert.Equal(1, other.Tank(ContainerKind.Vacuum, 2)!.Value.Count);

        // Spill it back out under a temp id, kept owned for the delivery arc.
        var temp = c.TempIds.Next();
        SpawnAdopt? adopted = null;
        c.Adopted += m => adopted = m;
        host.Receive(2, MsgId.ClientSpawn, BodyMessages.Encode(new ClientSpawn { TempId = temp, Source = ClientSpawnSource.VacuumSpill, Holder = 2, Pos = new Vec3(0, 1, 1), Rot = Quat.Identity, TrueScale = 1, Flags = ClientSpawnFlags.KeepOwned }), 0.2);
        Pump(0.2);
        Assert.NotNull(adopted);
        Assert.True(adopted!.Value.Accepted);
        var net = adopted.Value.NetId;
        Assert.Equal(net, c.AdoptedId(temp));
        Assert.Equal(0, c.TempIds.InFlight);
        Assert.True(c.OwnsLocally(net));
        Assert.Equal(SpawnOrigin.VacuumSpill, other.Get(net)!.Spawn.Origin);
        Assert.Equal(0, c.Tank(ContainerKind.Vacuum, 2)!.Value.Count);

        // The client may keep using the temp id after adoption.
        host.Receive(2, MsgId.BodyRelease, BodyMessages.Encode(new BodyRelease { Body = temp, Reason = ReleaseReason.TweenEnd, Pos = new Vec3(0, 0.5f, 2), Rot = Quat.Identity }), 0.3);
        Assert.False(host.Authority.IsOwnedBy(net, 2));
        Assert.Equal(2f, host.PoseOf(net).Pos.Z, 2);

        // An empty tank rejects the next spill.
        var temp2 = c.TempIds.Next();
        host.Receive(2, MsgId.ClientSpawn, BodyMessages.Encode(new ClientSpawn { TempId = temp2, Source = ClientSpawnSource.VacuumSpill, Holder = 2, Pos = new Vec3(0, 1, 1), Rot = Quat.Identity, TrueScale = 1 }), 0.4);
        Pump(0.4);
        Assert.False(adopted.Value.Accepted);
        Assert.Equal(AdoptReason.Empty, adopted.Value.Reason);
    }

    [Fact]
    public void Missed_spawns_are_repaired_by_the_audit_digest()
    {
        var host = new HostBodySync { DigestInterval = 1 };
        var c = new ClientBodyMirror(2);
        host.AddClient(2, new ReceiverView(2, Vec3.Zero));
        var ids = Enumerable.Range(0, 300).Select(i => host.Spawn(new Vec3(i % 20, 0.5f, i / 20), Quat.Identity, 0, resting: true)).ToList();
        uint tick = 0;
        // Tick 0: the spawn batch is "lost" (not applied); the digest arrives later.
        host.Tick(0);
        for (double t = Dt; t < 1.2; t += Dt)
        {
            foreach (var o in host.Tick(t)) c.Receive(o.Id, o.Payload, tick, t);
            if (c.TakeNack() is { } nack) host.Receive(2, MsgId.BodyAudit, BodyMessages.Encode(nack), t);
            tick++;
        }
        Assert.Equal(300, c.Count);
        Assert.Equal(BodyAudit.ComputeDigest(host.LiveIds), c.Digest());
        _ = ids;
    }
}
