using HarmonyLib;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Peel shaving ribbons as shared floor mess. A ribbon the peeler's game spawns goes out as its sample strip
/// (ShavingSpawn 0x86, any -> host -> others) and every machine spawns the same ribbon from it. Sweeping an area
/// (ShavingSweepReq 0x87) removes the ribbons there on every machine; the sweeper's own game pays the sweep income as before
/// (forwarded to the host), relayed sweeps pay nothing.
/// </summary>
internal static class ShavingSync
{
    static bool _applying;
    public static long Out, In, SweepsOut, SweepsIn;

    [HarmonyPatch(typeof(PotatoPeeler.PeelShavingManager), nameof(PotatoPeeler.PeelShavingManager.TrySpawn),
        new[] { typeof(Il2CppSystem.Collections.Generic.List<PotatoPeeler.ShavingSample>), typeof(float), typeof(Vector3), typeof(Vector2),
                typeof(PotatoPeeler.RibbonStyle), typeof(bool), typeof(bool), typeof(Il2CppSystem.Nullable<Color>) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
    static class SpawnPatch
    {
        static void Postfix(Il2CppSystem.Collections.Generic.List<PotatoPeeler.ShavingSample> samples, float halfWidth, Vector3 ejectDir, Vector2 uvOffset,
            ref PotatoPeeler.RibbonStyle style, bool applyCurl, bool fleshTint, Il2CppSystem.Nullable<Color> fleshTintOverride, bool __result)
        {
            if (_applying || !__result || !MpRuntime.Active || samples == null || samples.Count < 2) return;
            try
            {
                int n = Math.Min(samples.Count, 96);
                var w = new NetWriter();
                w.F32(halfWidth); V3(w, ejectDir); w.F32(uvOffset.x); w.F32(uvOffset.y);
                w.F32(style.edgeNoiseAmplitude); w.F32(style.edgeNoiseFrequency); w.F32(style.edgeScallopPeriod);
                w.F32(style.edgeScallopAmplitude); w.F32(style.crinkleAmplitude); w.F32(style.crinkleFrequency);
                bool hasTint = fleshTintOverride != null && fleshTintOverride.HasValue;
                w.U8((byte)((applyCurl ? 1 : 0) | (fleshTint ? 2 : 0) | (hasTint ? 4 : 0)));
                if (hasTint) { var c = fleshTintOverride!.Value; w.F32(c.r); w.F32(c.g); w.F32(c.b); w.F32(c.a); }
                w.U8((byte)n);
                for (int i = 0; i < n; i++) { var s = samples[i]; V3(w, s.Position); V3(w, s.Normal); w.F32(s.WidthScale); w.F32(s.Thickness); }
                if (MpRuntime.Host is { } h) h.Broadcast(MsgId.ShavingSpawn, w.Span);
                else if (MpRuntime.Client is { State: ClientState.Playing } cl) cl.Send(MsgId.ShavingSpawn, w.Span);
                Out++;
            }
            catch (Exception e) { Plugin.L.LogWarning($"[shavings] out: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.PeelShavingManager), nameof(PotatoPeeler.PeelShavingManager.SweepArea))]
    static class SweepPatch
    {
        static void Postfix(Vector2 min, Vector2 max, float maxY)
        {
            if (_applying || !MpRuntime.Active) return;
            var w = new NetWriter(); w.F32(min.x); w.F32(min.y); w.F32(max.x); w.F32(max.y); w.F32(maxY);
            if (MpRuntime.Host is { } h) h.Broadcast(MsgId.ShavingSweepReq, w.Span);
            else if (MpRuntime.Client is { State: ClientState.Playing } cl) cl.Send(MsgId.ShavingSweepReq, w.Span);
            SweepsOut++;
        }
    }

    static void V3(NetWriter w, Vector3 v) { w.F32(v.x); w.F32(v.y); w.F32(v.z); }
    static Vector3 RV(ref NetReader r) => new(r.F32(), r.F32(), r.F32());

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if (id != MsgId.ShavingSpawn && id != MsgId.ShavingSweepReq) return false;
        try
        {
            if (from != null && MpRuntime.Host is { } h) h.Broadcast(id, payload, exceptSlot: from.Slot);
            var mgr = PotatoPeeler.PeelShavingManager.Get();
            if (mgr == null) return true;
            var r = new NetReader(payload);
            _applying = true;
            try
            {
                if (id == MsgId.ShavingSweepReq)
                {
                    var min = new Vector2(r.F32(), r.F32()); var max = new Vector2(r.F32(), r.F32()); float maxY = r.F32();
                    mgr.SweepArea(min, max, maxY);
                    SweepsIn++;
                    return true;
                }
                float hw = r.F32(); var eject = RV(ref r); var uv = new Vector2(r.F32(), r.F32());
                var style = new PotatoPeeler.RibbonStyle
                {
                    edgeNoiseAmplitude = r.F32(), edgeNoiseFrequency = r.F32(), edgeScallopPeriod = r.F32(),
                    edgeScallopAmplitude = r.F32(), crinkleAmplitude = r.F32(), crinkleFrequency = r.F32(),
                };
                byte flags = r.U8();
                var tint = new Il2CppSystem.Nullable<Color>();
                if ((flags & 4) != 0) tint = new Il2CppSystem.Nullable<Color>(new Color(r.F32(), r.F32(), r.F32(), r.F32()));
                int n = r.U8();
                var list = new Il2CppSystem.Collections.Generic.List<PotatoPeeler.ShavingSample>();
                for (int i = 0; i < n; i++)
                {
                    var s = new PotatoPeeler.ShavingSample { Position = RV(ref r), Normal = RV(ref r), WidthScale = r.F32(), Thickness = r.F32() };
                    list.Add(s);
                }
                mgr.TrySpawn(list, hw, eject, uv, ref style, (flags & 1) != 0, (flags & 2) != 0, tint);
                In++;
            }
            finally { _applying = false; }
        }
        catch (WireFormatException) { }
        catch (Exception e) { Plugin.L.LogWarning($"[shavings] in: {e.Message}"); }
        return true;
    }

    public static object Describe()
    {
        int living = 0; try { living = PotatoPeeler.PeelShavingManager.Get()?.CountLiving() ?? 0; } catch { }
        return new { Out, In, SweepsOut, SweepsIn, living };
    }
}
