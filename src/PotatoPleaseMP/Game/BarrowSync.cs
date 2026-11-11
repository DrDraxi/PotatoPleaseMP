using HarmonyLib;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Wheelbarrows as shared objects. Every peer has the same barrows from the scene; they are matched by scene path.
/// - Whoever pushes a barrow (PhysicsGrabber holding its PhysicsGrabbable) simulates it and streams its pose (BarrowPose
///   0x7C, any -> host -> others); everyone else drives their copy kinematically from that stream, and the barrow goes back to
///   normal physics when the pusher lets go and it settles.
/// - Potatoes going into a client's barrow come out of the host's world through the container path (see PileSync), and
///   pouring brings them back as host bodies. The count shown inside each barrow is the pusher's.
/// </summary>
internal static class BarrowSync
{
    static readonly Dictionary<int, double> RemoteUntil = new();
    static readonly Dictionary<int, (Vector3 p, Quaternion r)> RemotePose = new();
    static double _nextSend;
    public static long Out, In;

    static List<PotatoPeeler.PotatoWheelbarrow> Barrows()
    {
        var l = UnityEngine.Object.FindObjectsOfType<PotatoPeeler.PotatoWheelbarrow>().ToList();
        l.Sort((a, b) => string.CompareOrdinal(Path(a.transform), Path(b.transform)));
        return l;
    }

    static string Path(Transform t) { var s = t.name; for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s; return s; }

    static PotatoPeeler.PhysicsGrabber? _grabber;

    /// <summary>Our player is looking at it to grab it: it needs normal physics for the grab to take.</summary>
    static bool FocusedLocally(PotatoPeeler.PotatoWheelbarrow b)
    {
        if (_grabber == null) _grabber = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PhysicsGrabber>();
        var f = _grabber?._focused;
        return f != null && b._grabbable != null && f.Pointer == b._grabbable.Pointer;
    }

    static bool PushedLocally(PotatoPeeler.PotatoWheelbarrow b)
    {
        if (_grabber == null) _grabber = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PhysicsGrabber>();
        var g = _grabber?._grabbed;
        return g != null && b._grabbable != null && g.Pointer == b._grabbable.Pointer;
    }

    public static void Update()
    {
        if (!MpRuntime.Active) return;
        var now = Time.realtimeSinceStartupAsDouble;
        var list = Barrows();
        // Others' barrows: follow the stream while it is fresh, then let physics have it again.
        for (int i = 0; i < list.Count; i++)
        {
            var b = list[i];
            var rb = b._grabbable?._body;
            if (rb == null) continue;
            if (RemoteUntil.TryGetValue(i, out var until) && now < until && !PushedLocally(b) && !(MpRuntime.IsClient && FocusedLocally(b) && (rb.position - RemotePose[i].p).sqrMagnitude < 0.01f) && RemotePose.TryGetValue(i, out var pr))
            {
                if (!rb.isKinematic) rb.isKinematic = true;
                rb.position = Vector3.Lerp(rb.position, pr.p, 0.4f);
                rb.rotation = Quaternion.Slerp(rb.rotation, pr.r, 0.4f);
            }
            else
            {
                RemoteUntil.Remove(i);
                // Clients: a barrow nobody is pushing belongs to the host; ours is a fixed obstacle until our player grabs it
                // (bumping into it must not move it on one screen only). The host's own physics runs as normal.
                bool mine = PushedLocally(b) || FocusedLocally(b) || (PushedAt.TryGetValue(i, out var pa) && now - pa < 1.5);
                if (MpRuntime.IsClient && !mine) { if (!rb.isKinematic) rb.isKinematic = true; }
                else if (rb.isKinematic && (MpRuntime.IsHost || mine)) { rb.isKinematic = false; rb.WakeUp(); }
            }
        }
        if (now < _nextSend) return;
        _nextSend = now + 0.05;
        bool keep = now >= _nextKeep;
        // Ours: stream while pushed and for a moment after (it rolls on).
        for (int i = 0; i < list.Count; i++)
        {
            var b = list[i];
            var rb = b._grabbable?._body;
            if (rb == null) continue;
            bool pushed = PushedLocally(b);
            if (pushed) PushedAt[i] = now;
            if (MpRuntime.IsHost && !RemoteUntil.ContainsKey(i) && (rb.linearVelocity.sqrMagnitude > 0.0004f || now >= _nextKeep)) PushedAt[i] = now;
            if (!PushedAt.TryGetValue(i, out var at) || now - at > 1.5) continue;
            var w = new NetWriter();
            w.U8((byte)i); w.F32(rb.position.x); w.F32(rb.position.y); w.F32(rb.position.z);
            w.F32(rb.rotation.x); w.F32(rb.rotation.y); w.F32(rb.rotation.z); w.F32(rb.rotation.w);
            if (MpRuntime.Host is { } h) h.Broadcast(MsgId.BarrowPose, w.Span);
            else if (MpRuntime.Client is { State: ClientState.Playing } c) c.Send(MsgId.BarrowPose, w.Span);
            Out++;
        }
        if (keep) _nextKeep = now + 1.0; // the host refreshes resting barrows once a second
    }

    static double _nextKeep;

    static readonly Dictionary<int, double> PushedAt = new();

    /// <summary>Client: about to grab one of our (kinematic) barrows: give it physics back so the grab can take it.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PhysicsGrabber), nameof(PotatoPeeler.PhysicsGrabber.TryGrab))]
    static class GrabPrep
    {
        static void Prefix(PotatoPeeler.PhysicsGrabber __instance)
        {
            if (!MpRuntime.IsClient) return;
            var f = __instance._focused;
            if (f == null || f._body == null) return;
            foreach (var b in Barrows())
                if (b._grabbable != null && b._grabbable.Pointer == f.Pointer && f._body.isKinematic) { f._body.isKinematic = false; f._body.WakeUp(); }
        }
    }

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if (id != MsgId.BarrowPose) return false;
        try
        {
            var r = new NetReader(payload);
            int i = r.U8();
            var p = new Vector3(r.F32(), r.F32(), r.F32()); var q = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            // never fight our own player's push
            if (PushedAt.TryGetValue(i, out var at) && Time.realtimeSinceStartupAsDouble - at < 1.5) return true;
            RemotePose[i] = (p, q);
            RemoteUntil[i] = Time.realtimeSinceStartupAsDouble + 2.0;
            In++;
            if (from != null && MpRuntime.Host is { } h) h.Broadcast(MsgId.BarrowPose, payload, exceptSlot: from.Slot);
        }
        catch (WireFormatException) { }
        catch (Exception e) { Plugin.L.LogWarning($"[barrow] {e.Message}"); }
        return true;
    }

    public static object Describe() => new
    {
        Out, In,
        barrows = Barrows().Select((b, i) => new { i, id = "#" + Dev.Reflect.Remember(b), pos = Dev.Reflect.Describe(b.transform.position), stored = b._stored, pushed = PushedLocally(b), following = RemoteUntil.ContainsKey(i) }).ToArray(),
    };
}
