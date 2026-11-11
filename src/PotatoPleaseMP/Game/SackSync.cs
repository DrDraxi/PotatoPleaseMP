using HarmonyLib;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Sacks as networked objects. The host owns every sack.
/// - Host: gives each <c>PatatoSackInteractable</c> an id and streams SackState (0x79): pose, ripped, potatoes left. Changed
///   sacks go out at 10 Hz, the full list every second (sacks that vanish from it are gone).
/// - Client: maps host ids to its own sacks (the ones its join save loaded, matched by position once), creates sacks the host
///   made later (packager, claw drop unit, deliveries) from the sack template, hides sacks the host no longer has, and drives
///   them kinematically from the host pose.
/// - A client carrying (or throwing) a sack owns it: it streams SackOwned (0x7B) while carried and until the sack settles,
///   then hands it back with its velocity (SackIntent Release). The host follows that stream meanwhile.
/// - Ripping: a client's rip goes to the host by id (SackIntent Rip); the host rips for real (its spill makes host bodies) and
///   the ripped flag reaches everyone, who play the flip-and-rip locally. Clients' own spills are put away.
/// </summary>
internal static class SackSync
{
    const byte IntentRip = 1, IntentRelease = 2;
    const byte FRipped = 1, FHostCarried = 2;

    [ThreadStatic] static bool _applying;
    public static long Sent, Applied, Missed, Suppressed, Created, Hidden, Owned;

    // host
    static readonly Dictionary<IntPtr, ushort> IdOfPtr = new();
    static readonly Dictionary<ushort, PotatoPeeler.PatatoSackInteractable> HostById = new();
    static readonly Dictionary<ushort, (Vector3 p, Quaternion r, byte f, byte n)> LastSent = new();
    static readonly HashSet<ushort> RemoteDriven = new();
    static readonly Dictionary<ushort, double> ReleasedAt = new();
    static readonly Dictionary<ushort, (Vector3 p, Quaternion r)> RemotePose = new();
    static ushort _nextId = 1;
    static double _nextTick, _nextFull;

    // client
    static readonly Dictionary<ushort, PotatoPeeler.PatatoSackInteractable> ClientById = new();
    static readonly Dictionary<ushort, (Vector3 p, Quaternion r, byte f)> Target = new();
    static readonly Dictionary<ushort, double> LocalOwnedSince = new(); // NaN-free: time it was last carried
    static bool _matched;
    static readonly Dictionary<ushort, double> RecentMove = new();
    static double _nextOwnedSend;

    public static void Reset()
    {
        IdOfPtr.Clear(); HostById.Clear(); LastSent.Clear(); RemoteDriven.Clear(); RemotePose.Clear(); _nextId = 1;
        ClientById.Clear(); Target.Clear(); LocalOwnedSince.Clear(); _matched = false;
    }

    public static void OnPlayerJoined() => _nextFull = 0;

    static IEnumerable<PotatoPeeler.PatatoSackInteractable> Sacks() => UnityEngine.Object.FindObjectsOfType<PotatoPeeler.PatatoSackInteractable>();

    // ================================================================ host

    public static void UpdateHost(HostSession host)
    {
        var now = Time.realtimeSinceStartupAsDouble;
        DriveRemote();
        if (now < _nextTick) return;
        _nextTick = now + 0.1;
        bool full = now >= _nextFull;
        if (full) _nextFull = now + 1.0;
        try
        {
            var live = new HashSet<ushort>();
            var w = new NetWriter();
            w.U8(full ? (byte)1 : (byte)0);
            var entries = new List<(ushort id, Vector3 p, Quaternion r, byte f, byte n)>();
            foreach (var s in Sacks())
            {
                if (!IdOfPtr.TryGetValue(s.Pointer, out var id)) { id = _nextId++; IdOfPtr[s.Pointer] = id; HostById[id] = s; }
                live.Add(id);
                var t = s.body != null ? s.body.transform : s.transform;
                byte f = (byte)((s.RipStarted ? FRipped : 0) | (s.IsCarried ? FHostCarried : 0));
                byte n = (byte)Math.Clamp(SafeLeft(s), 0, 255);
                var e = (id, t.position, t.rotation, f, n);
                bool changed = !LastSent.TryGetValue(id, out var last) || (last.p - t.position).sqrMagnitude > 0.0001f
                               || Quaternion.Angle(last.r, t.rotation) > 1f || last.f != f || last.n != n;
                if (full || changed) entries.Add(e);
                LastSent[id] = (t.position, t.rotation, f, n);
            }
            foreach (var id in HostById.Keys.ToList())
                if (!live.Contains(id)) { HostById.Remove(id); LastSent.Remove(id); RemoteDriven.Remove(id); }
            foreach (var k in IdOfPtr.Where(kv => !live.Contains(kv.Value)).Select(kv => kv.Key).ToList()) IdOfPtr.Remove(k);
            if (!full && entries.Count == 0) return;
            w.U8((byte)Math.Min(entries.Count, 120));
            foreach (var (id, p, r, f, n) in entries.Take(120))
            {
                w.U16(id); w.F32(p.x); w.F32(p.y); w.F32(p.z); w.F32(r.x); w.F32(r.y); w.F32(r.z); w.F32(r.w); w.U8(f); w.U8(n);
            }
            host.Broadcast(MsgId.SackState, w.Span);
            Sent++;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[sacks] poll: {e.Message}"); _nextTick = now + 5; }
    }

    static int SafeLeft(PotatoPeeler.PatatoSackInteractable s) { try { return s.RemainingPotatoes; } catch { return 0; } }

    /// <summary>Host: sacks a client carries follow its stream.</summary>
    static void DriveRemote()
    {
        foreach (var id in RemoteDriven)
        {
            if (!HostById.TryGetValue(id, out var s) || s == null || s.body == null || !RemotePose.TryGetValue(id, out var pr)) continue;
            if (!s.body.isKinematic) s.body.isKinematic = true;
            s.body.position = Vector3.Lerp(s.body.position, pr.p, 0.5f);
            s.body.rotation = Quaternion.Slerp(s.body.rotation, pr.r, 0.5f);
        }
    }

    static void OnIntent(PlayerInfo from, ref NetReader r)
    {
        byte op = r.U8(); ushort id = r.U16();
        if (!HostById.TryGetValue(id, out var s) || s == null) { Missed++; return; }
        if (op == IntentRip)
        {
            if (!s.RipStarted) { _applying = true; try { s.RipByHand(false); } finally { _applying = false; } }
            Applied++;
            Plugin.L.LogMessage($"[sacks] slot {from.Slot} ripped sack {id}");
        }
        else if (op == IntentRelease)
        {
            var p = new Vector3(r.F32(), r.F32(), r.F32()); var q = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32()); var v = new Vector3(r.F32(), r.F32(), r.F32());
            RemoteDriven.Remove(id); RemotePose.Remove(id);
            ReleasedAt[id] = Time.realtimeSinceStartupAsDouble;
            if (s.body != null) { s.body.position = p; s.body.rotation = q; s.body.isKinematic = false; s.body.linearVelocity = v; s.body.WakeUp(); }
        }
    }

    static void OnOwned(PlayerInfo from, ref NetReader r)
    {
        int n = r.U8();
        for (int i = 0; i < n; i++)
        {
            ushort id = r.U16(); var p = new Vector3(r.F32(), r.F32(), r.F32()); var q = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            if (!HostById.TryGetValue(id, out var s) || s == null || s.IsCarried) continue; // the host's own hands win
            // the stream is unreliable and the release is reliable: a late sample must not claim the sack again
            if (ReleasedAt.TryGetValue(id, out var rel) && Time.realtimeSinceStartupAsDouble - rel < 1.5) continue;
            if (RemoteDriven.Add(id)) Owned++;
            RemotePose[id] = (p, q);
        }
    }

    // ================================================================ client

    static PotatoPeeler.PatatoSackInteractable? Template()
    {
        PotatoPeeler.PatatoSackInteractable? any = null;
        foreach (var o in Dev.Reflect.FindAll(typeof(PotatoPeeler.PatatoSackInteractable), true))
            if (o is PotatoPeeler.PatatoSackInteractable si) { any ??= si; if (!si.gameObject.activeInHierarchy) return si; }
        return any;
    }

    static PotatoPeeler.PatatoSackInteractable? Create(Vector3 p, Quaternion r, int count)
    {
        var tpl = Template();
        if (tpl == null) return null;
        var go = UnityEngine.Object.Instantiate(tpl.gameObject, p, r);
        go.SetActive(true);
        var sack = go.GetComponent<PotatoPeeler.PatatoSackInteractable>();
        try { sack.Initialize(Math.Max(1, count), UnityEngine.Object.FindObjectOfType<PotatoPeeler.PotatoPileActivator>()); } catch { }
        Created++;
        return sack;
    }

    static void ApplyState(ref NetReader r)
    {
        bool full = r.U8() == 1;
        int n = r.U8();
        var seen = new HashSet<ushort>();
        var entries = new List<(ushort id, Vector3 p, Quaternion q, byte f, byte left)>();
        for (int i = 0; i < n; i++)
            entries.Add((r.U16(), new Vector3(r.F32(), r.F32(), r.F32()), new Quaternion(r.F32(), r.F32(), r.F32(), r.F32()), r.U8(), r.U8()));

        // First full list: pair the host's sacks with the ones our copy of the save loaded.
        if (full && !_matched)
        {
            _matched = true;
            var mine = Sacks().ToList();
            foreach (var e in entries.OrderBy(e => e.id))
            {
                PotatoPeeler.PatatoSackInteractable? best = null; float bd = 2f * 2f;
                foreach (var s in mine)
                {
                    float d = (s.transform.position - e.p).sqrMagnitude;
                    if (d < bd) { bd = d; best = s; }
                }
                if (best != null) { ClientById[e.id] = best; mine.Remove(best); }
            }
            foreach (var extra in mine) { extra.gameObject.SetActive(false); Hidden++; } // the host has no such sack
        }

        foreach (var e in entries)
        {
            seen.Add(e.id);
            if (!ClientById.TryGetValue(e.id, out var s) || s == null)
            {
                if (!_matched) continue;
                s = Create(e.p, e.q, e.left);
                if (s == null) continue;
                ClientById[e.id] = s;
            }
            if (!Target.TryGetValue(e.id, out var old) || (old.p - e.p).sqrMagnitude > 0.0001f) RecentMove[e.id] = Time.realtimeSinceStartupAsDouble;
            Target[e.id] = (e.p, e.q, e.f);
            if ((e.f & FRipped) != 0 && !s.RipStarted)
            {
                _applying = true;
                try { s.RipByHand(false); } catch (Exception ex) { Plugin.L.LogWarning($"[sacks] rip replay: {ex.Message}"); }
                finally { _applying = false; }
                Applied++;
            }
        }
        if (full)
            foreach (var id in ClientById.Keys.ToList())
                if (!seen.Contains(id))
                {
                    var s = ClientById[id];
                    if (s != null && s.gameObject.activeSelf && !s.RipStarted) { s.gameObject.SetActive(false); Hidden++; }
                    ClientById.Remove(id); Target.Remove(id); LocalOwnedSince.Remove(id);
                }
    }

    public static void UpdateClient()
    {
        if (ClientById.Count == 0 || MpRuntime.Client is not { State: ClientState.Playing } c) return;
        var now = Time.realtimeSinceStartupAsDouble;
        var owned = new List<(ushort id, Vector3 p, Quaternion q)>();
        foreach (var (id, s) in ClientById.ToList())
        {
            if (s == null || s.body == null) continue;
            var rb = s.body;
            if (s.RipStarted) continue; // its own flip-and-rip animation runs locally
            if (s.IsCarried || HeldByLocalHand(rb)) LocalOwnedSince[id] = now;
            if (LocalOwnedSince.TryGetValue(id, out var since))
            {
                // ours: simulate, stream, and hand back once it has settled after we let go
                if (rb.isKinematic) rb.isKinematic = false;
                owned.Add((id, rb.position, rb.rotation));
                bool settled = rb.IsSleeping() || rb.linearVelocity.sqrMagnitude < 0.01f;
                if (!s.IsCarried && !HeldByLocalHand(rb) && ((settled && now - since > 0.6) || now - since > 4))
                {
                    var w = new NetWriter(); w.U8(IntentRelease); w.U16(id);
                    w.F32(rb.position.x); w.F32(rb.position.y); w.F32(rb.position.z);
                    w.F32(rb.rotation.x); w.F32(rb.rotation.y); w.F32(rb.rotation.z); w.F32(rb.rotation.w);
                    var v = rb.linearVelocity; w.F32(v.x); w.F32(v.y); w.F32(v.z);
                    c.Send(MsgId.SackIntent, w.Span);
                    LocalOwnedSince.Remove(id);
                }
                continue;
            }
            if (!Target.TryGetValue(id, out var tg)) continue;
            bool there = (rb.position - tg.p).sqrMagnitude < 0.0004f && Quaternion.Angle(rb.rotation, tg.r) < 2f;
            bool hostMoving = RecentMove.TryGetValue(id, out var mv) && now - mv < 0.4;
            if (there && !hostMoving)
            {
                // At rest on the host: a normal, sleeping sack here too, so our player can grab, carry or tear it.
                if (rb.isKinematic) { rb.isKinematic = false; rb.position = tg.p; rb.rotation = tg.r; rb.Sleep(); }
                continue;
            }
            if (!rb.isKinematic) rb.isKinematic = true;
            var np = Vector3.Lerp(rb.position, tg.p, 0.35f);
            var nq = Quaternion.Slerp(rb.rotation, tg.r, 0.35f);
            if ((np - tg.p).sqrMagnitude < 0.0001f) { np = tg.p; nq = tg.r; }
            rb.position = np; rb.rotation = nq;
            rb.transform.SetPositionAndRotation(np, nq);
        }
        if (owned.Count > 0 && now >= _nextOwnedSend)
        {
            _nextOwnedSend = now + 0.05;
            var w = new NetWriter(); w.U8((byte)owned.Count);
            foreach (var (id, p, q) in owned) { w.U16(id); w.F32(p.x); w.F32(p.y); w.F32(p.z); w.F32(q.x); w.F32(q.y); w.F32(q.z); w.F32(q.w); }
            c.Send(MsgId.SackOwned, w.Span);
        }
    }

    static PatatoSackAttractor? _hand;
    static bool HeldByLocalHand(Rigidbody rb)
    {
        if (_hand == null) _hand = UnityEngine.Object.FindObjectOfType<PatatoSackAttractor>();
        return _hand != null && _hand.capturedScak != null && _hand.capturedScak.Pointer == rb.Pointer;
    }

    /// <summary>Client: our hand grabbing a networked sack makes it ours right away (dynamic, streamed to the host).</summary>
    [HarmonyPatch(typeof(PatatoSackAttractor), nameof(PatatoSackAttractor.SetCaptured))]
    static class CapturePatch
    {
        static void Prefix(Rigidbody body)
        {
            if (!MpRuntime.IsClient || body == null) return;
            foreach (var (id, s) in ClientById)
                if (s != null && s.body != null && s.body.Pointer == body.Pointer)
                {
                    LocalOwnedSince[id] = Time.realtimeSinceStartupAsDouble;
                    if (body.isKinematic) body.isKinematic = false;
                    CaptureHits++;
                    break;
                }
        }
    }

    public static long CaptureHits;

    static ushort ClientIdOf(PotatoPeeler.PatatoSackInteractable s)
    {
        foreach (var (id, x) in ClientById) if (x != null && x.Pointer == s.Pointer) return id;
        return 0;
    }

    // ================================================================ patches

    /// <summary>Client: a rip by our player goes to the host by id (the local flip-and-rip still plays).</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PatatoSackInteractable), nameof(PotatoPeeler.PatatoSackInteractable.RipByHand))]
    static class RipPatch
    {
        static void Prefix(PotatoPeeler.PatatoSackInteractable __instance, out bool __state) => __state = __instance.RipStarted;

        static void Postfix(PotatoPeeler.PatatoSackInteractable __instance, bool __result, bool __state)
        {
            if (_applying || !__result || __state || !MpRuntime.IsClient) return;
            try
            {
                ushort id = ClientIdOf(__instance);
                if (id == 0 || MpRuntime.Client is not { State: ClientState.Playing } c) { Missed++; return; }
                var w = new NetWriter(); w.U8(IntentRip); w.U16(id);
                c.Send(MsgId.SackIntent, w.Span);
                Sent++;
            }
            catch (Exception e) { Plugin.L.LogWarning($"[sacks] rip: {e.Message}"); }
        }
    }

    /// <summary>Client: potatoes from its own sacks never become local bodies; the host's spill arrives as proxies.</summary>
    [HarmonyPatch(typeof(PatatoSack), nameof(PatatoSack.SpawnPotato))]
    static class SpawnPatch
    {
        static void Postfix(Rigidbody __result)
        {
            if (!MpRuntime.IsClient || __result == null) return;
            try { __result.gameObject.SetActive(false); Suppressed++; }
            catch (Exception e) { Plugin.L.LogWarning($"[sacks] suppress: {e.Message}"); }
        }
    }

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if (id is not (MsgId.SackState or MsgId.SackIntent or MsgId.SackOwned)) return false;
        try
        {
            var r = new NetReader(payload);
            if (from == null) { if (id == MsgId.SackState) ApplyState(ref r); }
            else if (MpRuntime.IsHost)
            {
                if (id == MsgId.SackIntent) OnIntent(from, ref r);
                else if (id == MsgId.SackOwned) OnOwned(from, ref r);
            }
        }
        catch (WireFormatException) { }
        catch (Exception e) { Plugin.L.LogWarning($"[sacks] {id}: {e.Message}"); }
        return true;
    }

    public static object Describe() => new
    {
        Sent, Applied, Missed, Suppressed, Created, Hidden, Owned, CaptureHits,
        hostIds = HostById.Count, clientIds = ClientById.Count, remoteDriven = RemoteDriven.ToArray(), localOwned = LocalOwnedSince.Keys.ToArray(),
        sacks = Sacks().Select(s => new { id = "#" + Dev.Reflect.Remember(s), pos = Dev.Reflect.Describe(s.transform.position), ripped = s.RipStarted, left = SafeLeft(s), carried = s.IsCarried }).ToArray(),
    };
}
