using PotatoPleaseMP.Core.Bodies;

namespace PotatoPleaseMP.Core.Tests.Bodies;

public class NetIdTests
{
    [Fact]
    public void Ranges()
    {
        Assert.True(new NetId(1).IsHost); Assert.True(new NetId(0x7FFF).IsHost);
        Assert.True(new NetId(0x8000).IsTemp); Assert.True(new NetId(0xBFFF).IsTemp);
        Assert.True(new NetId(0xC000).IsStatic); Assert.True(new NetId(0xFFFE).IsStatic);
        Assert.False(NetId.None.IsValid); Assert.False(NetId.Invalid.IsValid);
        Assert.Equal(new NetId(0xC005), NetId.Static(5));
        Assert.Equal(new NetId(0xBFFF), NetId.Temp(15, 1023));
        Assert.Equal(7, NetId.Temp(7, 3).TempSlot);
        Assert.Throws<ArgumentOutOfRangeException>(() => NetId.Temp(0, 1));
    }

    [Fact]
    public void Host_allocator_is_round_robin_and_never_reuses_inside_the_quarantine()
    {
        var a = new HostIdAllocator();
        var first = a.Allocate(0);
        Assert.Equal(new NetId(1), first);
        Assert.Equal(new NetId(2), a.Allocate(0));
        Assert.True(a.Free(first, 10));
        Assert.False(a.Free(first, 10));
        // Round robin: the freed id is not handed out next even when out of quarantine.
        Assert.Equal(new NetId(3), a.Allocate(20));

        // Churn: ids freed at time t are never returned before t + 3 s.
        var freedAt = new Dictionary<ushort, double>();
        var live = new List<NetId>();
        var rng = new Random(3);
        double now = 0;
        for (int i = 0; i < 200_000; i++)
        {
            now += 0.0005;
            if (live.Count > 2000 || (live.Count > 0 && rng.Next(2) == 0))
            {
                int k = rng.Next(live.Count);
                var id = live[k]; live.RemoveAt(k);
                a.Free(id, now);
                freedAt[id.Value] = now;
            }
            else
            {
                var id = a.Allocate(now);
                Assert.True(id.IsHost);
                if (freedAt.TryGetValue(id.Value, out double t)) Assert.True(now - t >= 3.0, $"{id} reused after {now - t:0.000} s");
                live.Add(id);
            }
        }
    }

    [Fact]
    public void Host_allocator_reports_exhaustion()
    {
        var a = new HostIdAllocator();
        for (int i = 0; i < NetId.HostLast; i++) Assert.True(a.Allocate(0).IsValid);
        Assert.Equal(NetId.None, a.Allocate(0));
        Assert.Equal(1, a.Exhausted);
        a.Free(new NetId(100), 0);
        Assert.Equal(NetId.None, a.Allocate(1));      // quarantined
        Assert.Equal(new NetId(100), a.Allocate(3.5)); // released
    }

    [Fact]
    public void Temp_ranges_are_disjoint_per_slot_and_recycled()
    {
        var seen = new HashSet<ushort>();
        for (byte s = 1; s <= 15; s++)
        {
            var t = new TempIdAllocator(s);
            for (int i = 0; i < NetId.TempPerSlot; i++)
            {
                var id = t.Next();
                Assert.True(id.IsTemp);
                Assert.Equal(s, id.TempSlot);
                Assert.True(seen.Add(id.Value));
            }
            Assert.Equal(NetId.None, t.Next());
            Assert.True(t.Release(NetId.Temp(s, 500)));
            Assert.False(t.Release(NetId.Temp(s, 500)));
            Assert.Equal(NetId.Temp(s, 500), t.Next());
            Assert.False(t.Release(NetId.Temp((byte)(s % 15 + 1), 1)));
        }
        Assert.Equal(15 * 1024, seen.Count);
    }
}

public class AuthorityTableTests
{
    static AuthorityTable Table(params ushort[] ids)
    {
        var t = new AuthorityTable();
        foreach (var i in ids) t.Add(new NetId(i));
        return t;
    }

    [Fact]
    public void Claim_grants_a_free_body_and_the_first_of_two_claimants_wins()
    {
        var t = Table(1);
        var a = t.TryClaim(new NetId(1), 2, ClaimReason.Hand, 7, 2f, 0);
        var b = t.TryClaim(new NetId(1), 3, ClaimReason.Hand, 9, 1f, 0);
        Assert.True(a.Granted);
        Assert.Equal(2, a.Entry.Owner); Assert.Equal(BodyMode.ClientOwned, a.Entry.Mode); Assert.Equal(7, a.Entry.ClaimSeq);
        Assert.Equal(AuthResult.Denied, b.Entry.Result);
        Assert.Equal(DenyReason.OwnedByOther, b.Deny);
        Assert.Equal(9, b.Entry.ClaimSeq);       // the loser can match its pending claim
        Assert.Equal(2, b.Entry.Owner);          // ...and learns the real owner
        Assert.Equal(1, t.OwnedCount(2));
        Assert.Equal(0, t.OwnedCount(3));
        // The owner may re-claim (new capture of the same body).
        Assert.True(t.TryClaim(new NetId(1), 2, ClaimReason.Vacuum, 8, 1f, 0).Granted);
        Assert.Equal(1, t.OwnedCount(2));
    }

    [Fact]
    public void Deny_rules()
    {
        var t = Table(1, 2, 3, 4);
        t.Get(new NetId(1))!.HostCarried = true;
        t.Get(new NetId(2))!.HostPulling = true;
        t.SetContained(new NetId(3), 9);
        Assert.Equal(DenyReason.HostCarried, t.TryClaim(new NetId(1), 2, ClaimReason.Hand, 1, 1, 0).Deny);
        Assert.Equal(DenyReason.HostPulling, t.TryClaim(new NetId(2), 2, ClaimReason.Hand, 1, 1, 0).Deny);
        Assert.Equal(DenyReason.NotHostSim, t.TryClaim(new NetId(3), 2, ClaimReason.Hand, 1, 1, 0).Deny);
        Assert.Equal(DenyReason.TooFar, t.TryClaim(new NetId(4), 2, ClaimReason.Hand, 1, 5.01f, 0).Deny);
        Assert.True(t.TryClaim(new NetId(4), 2, ClaimReason.Hand, 1, 5.0f, 0).Granted);
        Assert.Equal(DenyReason.UnknownBody, t.TryClaim(new NetId(99), 2, ClaimReason.Hand, 1, 1, 0).Deny);
        Assert.Equal(DenyReason.BadSlot, t.TryClaim(new NetId(4), 15, ClaimReason.Hand, 1, 1, 0).Deny);
    }

    [Fact]
    public void At_most_16_owned_per_client()
    {
        var t = new AuthorityTable();
        for (ushort i = 1; i <= 17; i++) t.Add(new NetId(i));
        for (ushort i = 1; i <= 16; i++) Assert.True(t.TryClaim(new NetId(i), 4, ClaimReason.Vacuum, (byte)i, 1, 0).Granted);
        Assert.Equal(DenyReason.TooMany, t.TryClaim(new NetId(17), 4, ClaimReason.Vacuum, 17, 1, 0).Deny);
        Assert.NotNull(t.Release(new NetId(3), 4));
        Assert.True(t.TryClaim(new NetId(17), 4, ClaimReason.Vacuum, 18, 1, 0).Granted);
    }

    [Fact]
    public void Release_revoke_timeout_and_player_left()
    {
        var t = Table(1, 2, 3, 4);
        foreach (ushort i in new ushort[] { 1, 2, 3 }) t.TryClaim(new NetId(i), 2, ClaimReason.Hand, 1, 1, 0);
        t.TryClaim(new NetId(4), 3, ClaimReason.Peel, 1, 1, 0);
        Assert.Equal(BodyMode.PeelHeld, t.Get(new NetId(4))!.Mode);

        Assert.Null(t.Release(new NetId(1), 3));                 // not the owner
        var rel = t.Release(new NetId(1), 2)!.Value;
        Assert.Equal(AuthResult.Returned, rel.Entry.Result);
        Assert.Equal(BodyCodec.HostOwner, rel.Entry.Owner);
        Assert.Equal(BodyMode.HostSim, rel.Entry.Mode);

        Assert.Equal(AuthResult.Revoked, t.Revoke(new NetId(2))!.Value.Entry.Result);
        Assert.Null(t.Revoke(new NetId(2)));

        // Body 3 owned by slot 2: samples keep it alive, silence returns it after 2 s.
        Assert.True(t.NoteOwnerSample(new NetId(3), 2, 1.5));
        Assert.False(t.NoteOwnerSample(new NetId(3), 3, 1.5));
        Assert.True(t.NoteOwnerSample(new NetId(4), 3, 3.0));
        var early = t.Timeout(3.4);
        Assert.DoesNotContain(early, c => c.Entry.Id == new NetId(3));
        var to = t.Timeout(3.6);
        Assert.Contains(to, c => c.Entry.Id == new NetId(3) && c.Entry.Result == AuthResult.Returned);
        Assert.DoesNotContain(to, c => c.Entry.Id == new NetId(4)); // peel owner sampled at 3.0
        Assert.Contains(t.Timeout(5.1), c => c.Entry.Id == new NetId(4));
    }

    [Fact]
    public void Player_left_returns_only_their_bodies()
    {
        var t = Table(1, 2, 3);
        t.TryClaim(new NetId(1), 2, ClaimReason.Hand, 1, 1, 10);
        t.TryClaim(new NetId(2), 3, ClaimReason.Hand, 1, 1, 10);
        var back = t.PlayerLeft(2);
        Assert.Single(back);
        Assert.Equal(new NetId(1), back[0].Entry.Id);
        Assert.Equal(0, t.OwnedCount(2));
        Assert.True(t.IsOwnedBy(new NetId(2), 3));
        Assert.True(t.TryClaim(new NetId(1), 3, ClaimReason.Hand, 2, 1, 11).Granted);
    }
}
