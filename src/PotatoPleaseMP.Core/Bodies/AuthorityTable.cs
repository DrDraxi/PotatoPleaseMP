namespace PotatoPleaseMP.Core.Bodies;

/// <summary>Host-side authority of one body (spec 07 §4).</summary>
public sealed class AuthRecord
{
    public NetId Id;
    public BodyMode Mode = BodyMode.HostSim;
    /// <summary>Owning slot; <see cref="BodyCodec.HostOwner"/> (15) for the host.</summary>
    public byte Owner = BodyCodec.HostOwner;
    public byte ClaimSeq;
    public ClaimReason Reason;
    /// <summary>Host time of the last owner sample (or the grant).</summary>
    public double LastOwnerTime;
    /// <summary>Container holder for <see cref="BodyMode.Contained"/>.</summary>
    public ushort Holder;
    public bool Resting;
    /// <summary>Host systems carry it (vacuum carry, intake tween, machine ride): claims are denied.</summary>
    public bool HostCarried;
    /// <summary>The host's own hand/vacuum pulls it (<c>PotatoPuller.IsAnyPulling</c>): claims are denied.</summary>
    public bool HostPulling;

    public bool IsRemoteOwned => Owner != BodyCodec.HostOwner && Mode is BodyMode.ClientOwned or BodyMode.PeelHeld;
    public byte AuthByte => BodyCodec.PackAuth(Owner, Mode, Resting);
}

/// <summary>Why a claim was denied (host log/dev stat; the wire only carries <see cref="AuthResult.Denied"/>).</summary>
public enum DenyReason : byte { None = 0, UnknownBody, NotHostSim, OwnedByOther, HostCarried, HostPulling, TooFar, TooMany, BadSlot }

/// <summary>Result of a host decision, ready to go out as one <see cref="BodyAuth"/> entry to all.</summary>
public readonly record struct AuthChange(AuthEntry Entry, DenyReason Deny = DenyReason.None)
{
    public bool Granted => Entry.Result == AuthResult.Granted;
}

/// <summary>
/// Host authority rules (spec 07 §4): first claim in the host's ordered stream wins; grant iff the body is
/// <see cref="BodyMode.HostSim"/>, not carried or pulled by a host system, not owned by another player, within
/// <c>captureRange + 1.5 m</c> of the claimant's eye and the claimant owns fewer than 16 bodies. Release, revoke,
/// 2 s owner timeout and player-left return the body to <see cref="BodyMode.HostSim"/>.
/// </summary>
public sealed class AuthorityTable
{
    readonly Dictionary<ushort, AuthRecord> _bodies = new();
    readonly int[] _owned = new int[16];

    public int MaxOwnedPerClient { get; set; } = 16;
    public double OwnerTimeoutSeconds { get; set; } = 2.0;
    /// <summary><c>PotatoAttractor.captureRange</c> (3.5 m); claims reach this + <see cref="ClaimSlack"/>.</summary>
    public float CaptureRange { get; set; } = 3.5f;
    public float ClaimSlack { get; set; } = 1.5f;

    public int Count => _bodies.Count;
    public IEnumerable<AuthRecord> All => _bodies.Values;

    public AuthRecord Add(NetId id)
    {
        if (!_bodies.TryGetValue(id.Value, out var rec)) _bodies[id.Value] = rec = new AuthRecord { Id = id };
        return rec;
    }

    /// <summary>Forgets a despawned body (an owner loses it with the despawn, spec 07 §4 "host takes over").</summary>
    public bool Remove(NetId id)
    {
        if (!_bodies.Remove(id.Value, out var rec)) return false;
        if (rec.IsRemoteOwned) _owned[rec.Owner]--;
        return true;
    }

    public AuthRecord? Get(NetId id) => _bodies.TryGetValue(id.Value, out var r) ? r : null;
    public int OwnedCount(byte slot) => slot < 16 ? _owned[slot] : 0;
    public bool IsOwnedBy(NetId id, byte slot) => Get(id) is { } r && r.IsRemoteOwned && r.Owner == slot;

    /// <summary>
    /// A client claim. <paramref name="eyeDistance"/> is the distance from the claimant's eye to the body (pass 0 when
    /// unknown). A repeated claim by the current owner is granted again with the new sequence.
    /// </summary>
    public AuthChange TryClaim(NetId id, byte slot, ClaimReason reason, byte claimSeq, float eyeDistance, double now)
    {
        var rec = Get(id);
        DenyReason deny = DenyReason.None;
        if (slot == 0 || slot >= BodyCodec.HostOwner) deny = DenyReason.BadSlot;
        else if (rec == null) deny = DenyReason.UnknownBody;
        else if (rec.IsRemoteOwned && rec.Owner == slot) { rec.ClaimSeq = claimSeq; rec.Reason = reason; rec.LastOwnerTime = now; return Make(rec, AuthResult.Granted); }
        else if (rec.IsRemoteOwned) deny = DenyReason.OwnedByOther;
        else if (rec.Mode != BodyMode.HostSim) deny = DenyReason.NotHostSim;
        else if (rec.HostCarried) deny = DenyReason.HostCarried;
        else if (rec.HostPulling) deny = DenyReason.HostPulling;
        else if (eyeDistance > CaptureRange + ClaimSlack) deny = DenyReason.TooFar;
        else if (_owned[slot] >= MaxOwnedPerClient) deny = DenyReason.TooMany;

        if (deny != DenyReason.None)
        {
            byte auth = rec?.AuthByte ?? BodyCodec.PackAuth(BodyCodec.HostOwner, BodyMode.HostSim);
            return new AuthChange(new AuthEntry(id, auth, AuthResult.Denied, claimSeq), deny);
        }

        rec!.Mode = reason == ClaimReason.Peel ? BodyMode.PeelHeld : BodyMode.ClientOwned;
        rec.Owner = slot;
        rec.ClaimSeq = claimSeq;
        rec.Reason = reason;
        rec.LastOwnerTime = now;
        rec.Resting = false;
        _owned[slot]++;
        return Make(rec, AuthResult.Granted);
    }

    /// <summary>Grants a body to a player on the host's own initiative (peel lock, adopted keep-owned spawn).</summary>
    public AuthChange Grant(NetId id, byte slot, ClaimReason reason, double now)
    {
        var rec = Add(id);
        if (rec.IsRemoteOwned) _owned[rec.Owner]--;
        rec.Mode = reason == ClaimReason.Peel ? BodyMode.PeelHeld : BodyMode.ClientOwned;
        rec.Owner = slot; rec.Reason = reason; rec.LastOwnerTime = now; rec.Resting = false;
        _owned[slot]++;
        return Make(rec, AuthResult.Granted);
    }

    /// <summary>An owner sample arrived; false (drop it) unless <paramref name="slot"/> owns the body.</summary>
    public bool NoteOwnerSample(NetId id, byte slot, double now)
    {
        var rec = Get(id);
        if (rec == null || !rec.IsRemoteOwned || rec.Owner != slot) return false;
        rec.LastOwnerTime = now;
        return true;
    }

    /// <summary>The owner releases (drop, throw, tween end...). Null when <paramref name="slot"/> does not own it.</summary>
    public AuthChange? Release(NetId id, byte slot)
    {
        var rec = Get(id);
        if (rec == null || !rec.IsRemoteOwned || rec.Owner != slot) return null;
        ToHost(rec);
        return Make(rec, AuthResult.Returned);
    }

    /// <summary>The host takes a body back (host system needs it, validation failure). Null when it was not remote-owned.</summary>
    public AuthChange? Revoke(NetId id)
    {
        var rec = Get(id);
        if (rec == null || !rec.IsRemoteOwned) return null;
        ToHost(rec);
        return Make(rec, AuthResult.Revoked);
    }

    /// <summary>Owned bodies with no owner sample for <see cref="OwnerTimeoutSeconds"/> go back to the host.</summary>
    public List<AuthChange> Timeout(double now)
    {
        var list = new List<AuthChange>();
        foreach (var rec in _bodies.Values)
        {
            if (!rec.IsRemoteOwned || now - rec.LastOwnerTime < OwnerTimeoutSeconds) continue;
            ToHost(rec);
            list.Add(Make(rec, AuthResult.Returned));
        }
        return list;
    }

    /// <summary>Every body owned by a leaving player returns to the host.</summary>
    public List<AuthChange> PlayerLeft(byte slot)
    {
        var list = new List<AuthChange>();
        foreach (var rec in _bodies.Values)
        {
            if (!rec.IsRemoteOwned || rec.Owner != slot) continue;
            ToHost(rec);
            list.Add(Make(rec, AuthResult.Returned));
        }
        return list;
    }

    /// <summary>Body goes into a container slot (bucket real, station slot): no transform sync, no claims.</summary>
    public void SetContained(NetId id, ushort holder)
    {
        var rec = Add(id);
        if (rec.IsRemoteOwned) _owned[rec.Owner]--;
        rec.Mode = BodyMode.Contained; rec.Owner = BodyCodec.HostOwner; rec.Holder = holder;
    }

    public void SetHostSim(NetId id)
    {
        var rec = Add(id);
        ToHost(rec);
    }

    void ToHost(AuthRecord rec)
    {
        if (rec.IsRemoteOwned) _owned[rec.Owner]--;
        rec.Mode = BodyMode.HostSim;
        rec.Owner = BodyCodec.HostOwner;
        rec.Holder = 0;
    }

    static AuthChange Make(AuthRecord rec, AuthResult result)
        => new(new AuthEntry(rec.Id, rec.AuthByte, result, rec.ClaimSeq));
}
