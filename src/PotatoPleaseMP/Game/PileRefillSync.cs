using HarmonyLib;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// 1.0.4's State telephone: after the ending, a nearly empty pile can be refilled ("order a new pile").
/// - Only the host builds piles. A client using the phone asks the host (DeviceRequest kind 11); the host's phone places the order.
/// - The host builds the new heap from a seed it picks and sends (seed, count, layout hash) as PileRefill (DeviceRequest-range
///   MsgId 0xAE, host -> clients). Clients build the same heap from the same seed, so the pile indices that pile sync sends
///   keep pointing at the same potatoes. If a client's heap still comes out different, it says so (rejoin fixes it).
/// </summary>
internal static class PileRefillSync
{
    [ThreadStatic] static bool _applying;
    static int _seed;
    public static long Sent, Applied, Mismatch;
    public static string Last = "";

    static PotatoPeeler.PotatoPileRenderer? Renderer => UnityEngine.Object.FindObjectOfType<PotatoPeeler.PotatoPileRenderer>();

    /// <summary>FNV hash of the baked heap layout (positions only, a sample of entries).</summary>
    public static uint LayoutHash()
    {
        try
        {
            var d = Renderer?.data;
            if (d == null) return 0;
            uint h = 2166136261;
            int n = d.Count, step = Math.Max(1, n / 512);
            h = (h ^ (uint)n) * 16777619;
            for (int i = 0; i < n; i += step)
            {
                var p = d.GetPosition(i);
                h = (h ^ (uint)Mathf.RoundToInt(p.x * 100)) * 16777619;
                h = (h ^ (uint)Mathf.RoundToInt(p.y * 100)) * 16777619;
                h = (h ^ (uint)Mathf.RoundToInt(p.z * 100)) * 16777619;
            }
            return h;
        }
        catch { return 0; }
    }

    /// <summary>Which baked entries are gone (FNV over the removal bitset).</summary>
    public static uint RemovedHash()
    {
        try
        {
            var bits = Renderer?._removedBaked;
            if (bits == null) return 0;
            uint h = 2166136261;
            for (int i = 0; i < bits.Length; i++) { ulong v = bits[i]; h = (h ^ (uint)v) * 16777619; h = (h ^ (uint)(v >> 32)) * 16777619; }
            return h;
        }
        catch { return 0; }
    }

    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileActivator), nameof(PotatoPeeler.PotatoPileActivator.RefillPile))]
    static class RefillPatch
    {
        static bool Prefix(ref int __result)
        {
            if (_applying || !MpRuntime.Active) return true;
            if (MpRuntime.IsClient) { __result = 0; return false; } // the host's pile is the pile
            _seed = Environment.TickCount ^ 0x5A17;
            UnityEngine.Random.InitState(_seed);
            return true;
        }

        static void Postfix(int count, int __result)
        {
            if (_applying || !MpRuntime.IsHost || MpRuntime.Host is not { } h) return;
            PileSync.Reset(); // a new heap: removal tracking starts over
            var w = new NetWriter(); w.I32(_seed); w.I32(count); w.U32(LayoutHash()); w.U32(RemovedHash());
            h.Broadcast(MsgId.PileRefill, w.Span);
            Sent++;
            Last = $"refilled {__result} (asked {count}), seed {_seed}";
            Plugin.L.LogMessage($"[pile] {Last}");
        }
    }

    /// <summary>Client: the phone places the host's order.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PileRefillPhone), nameof(PotatoPeeler.PileRefillPhone.OnInteract))]
    static class PhonePatch
    {
        static bool Prefix()
        {
            if (!MpRuntime.IsClient || MpRuntime.Client is not { State: ClientState.Playing } c) return true;
            var w = new NetWriter(); w.U8(11); w.U8(0); w.U8(1); c.Send(MsgId.DeviceRequest, w.Span);
            UI.Notice.Show("Calling the State for a new pile...");
            return false;
        }
    }

    /// <summary>Host: a client used the phone.</summary>
    public static void OnRequest(PlayerInfo from)
    {
        var phone = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PileRefillPhone>();
        var inter = UnityEngine.Object.FindObjectOfType<PotatoPeeler.Interactor>();
        if (phone == null) return;
        try { if (phone.CanInteract) phone.OnInteract(inter); } catch (Exception e) { Plugin.L.LogWarning($"[pile] phone: {e.Message}"); }
        Last = $"slot {from.Slot} ordered a new pile";
    }

    public static void Apply(ref NetReader r)
    {
        int seed = r.I32(), count = r.I32(); uint hash = r.U32(); uint rhash = r.Remaining >= 4 ? r.U32() : 0;
        var act = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PotatoPileActivator>();
        if (act == null) return;
        _applying = true;
        int got;
        try { UnityEngine.Random.InitState(seed); got = act.RefillPile(count); }
        finally { _applying = false; }
        PileSync.Reset(); PileSync.ResetPending();
        Applied++;
        uint mine = LayoutHash(), rmine = RemovedHash();
        if ((hash != 0 && mine != hash) || (rhash != 0 && rmine != rhash))
        {
            Mismatch++;
            Plugin.L.LogWarning($"[pile] new pile differs from the host's (layout {mine:x8}/{hash:x8}, removed {rmine:x8}/{rhash:x8})");
        }
        Last = $"refill applied: {got}, {(mine == hash && rmine == rhash ? "same as the host" : "DIFFERENT")}";
        Plugin.L.LogMessage($"[pile] {Last}");
    }

    public static object Describe() => new { Sent, Applied, Mismatch, Last, hash = LayoutHash().ToString("x8"), removedHash = RemovedHash().ToString("x8"), removed = Renderer?._removedBakedCount, count = Renderer?.data?.Count };
}
