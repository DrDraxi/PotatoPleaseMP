using HarmonyLib;
using PotatoPleaseMP.Core.Bodies;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Extra workstations (spec 14), first cut. In a session every peer clones the game's one Workstation to the same fixed
/// poses, so there is a table per player. The host owns every table: its potatoes are host bodies and only the host's
/// tables capture them. A client that steps up to any table asks the host (StationTakeReq); the host takes the next raw
/// potato off that table, hands it to the client (peel authority) and answers (StationTakeGrant); the client then starts its
/// own peel session with that potato. When the client finishes (PlaceClean) or walks away (PlaceRaw) the potato goes back
/// to the host, which places it on the table itself. Carving travels through <see cref="PeelSync"/>.
/// Not yet: reservations for instant chaining, occupancy notices, StationSet/Verify, sidecar poses.
/// </summary>
internal static class StationSync
{
    /// <summary>Spec 14 §2.3: free floor in the peel room. Yaw 0 = like the original (player faces -z).</summary>
    static readonly (Vector3 pos, float yaw)[] Poses =
    {
        (new Vector3(4.00f, 0f, -6.80f), 0f),
        (new Vector3(-1.20f, 0f, -4.70f), 90f),
        (new Vector3(3.50f, 0f, -3.70f), 0f),
    };

    public static int Extra = 1;
    static readonly Dictionary<int, byte> Occupant = new(); // host: table index -> client slot peeling there
    static readonly List<PotatoPeeler.Workstation> Stations = new();
    static bool _cloned;
    [ThreadStatic] static bool _beginning;
    static PotatoPeeler.PotatoInstance? _granted;
    static ushort _reqId;
    static float _requestUntil;
    static int _chainTable = -1;
    static float _chainAt;

    /// <summary>Client: keep peeling at the same table after a clean potato, like the game's own chaining.</summary>
    static void TickChain()
    {
        if (_chainTable < 0 || Time.realtimeSinceStartup < _chainAt) return;
        int idx = _chainTable; _chainTable = -1;
        if (PeelSession is { IsActive: true } || MpRuntime.Client is not { State: ClientState.Playing } c) return;
        if (idx >= Stations.Count || Stations[idx] == null) return;
        // only if we are still standing at that table
        var player = UnityEngine.Object.FindObjectOfType<PotatoPeeler.Player>();
        if (player == null || (player.transform.position - Stations[idx].transform.position).sqrMagnitude > 3.5f * 3.5f) return;
        var w = new NetWriter(); w.U16(++_reqId); w.U8((byte)idx);
        c.Send(MsgId.StationTakeReq, w.Span);
        _requestUntil = Time.realtimeSinceStartup + 1.5f;
        Requests++;
    }

    static void Notice(string text)
    {
        PotatoPleaseMP.UI.Notice.Show(text);
    }

    /// <summary>Client: a grant we cannot use goes straight back to the host (else the potato stays locked to us).</summary>
    static void GiveBack(int idx, ushort netId)
    {
        if (MpRuntime.Client is not { State: ClientState.Playing } c) return;
        var w = new NetWriter(); w.U8((byte)idx); w.U16(netId); c.Send(MsgId.StationRelease, w.Span);
    }
    static ushort _peelId;
    public static long Requests, Grants, Refusals, Placed;
    public static string Last = "";

    public static int IndexOf(PotatoPeeler.Workstation w)
    {
        for (int i = 0; i < Stations.Count; i++) if (Stations[i] != null && Stations[i].Pointer == w.Pointer) return i;
        return -1;
    }

    // ---------------- cloning ----------------

    public static void Update()
    {
        if (!MpRuntime.Active) { if (_cloned) Reset(); return; }
        if (MpRuntime.IsClient) TickChain();
        if (_cloned || SceneManager.GetActiveScene().name != "Gameplay") return;
        if (MpRuntime.IsClient && MpRuntime.Client is not { State: ClientState.Playing }) return;
        try { Clone(); }
        catch (Exception e) { Plugin.L.LogError($"[stations] clone failed: {e}"); }
        _cloned = true;
    }

    static void Reset()
    {
        foreach (var w in Stations.Skip(1)) if (w != null) UnityEngine.Object.Destroy(w.gameObject);
        Stations.Clear();
        _cloned = false; _granted = null;
    }

    static void Clone()
    {
        var all = UnityEngine.Object.FindObjectsOfType<PotatoPeeler.Workstation>();
        var original = all.FirstOrDefault(w => !w.gameObject.name.StartsWith("Workstation_MP"));
        if (original == null) { Plugin.L.LogWarning("[stations] no workstation in the scene"); return; }
        Stations.Clear();
        Stations.Add(original);
        var parent = original.transform.parent;
        var holder = new GameObject("PPMP_StationTemplates");
        holder.SetActive(false);
        holder.transform.SetParent(parent, false);
        var tpl = UnityEngine.Object.Instantiate(original.gameObject, holder.transform, false);
        int stripped = 0;
        foreach (var p in tpl.GetComponentsInChildren<PotatoPeeler.PotatoInstance>(true)) { UnityEngine.Object.DestroyImmediate(p.gameObject); stripped++; }
        foreach (var t in tpl.GetComponentsInChildren<PotatoPeeler.PotatoThrower>(true)) UnityEngine.Object.DestroyImmediate(t.gameObject);
        for (int k = 0; k < Math.Min(Extra, Poses.Length); k++)
        {
            var go = UnityEngine.Object.Instantiate(tpl, parent, false);
            go.name = "Workstation_MP" + (k + 1);
            go.transform.SetPositionAndRotation(Poses[k].pos, Quaternion.Euler(0, Poses[k].yaw, 0));
            go.SetActive(true);
            Stations.Add(go.GetComponent<PotatoPeeler.Workstation>());
        }
        UnityEngine.Object.Destroy(holder);
        Plugin.L.LogMessage($"[stations] {Stations.Count - 1} extra workstation(s) placed ({stripped} parked potatoes stripped from the template).");
    }

    // ---------------- client side ----------------

    /// <summary>Client: stepping up to a table asks the host for a potato instead of taking one locally.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.Workstation), nameof(PotatoPeeler.Workstation.OnInteract))]
    static class InteractPatch
    {
        static bool Prefix(PotatoPeeler.Workstation __instance)
        {
            if (!MpRuntime.IsClient || _beginning) return true;
            if (Time.realtimeSinceStartup < _requestUntil) return false; // one request at a time
            if (PeelSession is { IsStartLocked: true }) return true; // let the game show its own "locked" notice
            try
            {
                _requestUntil = Time.realtimeSinceStartup + 1.5f;
                int idx = IndexOf(__instance);
                if (idx < 0 || MpRuntime.Client is not { State: ClientState.Playing } c) return false;
                var w = new NetWriter();
                w.U16(++_reqId); w.U8((byte)idx);
                c.Send(MsgId.StationTakeReq, w.Span);
                Requests++;
                Last = $"asked for a potato at table {idx}";
            }
            catch (Exception e) { Plugin.L.LogWarning($"[stations] interact: {e.Message}"); }
            return false;
        }
    }

    /// <summary>Client: the host's tables hold the potatoes. Ours never capture (or move) the proxies.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.Workstation), nameof(PotatoPeeler.Workstation.Update))]
    static class UpdatePatch
    {
        static bool Prefix() => !MpRuntime.IsClient;
    }

    /// <summary>Client: the only raw potato a table hands out is the one the host granted.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.Workstation), nameof(PotatoPeeler.Workstation.TakeNextRaw))]
    static class TakePatch
    {
        static bool Prefix(ref PotatoPeeler.PotatoInstance? __result)
        {
            if (!MpRuntime.IsClient) return true;
            __result = _granted;
            _granted = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.Workstation), nameof(PotatoPeeler.Workstation.PlaceClean))]
    static class PlaceCleanPatch
    {
        static bool Prefix(PotatoPeeler.Workstation __instance, PotatoPeeler.PotatoInstance potato) => !MpRuntime.IsClient || !HandBack(__instance, potato, MsgId.StationPlaceClean);
    }

    [HarmonyPatch(typeof(PotatoPeeler.Workstation), nameof(PotatoPeeler.Workstation.PlaceRaw))]
    static class PlaceRawPatch
    {
        static bool Prefix(PotatoPeeler.Workstation __instance, PotatoPeeler.PotatoInstance potato) => !MpRuntime.IsClient || !HandBack(__instance, potato, MsgId.StationRelease);
    }

    /// <summary>Client: give a peeled (or abandoned) potato back to the host, which places it. True = handled.</summary>
    static bool HandBack(PotatoPeeler.Workstation station, PotatoPeeler.PotatoInstance potato, MsgId msg)
    {
        try
        {
            if (potato == null || MpRuntime.Client is not { State: ClientState.Playing } c) return false;
            var id = BodySync.IdOf(potato);
            if (!id.IsValid) return false;
            var w = new NetWriter();
            w.U8((byte)Math.Max(0, IndexOf(station))); w.U16(id.Value);
            if (msg == MsgId.StationPlaceClean) WriteExtras(w, potato);
            c.Send(msg, w.Span);
            BodySync.UnlockPeel(id);
            Placed++;
            if (msg == MsgId.StationPlaceClean) { _chainTable = IndexOf(station); _chainAt = Time.realtimeSinceStartup + 0.6f; }
            Last = $"{(msg == MsgId.StationPlaceClean ? "handed in" : "put back")} potato {id.Value}";
            return true;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[stations] hand back: {e.Message}"); return false; }
    }

    static void OnGrant(ref NetReader r)
    {
        r.U16();
        int idx = r.U8();
        byte result = r.U8();
        var id = new NetId(r.U16());
        _requestUntil = 0;
        if (result != 0)
        {
            Refusals++;
            Last = result == 1 ? "table is empty" : "table is busy";
            Notice(result == 1 ? "No raw potatoes on this table." : "Someone is already peeling at this table.");
            Plugin.L.LogMessage($"[stations] host: {Last} (table {idx}).");
            return;
        }
        var inst = BodySync.Get(id);
        var session = PeelSession;
        if (inst == null || idx >= Stations.Count || session == null || session.IsActive)
        {
            Last = $"grant for potato {id.Value} not usable here; given back";
            GiveBack(idx, id.Value);
            return;
        }
        BodySync.LockForPeel(id);
        if (inst.Body != null) inst.Body.isKinematic = false;
        _granted = inst; _peelInst = inst; _peelIdx = idx;
        _beginning = true;
        bool ok;
        try { ok = session.BeginAtStation(Stations[idx]); }
        finally { _beginning = false; }
        Grants++;
        Last = $"peeling potato {id.Value} at table {idx} (begin {ok})";
        if (!ok) { _granted = null; BodySync.UnlockPeel(id); GiveBack(idx, id.Value); }
    }

    static PotatoPeeler.PeelSession? PeelSession => PotatoPeeler.PeelSession.Instance ?? UnityEngine.Object.FindObjectOfType<PotatoPeeler.PeelSession>();

    // ---------------- host side ----------------

    static void OnTakeReq(PlayerInfo from, ref NetReader r)
    {
        ushort req = r.U16();
        int idx = r.U8();
        byte result = 1; ushort netId = 0;
        if (idx < Stations.Count && Stations[idx] is { } st)
        {
            var mine = PeelSession;
            if (mine != null && mine.IsActive && mine.Station != null && mine.Station.Pointer == st.Pointer) result = 2;
            else if (Occupant.TryGetValue(idx, out var occ) && occ != from.Slot && MpRuntime.Host?.Players.BySlot(occ) != null && BodySync.HoldsPeel(occ)) result = 2;
            else
            {
                float threshold = mine != null ? mine.CompleteThreshold : 0.95f;
                var p = st.TakeNextRaw(threshold, st.transform.position);
                var id = p != null ? BodySync.IdOf(p) : NetId.None;
                if (id.IsValid)
                {
                    BodySync.HostGrant(id, from.Slot);
                    result = 0; netId = id.Value;
                    Occupant[idx] = from.Slot;
                }
                else if (p != null) st.PlaceRaw(p);
            }
        }
        var w = new NetWriter();
        w.U16(req); w.U8((byte)idx); w.U8(result); w.U16(netId);
        MpRuntime.Host?.SendTo(from.Slot, MsgId.StationTakeGrant, w.Span);
        if (result == 0) Grants++; else Refusals++;
        Last = $"slot {from.Slot} table {idx}: {(result == 0 ? $"granted potato {netId}" : result == 1 ? "empty" : "busy")}";
        Plugin.L.LogMessage($"[stations] {Last}");
    }

    /// <summary>Client: what our peel added to the potato beyond its shape (combo bonus money, blood splats).</summary>
    static void WriteExtras(NetWriter w, PotatoPeeler.PotatoInstance potato)
    {
        long bonus = 0; try { bonus = potato.ComboBonus; } catch { }
        w.I64(bonus);
        var splats = potato._bloodSplats;
        int n = splats == null ? 0 : Math.Min(splats.Count, 24);
        int total = 0; try { total = potato.BloodSplatCount; } catch { }
        w.U8((byte)n); w.U16((ushort)Math.Clamp(total, 0, 65535));
        for (int i = 0; i < n; i++) { var sp = splats![i]; w.F32(sp.x); w.F32(sp.y); w.F32(sp.z); w.F32(sp.w); }
    }

    static void ApplyExtras(ref NetReader r, PotatoPeeler.PotatoInstance? p)
    {
        if (r.Remaining < 8) return;
        long bonus = r.I64();
        int n = r.U8(); int total = r.U16();
        var list = new Il2CppSystem.Collections.Generic.List<Vector4>();
        for (int i = 0; i < n; i++) list.Add(new Vector4(r.F32(), r.F32(), r.F32(), r.F32()));
        if (p == null) return;
        try { long have = p.ComboBonus; if (bonus > have) p.AddComboBonus(bonus - have); ExtrasApplied++; LastBonus = bonus; } catch (Exception e) { Plugin.L.LogWarning($"[stations] combo: {e.Message}"); }
        try { if (n > 0) p.ImportBloodSplats(list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<Vector4>>(), total); } catch (Exception e) { Plugin.L.LogWarning($"[stations] blood: {e.Message}"); }
    }

    public static long ExtrasApplied, LastBonus;
    static PotatoPeeler.PotatoInstance? _peelInst;
    static int _peelIdx = -1;

    /// <summary>Dev (client): hand in the potato we are peeling as clean, with a combo bonus on it, then end the session.</summary>
    public static object DevHandIn(long bonus)
    {
        if (_peelInst == null || _peelIdx < 0 || _peelIdx >= Stations.Count) return new { error = "not peeling" };
        try { _peelInst.AddComboBonus(bonus); } catch (Exception e) { return new { error = e.Message }; }
        bool sent = HandBack(Stations[_peelIdx], _peelInst, MsgId.StationPlaceClean);
        _peelInst = null;
        try { PeelSession?.ForceEnd(); } catch { }
        return new { sent };
    }

    static void OnHandBack(PlayerInfo from, MsgId msg, ref NetReader r)
    {
        int idx = r.U8();
        var id = new NetId(r.U16());
        var p = BodySync.Get(id);
        if (msg == MsgId.StationPlaceClean) ApplyExtras(ref r, p);
        BodySync.HostTakeBack(id);
        BodySync.HostForgetRemote(id); // we place it now: do not pull it back to the client's last pose
        if (idx < Stations.Count && Occupant.TryGetValue(idx, out var occ) && occ == from.Slot) Occupant.Remove(idx);
        if (p == null || idx >= Stations.Count) return;
        var st = Stations[idx];
        if (p.Body != null) p.Body.isKinematic = false;
        if (msg == MsgId.StationPlaceClean)
        {
            // The client's own tracker is gated off: the team's peel count moves here.
            try { PotatoPeeler.PotatoTracker.Instance?.HandlePeeled(p, PotatoPeeler.PeelSource.Hand); }
            catch (Exception e) { Plugin.L.LogWarning($"[stations] count peel: {e.Message}"); }
            st.PlaceClean(p, true);
        }
        else st.PlaceRaw(p);
        Placed++;
        Last = $"slot {from.Slot} {(msg == MsgId.StationPlaceClean ? "handed in" : "put back")} potato {id.Value} at table {idx}";
        Plugin.L.LogMessage($"[stations] {Last}");
    }

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if ((byte)id < 0xB0 || (byte)id > 0xBF) return false;
        try
        {
            var r = new NetReader(payload);
            if (from != null && MpRuntime.IsHost)
            {
                if (id == MsgId.StationTakeReq) OnTakeReq(from, ref r);
                else if (id is MsgId.StationPlaceClean or MsgId.StationRelease) OnHandBack(from, id, ref r);
            }
            else if (from == null && id == MsgId.StationTakeGrant) OnGrant(ref r);
        }
        catch (WireFormatException) { }
        catch (Exception e) { Plugin.L.LogWarning($"[stations] {id}: {e.Message}"); }
        return true;
    }

    public static object Describe() => new
    {
        cloned = _cloned, ExtrasApplied, LastBonus, Requests, Grants, Refusals, Placed, last = Last,
        stations = Stations.Select((w, i) => w == null ? null : new
        {
            i, id = "#" + Dev.Reflect.Remember(w), name = w.gameObject.name, pos = Dev.Reflect.Describe(w.transform.position),
            raw = SafeCount(w),
        }).ToArray(),
    };

    static int SafeCount(PotatoPeeler.Workstation w)
    {
        try { return w.CountRaw(0.95f); } catch { return -1; }
    }
}
