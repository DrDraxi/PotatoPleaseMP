using PotatoPleaseMP.Core.Bodies;
using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Tests.Bodies;

public class BodyMessageTests
{
    static T RoundTrip<T>(T msg) where T : struct, IWireMessage => BodyMessages.Decode<T>(BodyMessages.Encode(msg));

    static void Near(Vec3 a, Vec3 b, float eps) => Assert.True(Vec3.Distance(a, b) <= eps, $"{a} vs {b}");
    static void Near(Quat a, Quat b) => Assert.True(Quat.AngleDeg(a, b) <= 0.2f, $"{a} vs {b}");

    static readonly Quat SomeRot = new Quat(0.1f, 0.7f, -0.2f, 0.67f).Normalized;

    [Fact]
    public void BodySpawn_fixed_entry_is_34_bytes_and_optional_parts_round_trip()
    {
        var plain = new SpawnEntry
        {
            Id = new NetId(17), Form = SpawnForm.Pooled, Origin = SpawnOrigin.Blast, Auth = BodyCodec.PackAuth(15, BodyMode.HostSim),
            Pos = new Vec3(1.5f, 2, -3), Rot = SomeRot, Vel = new Vec3(0.5f, -1, 2), Variant = 3, TrueScale = 1.125f,
            Flags = SpawnFlags.RegisterActive, Frost = 9, Char = 200, EyeSeed = 0xDEADBEEF, CarveVer = 77,
        };
        Assert.Equal(2 + SpawnEntry.FixedSize, BodyMessages.Encode(new BodySpawn { Entries = new() { plain } }).Length);

        var full = plain with
        {
            Id = new NetId(0xC003), Form = SpawnForm.BindByRef, RefKind = 4, RefId = 999, ChildIdx = 2,
            Flags = SpawnFlags.Factory | SpawnFlags.HasCombo | SpawnFlags.HasRot | SpawnFlags.HasBlood | SpawnFlags.Resting,
            FactoryDepth = 0.25f, Combo = -12345, RotSpots = new[] { new Vec4(0.1f, 0.2f, 0.3f, 0.5f) },
            BloodCount = 5, BloodSpots = new[] { new Vec4(1, 2, 3, 4), new Vec4(-1, -2, -3, -4) },
        };
        var veg = plain with { Id = new NetId(18), Form = SpawnForm.Vegetable, VegetableKind = 2 };
        var back = RoundTrip(new BodySpawn { Entries = new() { plain, full, veg } }).Entries;
        Assert.Equal(3, back.Count);
        var p = back[0];
        Assert.Equal(plain.Id, p.Id); Assert.Equal(SpawnOrigin.Blast, p.Origin); Assert.Equal(plain.Flags, p.Flags);
        Near(plain.Pos, p.Pos, 0.003f); Near(plain.Rot, p.Rot); Near(plain.Vel, p.Vel, 0.005f);
        Assert.Equal(1.125f, p.TrueScale); Assert.Equal(0xDEADBEEF, p.EyeSeed); Assert.Equal((ushort)77, p.CarveVer);
        Assert.Equal(9, p.Frost); Assert.Equal(200, p.Char); Assert.Equal(3, p.Variant);
        var f = back[1];
        Assert.Equal(SpawnForm.BindByRef, f.Form); Assert.Equal(4, f.RefKind); Assert.Equal(999, f.RefId); Assert.Equal(2, f.ChildIdx);
        Assert.Equal(0.25f, f.FactoryDepth); Assert.Equal(-12345, f.Combo);
        Assert.Single(f.RotSpots!); Assert.Equal(0.5f, f.RotSpots![0].W);
        Assert.Equal(5, f.BloodCount); Assert.Equal(2, f.BloodSpots!.Length); Assert.Equal(-4f, f.BloodSpots[1].W);
        Assert.Equal(SpawnForm.Vegetable, back[2].Form); Assert.Equal(2, back[2].VegetableKind);
    }

    [Fact]
    public void BodySpawn_chunks_stay_under_the_cap_and_keep_every_entry()
    {
        var list = Enumerable.Range(1, 2000).Select(i => new SpawnEntry { Id = new NetId((ushort)i), Pos = new Vec3(i % 50, 1, 0), Rot = Quat.Identity, TrueScale = 1 }).ToList();
        var chunks = BodySpawn.EncodeChunks(list, 16 * 1024);
        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(c.Length <= 16 * 1024));
        var ids = chunks.SelectMany(c => BodyMessages.Decode<BodySpawn>(c).Entries).Select(e => (int)e.Id.Value).ToList();
        Assert.Equal(Enumerable.Range(1, 2000), ids);
    }

    [Fact]
    public void Small_messages_have_the_spec_sizes_and_round_trip()
    {
        var despawn = new BodyDespawn { Entries = new() { new(new NetId(5), DespawnReason.Sold, 3), new(new NetId(6), DespawnReason.Destroyed) } };
        Assert.Equal(2 + 2 * 4, BodyMessages.Encode(despawn).Length);
        Assert.Equal(despawn.Entries, RoundTrip(despawn).Entries);

        var owned = new BodyOwnedState { Entries = new() { new(new NetId(0x8401), new Vec3(1, 2, 3), SomeRot, new Vec3(4, 5, 6)) } };
        Assert.Equal(1 + 18, BodyMessages.Encode(owned).Length);
        var ob = RoundTrip(owned).Entries[0];
        Assert.Equal(new NetId(0x8401), ob.Id); Near(ob.Pos, new Vec3(1, 2, 3), 0.003f); Near(ob.Vel, new Vec3(4, 5, 6), 0.005f);

        var claim = new BodyClaim { Entries = new() { new(new NetId(9), ClaimReason.VacuumBulk, 200) } };
        Assert.Equal(1 + 4, BodyMessages.Encode(claim).Length);
        Assert.Equal(claim.Entries, RoundTrip(claim).Entries);

        var auth = new BodyAuth { Entries = new() { new(new NetId(9), BodyCodec.PackAuth(2, BodyMode.ClientOwned), AuthResult.Granted, 200) } };
        Assert.Equal(1 + 5, BodyMessages.Encode(auth).Length);
        var ab = RoundTrip(auth).Entries[0];
        Assert.Equal(auth.Entries[0], ab); Assert.Equal(2, ab.Owner); Assert.Equal(BodyMode.ClientOwned, ab.Mode);

        var rel = new BodyRelease { Body = new NetId(9), Reason = ReleaseReason.Throw, Pos = new Vec3(1, 1, 1), Rot = SomeRot, Vel = new Vec3(8, 3, -2), AngVel = new Vec3(5, -5, 1), OwnerTick = 4321 };
        Assert.Equal(BodyRelease.Size, BodyMessages.Encode(rel).Length);
        var rb = RoundTrip(rel);
        Assert.Equal(ReleaseReason.Throw, rb.Reason); Assert.Equal(4321, rb.OwnerTick); Near(rb.AngVel, rel.AngVel, 0.25f);

        var cs = new ClientSpawn { TempId = NetId.Temp(3, 7), Source = ClientSpawnSource.VacuumSpill, Holder = 3, Pos = new Vec3(0, 1, 0), Rot = SomeRot, Variant = 2, TrueScale = 0.9f, Flags = ClientSpawnFlags.KeepOwned };
        Assert.Equal(28, BodyMessages.Encode(cs).Length);
        var cs2 = cs with { Flags = ClientSpawnFlags.HasFactoryDepth | ClientSpawnFlags.PeeledOnSpill, FactoryDepth = 0.5f };
        Assert.Equal(30, BodyMessages.Encode(cs2).Length);
        var csb = RoundTrip(cs2);
        Assert.Equal(cs2.TempId, csb.TempId); Assert.Equal(0.5f, csb.FactoryDepth); Assert.Equal(cs2.Flags, csb.Flags);

        var adopt = new SpawnAdopt { TempId = NetId.Temp(3, 7), NetId = new NetId(42), Reason = AdoptReason.Ok };
        Assert.Equal(5, BodyMessages.Encode(adopt).Length);
        Assert.Equal(adopt, RoundTrip(adopt));

        var consume = new BodyConsume { Kind = ConsumeKind.Bucket, Holder = 2, Ids = new() { new NetId(1), new NetId(2), new NetId(3) } };
        Assert.Equal(4 + 6, BodyMessages.Encode(consume).Length);
        Assert.Equal(consume.Ids, RoundTrip(consume).Ids);

        var cc = new ContainerCount { Holder = 2, Kind = ContainerKind.Vacuum, OwnerSlot = 2, Count = 10, Peeled = 4, Props = new() { new(1, 1.0f), new(2, 0.85f) } };
        Assert.Equal(9 + 4, BodyMessages.Encode(cc).Length);
        var ccb = RoundTrip(cc);
        Assert.Equal(10, ccb.Count); Assert.Equal(4, ccb.Peeled); Assert.Equal(0.85f, ccb.Props[1].Scale, 2);

        var slot = new BodySlot { Entries = new() { new(new NetId(7), 3, SlotEntry.Left, SlotKind.StationRaw) } };
        Assert.Equal(1 + 6, BodyMessages.Encode(slot).Length);
        Assert.Equal(slot.Entries, RoundTrip(slot).Entries);

        var fx = new BodyFx { Entries = new() { new(FxKind.Impact, new NetId(7), new Vec3(1, 1, 1), 0xABCDEF01, 0.5f, 1.1f) } };
        Assert.Equal(1 + BodyFx.EntrySize, BodyMessages.Encode(fx).Length);
        var fb = RoundTrip(fx).Entries[0];
        Assert.Equal(0xABCDEF01u, fb.Clip); Assert.Equal(0.5f, fb.Volume, 2); Assert.Equal(1.1f, fb.Pitch, 2);

        var sc = new BodyScalar { Entries = new() { new(new NetId(7), ScalarMask.Frost | ScalarMask.Flags, 33, 0, SpawnFlags.Frozen), new(new NetId(8), ScalarMask.Char, 0, 12, 0) } };
        Assert.Equal(1 + 6 + 4, BodyMessages.Encode(sc).Length);
        Assert.Equal(sc.Entries, RoundTrip(sc).Entries);
    }

    [Fact]
    public void BodyRest_uses_the_float_pose_outside_the_level_box()
    {
        var rest = new BodyRest { Entries = new() { new(new NetId(1), new Vec3(1, 2, 3), SomeRot, RestFlags.KinematicAtRest), new(new NetId(2), new Vec3(200, -50, 3), SomeRot) } };
        var bytes = BodyMessages.Encode(rest);
        Assert.Equal(2 + 13 + 31, bytes.Length);
        var back = RoundTrip(rest).Entries;
        Near(back[0].Pos, new Vec3(1, 2, 3), 0.003f);
        Assert.Equal(RestFlags.KinematicAtRest, back[0].Flags);
        Assert.Equal(new Vec3(200, -50, 3), back[1].Pos);
        Assert.True((back[1].Flags & RestFlags.FloatPose) != 0);
    }

    [Fact]
    public void BodyState_is_10_bytes_per_entry_with_id_deltas_and_escapes()
    {
        var anchor = new Vec3(5.123f, 1.7f, 9.5f);
        var rng = new Random(5);
        var entries = new List<StateEntry>();
        ushort id = 100;
        for (int i = 0; i < 100; i++)
        {
            id += (ushort)rng.Next(1, 20);
            entries.Add(new StateEntry(new NetId(id), anchor + new Vec3((float)rng.NextDouble() * 20 - 10, (float)rng.NextDouble() * 4 - 2, (float)rng.NextDouble() * 20 - 10), SomeRot));
        }
        var msg = new BodyState { Anchor = anchor, Entries = entries };
        var bytes = BodyMessages.Encode(msg);
        // First id 100+ escapes (delta > 63 from 0), the rest are 1-byte deltas: 7 + 12 + 99 x 10.
        Assert.Equal(BodyState.Measure(anchor, entries), bytes.Length);
        Assert.Equal(7 + 12 + 99 * 10, bytes.Length);
        Assert.True(DatagramBuilder.Cost(bytes.Length) <= MessageRegistry.MaxUnreliablePayload);

        var back = BodyMessages.Decode<BodyState>(bytes);
        Assert.Equal(100, back.Entries.Count);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(entries[i].Id, back.Entries[i].Id);
            Assert.True(MathF.Abs(entries[i].Pos.X - back.Entries[i].Pos.X) <= 0.0011f);
            Assert.True(MathF.Abs(entries[i].Pos.Y - back.Entries[i].Pos.Y) <= 0.0021f);
            Near(entries[i].Rot, back.Entries[i].Rot);
        }

        // Escapes: a big gap, a decreasing id, a far position (absolute form) and the snap bit.
        var odd = new List<StateEntry>
        {
            new(new NetId(10), anchor, SomeRot), new(new NetId(500), anchor, SomeRot, true), new(new NetId(20), anchor + new Vec3(40, 0, 0), SomeRot),
        };
        var ob = BodyMessages.Encode(new BodyState { Anchor = anchor, Entries = odd });
        Assert.Equal(7 + 10 + 12 + 13, ob.Length);
        var oback = BodyMessages.Decode<BodyState>(ob).Entries;
        Assert.Equal(new[] { 10, 500, 20 }, oback.Select(e => (int)e.Id.Value));
        Assert.True(oback[1].Snap); Assert.False(oback[0].Snap);
        Near(oback[2].Pos, anchor + new Vec3(40, 0, 0), 0.003f);
    }

    [Fact]
    public void BodyAudit_digest_and_nack()
    {
        var ids = Enumerable.Range(1, 600).Select(i => new NetId((ushort)i)).ToList();
        var d1 = BodyAudit.ComputeDigest(ids);
        var shuffled = ids.OrderBy(i => (i.Value * 7919) % 601).ToList();
        Assert.Equal(d1, BodyAudit.ComputeDigest(shuffled));
        ids.Remove(new NetId(300));
        var d2 = BodyAudit.ComputeDigest(ids);
        Assert.Equal(new byte[] { 1 }, BodyAudit.Diff(d1, d2));

        var dm = new BodyAudit { Sub = AuditSub.Digest, Digest = d1 };
        Assert.Equal(257, BodyMessages.Encode(dm).Length);
        Assert.Equal(d1, BodyMessages.Decode<BodyAudit>(BodyMessages.Encode(dm)).Digest);
        var nack = new BodyAudit { Sub = AuditSub.Nack, NackBlocks = new() { 1, 7 } };
        Assert.Equal(new byte[] { 1, 7 }, BodyMessages.Decode<BodyAudit>(BodyMessages.Encode(nack)).NackBlocks);
    }

    [Fact]
    public void Registry_has_every_body_codec_and_passes_the_self_check()
    {
        var g = MessageRegistry.CreateDefault();
        Assert.Empty(g.SelfCheck(i => (byte)i.Id >= 0x40 && (byte)i.Id <= 0x4F));
        for (byte id = 0x40; id <= 0x4F; id++)
        {
            var info = g[(MsgId)id]!;
            Assert.Equal(7, info.Owner);
            Assert.Equal((MsgId)id, info.Codec!().Id);
        }
    }

    [Fact]
    public void Garbage_only_throws_WireFormatException()
    {
        var g = MessageRegistry.CreateDefault();
        var rng = new Random(77);
        var seeds = new List<byte[]>
        {
            BodyMessages.Encode(new BodyState { Anchor = new Vec3(1, 2, 3), Entries = new() { new(new NetId(3), new Vec3(1, 2, 3), SomeRot), new(new NetId(90), new Vec3(60, 2, 3), SomeRot) } }),
            BodyMessages.Encode(new BodySpawn { Entries = new() { new SpawnEntry { Id = new NetId(3), Flags = SpawnFlags.HasRot | SpawnFlags.HasBlood, RotSpots = new[] { new Vec4(1, 1, 1, 1) } } } }),
            BodyMessages.Encode(new BodyRest { Entries = new() { new(new NetId(2), new Vec3(500, 0, 0), SomeRot) } }),
        };
        for (int i = 0; i < 20_000; i++)
        {
            byte[] data;
            if (i % 3 == 0) { data = new byte[rng.Next(0, 64)]; rng.NextBytes(data); }
            else
            {
                data = (byte[])seeds[rng.Next(seeds.Count)].Clone();
                for (int k = rng.Next(1, 4); k > 0; k--) data[rng.Next(data.Length)] ^= (byte)rng.Next(1, 256);
                if (rng.Next(4) == 0) Array.Resize(ref data, rng.Next(data.Length + 1));
            }
            var codec = g[(MsgId)(0x40 + rng.Next(16))]!.Codec!();
            try
            {
                var r = new NetReader(data);
                codec.Read(ref r);
            }
            catch (WireFormatException) { }
        }
    }
}
