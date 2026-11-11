using HarmonyLib;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// The potato pile (spec 06), first cut. The host owns the pile.
/// - Baked entries the host takes out of the pile (digging, the hand, the vacuum, machines, grain wakes) show up as new bits in
///   its <c>_removedBaked</c> bitset. Every 100 ms the host diffs that bitset and sends the new indices (PileDelta 0x31);
///   clients take the same entries out of their pile with the game's own <c>TakeNearest</c>, so the hole appears for everyone
///   and the game's collapse/grain look plays locally. A joining client gets every index removed since the save it loaded.
/// - A client's hand never activates pile potatoes itself: <c>ActivateAt</c> becomes a request (PileDigIntent 0x35); the
///   host activates them and they reach the client as bodies its hand can then grab.
/// Not yet: live (returned) pile entries, digests/repair, frost bytes, vacuum bulk intake from a client.
/// </summary>
internal static class PileSync
{
    static ulong[] _shadow = Array.Empty<ulong>();
    static double _next;
    static readonly Queue<int> Pending = new();
    public static long SentIdx, AppliedIdx, Digs, Missed;
    public static string Last = "";
    [ThreadStatic] static bool _hostDig;
    [ThreadStatic] static bool _inReturn, _applying;
    static readonly List<byte[]> HostRecords = new();
    public static long LiveSent, LiveApplied, TakesSent, TakesApplied, Protected;
    const int Chunk = 240;

    static PotatoPeeler.PotatoPileRenderer? Renderer => UnityEngine.Object.FindObjectOfType<PotatoPeeler.PotatoPileRenderer>();

    public static void Reset() { _shadow = Array.Empty<ulong>(); Pending.Clear(); }
    public static void ResetPending() => Pending.Clear();

    static ulong[] Snapshot(PotatoPeeler.PotatoPileRenderer r)
    {
        var bits = r._removedBaked;
        if (bits == null) return Array.Empty<ulong>();
        var a = new ulong[bits.Length];
        for (int i = 0; i < a.Length; i++) a[i] = bits[i];
        return a;
    }

    static void SendIndices(Action<byte[]> send, List<int> idx)
    {
        for (int s = 0; s < idx.Count; s += Chunk)
        {
            var w = new NetWriter();
            int n = Math.Min(Chunk, idx.Count - s);
            w.U8(2); w.U16((ushort)n);
            for (int i = 0; i < n; i++) w.I32(idx[s + i]);
            send(w.Span.ToArray());
            SentIdx += n;
        }
    }

    // ---------------- host ----------------

    public static void UpdateHost(HostSession host)
    {
        var now = Time.realtimeSinceStartupAsDouble;
        if (now < _next) return;
        _next = now + 0.1;
        try
        {
            var r = Renderer;
            if (r == null) return;
            var cur = Snapshot(r);
            if (_shadow.Length != cur.Length) { _shadow = cur; return; }
            List<int>? fresh = null;
            for (int wi = 0; wi < cur.Length; wi++)
            {
                ulong d = cur[wi] & ~_shadow[wi];
                while (d != 0)
                {
                    int b = System.Numerics.BitOperations.TrailingZeroCount(d);
                    (fresh ??= new List<int>()).Add(wi * 64 + b);
                    d &= d - 1;
                }
            }
            _shadow = cur;
            if (fresh != null) SendIndices(b => host.Broadcast(MsgId.PileDelta, b), fresh);
            FlushRecords(host);
        }
        catch (Exception e) { Plugin.L.LogWarning($"[pile] poll: {e.Message}"); _next = now + 5; }
    }

    static void FlushRecords(HostSession host)
    {
        if (HostRecords.Count == 0) return;
        int i = 0;
        while (i < HostRecords.Count)
        {
            int n = Math.Min(40, HostRecords.Count - i);
            var w = new NetWriter();
            w.U8(3); w.U8((byte)n);
            for (int k = 0; k < n; k++) foreach (var b in HostRecords[i + k]) w.U8(b);
            host.Broadcast(MsgId.PileDelta, w.Span);
            i += n;
        }
        HostRecords.Clear();
    }

    /// <summary>Host: a body went back into the pile as a live entry: clients add the same entry.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileRenderer), nameof(PotatoPeeler.PotatoPileRenderer.Return))]
    static class ReturnScope
    {
        static void Prefix() => _inReturn = true;
        static void Postfix() => _inReturn = false;
        static Exception? Finalizer(Exception? __exception) { _inReturn = false; return __exception; }
    }

    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileRenderer), nameof(PotatoPeeler.PotatoPileRenderer.AddInstance))]
    static class AddLiveCapture
    {
        static void Postfix(Vector3 position, Quaternion rotation, float scale, float frost, int variant)
        {
            if (!_inReturn || _applying || !MpRuntime.IsHost) return;
            var w = new NetWriter();
            w.U8(1); w.F32(position.x); w.F32(position.y); w.F32(position.z);
            w.F32(rotation.x); w.F32(rotation.y); w.F32(rotation.z); w.F32(rotation.w); w.F32(scale); w.F32(frost); w.U8((byte)variant);
            HostRecords.Add(w.Span.ToArray());
            LiveSent++;
        }
    }

    /// <summary>Host: takes that removed live entries are replayed by clients (baked ones also come as bits).</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileRenderer), nameof(PotatoPeeler.PotatoPileRenderer.TakeNearest))]
    static class TakeCapture
    {
        static void Prefix(PotatoPeeler.PotatoPileRenderer __instance, out int __state)
        {
            __state = -1;
            if (MpRuntime.IsHost && !_applying) try { __state = __instance.LiveCount; } catch { }
        }

        static void Postfix(PotatoPeeler.PotatoPileRenderer __instance, Vector3 center, float radius, int maxCount, bool includeBuried, int __state)
        {
            if (__state < 0) return;
            int now; try { now = __instance.LiveCount; } catch { return; }
            if (now >= __state) return;
            var w = new NetWriter();
            w.U8(2); w.F32(center.x); w.F32(center.y); w.F32(center.z); w.F32(radius); w.U8((byte)Math.Min(255, __state - now)); w.Bool(includeBuried);
            HostRecords.Add(w.Span.ToArray());
            TakesSent++;
        }
    }

    /// <summary>
    /// Host: the game returns pile potatoes that are far from "the player" (kinematic or asleep) to the pile. Bodies a client
    /// holds, or that lie near a remote player, are not far from a player: hide them from that sweep for the call.
    /// </summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileActivator), nameof(PotatoPeeler.PotatoPileActivator.ReturnFarPotatoes))]
    static class KeepNearRemote
    {
        static void Prefix(out List<Rigidbody>? __state)
        {
            __state = null;
            if (!MpRuntime.IsHost) return;
            try
            {
                var feet = MpRuntime.RemoteFeet().ToList();
                if (feet.Count == 0) return;
                foreach (var p in BodySync.HostBodies())
                {
                    var rb = p.Body;
                    if (rb == null) continue;
                    bool owned = BodySync.IsRemoteDriven(p);
                    bool near = !owned && feet.Any(f => (f - p.transform.position).sqrMagnitude < 5f * 5f);
                    // The sweep only takes bodies that are kinematic or asleep: make these neither, for the call.
                    if ((owned || near) && rb.isKinematic) { rb.isKinematic = false; (__state ??= new()).Add(rb); Protected++; }
                    else if (near && rb.IsSleeping()) { rb.WakeUp(); Protected++; }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[pile] keep: {e.Message}"); }
        }

        static void Postfix(List<Rigidbody>? __state)
        {
            if (__state == null) return;
            foreach (var rb in __state) if (rb != null) rb.isKinematic = true;
        }
    }

    /// <summary>Host: a new client loaded our last save; send everything removed so far (it applies what it lacks).</summary>
    public static void OnPlayerJoined(PlayerInfo p)
    {
        try
        {
            var r = Renderer;
            if (r == null || MpRuntime.Host is not { } h) return;
            var cur = Snapshot(r);
            var all = new List<int>();
            for (int wi = 0; wi < cur.Length; wi++)
            {
                ulong d = cur[wi];
                while (d != 0) { all.Add(wi * 64 + System.Numerics.BitOperations.TrailingZeroCount(d)); d &= d - 1; }
            }
            SendIndices(b => h.SendTo(p.Slot, MsgId.PileDelta, b), all);
            Plugin.L.LogMessage($"[pile] sent {all.Count} removed pile entries to {p.Name}.");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[pile] join state: {e.Message}"); }
    }

    const byte OpActivate = 0, OpConsume = 1, OpSpawnDetached = 2, OpConsumeNear = 3;

    static void OnDig(PlayerInfo from, ref NetReader rd)
    {
        byte op = rd.U8();
        var act = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PotatoPileActivator>();
        if (act == null) return;
        if (op == OpConsume)
        {
            // A client's tool (vacuum, bucket, barrow) took one of our bodies into its container.
            var p = BodySync.Get(new Core.Bodies.NetId(rd.U16()));
            if (p != null) { _hostDig = true; try { act.ConsumeActive(p); } finally { _hostDig = false; } }
            Last = $"slot {from.Slot} took a potato into a container";
            return;
        }
        if (op == OpSpawnDetached)
        {
            // A client's container let a potato out (spill, pour): it becomes a real body here and reaches everyone.
            var pos = new Vector3(rd.F32(), rd.F32(), rd.F32()); var rot = new Quaternion(rd.F32(), rd.F32(), rd.F32(), rd.F32());
            float scale = rd.F32(); int variant = rd.U8();
            _hostDig = true; try { act.SpawnFromDetached(pos, rot, scale, variant); } finally { _hostDig = false; }
            Last = $"slot {from.Slot} let a potato out of a container";
            return;
        }
        if (op == OpConsumeNear)
        {
            var cc = new Vector3(rd.F32(), rd.F32(), rd.F32()); float rr = rd.F32(); int mm = rd.U8(); bool bulk = rd.Bool();
            var tmp = new Il2CppSystem.Collections.Generic.List<PotatoPeeler.PotatoPileRenderer.PileInstance>();
            _hostDig = true; try { act.ConsumePileNear(cc, Mathf.Min(rr, 3f), Mathf.Min(mm, 64), tmp, bulk); } finally { _hostDig = false; }
            Last = $"slot {from.Slot} scooped {tmp.Count} from the pile";
            return;
        }
        var c = new Vector3(rd.F32(), rd.F32(), rd.F32()); float radius = rd.F32(); int max = rd.U8();
        if (radius > 3f || max > 16) return;
        _hostDig = true;
        try { act.ActivateAt(c, radius, max); }
        finally { _hostDig = false; }
        Digs++;
        Last = $"slot {from.Slot} pulled from the pile at {c} r={radius} max={max} -> removed {Renderer?._removedBakedCount}";
    }

    // ---------------- client ----------------

    static void Enqueue(ref NetReader rd)
    {
        int n = rd.U16();
        for (int i = 0; i < n; i++) Pending.Enqueue(rd.I32());
    }

    public static void UpdateClient()
    {
        if (Pending.Count == 0) return;
        var r = Renderer;
        if (r == null || r.data == null || r._removedBaked == null) return;
        var tmp = new Il2CppSystem.Collections.Generic.List<PotatoPeeler.PotatoPileRenderer.PileInstance>();
        int budget = 400; // per frame; a join backlog drains over a few frames
        while (Pending.Count > 0 && budget-- > 0)
        {
            int i = Pending.Dequeue();
            if (i < 0 || i >= r.BakedTotal) continue;
            if ((r._removedBaked[i >> 6] & (1UL << (i & 63))) != 0) continue; // already gone here
            r.data.GetInstance(i, out var pos, out _, out _);
            tmp.Clear();
            int got = r.TakeNearest(pos, 0.01f, 1, tmp, true);
            if (got > 0) AppliedIdx++; else Missed++;
        }
    }

    /// <summary>Client: the hand asks the host to bring potatoes out of the pile instead of doing it locally.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileActivator), nameof(PotatoPeeler.PotatoPileActivator.ActivateAt))]
    static class ActivateGate
    {
        static bool Prefix(Vector3 center, float radius, int maxCount)
        {
            if (!MpRuntime.IsClient) return true;
            try
            {
                if (MpRuntime.Client is { State: ClientState.Playing } c)
                {
                    var w = new NetWriter();
                    w.U8(OpActivate); w.F32(center.x); w.F32(center.y); w.F32(center.z); w.F32(radius); w.U8((byte)Math.Clamp(maxCount, 0, 16));
                    c.Send(MsgId.PileDigIntent, w.Span);
                    Digs++;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[pile] dig request: {e.Message}"); }
            return false;
        }
    }

    static void SendDig(Action<NetWriter> body)
    {
        if (MpRuntime.Client is not { State: ClientState.Playing } c) return;
        var w = new NetWriter(); body(w); c.Send(MsgId.PileDigIntent, w.Span); Digs++;
    }

    /// <summary>Client: a tool takes a networked potato into its container (local tank count stays, the host removes the body).</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileActivator), nameof(PotatoPeeler.PotatoPileActivator.ConsumeActive))]
    static class ConsumeForward
    {
        static void Prefix(PotatoPeeler.PotatoInstance potato, out bool __state)
        {
            __state = false;
            if (!MpRuntime.IsClient || BodySync.ApplyingPool || potato == null) return;
            try
            {
                var id = BodySync.ForgetProxy(potato);
                if (id.IsValid) { __state = true; SendDig(w => { w.U8(OpConsume); w.U16(id.Value); }); }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[pile] consume: {e.Message}"); }
        }

        // Proxies are not in the client's active list, so the game may refuse to pool them: hide them ourselves.
        static void Postfix(PotatoPeeler.PotatoInstance potato, bool __result, bool __state)
        {
            if (__state && !__result && potato != null && potato.gameObject.activeSelf) potato.gameObject.SetActive(false);
        }
    }

    /// <summary>Client: a container lets a potato out. The host makes the real body; our local one is put away.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileActivator), nameof(PotatoPeeler.PotatoPileActivator.SpawnFromDetached))]
    static class SpawnDetachedForward
    {
        static void Postfix(Vector3 position, Quaternion rotation, float scale, int variant, PotatoPeeler.PotatoInstance __result)
        {
            if (!MpRuntime.IsClient) return;
            try
            {
                SendDig(w => { w.U8(OpSpawnDetached); w.F32(position.x); w.F32(position.y); w.F32(position.z);
                    w.F32(rotation.x); w.F32(rotation.y); w.F32(rotation.z); w.F32(rotation.w); w.F32(scale); w.U8((byte)variant); });
                if (__result != null) __result.gameObject.SetActive(false);
            }
            catch (Exception e) { Plugin.L.LogWarning($"[pile] spawn: {e.Message}"); }
        }
    }

    /// <summary>Client: bulk scoops (vacuum bulk, bucket) take from the host's pile too.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoPileActivator), nameof(PotatoPeeler.PotatoPileActivator.ConsumePileNear))]
    static class ConsumeNearForward
    {
        static void Postfix(Vector3 center, float radius, int maxCount, bool bulk, int __result)
        {
            if (!MpRuntime.IsClient || __result <= 0) return;
            SendDig(w => { w.U8(OpConsumeNear); w.F32(center.x); w.F32(center.y); w.F32(center.z); w.F32(radius); w.U8((byte)Math.Min(255, __result)); w.Bool(bulk); });
        }
    }

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if ((byte)id < 0x30 || (byte)id > 0x3F) return false;
        try
        {
            var r = new NetReader(payload);
            if (id == MsgId.PileDelta && from == null)
            {
                byte tag = r.U8();
                if (tag == 2) Enqueue(ref r);
                else if (tag == 3) ApplyRecords(ref r);
            }
            else if (id == MsgId.PileDigIntent && from != null && MpRuntime.IsHost) OnDig(from, ref r);
            else if (id == MsgId.PileRefill && from == null) PileRefillSync.Apply(ref r);
        }
        catch (WireFormatException) { }
        catch (Exception e) { Plugin.L.LogWarning($"[pile] {id}: {e.Message}"); }
        return true;
    }

    static void ApplyRecords(ref NetReader rd)
    {
        int n = rd.U8();
        var rend = Renderer;
        var tmp = new Il2CppSystem.Collections.Generic.List<PotatoPeeler.PotatoPileRenderer.PileInstance>();
        _applying = true;
        try
        {
            for (int i = 0; i < n; i++)
            {
                byte k = rd.U8();
                if (k == 1)
                {
                    var pos = new Vector3(rd.F32(), rd.F32(), rd.F32()); var rot = new Quaternion(rd.F32(), rd.F32(), rd.F32(), rd.F32());
                    float scale = rd.F32(), frost = rd.F32(); int variant = rd.U8();
                    if (rend != null) { rend.AddInstance(pos, rot, scale, frost, variant); LiveApplied++; }
                }
                else if (k == 2)
                {
                    var c = new Vector3(rd.F32(), rd.F32(), rd.F32()); float radius = rd.F32(); int max = rd.U8(); bool buried = rd.Bool();
                    if (rend != null) { tmp.Clear(); rend.TakeNearest(c, radius, max, tmp, buried); TakesApplied++; }
                }
            }
        }
        finally { _applying = false; }
    }

    public static object Describe()
    {
        var r = Renderer;
        return new { SentIdx, AppliedIdx, Missed, Digs, LiveSent, LiveApplied, TakesSent, TakesApplied, Protected, pending = Pending.Count, removed = r?._removedBakedCount, live = SafeLive(r), last = Last };
    }

    static int? SafeLive(PotatoPeeler.PotatoPileRenderer? r) { try { return r?.LiveCount; } catch { return null; } }
}
