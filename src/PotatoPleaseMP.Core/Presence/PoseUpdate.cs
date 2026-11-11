using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Presence;

[Flags]
public enum PoseFlags : ushort
{
    None = 0, Grounded = 1 << 0, Sprint = 1 << 1, Peeling = 1 << 2, WorldPeel = 1 << 3, Shaving = 1 << 4, Firing = 1 << 5,
    Scrubbing = 1 << 6, Sucking = 1 << 7, Sack = 1 << 8, Busy = 1 << 9, Seated = 1 << 10, Downed = 1 << 11,
    Cinematic = 1 << 12, Smoking = 1 << 13,
}

/// <summary>
/// 0x10 <c>PoseUpdate</c> (spec 04 §4.1), 24 B (30 B wide): slot u8, seq u8, timeMs u16, meta u8 (bits 0-1 teleportSeq,
/// 2-4 platform, 5 wide), pos 3 x i16 cm (3 x f32 when wide), yaw u16, pitch i16 (0.01 deg), vel 3 x i8 (x,z 0.1 m/s,
/// y 0.2 m/s), crouch u8, flags u16, tool u8, carry u8, temp u8. Unreliable; the host relays it with the slot overwritten
/// from the sender (spec 01 §5.2), so <see cref="SlotOffset"/> is 0.
/// </summary>
public struct PoseUpdate : IWireMessage
{
    public const int SlotOffset = 0;
    public const int Size = 24, WideSize = 30;

    public byte Slot; public byte Seq; public ushort TimeMs; public byte TeleportSeq; public byte Platform;
    public Vec3 Pos; public float YawDeg; public float PitchDeg; public Vec3 Vel; public float Crouch;
    public PoseFlags Flags; public byte Tool; public byte Carry; public float Warmth;

    public MsgId Id => MsgId.PoseUpdate;

    public void Write(NetWriter w)
    {
        bool wide = MathF.Abs(Pos.X) > 327.67f || MathF.Abs(Pos.Y) > 327.67f || MathF.Abs(Pos.Z) > 327.67f;
        w.U8(Slot); w.U8(Seq); w.U16(TimeMs);
        w.U8((byte)((TeleportSeq & 3) | ((Platform & 7) << 2) | (wide ? 1 << 5 : 0)));
        if (wide) { w.F32(Pos.X); w.F32(Pos.Y); w.F32(Pos.Z); }
        else Quant.WritePos16(w, Pos);
        w.U16(Quant.Yaw16(YawDeg));
        w.I16((short)Math.Clamp(MathF.Round(PitchDeg * 100f), -9000, 9000));
        w.I8(Clamp8(Vel.X * 10f)); w.I8(Clamp8(Vel.Y * 5f)); w.I8(Clamp8(Vel.Z * 10f));
        w.U8(Quant.UNorm8(Crouch));
        w.U16((ushort)Flags);
        w.U8(Tool); w.U8(Carry);
        w.U8(Quant.UNorm8(Warmth));
    }

    public void Read(ref NetReader r)
    {
        Slot = r.U8(); Seq = r.U8(); TimeMs = r.U16();
        byte meta = r.U8();
        TeleportSeq = (byte)(meta & 3); Platform = (byte)((meta >> 2) & 7);
        if ((meta & (1 << 5)) != 0) Pos = new Vec3(r.F32(), r.F32(), r.F32());
        else Pos = Quant.ReadPos16(ref r) ?? throw new WireFormatException("Pose without a position.");
        YawDeg = Quant.Yaw16ToDeg(r.U16());
        PitchDeg = r.I16() / 100f;
        Vel = new Vec3(r.I8() / 10f, r.I8() / 5f, r.I8() / 10f);
        Crouch = Quant.UNorm8(r.U8());
        Flags = (PoseFlags)r.U16();
        Tool = r.U8(); Carry = r.U8();
        Warmth = Quant.UNorm8(r.U8());
        r.ExpectEnd();
    }

    static sbyte Clamp8(float v) => (sbyte)Math.Clamp((int)MathF.Round(v), -127, 127);
}
