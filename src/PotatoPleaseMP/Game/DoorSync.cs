using HarmonyLib;
using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Doors (gamemap 08/13): every saved door has a unique 32-hex saveKey, and doors are curve-animated, so a door syncs as one
/// open/closed event and each peer plays the swing itself. Host-authoritative: a client's door use goes to the host as
/// InteractReq; the host applies it and broadcasts ObjState to everyone. Patch targets are unique bodies (spec 03).
/// 1.0.4 power switches (Main Switch, Spoon Catapult switch, ...) are ISavedDoors too and ride the same messages: machines
/// ask PowerGrid.IsPowered on the host, so the host's switch is the one that counts. PowerSwitch.Set is inlined everywhere
/// (Toggle, upgrades, storm TripMains), so the host polls the switches and broadcasts changes; a client's Toggle goes to the host.
/// </summary>
internal static class DoorSync
{
    const byte KindHinged = 1, KindSliding = 2, KindCarpet = 3, KindSwitch = 4;
    [ThreadStatic] static bool _applying;
    public static long RefusedLocked;

    static void Report(byte kind, string key, bool open)
    {
        if (_applying || !MpRuntime.Active || string.IsNullOrEmpty(key)) return;
        var w = new NetWriter();
        w.U8(kind); w.Str8(key); w.Bool(open);
        if (MpRuntime.Host is { } host) host.Broadcast(MsgId.ObjState, w.Span);
        else if (MpRuntime.Client is { State: ClientState.Playing } c) c.Send(MsgId.InteractReq, w.Span);
    }

    /// <summary>The folding carpet (it hides a hatch): when it settles, everyone's carpet takes the same state.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.CarpetFold), nameof(PotatoPeeler.CarpetFold.Settle))]
    static class CarpetPatch
    {
        static void Postfix(PotatoPeeler.CarpetFold __instance) { try { Report(KindCarpet, __instance.saveKey, __instance.IsOpen); } catch { } }
    }

    [HarmonyPatch(typeof(PotatoPeeler.HingedDoor), nameof(PotatoPeeler.HingedDoor.SetOpen))]
    static class HingedPatch
    {
        static void Postfix(PotatoPeeler.HingedDoor __instance, bool open) => Report(KindHinged, __instance.saveKey, open);
    }

    [HarmonyPatch(typeof(PotatoPeeler.SlidingDoorInteractable), nameof(PotatoPeeler.SlidingDoorInteractable.SetOpen))]
    static class SlidingPatch
    {
        static void Postfix(PotatoPeeler.SlidingDoorInteractable __instance, bool open) => Report(KindSliding, __instance.saveKey, open);
    }

    /// <summary>Client: flipping a power switch asks the host (the local flip stays as a prediction until the host answers).</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PowerSwitch), nameof(PotatoPeeler.PowerSwitch.Toggle))]
    static class SwitchPatch
    {
        static void Postfix(PotatoPeeler.PowerSwitch __instance)
        {
            if (!MpRuntime.IsClient) return; // the host's poll reports its own flips
            try { Report(KindSwitch, __instance.saveKey, __instance.IsOn); SwitchFlips++; } catch { }
        }
    }

    static readonly Dictionary<string, bool> _switchSent = new();
    static float _switchPoll, _switchFull;
    public static long SwitchFlips, SwitchSends;

    /// <summary>Host: report switch changes (any cause) and, every few seconds, every switch (repairs a client's local drift).</summary>
    public static void UpdateHost(Core.Session.HostSession h)
    {
        float now = UnityEngine.Time.unscaledTime;
        if (now < _switchPoll) return;
        _switchPoll = now + 0.2f;
        bool full = now >= _switchFull;
        if (full) _switchFull = now + 5f;
        try
        {
            foreach (var s in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.PowerSwitch>())
            {
                var key = s.saveKey;
                if (string.IsNullOrEmpty(key)) continue;
                bool on = s.IsOn;
                if (!full && _switchSent.TryGetValue(key, out var last) && last == on) continue;
                _switchSent[key] = on;
                var w = new NetWriter(); w.U8(KindSwitch); w.Str8(key); w.Bool(on);
                h.Broadcast(MsgId.ObjState, w.Span);
                SwitchSends++;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[doors] switches: {e.Message}"); _switchPoll = now + 5f; }
    }

    public static void ResetSwitches() { _switchSent.Clear(); _switchPoll = _switchFull = 0; }

    public static object DescribeSwitches()
    {
        var list = new List<object>();
        try
        {
            foreach (var s in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.PowerSwitch>(true))
                list.Add(new { key = s.saveKey, label = SafeLabel(s), main = s.isMain, on = s.IsOn, installed = s.IsInstalled, active = s.isActiveAndEnabled });
        }
        catch (Exception e) { list.Add(new { error = e.Message }); }
        return new { mainsOn = SafeMains(), SwitchFlips, SwitchSends, switches = list };
    }

    static string SafeLabel(PotatoPeeler.PowerSwitch s) { try { return s.Label; } catch { return s.gameObject.name; } }
    static bool? SafeMains() { try { return PotatoPeeler.PowerGrid.MainsOn; } catch { return null; } }

    /// <summary>Host: a client opened/closed a door. Client: the host's verdict. Returns true when the message was ours.</summary>
    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if (id != MsgId.ObjState && id != MsgId.InteractReq) return false;
        byte kind; string key; bool open;
        try { var r = new NetReader(payload); kind = r.U8(); key = r.Str8(); open = r.Bool(); }
        catch (WireFormatException) { return true; }
        if (!Apply(kind, key, open)) Plugin.L.LogWarning($"[doors] no door with key {key}");
        // The host re-broadcasts a client's change so every other client follows.
        if (id == MsgId.InteractReq && MpRuntime.Host is { } host)
        {
            bool real = CurrentOpen(kind, key) ?? open;
            var w = new NetWriter(); w.U8(kind); w.Str8(key); w.Bool(real);
            host.Broadcast(MsgId.ObjState, w.Span);
        }
        return true;
    }

    /// <summary>Host: a joiner gets the live open/closed state of every saved door.</summary>
    public static void SendAllTo(byte slot)
    {
        if (MpRuntime.Host is not { } h) return;
        try
        {
            foreach (var d in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.HingedDoor>())
                if (!string.IsNullOrEmpty(d.saveKey)) { var w = new NetWriter(); w.U8(KindHinged); w.Str8(d.saveKey); w.Bool(d.IsOpen); h.SendTo(slot, MsgId.ObjState, w.Span); }
            foreach (var d in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.SlidingDoorInteractable>())
                if (!string.IsNullOrEmpty(d.saveKey)) { var w = new NetWriter(); w.U8(KindSliding); w.Str8(d.saveKey); w.Bool(d.IsOpen); h.SendTo(slot, MsgId.ObjState, w.Span); }
            foreach (var d in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.CarpetFold>())
                if (!string.IsNullOrEmpty(d.saveKey)) { var w = new NetWriter(); w.U8(KindCarpet); w.Str8(d.saveKey); w.Bool(d.IsOpen); h.SendTo(slot, MsgId.ObjState, w.Span); }
            foreach (var d in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.PowerSwitch>())
                if (!string.IsNullOrEmpty(d.saveKey)) { var w = new NetWriter(); w.U8(KindSwitch); w.Str8(d.saveKey); w.Bool(d.IsOn); h.SendTo(slot, MsgId.ObjState, w.Span); }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[doors] join state: {e.Message}"); }
    }

    static bool? CurrentOpen(byte kind, string key)
    {
        if (kind == KindHinged) { foreach (var d in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.HingedDoor>()) if (d.saveKey == key) return d.IsOpen; }
        else if (kind == KindSliding) { foreach (var d in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.SlidingDoorInteractable>()) if (d.saveKey == key) return d.IsOpen; }
        else if (kind == KindCarpet) { foreach (var d in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.CarpetFold>()) if (d.saveKey == key) return d.IsOpen; }
        else if (kind == KindSwitch) { foreach (var d in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.PowerSwitch>()) if (d.saveKey == key) return d.IsOn; }
        return null;
    }

    static bool Apply(byte kind, string key, bool open)
    {
        _applying = true;
        try
        {
            if (kind == KindHinged)
            {
                foreach (var d in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.HingedDoor>())
                    if (d.saveKey == key) { if (d.IsOpen != open) d.SetOpen(open, false); return true; }
            }
            else if (kind == KindSliding)
            {
                foreach (var d in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.SlidingDoorInteractable>())
                    if (d.saveKey == key)
                    {
                        // A client's copy may not know the door is locked (storm, quest gates): the host's lock wins.
                        if (MpRuntime.IsHost && open && d.locked) { RefusedLocked++; return true; }
                        if (d.IsOpen != open) d.SetOpen(open);
                        return true;
                    }
            }
            else if (kind == KindCarpet)
            {
                foreach (var d in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.CarpetFold>())
                    if (d.saveKey == key) { if (d.IsOpen != open && !d._held) d.RestoreOpen(open); return true; }
            }
            else if (kind == KindSwitch)
            {
                foreach (var d in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.PowerSwitch>())
                    if (d.saveKey == key) { if (d.IsOn != open) d.Set(open, true); return true; }
            }
            return false;
        }
        finally { _applying = false; }
    }
}
