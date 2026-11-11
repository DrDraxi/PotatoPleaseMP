using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Bodies;

// ==================================================================== enums (spec 07 §5)

/// <summary><c>form</c> low nibble; the high nibble carries the vegetable kind for <see cref="Vegetable"/>.</summary>
public enum SpawnForm : byte { Pooled = 0, Instantiated = 1, Vegetable = 2, BindByRef = 3, BindStatic = 4 }

public enum SpawnOrigin : byte
{
    Activate = 0, Blast, Sack, BucketPour, VacuumSpill, BarrowPour, MachineEject, Thrower, Save, Recovery, Adopt, Story,
}

[Flags]
public enum SpawnFlags : ushort
{
    None = 0, IsHeld = 1 << 0, IsCarried = 1 << 1, IsParked = 1 << 2, IsPinned = 1 << 3, PeelForced = 1 << 4,
    PeelCounted = 1 << 5, ComboPopupShown = 1 << 6, RestKinematic = 1 << 7, RegisterActive = 1 << 8, Factory = 1 << 9,
    HasCarve = 1 << 10, HasRot = 1 << 11, HasBlood = 1 << 12, HasCombo = 1 << 13, Frozen = 1 << 14, Resting = 1 << 15,
}

public enum DespawnReason : byte
{
    Pooled = 0, Sold, Swallowed, Bucketed, Barrow, Machine, Packaged, Repiled, Claw, Ruined, Lost, Destroyed,
}

[Flags]
public enum RestFlags : byte { None = 0, KinematicAtRest = 1, Parked = 2, /// <summary>Pose outside the level box: 3 x f32 + 4 x f32.</summary>
    FloatPose = 0x80 }

public enum ClaimReason : byte { Hand = 0, Vacuum, VacuumBulk, BucketReal, Barrow, Peel }
public enum AuthResult : byte { Granted = 0, Denied, Revoked, Returned }
public enum ReleaseReason : byte { Drop = 0, Throw, TweenEnd, Detach, Toss, Barrow }
public enum ClientSpawnSource : byte { BucketPour = 0, VacuumSpill, VacuumDelivery, BarrowPour }

[Flags]
public enum ClientSpawnFlags : byte { None = 0, KeepOwned = 1, PeeledOnSpill = 2, /// <summary>Optional f16 factory depth follows.</summary>
    HasFactoryDepth = 4 }

public enum AdoptReason : byte { Ok = 0, Empty, BadHolder, NoIds, Duplicate, Unknown }
public enum ConsumeKind : byte { Vacuum = 0, Bucket }
public enum ContainerKind : byte { Vacuum = 0, Bucket, Barrow }
public enum SlotKind : byte { StationRaw = 0, BucketReal, Thrower, MachineRide }
public enum FxKind : byte { Impact = 0, ChannelSplash, BasinSplash, Accept, Reject }

[Flags]
public enum ScalarMask : byte { None = 0, Frost = 1, Char = 2, Flags = 4 }

/// <summary>Unity-free Vector4 (rot/blood spots in <see cref="BodySpawn"/>, written as 4 x f16).</summary>
public readonly record struct Vec4(float X, float Y, float Z, float W);

// ==================================================================== helpers

public static class BodyMessages
{
    /// <summary>Attaches every 0x40-0x4F codec to the registry.</summary>
    public static void AttachCodecs(MessageRegistry g)
    {
        g.SetCodec(MsgId.BodySpawn, () => new BodySpawn());
        g.SetCodec(MsgId.BodyDespawn, () => new BodyDespawn());
        g.SetCodec(MsgId.BodyState, () => new BodyState());
        g.SetCodec(MsgId.BodyOwnedState, () => new BodyOwnedState());
        g.SetCodec(MsgId.BodyRest, () => new BodyRest());
        g.SetCodec(MsgId.BodyClaim, () => new BodyClaim());
        g.SetCodec(MsgId.BodyAuth, () => new BodyAuth());
        g.SetCodec(MsgId.BodyRelease, () => new BodyRelease());
        g.SetCodec(MsgId.ClientSpawn, () => new ClientSpawn());
        g.SetCodec(MsgId.SpawnAdopt, () => new SpawnAdopt());
        g.SetCodec(MsgId.BodyConsume, () => new BodyConsume());
        g.SetCodec(MsgId.ContainerCount, () => new ContainerCount());
        g.SetCodec(MsgId.BodySlot, () => new BodySlot());
        g.SetCodec(MsgId.BodyFx, () => new BodyFx());
        g.SetCodec(MsgId.BodyScalar, () => new BodyScalar());
        g.SetCodec(MsgId.BodyAudit, () => new BodyAudit());
    }

    public static byte[] Encode<T>(in T msg) where T : IWireMessage
    {
        using var w = new NetWriter(256);
        msg.Write(w);
        return w.ToArray();
    }

    public static T Decode<T>(ReadOnlySpan<byte> payload) where T : struct, IWireMessage
    {
        var m = new T();
        var r = new NetReader(payload);
        m.Read(ref r);
        return m;
    }

    internal static T Enum<T>(byte v, T max) where T : struct, System.Enum
    {
        if (v > Convert.ToByte(max)) throw new WireFormatException($"{typeof(T).Name} value {v} out of range.");
        return (T)(object)v;
    }

    /// <summary>u16 batch count checked against the bytes left.</summary>
    internal static int Count16(ref NetReader r, int minEach, int max = 4096)
    {
        int n = r.U16();
        if (n > max || (long)n * minEach > r.Remaining) throw new WireFormatException($"Body batch count {n} does not fit.");
        return n;
    }

    internal static int Count8(ref NetReader r, int minEach)
    {
        int n = r.U8();
        if ((long)n * minEach > r.Remaining) throw new WireFormatException($"Body batch count {n} does not fit.");
        return n;
    }

    internal static void CheckCount(int n, int max, string what)
    {
        if (n > max) throw new ArgumentException($"{what}: {n} entries, the wire holds {max}.");
    }

    internal static NetId Id(ref NetReader r) => new(r.U16());

    /// <summary>Hundredths in a byte (0..2.55): ContainerCount prop scale, BodyFx pitch.</summary>
    internal static byte Centi8(float v) => float.IsFinite(v) ? (byte)Math.Clamp(MathF.Round(v * 100f), 0, 255) : (byte)0;
}

// ==================================================================== 0x40 BodySpawn

public struct SpawnEntry
{
    public NetId Id; public SpawnForm Form; public byte VegetableKind; public SpawnOrigin Origin; public byte Auth;
    public Vec3 Pos; public Quat Rot; public Vec3 Vel; public byte Variant; public float TrueScale; public SpawnFlags Flags;
    public byte Frost; public byte Char; public uint EyeSeed; public ushort CarveVer;
    // BindByRef
    public byte RefKind; public ushort RefId; public byte ChildIdx;
    // Flags.Factory / HasCombo / HasRot / HasBlood
    public float FactoryDepth; public int Combo; public Vec4[]? RotSpots; public byte BloodCount; public Vec4[]? BloodSpots;

    public const int FixedSize = 34;
}

/// <summary>0x40 H→C R: n u16, per entry 34 B + optional parts (BindByRef 4, factory 2, combo 4, rot 1+8n, blood 2+8n).</summary>
public struct BodySpawn : IWireMessage
{
    public List<SpawnEntry> Entries;
    public MsgId Id => MsgId.BodySpawn;

    public void Write(NetWriter w)
    {
        int n = Entries?.Count ?? 0;
        BodyMessages.CheckCount(n, ushort.MaxValue, nameof(BodySpawn));
        w.U16((ushort)n);
        for (int i = 0; i < n; i++) WriteEntry(w, Entries![i]);
    }

    /// <summary>Encodes a large list as several <see cref="BodySpawn"/> payloads of at most <paramref name="maxBytes"/> each.</summary>
    public static List<byte[]> EncodeChunks(List<SpawnEntry> entries, int maxBytes = 48 * 1024)
    {
        var chunks = new List<byte[]>();
        if (entries.Count == 0) return chunks;
        using var body = new NetWriter(4096);
        using var one = new NetWriter(64);
        int n = 0;
        void Flush()
        {
            var b = new byte[2 + body.Length];
            b[0] = (byte)n; b[1] = (byte)(n >> 8);
            body.Span.CopyTo(b.AsSpan(2));
            chunks.Add(b);
            body.Reset(); n = 0;
        }
        foreach (var e in entries)
        {
            one.Reset();
            WriteEntry(one, e);
            if (n > 0 && (2 + body.Length + one.Length > maxBytes || n == ushort.MaxValue)) Flush();
            body.Raw(one.Span);
            n++;
        }
        Flush();
        return chunks;
    }

    public static void WriteEntry(NetWriter w, in SpawnEntry e)
    {
        w.U16(e.Id.Value);
        w.U8((byte)(((byte)e.Form & 0xF) | ((e.VegetableKind & 0xF) << 4)));
        w.U8((byte)e.Origin); w.U8(e.Auth);
        BodyCodec.WritePosAbs(w, e.Pos); BodyCodec.WriteRot(w, e.Rot); BodyCodec.WriteVel(w, e.Vel);
        w.U8(e.Variant); BodyCodec.WriteHalf(w, e.TrueScale); w.U16((ushort)e.Flags);
        w.U8(e.Frost); w.U8(e.Char); w.U32(e.EyeSeed); w.U16(e.CarveVer);
        if (e.Form == SpawnForm.BindByRef) { w.U8(e.RefKind); w.U16(e.RefId); w.U8(e.ChildIdx); }
        if ((e.Flags & SpawnFlags.Factory) != 0) BodyCodec.WriteHalf(w, e.FactoryDepth);
        if ((e.Flags & SpawnFlags.HasCombo) != 0) w.I32(e.Combo);
        if ((e.Flags & SpawnFlags.HasRot) != 0) WriteSpots(w, e.RotSpots);
        if ((e.Flags & SpawnFlags.HasBlood) != 0) { w.U8(e.BloodCount); WriteSpots(w, e.BloodSpots); }
    }

    static void WriteSpots(NetWriter w, Vec4[]? s)
    {
        int n = s?.Length ?? 0;
        BodyMessages.CheckCount(n, 255, "spots");
        w.U8((byte)n);
        for (int i = 0; i < n; i++)
        {
            BodyCodec.WriteHalf(w, s![i].X); BodyCodec.WriteHalf(w, s[i].Y); BodyCodec.WriteHalf(w, s[i].Z); BodyCodec.WriteHalf(w, s[i].W);
        }
    }

    public void Read(ref NetReader r)
    {
        int n = BodyMessages.Count16(ref r, SpawnEntry.FixedSize, ushort.MaxValue);
        Entries = new List<SpawnEntry>(n);
        for (int i = 0; i < n; i++) Entries.Add(ReadEntry(ref r));
        r.ExpectEnd();
    }

    public static SpawnEntry ReadEntry(ref NetReader r)
    {
        var e = new SpawnEntry { Id = BodyMessages.Id(ref r) };
        byte form = r.U8();
        e.Form = BodyMessages.Enum((byte)(form & 0xF), SpawnForm.BindStatic);
        e.VegetableKind = (byte)(form >> 4);
        e.Origin = BodyMessages.Enum(r.U8(), SpawnOrigin.Story);
        e.Auth = r.U8();
        BodyCodec.UnpackAuth(e.Auth);
        e.Pos = BodyCodec.ReadPosAbs(ref r); e.Rot = BodyCodec.ReadRot(ref r); e.Vel = BodyCodec.ReadVel(ref r);
        e.Variant = r.U8(); e.TrueScale = BodyCodec.ReadHalf(ref r); e.Flags = (SpawnFlags)r.U16();
        e.Frost = r.U8(); e.Char = r.U8(); e.EyeSeed = r.U32(); e.CarveVer = r.U16();
        if (e.Form == SpawnForm.BindByRef) { e.RefKind = r.U8(); e.RefId = r.U16(); e.ChildIdx = r.U8(); }
        if ((e.Flags & SpawnFlags.Factory) != 0) e.FactoryDepth = BodyCodec.ReadHalf(ref r);
        if ((e.Flags & SpawnFlags.HasCombo) != 0) e.Combo = r.I32();
        if ((e.Flags & SpawnFlags.HasRot) != 0) e.RotSpots = ReadSpots(ref r);
        if ((e.Flags & SpawnFlags.HasBlood) != 0) { e.BloodCount = r.U8(); e.BloodSpots = ReadSpots(ref r); }
        return e;
    }

    static Vec4[] ReadSpots(ref NetReader r)
    {
        int n = BodyMessages.Count8(ref r, 8);
        var s = new Vec4[n];
        for (int i = 0; i < n; i++) s[i] = new Vec4(r.F16(), r.F16(), r.F16(), r.F16());
        return s;
    }
}

// ==================================================================== 0x41 BodyDespawn

public readonly record struct DespawnEntry(NetId Id, DespawnReason Reason, byte Arg = 0);

/// <summary>0x41 H→C R: n u16, per id 2, reason 1, arg 1 = 4 B.</summary>
public struct BodyDespawn : IWireMessage
{
    public List<DespawnEntry> Entries;
    public MsgId Id => MsgId.BodyDespawn;

    public void Write(NetWriter w)
    {
        int n = Entries?.Count ?? 0;
        BodyMessages.CheckCount(n, ushort.MaxValue, nameof(BodyDespawn));
        w.U16((ushort)n);
        for (int i = 0; i < n; i++) { var e = Entries![i]; w.U16(e.Id.Value); w.U8((byte)e.Reason); w.U8(e.Arg); }
    }

    public void Read(ref NetReader r)
    {
        int n = BodyMessages.Count16(ref r, 4, ushort.MaxValue);
        Entries = new List<DespawnEntry>(n);
        for (int i = 0; i < n; i++)
            Entries.Add(new DespawnEntry(BodyMessages.Id(ref r), BodyMessages.Enum(r.U8(), DespawnReason.Destroyed), r.U8()));
        r.ExpectEnd();
    }
}

// ==================================================================== 0x42 BodyState

public readonly record struct StateEntry(NetId Id, Vec3 Pos, Quat Rot, bool Snap = false);

/// <summary>
/// 0x42 H→C U, per receiver: header n u8 + anchor 3 x i16 cm = 7 B; per entry <c>head</c> u8 (bits 0-5 id delta 1..63
/// from the previous id, 0 = u16 id follows; bit 6 absolute pos; bit 7 snap), then pos (5 B relative or 6 B level box)
/// and rot 4 B: 10 B typical, 12-13 B worst. Entries in ascending id order give the 1-byte id form; any order is legal.
/// The envelope carries the host tick.
/// </summary>
public struct BodyState : IWireMessage
{
    public const int HeaderSize = 7, MaxEntries = 255;
    const byte AbsBit = 0x40, SnapBit = 0x80, DeltaMask = 0x3F;

    public Vec3 Anchor;
    public List<StateEntry> Entries;
    public MsgId Id => MsgId.BodyState;

    /// <summary>Exact size of an entry written after <paramref name="prev"/> with the (quantised) anchor.</summary>
    public static int EntrySize(NetId prev, in StateEntry e, Vec3 quantAnchor)
    {
        int d = e.Id.Value - prev.Value;
        return 1 + (d >= 1 && d <= DeltaMask ? 0 : 2) + (BodyCodec.FitsRel(e.Pos, quantAnchor) ? 5 : 6) + 4;
    }

    /// <summary>Exact payload size of <paramref name="entries"/> in the given order.</summary>
    public static int Measure(Vec3 anchor, IReadOnlyList<StateEntry> entries)
    {
        var qa = BodyCodec.QuantizeAnchor(anchor);
        int size = HeaderSize;
        var prev = NetId.None;
        for (int i = 0; i < entries.Count; i++) { size += EntrySize(prev, entries[i], qa); prev = entries[i].Id; }
        return size;
    }

    public void Write(NetWriter w)
    {
        int n = Entries?.Count ?? 0;
        BodyMessages.CheckCount(n, MaxEntries, nameof(BodyState));
        w.U8((byte)n);
        BodyCodec.WriteAnchor(w, Anchor);
        var qa = BodyCodec.QuantizeAnchor(Anchor);
        var prev = NetId.None;
        for (int i = 0; i < n; i++)
        {
            var e = Entries![i];
            int d = e.Id.Value - prev.Value;
            bool delta = d >= 1 && d <= DeltaMask;
            bool rel = BodyCodec.FitsRel(e.Pos, qa);
            w.U8((byte)((delta ? d : 0) | (rel ? 0 : AbsBit) | (e.Snap ? SnapBit : 0)));
            if (!delta) w.U16(e.Id.Value);
            if (rel) BodyCodec.WritePosRel(w, e.Pos, qa); else BodyCodec.WritePosAbs(w, e.Pos);
            BodyCodec.WriteRot(w, e.Rot);
            prev = e.Id;
        }
    }

    public void Read(ref NetReader r)
    {
        int n = r.U8();
        Anchor = BodyCodec.ReadAnchor(ref r);
        if ((long)n * 10 > r.Remaining) throw new WireFormatException($"BodyState count {n} does not fit.");
        Entries = new List<StateEntry>(n);
        var prev = NetId.None;
        for (int i = 0; i < n; i++)
        {
            byte head = r.U8();
            int d = head & DeltaMask;
            var id = d != 0 ? new NetId((ushort)(prev.Value + d)) : BodyMessages.Id(ref r);
            var pos = (head & AbsBit) != 0 ? BodyCodec.ReadPosAbs(ref r) : BodyCodec.ReadPosRel(ref r, Anchor);
            var rot = BodyCodec.ReadRot(ref r);
            Entries.Add(new StateEntry(id, pos, rot, (head & SnapBit) != 0));
            prev = id;
        }
        r.ExpectEnd();
    }
}

// ==================================================================== 0x43 BodyOwnedState

public readonly record struct OwnedEntry(NetId Id, Vec3 Pos, Quat Rot, Vec3 Vel);

/// <summary>0x43 C→H U: n u8, per id 2 (temp or net), posA 6, rot 4, v 6 = 18 B.</summary>
public struct BodyOwnedState : IWireMessage
{
    public const int EntrySize = 18;
    public List<OwnedEntry> Entries;
    public MsgId Id => MsgId.BodyOwnedState;

    public void Write(NetWriter w)
    {
        int n = Entries?.Count ?? 0;
        BodyMessages.CheckCount(n, 255, nameof(BodyOwnedState));
        w.U8((byte)n);
        for (int i = 0; i < n; i++)
        {
            var e = Entries![i];
            w.U16(e.Id.Value); BodyCodec.WritePosAbs(w, e.Pos); BodyCodec.WriteRot(w, e.Rot); BodyCodec.WriteVel(w, e.Vel);
        }
    }

    public void Read(ref NetReader r)
    {
        int n = BodyMessages.Count8(ref r, EntrySize);
        Entries = new List<OwnedEntry>(n);
        for (int i = 0; i < n; i++)
            Entries.Add(new OwnedEntry(BodyMessages.Id(ref r), BodyCodec.ReadPosAbs(ref r), BodyCodec.ReadRot(ref r), BodyCodec.ReadVel(ref r)));
        r.ExpectEnd();
    }
}

// ==================================================================== 0x44 BodyRest

public readonly record struct RestEntry(NetId Id, Vec3 Pos, Quat Rot, RestFlags Flags = RestFlags.None);

/// <summary>
/// 0x44 H→all R: n u16, per id 2, flags 1, then posA 6 + rot 4 (13 B), or with <see cref="RestFlags.FloatPose"/>
/// 3 x f32 + 4 x f32 (31 B). The writer sets FloatPose itself for a pose outside the level box.
/// </summary>
public struct BodyRest : IWireMessage
{
    public List<RestEntry> Entries;
    public MsgId Id => MsgId.BodyRest;

    public static int EntrySize(in RestEntry e) => BodyCodec.InLevelBox(e.Pos) ? 13 : 31;

    public void Write(NetWriter w)
    {
        int n = Entries?.Count ?? 0;
        BodyMessages.CheckCount(n, ushort.MaxValue, nameof(BodyRest));
        w.U16((ushort)n);
        for (int i = 0; i < n; i++)
        {
            var e = Entries![i];
            bool f32 = !BodyCodec.InLevelBox(e.Pos);
            var flags = f32 ? e.Flags | RestFlags.FloatPose : e.Flags & ~RestFlags.FloatPose;
            w.U16(e.Id.Value); w.U8((byte)flags);
            if (f32) BodyCodec.WritePoseF32(w, e.Pos, e.Rot);
            else { BodyCodec.WritePosAbs(w, e.Pos); BodyCodec.WriteRot(w, e.Rot); }
        }
    }

    public void Read(ref NetReader r)
    {
        int n = BodyMessages.Count16(ref r, 13, ushort.MaxValue);
        Entries = new List<RestEntry>(n);
        for (int i = 0; i < n; i++)
        {
            var id = BodyMessages.Id(ref r);
            var flags = (RestFlags)r.U8();
            if ((flags & RestFlags.FloatPose) != 0)
            {
                var (p, q) = BodyCodec.ReadPoseF32(ref r);
                Entries.Add(new RestEntry(id, p, q, flags));
            }
            else Entries.Add(new RestEntry(id, BodyCodec.ReadPosAbs(ref r), BodyCodec.ReadRot(ref r), flags));
        }
        r.ExpectEnd();
    }
}

// ==================================================================== 0x45 BodyClaim

public readonly record struct ClaimEntry(NetId Id, ClaimReason Reason, byte ClaimSeq);

/// <summary>0x45 C→H R: n u8, per id 2, reason 1, claimSeq 1 = 4 B.</summary>
public struct BodyClaim : IWireMessage
{
    public List<ClaimEntry> Entries;
    public MsgId Id => MsgId.BodyClaim;

    public void Write(NetWriter w)
    {
        int n = Entries?.Count ?? 0;
        BodyMessages.CheckCount(n, 255, nameof(BodyClaim));
        w.U8((byte)n);
        for (int i = 0; i < n; i++) { var e = Entries![i]; w.U16(e.Id.Value); w.U8((byte)e.Reason); w.U8(e.ClaimSeq); }
    }

    public void Read(ref NetReader r)
    {
        int n = BodyMessages.Count8(ref r, 4);
        Entries = new List<ClaimEntry>(n);
        for (int i = 0; i < n; i++)
            Entries.Add(new ClaimEntry(BodyMessages.Id(ref r), BodyMessages.Enum(r.U8(), ClaimReason.Peel), r.U8()));
        r.ExpectEnd();
    }
}

// ==================================================================== 0x46 BodyAuth

public readonly record struct AuthEntry(NetId Id, byte Auth, AuthResult Result, byte ClaimSeq)
{
    public byte Owner => (byte)(Auth & 0xF);
    public BodyMode Mode => BodyCodec.UnpackAuth(Auth).Mode;
}

/// <summary>0x46 H→all R (Denied only to the claimant): n u8, per id 2, auth 1, result 1, claimSeq 1 = 5 B.</summary>
public struct BodyAuth : IWireMessage
{
    public List<AuthEntry> Entries;
    public MsgId Id => MsgId.BodyAuth;

    public void Write(NetWriter w)
    {
        int n = Entries?.Count ?? 0;
        BodyMessages.CheckCount(n, 255, nameof(BodyAuth));
        w.U8((byte)n);
        for (int i = 0; i < n; i++)
        {
            var e = Entries![i];
            w.U16(e.Id.Value); w.U8(e.Auth); w.U8((byte)e.Result); w.U8(e.ClaimSeq);
        }
    }

    public void Read(ref NetReader r)
    {
        int n = BodyMessages.Count8(ref r, 5);
        Entries = new List<AuthEntry>(n);
        for (int i = 0; i < n; i++)
        {
            var id = BodyMessages.Id(ref r);
            byte auth = r.U8();
            BodyCodec.UnpackAuth(auth);
            Entries.Add(new AuthEntry(id, auth, BodyMessages.Enum(r.U8(), AuthResult.Returned), r.U8()));
        }
        r.ExpectEnd();
    }
}

// ==================================================================== 0x47 BodyRelease

/// <summary>0x47 C→H R: id 2, reason 1, posA 6, rot 4, v 6, w 3, ownerTick 2 = 24 B.</summary>
public struct BodyRelease : IWireMessage
{
    public const int Size = 24;
    public NetId Body; public ReleaseReason Reason; public Vec3 Pos; public Quat Rot; public Vec3 Vel; public Vec3 AngVel;
    public ushort OwnerTick;
    public MsgId Id => MsgId.BodyRelease;

    public void Write(NetWriter w)
    {
        w.U16(Body.Value); w.U8((byte)Reason);
        BodyCodec.WritePosAbs(w, Pos); BodyCodec.WriteRot(w, Rot); BodyCodec.WriteVel(w, Vel); BodyCodec.WriteAngVel(w, AngVel);
        w.U16(OwnerTick);
    }

    public void Read(ref NetReader r)
    {
        Body = BodyMessages.Id(ref r); Reason = BodyMessages.Enum(r.U8(), ReleaseReason.Barrow);
        Pos = BodyCodec.ReadPosAbs(ref r); Rot = BodyCodec.ReadRot(ref r); Vel = BodyCodec.ReadVel(ref r); AngVel = BodyCodec.ReadAngVel(ref r);
        OwnerTick = r.U16();
        r.ExpectEnd();
    }
}

// ==================================================================== 0x48 ClientSpawn

/// <summary>
/// 0x48 C→H R: tempId 2, source 1, holder 2, posA 6, rot 4, v 6, w 3, variant 1, trueScale f16 2, flags 1
/// [factoryDepth f16 2 when <see cref="ClientSpawnFlags.HasFactoryDepth"/>] = 28-30 B.
/// </summary>
public struct ClientSpawn : IWireMessage
{
    public NetId TempId; public ClientSpawnSource Source; public ushort Holder; public Vec3 Pos; public Quat Rot; public Vec3 Vel;
    public Vec3 AngVel; public byte Variant; public float TrueScale; public ClientSpawnFlags Flags; public float FactoryDepth;
    public MsgId Id => MsgId.ClientSpawn;

    public void Write(NetWriter w)
    {
        w.U16(TempId.Value); w.U8((byte)Source); w.U16(Holder);
        BodyCodec.WritePosAbs(w, Pos); BodyCodec.WriteRot(w, Rot); BodyCodec.WriteVel(w, Vel); BodyCodec.WriteAngVel(w, AngVel);
        w.U8(Variant); BodyCodec.WriteHalf(w, TrueScale); w.U8((byte)Flags);
        if ((Flags & ClientSpawnFlags.HasFactoryDepth) != 0) BodyCodec.WriteHalf(w, FactoryDepth);
    }

    public void Read(ref NetReader r)
    {
        TempId = BodyMessages.Id(ref r); Source = BodyMessages.Enum(r.U8(), ClientSpawnSource.BarrowPour); Holder = r.U16();
        Pos = BodyCodec.ReadPosAbs(ref r); Rot = BodyCodec.ReadRot(ref r); Vel = BodyCodec.ReadVel(ref r); AngVel = BodyCodec.ReadAngVel(ref r);
        Variant = r.U8(); TrueScale = BodyCodec.ReadHalf(ref r); Flags = (ClientSpawnFlags)r.U8();
        if ((Flags & ClientSpawnFlags.HasFactoryDepth) != 0) FactoryDepth = BodyCodec.ReadHalf(ref r);
        r.ExpectEnd();
    }
}

// ==================================================================== 0x49 SpawnAdopt

/// <summary>0x49 H→owner R: tempId 2, netId 2 (0 = rejected), reason 1 = 5 B.</summary>
public struct SpawnAdopt : IWireMessage
{
    public NetId TempId; public NetId NetId; public AdoptReason Reason;
    public MsgId Id => MsgId.SpawnAdopt;
    public bool Accepted => NetId.IsValid;

    public void Write(NetWriter w) { w.U16(TempId.Value); w.U16(NetId.Value); w.U8((byte)Reason); }

    public void Read(ref NetReader r)
    {
        TempId = BodyMessages.Id(ref r); NetId = BodyMessages.Id(ref r); Reason = BodyMessages.Enum(r.U8(), AdoptReason.Unknown);
        r.ExpectEnd();
    }
}

// ==================================================================== 0x4A BodyConsume

/// <summary>0x4A C→H R: kind 1, holder 2, n u8, n x id 2 = 4 + 2n.</summary>
public struct BodyConsume : IWireMessage
{
    public ConsumeKind Kind; public ushort Holder; public List<NetId> Ids;
    public MsgId Id => MsgId.BodyConsume;

    public void Write(NetWriter w)
    {
        int n = Ids?.Count ?? 0;
        BodyMessages.CheckCount(n, 255, nameof(BodyConsume));
        w.U8((byte)Kind); w.U16(Holder); w.U8((byte)n);
        for (int i = 0; i < n; i++) w.U16(Ids![i].Value);
    }

    public void Read(ref NetReader r)
    {
        Kind = BodyMessages.Enum(r.U8(), ConsumeKind.Bucket); Holder = r.U16();
        int n = BodyMessages.Count8(ref r, 2);
        Ids = new List<NetId>(n);
        for (int i = 0; i < n; i++) Ids.Add(BodyMessages.Id(ref r));
        r.ExpectEnd();
    }
}

// ==================================================================== 0x4B ContainerCount

public readonly record struct ContainerProp(byte Variant, float Scale);

/// <summary>
/// 0x4B H→all R: holder u16, kind 1, ownerSlot 1, count u16, peeled u16, m u8 + m x (variant 1, scale u8 in 0.01) with
/// m ≤ 8 newest props = 9-25 B.
/// </summary>
public struct ContainerCount : IWireMessage
{
    public const int MaxProps = 8;
    public ushort Holder; public ContainerKind Kind; public byte OwnerSlot; public ushort Count; public ushort Peeled;
    public List<ContainerProp> Props;
    public MsgId Id => MsgId.ContainerCount;

    public void Write(NetWriter w)
    {
        int m = Math.Min(Props?.Count ?? 0, MaxProps);
        w.U16(Holder); w.U8((byte)Kind); w.U8(OwnerSlot); w.U16(Count); w.U16(Peeled); w.U8((byte)m);
        for (int i = 0; i < m; i++) { w.U8(Props![i].Variant); w.U8(BodyMessages.Centi8(Props[i].Scale)); }
    }

    public void Read(ref NetReader r)
    {
        Holder = r.U16(); Kind = BodyMessages.Enum(r.U8(), ContainerKind.Barrow); OwnerSlot = r.U8();
        Count = r.U16(); Peeled = r.U16();
        if (Peeled > Count) throw new WireFormatException($"ContainerCount peeled {Peeled} > count {Count}.");
        int m = r.U8();
        if (m > MaxProps) throw new WireFormatException($"ContainerCount has {m} props (max {MaxProps}).");
        Props = new List<ContainerProp>(m);
        for (int i = 0; i < m; i++) Props.Add(new ContainerProp(r.U8(), r.U8() / 100f));
        r.ExpectEnd();
    }
}

// ==================================================================== 0x4C BodySlot

public readonly record struct SlotEntry(NetId Id, ushort Holder, byte Slot, SlotKind Kind)
{
    public const byte Left = 0xFF;
}

/// <summary>0x4C H→all R: n u8, per id 2, holder 2, slot 1 (0xFF = left), kind 1 = 6 B.</summary>
public struct BodySlot : IWireMessage
{
    public List<SlotEntry> Entries;
    public MsgId Id => MsgId.BodySlot;

    public void Write(NetWriter w)
    {
        int n = Entries?.Count ?? 0;
        BodyMessages.CheckCount(n, 255, nameof(BodySlot));
        w.U8((byte)n);
        for (int i = 0; i < n; i++) { var e = Entries![i]; w.U16(e.Id.Value); w.U16(e.Holder); w.U8(e.Slot); w.U8((byte)e.Kind); }
    }

    public void Read(ref NetReader r)
    {
        int n = BodyMessages.Count8(ref r, 6);
        Entries = new List<SlotEntry>(n);
        for (int i = 0; i < n; i++)
            Entries.Add(new SlotEntry(BodyMessages.Id(ref r), r.U16(), r.U8(), BodyMessages.Enum(r.U8(), SlotKind.MachineRide)));
        r.ExpectEnd();
    }
}

// ==================================================================== 0x4D BodyFx

/// <summary>One effect; <see cref="Clip"/> is a clip-name hash for impacts or a volume/zone index for splashes and accepts.</summary>
public readonly record struct FxEntry(FxKind Kind, NetId Body, Vec3 Pos, uint Clip, float Volume, float Pitch);

/// <summary>0x4D H↔C UE: n u8, per kind 1, id 2 (0 = none), posA 6, clip u32 4, vol u8 (0..1), pitch u8 (0.01, 0..2.55) = 15 B.</summary>
public struct BodyFx : IWireMessage
{
    public const int EntrySize = 15;
    public List<FxEntry> Entries;
    public MsgId Id => MsgId.BodyFx;

    public void Write(NetWriter w)
    {
        int n = Entries?.Count ?? 0;
        BodyMessages.CheckCount(n, 255, nameof(BodyFx));
        w.U8((byte)n);
        for (int i = 0; i < n; i++)
        {
            var e = Entries![i];
            w.U8((byte)e.Kind); w.U16(e.Body.Value); BodyCodec.WritePosAbs(w, e.Pos); w.U32(e.Clip);
            w.U8(Quant.UNorm8(e.Volume)); w.U8(BodyMessages.Centi8(e.Pitch));
        }
    }

    public void Read(ref NetReader r)
    {
        int n = BodyMessages.Count8(ref r, EntrySize);
        Entries = new List<FxEntry>(n);
        for (int i = 0; i < n; i++)
            Entries.Add(new FxEntry(BodyMessages.Enum(r.U8(), FxKind.Reject), BodyMessages.Id(ref r), BodyCodec.ReadPosAbs(ref r),
                r.U32(), Quant.UNorm8(r.U8()), r.U8() / 100f));
        r.ExpectEnd();
    }
}

// ==================================================================== 0x4E BodyScalar

public readonly record struct ScalarEntry(NetId Id, ScalarMask Mask, byte Frost, byte Char, SpawnFlags Flags)
{
    public int Size => 3 + ((Mask & ScalarMask.Frost) != 0 ? 1 : 0) + ((Mask & ScalarMask.Char) != 0 ? 1 : 0) + ((Mask & ScalarMask.Flags) != 0 ? 2 : 0);
}

/// <summary>0x4E H→all R: n u8, per id 2, mask 1, then frost u8 / char u8 / flags u16 as the mask says (≤ 7 B).</summary>
public struct BodyScalar : IWireMessage
{
    public List<ScalarEntry> Entries;
    public MsgId Id => MsgId.BodyScalar;

    public void Write(NetWriter w)
    {
        int n = Entries?.Count ?? 0;
        BodyMessages.CheckCount(n, 255, nameof(BodyScalar));
        w.U8((byte)n);
        for (int i = 0; i < n; i++)
        {
            var e = Entries![i];
            w.U16(e.Id.Value); w.U8((byte)e.Mask);
            if ((e.Mask & ScalarMask.Frost) != 0) w.U8(e.Frost);
            if ((e.Mask & ScalarMask.Char) != 0) w.U8(e.Char);
            if ((e.Mask & ScalarMask.Flags) != 0) w.U16((ushort)e.Flags);
        }
    }

    public void Read(ref NetReader r)
    {
        int n = BodyMessages.Count8(ref r, 3);
        Entries = new List<ScalarEntry>(n);
        for (int i = 0; i < n; i++)
        {
            var id = BodyMessages.Id(ref r);
            var mask = (ScalarMask)r.U8();
            if ((byte)mask > 7) throw new WireFormatException($"BodyScalar mask {(byte)mask}.");
            byte frost = (mask & ScalarMask.Frost) != 0 ? r.U8() : (byte)0;
            byte ch = (mask & ScalarMask.Char) != 0 ? r.U8() : (byte)0;
            var flags = (mask & ScalarMask.Flags) != 0 ? (SpawnFlags)r.U16() : SpawnFlags.None;
            Entries.Add(new ScalarEntry(id, mask, frost, ch, flags));
        }
        r.ExpectEnd();
    }
}

// ==================================================================== 0x4F BodyAudit

public enum AuditSub : byte { Digest = 0, Nack = 1 }

/// <summary>
/// 0x4F: sub 0 <c>Digest</c> (H→C, unreliable every 5 s): 128 x u16, one order-independent hash of the live host ids per
/// 256-id block (ids 0x0000-0x7FFF) = 257 B. Sub 1 <c>Nack</c> (C→H, reliable): n u8 + block indices; the host resends
/// those blocks as <c>BodySpawn</c>/<c>BodyDespawn</c>.
/// </summary>
public struct BodyAudit : IWireMessage
{
    public const int Blocks = 128, BlockSize = 256;
    public AuditSub Sub; public ushort[]? Digest; public List<byte> NackBlocks;
    public MsgId Id => MsgId.BodyAudit;

    public void Write(NetWriter w)
    {
        w.U8((byte)Sub);
        if (Sub == AuditSub.Digest)
        {
            var d = Digest ?? new ushort[Blocks];
            if (d.Length != Blocks) throw new ArgumentException($"Digest needs {Blocks} blocks.");
            for (int i = 0; i < Blocks; i++) w.U16(d[i]);
        }
        else
        {
            int n = NackBlocks?.Count ?? 0;
            BodyMessages.CheckCount(n, Blocks, "BodyAudit nack");
            w.U8((byte)n);
            for (int i = 0; i < n; i++) w.U8(NackBlocks![i]);
        }
    }

    public void Read(ref NetReader r)
    {
        Sub = BodyMessages.Enum(r.U8(), AuditSub.Nack);
        if (Sub == AuditSub.Digest)
        {
            Digest = new ushort[Blocks];
            for (int i = 0; i < Blocks; i++) Digest[i] = r.U16();
        }
        else
        {
            int n = BodyMessages.Count8(ref r, 1);
            if (n > Blocks) throw new WireFormatException($"BodyAudit nack of {n} blocks.");
            NackBlocks = new List<byte>(n);
            for (int i = 0; i < n; i++)
            {
                byte b = r.U8();
                if (b >= Blocks) throw new WireFormatException($"BodyAudit block {b}.");
                NackBlocks.Add(b);
            }
        }
        r.ExpectEnd();
    }

    /// <summary>Per-block digest of live host ids (order-independent; static and temp ids are not audited).</summary>
    public static ushort[] ComputeDigest(IEnumerable<NetId> liveIds)
    {
        var sums = new uint[Blocks];
        foreach (var id in liveIds)
        {
            if (!id.IsHost) continue;
            sums[id.Value / BlockSize] += Mix(id.Value);
        }
        var d = new ushort[Blocks];
        for (int i = 0; i < Blocks; i++) d[i] = (ushort)(sums[i] ^ (sums[i] >> 16));
        return d;
    }

    /// <summary>Indices of blocks that differ between two digests.</summary>
    public static List<byte> Diff(ushort[] a, ushort[] b)
    {
        var l = new List<byte>();
        for (int i = 0; i < Blocks; i++) if (a[i] != b[i]) l.Add((byte)i);
        return l;
    }

    static uint Mix(uint x)
    {
        x ^= x >> 16; x *= 0x7FEB352D; x ^= x >> 15; x *= 0x846CA68B; x ^= x >> 16;
        return x | 1; // never 0, so a lone id always changes its block
    }
}
