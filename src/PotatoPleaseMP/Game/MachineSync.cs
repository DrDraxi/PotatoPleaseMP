using HarmonyLib;
using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Machines (spec 13), first cut: the host simulates every device; clients never decide (no intake, eject, jam, gantry or
/// cableway moves of their own), mirror the visible state, and send intents.
/// - Peel machines: clients skip intake/swallow/pass-through/feed/eject/jam; their peel progress is frozen below the head
///   and follows the host. MachRun (0xA3, 5 Hz) carries per machine the hopper load count, progress, crank speed and the jam
///   flag; clients match their hopper loads to the host count (grains stay local). Ejected potatoes are host bodies.
/// - Crank: a client holding the crank sends HoldOn (DeviceRequest 0xA5) while it cranks; the host cranks for it.
/// - Gantry: clients skip its FixedUpdate and take the host's trolley/frame/chain state (GantryState 0xA7, 10 Hz).
/// - Cableway: clients skip scan/pick-up/drop/release/attach and follow the host's rope travel (CablewayState 0xA8).
///   Riding a hook stays local to the rider.
/// - Thrower, packager, bombs, claw seat: host only for now (the claw seat says so on a client).
/// Not yet: hopper add/eject events, jam clearing by clients, claw driving by clients, sack poses on the gantry.
/// </summary>
internal static class MachineSync
{
    [ThreadStatic] static bool _applying;
    static double _next, _nextGantry, _nextClaw, _nextHold, _nextJamReq;
    static PotatoPeeler.Interactor? _hostInteractor;
    static readonly Dictionary<int, double> CrankHolds = new(); // host: machine index -> last HoldOn time
    static readonly Dictionary<int, (int loads, float progress, float crank, bool jammed)> HostRun = new();
    public static long RunOut, RunIn, Holds;
    public static string Last = "";

    static bool ClientBlocked => MpRuntime.IsClient && !_applying;

    static List<PotatoPeeler.PotatoPeelMachine> Machines()
    {
        var list = new List<PotatoPeeler.PotatoPeelMachine>();
        var all = PotatoPeeler.PotatoPeelMachine.All;
        if (all == null) return list;
        for (int i = 0; i < all.Count; i++) if (all[i] != null) list.Add(all[i]);
        list.Sort((a, b) => string.CompareOrdinal(Path(a.transform), Path(b.transform)));
        return list;
    }

    static string Path(Transform t) { var s = t.name; for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s; return s; }

    static PotatoPeeler.PotatoPeelMachineCrank? CrankOf(PotatoPeeler.PotatoPeelMachine m)
    {
        foreach (var c in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.PotatoPeelMachineCrank>()) if (c.machine != null && c.machine.Pointer == m.Pointer) return c;
        return null;
    }

    // ---------------- host ----------------

    public static void UpdateHost(HostSession host)
    {
        var now = Time.realtimeSinceStartupAsDouble;
        try
        {
            if (now >= _next)
            {
                _next = now + 0.2;
                var ms = Machines();
                var w = new NetWriter();
                w.U8((byte)ms.Count);
                foreach (var m in ms)
                {
                    var crank = CrankOf(m);
                    w.U8((byte)Math.Min(255, m._loads?.Count ?? 0));
                    w.F32(m._progress);
                    w.F32(crank != null ? crank._speed : 0);
                    w.Bool(m._jammed != null);
                    w.Str8(m._jammed != null ? (m._jammed.id ?? "") : "");
                }
                host.Broadcast(MsgId.MachRun, w.Span, delivery: Delivery.Unreliable);
                RunOut++;
            }
            if (now >= _nextClaw)
            {
                _nextClaw = now + 0.05; // 20 Hz: clients smooth between updates, a fast arm still needs dense samples
                // claw cranes: the host drives them (player or autopilot); clients see the arm move
                foreach (var claw in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.ClawCrane>())
                {
                    var w = new NetWriter();
                    w.U8(3);
                    V(w, claw._target); V(w, claw._pos); V(w, claw._vel); V(w, claw._swing); w.F32(claw._grip); w.U8(ClawSync.DriverByte(claw)); w.U16((ushort)Mathf.Clamp(claw.Load, 0, 65535));
                    host.Broadcast(MsgId.DevicePose, w.Span, delivery: Delivery.Unreliable);
                    break;
                }
            }
            if (now >= _nextGantry)
            {
                _nextGantry = now + 0.1;
                foreach (var g in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.SackGantry>())
                {
                    var w = new NetWriter();
                    w.U8(0); w.U8((byte)(int)g._state);
                    w.F32(g._fx); w.F32(g._fv); w.F32(g._tz); w.F32(g._tv); w.F32(g._len); w.F32(g._lenV); w.Bool(g._carried);
                    host.Broadcast(MsgId.GantryState, w.Span, delivery: Delivery.Unreliable);
                    break;
                }
                foreach (var cw in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.SackCableway>())
                {
                    var w = new NetWriter();
                    w.F32(cw._travel); w.F32(cw._speedScale); w.Bool(cw._running); w.Bool(cw._halted);
                    host.Broadcast(MsgId.CablewayState, w.Span, delivery: Delivery.Unreliable);
                    break;
                }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[machines] poll: {e.Message}"); _next = _nextGantry = now + 5; }
    }

    /// <summary>Host: a remote player cranking keeps the crank driven, as if the host cranked.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPeelMachineCrank), "Update")]
    static class CrankUpdate
    {
        static void Prefix(PotatoPeeler.PotatoPeelMachineCrank __instance)
        {
            if (!MpRuntime.IsHost || CrankHolds.Count == 0 || __instance.machine == null) return;
            int idx = Machines().FindIndex(m => m.Pointer == __instance.machine.Pointer);
            if (idx >= 0 && CrankHolds.TryGetValue(idx, out var t) && Time.realtimeSinceStartupAsDouble - t < 0.35)
            {
                // Crank exactly as the game does for its own player (OnInteract fires every held frame).
                _hostInteractor ??= UnityEngine.Object.FindObjectOfType<PotatoPeeler.Interactor>();
                if (_hostInteractor != null) __instance.OnInteract(_hostInteractor);
                else { __instance._crankedFrame = Time.frameCount; __instance._lastCrankTime = Time.time; }
            }
        }
    }

    // ---------------- client ----------------

    [HarmonyPatch(typeof(PotatoPeeler.PotatoPeelMachineCrank), nameof(PotatoPeeler.PotatoPeelMachineCrank.OnInteract))]
    static class CrankIntent
    {
        static void Postfix(PotatoPeeler.PotatoPeelMachineCrank __instance)
        {
            if (!MpRuntime.IsClient || __instance.machine == null) return;
            var now = Time.realtimeSinceStartupAsDouble;
            int jidx = Machines().FindIndex(m => m.Pointer == __instance.machine.Pointer);
            if (jidx >= 0 && HostJammed(jidx) && now >= _nextJamReq && MpRuntime.Client is { State: ClientState.Playing } jc)
            {
                _nextJamReq = now + 1.0;
                var jw = new NetWriter(); jw.U8(1); jw.U8((byte)jidx); jw.U8(5 /*ClearJam*/);
                jc.Send(MsgId.DeviceRequest, jw.Span);
                Last = $"asked the host to clear machine {jidx}";
                return;
            }
            if (now < _nextHold || MpRuntime.Client is not { State: ClientState.Playing } c) return;
            _nextHold = now + 0.1;
            int idx = Machines().FindIndex(m => m.Pointer == __instance.machine.Pointer);
            if (idx < 0) return;
            var w = new NetWriter(); w.U8(1); w.U8((byte)idx); w.U8(2 /*HoldOn*/);
            c.Send(MsgId.DeviceRequest, w.Span);
            Holds++;
        }
    }

    static void ApplyRun(ref NetReader r)
    {
        int n = r.U8();
        var ms = Machines();
        for (int i = 0; i < n; i++)
        {
            int loads = r.U8(); float progress = r.F32(), crank = r.F32(); bool jammed = r.Bool(); string jamItem = r.Str8();
            HostRun[i] = (loads, progress, crank, jammed);
            if (i >= ms.Count) continue;
            var m = ms[i];
            MirrorJam(m, jammed, jamItem);
            _applying = true;
            try
            {
                var list = m._loads;
                if (list != null)
                {
                    int guard = 0;
                    while (list.Count < loads && guard++ < 40)
                        m.AddLoad(1f, guard % 3, 1f, UnityEngine.Random.rotation, new Vector3(0, 0.6f, 0), Vector3.down);
                    if (m._head != null && !Contains(list, m._head)) m._head = null;
                    if (m._pinnedHead != null && !Contains(list, m._pinnedHead)) m._pinnedHead = null;
                    while (list.Count > loads && list.Count > 0 && guard++ < 80)
                    {
                        var l = list[0];
                        try { m.RemoveGrain(l); } catch { }
                        list.RemoveAt(0);
                        if (m._head != null && m._head.Pointer == l.Pointer) m._head = null;
                        if (m._pinnedHead != null && m._pinnedHead.Pointer == l.Pointer) m._pinnedHead = null;
                    }
                }
                var crankC = CrankOf(m);
                if (crankC != null && Time.frameCount - crankC._crankedFrame > 2) crankC._speed = crank;
            }
            catch (Exception e) { Plugin.L.LogWarning($"[machines] apply: {e.Message}"); }
            finally { _applying = false; }
        }
        RunIn++;
    }

    /// <summary>Client: progress never reaches the head on its own (the inlined peel step would eject); it follows the host.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPeelMachine), "LateUpdate")]
    static class FreezeProgress
    {
        static void Prefix(PotatoPeeler.PotatoPeelMachine __instance)
        {
            if (!MpRuntime.IsClient) return;
            __instance._secondsPerPotato = 1e9f;
            int idx = Machines().FindIndex(m => m.Pointer == __instance.Pointer);
            float host = idx >= 0 && HostRun.TryGetValue(idx, out var h) ? h.progress : 0;
            __instance._progress = Mathf.Min(host, 0.99f);
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.PotatoPeelMachine), "TickIntake")] static class IntakeGate { static bool Prefix() => !ClientBlocked; }
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPeelMachine), "Swallow")] static class SwallowGate { static bool Prefix() => !ClientBlocked; }
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPeelMachine), "SwallowFromPile")] static class SwallowPileGate { static bool Prefix() => !ClientBlocked; }
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPeelMachine), "PassThrough")] static class PassGate { static bool Prefix() => !ClientBlocked; }
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPeelMachine), "Eject")] static class EjectGate { static bool Prefix() => !ClientBlocked; }

    [HarmonyPatch(typeof(PotatoPeeler.PotatoPeelMachine), nameof(PotatoPeeler.PotatoPeelMachine.Feed))]
    static class FeedGate { static bool Prefix(ref int __result) { if (!ClientBlocked) return true; __result = 0; return false; } }

    [HarmonyPatch(typeof(PotatoPeeler.PotatoPeelMachine), nameof(PotatoPeeler.PotatoPeelMachine.TryJam))]
    static class JamGate { static bool Prefix(ref bool __result) { if (!ClientBlocked) return true; __result = false; return false; } }

    /// <summary>
    /// Client: the host's machine jammed. Our copy shows the jam (marker, the "clear the jam" option) with a stand-in item;
    /// the real item stays on the host.
    /// </summary>
    static bool Contains(Il2CppSystem.Collections.Generic.List<PotatoPeeler.PotatoPeelMachine.Load> list, PotatoPeeler.PotatoPeelMachine.Load l)
    {
        for (int k = 0; k < list.Count; k++) if (list[k] != null && list[k].Pointer == l.Pointer) return true;
        return false;
    }

    static readonly HashSet<int> JamNoticed = new();

    /// <summary>
    /// Client: the host's machine jammed. Our copy's internal jam state is NOT mirrored (the game's jam/alert code reads
    /// hopper internals that only the host has, and crashes natively on a client copy). Instead the player gets a notice, and
    /// using the machine (its crank) while it is jammed asks the host to clear it.
    /// </summary>
    static void MirrorJam(PotatoPeeler.PotatoPeelMachine m, bool jammed, string itemId)
    {
        int idx = Machines().FindIndex(x => x.Pointer == m.Pointer);
        if (jammed && JamNoticed.Add(idx))
            PotatoPleaseMP.UI.Notice.Show("A peeling machine is jammed: use its crank to clear it.");
        else if (!jammed) JamNoticed.Remove(idx);
    }

    public static bool HostJammed(int idx) => HostRun.TryGetValue(idx, out var h) && h.jammed;

    /// <summary>Client: clearing a jam asks the host, which clears it for real.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPeelMachine), nameof(PotatoPeeler.PotatoPeelMachine.ClearJam))]
    static class ClearJamGate
    {
        static bool Prefix(PotatoPeeler.PotatoPeelMachine __instance)
        {
            if (!ClientBlocked) return true;
            int idx = Machines().FindIndex(x => x.Pointer == __instance.Pointer);
            if (idx >= 0 && MpRuntime.Client is { State: ClientState.Playing } c)
            {
                var w = new NetWriter(); w.U8(1); w.U8((byte)idx); w.U8(5 /*ClearJam*/);
                c.Send(MsgId.DeviceRequest, w.Span);
                Last = $"asked the host to clear machine {idx}";
            }
            return false;
        }
    }

    /// <summary>
    /// Host: a client cleared the jam. The game's own ClearJam starts a peel session for whoever clears it, which must not
    /// happen to the host's player, so the host clears it directly: the stuck potato leaves the hopper, its hidden item goes
    /// into the team's inventory, and the marker goes away.
    /// </summary>
    static void HostClearJam(int idx, PlayerInfo from)
    {
        var ms = Machines();
        if (idx < 0 || idx >= ms.Count) return;
        var m = ms[idx];
        if (m._jammed == null) return;
        var item = m._jammed;
        try
        {
            var load = m._jamLoad;
            if (load != null && m._loads != null)
            {
                try { m.RemoveGrain(load); } catch { }
                m._loads.Remove(load);
                if (m._head != null && m._head.Pointer == load.Pointer) m._head = null;
                if (m._pinnedHead != null && m._pinnedHead.Pointer == load.Pointer) m._pinnedHead = null;
            }
            m._jammed = null; m._jamLoad = null;
            try { m.EndJamMarker(); } catch { }
            if (item != null) PotatoPeeler.HiddenItemInventory.Add(item, 1);
            Last = $"slot {from.Slot} cleared the jam on machine {idx} ({item?.id})";
            Plugin.L.LogMessage($"[machines] {Last}");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[machines] clear jam: {e.Message}"); }
    }

    [HarmonyPatch(typeof(PotatoPeeler.SackGantry), "FixedUpdate")] static class GantryGate { static bool Prefix() => !MpRuntime.IsClient; }

    static void V(NetWriter w, Vector3 v) { w.F32(v.x); w.F32(v.y); w.F32(v.z); }
    static Vector3 RV(ref NetReader r) => new(r.F32(), r.F32(), r.F32());

    static void ApplyClaw(ref NetReader r)
    {
        r.U8();
        var target = RV(ref r); var pos = RV(ref r); var vel = RV(ref r); var swing = RV(ref r); float grip = r.F32();
        byte driver = r.Remaining > 0 ? r.U8() : (byte)0xFF;
        if (r.Remaining >= 2) ClawSync.HostLoad = r.U16();
        foreach (var claw in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.ClawCrane>())
        {
            ClawSync.OnDriver(claw, driver);
            if (!ClawSync.DrivingLocally) claw._target = target;
            ClawSync.OnHostPose(claw, pos, vel); // the arm itself is moved smoothly every frame in ClientTick
            claw._grip = grip; // the cable swing is simulated locally from the arm's motion (ClawSync.ClientFollow)
            break;
        }
    }

    static void ApplyGantry(ref NetReader r)
    {
        r.U8(); int state = r.U8();
        float fx = r.F32(), fv = r.F32(), tz = r.F32(), tv = r.F32(), len = r.F32(), lenV = r.F32(); bool carried = r.Bool();
        foreach (var g in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.SackGantry>())
        {
            g._state = (PotatoPeeler.SackGantry.State)state;
            g._fx = fx; g._fv = fv; g._tz = tz; g._tv = tv; g._len = len; g._lenV = lenV; g._carried = carried;
            break;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.SackCableway), "Scan")] static class CwScan { static bool Prefix() => !ClientBlocked; }
    [HarmonyPatch(typeof(PotatoPeeler.SackCableway), "TryPickUp")] static class CwPick { static bool Prefix() => !ClientBlocked; }
    [HarmonyPatch(typeof(PotatoPeeler.SackCableway), "TryDrop")] static class CwDrop { static bool Prefix() => !ClientBlocked; }
    [HarmonyPatch(typeof(PotatoPeeler.SackCableway), "Release")] static class CwRelease { static bool Prefix() => !ClientBlocked; }
    [HarmonyPatch(typeof(PotatoPeeler.SackCableway), "Attach")] static class CwAttach { static bool Prefix() => !ClientBlocked; }

    static float _hostTravel = float.NaN;

    static void ApplyCableway(ref NetReader r)
    {
        _hostTravel = r.F32(); float speed = r.F32(); bool running = r.Bool(), halted = r.Bool();
        foreach (var cw in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.SackCableway>())
        {
            cw._speedScale = speed; cw._running = running; cw._halted = halted;
            break;
        }
    }

    /// <summary>Client: the rope runs locally but is pulled towards the host's travel (wraps by the loop length).</summary>
    [HarmonyPatch(typeof(PotatoPeeler.SackCableway), "FixedUpdate")]
    static class CwBlend
    {
        static void Postfix(PotatoPeeler.SackCableway __instance)
        {
            if (!MpRuntime.IsClient || float.IsNaN(_hostTravel)) return;
            float len = __instance._length > 0 ? __instance._length : 1f;
            float d = Mathf.Repeat(_hostTravel - __instance._travel + len / 2, len) - len / 2;
            __instance._travel += d * 0.1f;
        }
    }

    // Host-only world housekeeping: on a client these would take host potatoes out of the world through its own copies.
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileRecovery), "Update")] static class RecoveryUpdateGate { static bool Prefix() => !MpRuntime.IsClient; }
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileRecovery), nameof(PotatoPeeler.PotatoPileRecovery.AutoRecover))] static class AutoRecoverGate { static bool Prefix(ref int __result) { if (!MpRuntime.IsClient) return true; __result = 0; return false; } }
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileRecovery), nameof(PotatoPeeler.PotatoPileRecovery.Recover))] static class RecoverGate { static bool Prefix(ref int __result) { if (!MpRuntime.IsClient) return true; __result = 0; return false; } }
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileRecovery), nameof(PotatoPeeler.PotatoPileRecovery.StackStrays))] static class StackGate { static bool Prefix(ref int __result) { if (!MpRuntime.IsClient) return true; __result = 0; return false; } }
    [HarmonyPatch(typeof(PotatoPeeler.PotatoContainment), "Update")] static class ContainmentGate { static bool Prefix() => !MpRuntime.IsClient; }

    /// <summary>Client: the "repile" key at the desk asks the host to recount.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileRecovery), nameof(PotatoPeeler.PotatoPileRecovery.TryPlayerRecover))]
    static class PlayerRecoverForward
    {
        static bool Prefix(ref bool __result, ref int count)
        {
            if (!MpRuntime.IsClient) return true;
            if (MpRuntime.Client is { State: ClientState.Playing } c) { var w = new NetWriter(); w.U8(9); w.U8(0); w.U8(1); c.Send(MsgId.DeviceRequest, w.Span); }
            count = 0; __result = true;
            return false;
        }
    }

    // The claw autopilot and its drop unit dig the pile and pack sacks: host only.
    [HarmonyPatch(typeof(PotatoPeeler.ClawCrane), "Update")]
    static class ClawUpdateGate
    {
        static bool Prefix(PotatoPeeler.ClawCrane __instance)
        {
            if (MpRuntime.IsHost) { ClawSync.HostBeforeUpdate(__instance); return true; }
            if (!MpRuntime.IsClient) return true;
            ClawSync.ClientTick(__instance); // a client only reads its controls; the host simulates the arm
            return false;
        }
        static void Postfix(PotatoPeeler.ClawCrane __instance) { if (MpRuntime.IsHost) ClawSync.HostAfterUpdate(__instance); }
    }
    [HarmonyPatch(typeof(PotatoPeeler.ClawDropUnit), "Update")] static class DropUnitGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.PotatoThrower), "Update")] static class ThrowerGate { static bool Prefix() => !MpRuntime.IsClient; }
    [HarmonyPatch(typeof(PotatoPeeler.PileBombField), "Update")] static class BombFieldGate { static bool Prefix() => !MpRuntime.IsClient; }
    /// <summary>Client: finishing a package asks the host to package the potatoes in front of us (team sack limits, host sack).</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPackager), nameof(PotatoPeeler.PotatoPackager.Complete))]
    static class PackGate
    {
        static bool Prefix(PotatoPeeler.PotatoPackager __instance)
        {
            if (!MpRuntime.IsClient) return true;
            try
            {
                var center = __instance.GatherCenter(out var fwd);
                if (MpRuntime.Client is { State: ClientState.Playing } c)
                {
                    var w = new NetWriter(); w.U8(8); w.U8(0); w.U8(0);
                    w.F32(center.x); w.F32(center.y); w.F32(center.z); w.F32(fwd.x); w.F32(fwd.y); w.F32(fwd.z);
                    c.Send(MsgId.DeviceRequest, w.Span);
                    Last = "asked the host to package";
                }
                __instance._IsPackaging_k__BackingField = false;
            }
            catch (Exception e) { Plugin.L.LogWarning($"[machines] package request: {e.Message}"); }
            return false;
        }
    }

    [ThreadStatic] static bool _remotePack;
    static Vector3 _packCenter, _packFwd;

    /// <summary>Host: while packaging for a client, the gather spot is the client's.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPackager), nameof(PotatoPeeler.PotatoPackager.GatherCenter))]
    static class GatherOverride
    {
        static bool Prefix(ref Vector3 forward, ref Vector3 __result)
        {
            if (!_remotePack) return true;
            forward = _packFwd; __result = _packCenter;
            return false;
        }
    }

    static void OnPackageReq(PlayerInfo from, ref NetReader r)
    {
        var center = new Vector3(r.F32(), r.F32(), r.F32()); var fwd = new Vector3(r.F32(), r.F32(), r.F32());
        var pk = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PotatoPackager>();
        if (pk == null) return;
        _remotePack = true; _packCenter = center; _packFwd = fwd;
        try { pk.Complete(); }
        catch (Exception e) { Plugin.L.LogWarning($"[machines] package: {e.Message}"); }
        finally { _remotePack = false; }
        Last = $"slot {from.Slot} packaged a sack";
        Plugin.L.LogMessage($"[machines] {Last}");
    }

    static void OnRequest(PlayerInfo from, ref NetReader r)
    {
        byte kind = r.U8(), id = r.U8(), op = r.U8();
        if (kind == 1 && op == 2) { CrankHolds[id] = Time.realtimeSinceStartupAsDouble; Holds++; Last = $"slot {from.Slot} cranks machine {id}"; }
        else if (kind == 1 && op == 5) HostClearJam(id, from);
        else if (kind == 8) OnPackageReq(from, ref r);
        else if (kind == 10) ClawSync.OnRequest(from, op);
        else if (kind == 11) PileRefillSync.OnRequest(from);
        else if (kind == 9) { try { PotatoPeeler.PotatoPileRecovery.Instance?.RecoverAndAnnounce(); } catch { } Last = $"slot {from.Slot} asked for a repile"; }
    }

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if ((byte)id < 0xA0 || (byte)id > 0xAF) return false;
        try
        {
            var r = new NetReader(payload);
            if (from != null)
            {
                if (id == MsgId.DeviceRequest && MpRuntime.IsHost) OnRequest(from, ref r);
                else if (id == MsgId.DevicePose && MpRuntime.IsHost) ClawSync.OnControls(from, ref r);
            }
            else if (id == MsgId.MachRun) ApplyRun(ref r);
            else if (id == MsgId.HopperEject) { ushort nid = r.U16(); BodySync.ApplyFactory(nid, r.F32()); }
            else if (id == MsgId.GantryState) ApplyGantry(ref r);
            else if (id == MsgId.CablewayState) ApplyCableway(ref r);
            else if (id == MsgId.DevicePose) ApplyClaw(ref r);
        }
        catch (WireFormatException) { }
        catch (Exception e) { Plugin.L.LogWarning($"[machines] {id}: {e.Message}"); }
        return true;
    }

    public static object Describe() => new
    {
        RunOut, RunIn, Holds, last = Last,
        machines = Machines().Select((m, i) => new { name = m.gameObject.name, loads = m._loads?.Count, progress = m._progress, jammed = m._jammed != null || HostJammed(i), running = SafeRunning(m) }).ToArray(),
    };

    static bool? SafeRunning(PotatoPeeler.PotatoPeelMachine m) { try { return m.IsRunning; } catch { return null; } }
}
