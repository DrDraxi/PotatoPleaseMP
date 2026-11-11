using PotatoPleaseMP.Core.Bodies;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Peeling over the network (spec 05, first cut): every carve op captured by <see cref="PeelCapture"/> on a networked potato
/// is sent as CarveRecords {netId, op}; receivers replay it on their copy of that potato (made carveable first). The host
/// relays a client's ops to everyone else. Ops travel as raw floats for now (~100 B each; quantisation comes later).
/// </summary>
internal static class PeelSync
{
    public static long Sent, Applied, Unknown;

    public static void Init() => PeelCapture.OpApplied += OnLocalOp;

    static void OnLocalOp(PotatoPeeler.VoxelPotato voxel, CarveOp op)
    {
        if (!MpRuntime.Active) return;
        var inst = voxel.GetComponent<PotatoPeeler.PotatoInstance>();
        if (inst == null) return;
        var id = BodySync.IdOf(inst);
        if (!id.IsValid) return;
        var w = new NetWriter();
        w.U16(id.Value);
        Write(w, op);
        if (MpRuntime.Host is { } h) h.Broadcast(MsgId.CarveRecords, w.Span);
        else if (MpRuntime.Client is { State: ClientState.Playing } c) c.Send(MsgId.CarveRecords, w.Span);
        Sent++;
    }

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if (id != MsgId.CarveRecords) return false;
        ushort netId; CarveOp op;
        try { var r = new NetReader(payload); netId = r.U16(); op = Read(ref r); }
        catch (WireFormatException) { return true; }
        var inst = BodySync.Get(new NetId(netId));
        if (inst == null) { Unknown++; }
        else
        {
            var voxel = inst.EnsureCarveable();
            if (voxel != null && PeelCapture.Apply(voxel, op)) Applied++;
        }
        if (from != null && MpRuntime.Host is { } h) h.Broadcast(MsgId.CarveRecords, payload, exceptSlot: from.Slot);
        return true;
    }

    static void V3(NetWriter w, Vector3 v) { w.F32(v.x); w.F32(v.y); w.F32(v.z); }
    static Vector3 V3(ref NetReader r) => new(r.F32(), r.F32(), r.F32());

    static void Write(NetWriter w, CarveOp c)
    {
        w.U8((byte)c.Kind);
        w.U8((byte)c.Points.Length);
        foreach (var p in c.Points) { w.F32(p.x); w.F32(p.y); w.F32(p.z); }
        w.U8((byte)c.Rights.Length);
        foreach (var p in c.Rights) { w.F32(p.x); w.F32(p.y); w.F32(p.z); w.F32(p.w); }
        w.F32(c.Radius); w.F32(c.ToothPeriod); w.F32(c.ToothDepth); w.F32(c.HalfWidth);
        w.F32(c.EdgeToothPeriod); w.F32(c.EdgeToothDepth); w.F32(c.MaxDepth); w.F32(c.GuardDepth);
        V3(w, c.AffectedMin); V3(w, c.AffectedMax); V3(w, c.LatticeAxis); V3(w, c.PlanePoint); V3(w, c.PlaneNormal);
    }

    static CarveOp Read(ref NetReader r)
    {
        var c = new CarveOp { Kind = r.U8() };
        int n = r.U8();
        c.Points = new Vector4[n];
        for (int i = 0; i < n; i++) c.Points[i] = new Vector4(r.F32(), r.F32(), r.F32(), 0);
        n = r.U8();
        c.Rights = new Vector4[n];
        for (int i = 0; i < n; i++) c.Rights[i] = new Vector4(r.F32(), r.F32(), r.F32(), r.F32());
        c.Radius = r.F32(); c.ToothPeriod = r.F32(); c.ToothDepth = r.F32(); c.HalfWidth = r.F32();
        c.EdgeToothPeriod = r.F32(); c.EdgeToothDepth = r.F32(); c.MaxDepth = r.F32(); c.GuardDepth = r.F32();
        c.AffectedMin = V3(ref r); c.AffectedMax = V3(ref r); c.LatticeAxis = V3(ref r); c.PlanePoint = V3(ref r); c.PlaneNormal = V3(ref r);
        return c;
    }

    public static object Describe() => new { Sent, Applied, Unknown };
}
