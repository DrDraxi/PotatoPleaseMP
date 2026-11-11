using HarmonyLib;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Cleaning (spec 11), first cut.
/// - Floor: every player mops with their own mop. Each <c>DustField.Stamp</c> call (mop wipe) goes out as MopStrokes (0x80):
///   client -> host -> other clients; receivers apply the same stamp, so the floor gets clean for everyone.
/// - Duty: the cleanup call/complete cycle runs on the host only. It sends CleanupState (0x85) at 2 Hz and on change;
///   clients mirror its fields (active, completed, growing, the peel start lock) and play the call/complete moments with the
///   game's own Call()/Complete().
/// - Money for swept shavings: a client's sweep income goes to the host's shared wallet (IncomeReq via CleanupEvent 0x84).
/// - Water forces run on the host only (client bodies in water are kinematic proxies anyway).
/// - Ruins on a client count on the host (tracker + waste fines), see <see cref="ForwardRuin"/>.
/// Not yet: tile digests/repair, shaving ribbons of other players, bucket dirt, carpet, the dip enforcer across peers.
/// </summary>
internal static class CleanSync
{
    [ThreadStatic] static bool _applying;
    [ThreadStatic] static bool _inSweep;
    static double _next;
    static byte[] _last = Array.Empty<byte>();
    static bool _wasActive;
    static double _nextRefresh;
    static int _wasCompleted = -1;
    public static long StrokesOut, StrokesIn, StateOut, StateIn, Income, Ruins;
    public static string Last = "";

    const byte EvIncome = 1, EvRuin = 2;

    public static void OnPlayerJoined() => _last = Array.Empty<byte>();
    public static void ResetSession() { _last = Array.Empty<byte>(); _wasActive = false; _wasCompleted = -1; }
    public static bool IsApplying => _applying;
    public static bool InSweep => _inSweep;

    // ---------------- strokes ----------------

    [HarmonyPatch(typeof(PotatoPeeler.DustField), nameof(PotatoPeeler.DustField.Stamp))]
    static class StampPatch
    {
        static void Postfix(Il2CppSystem.Collections.Generic.List<Vector3> points, float radius, float dustAmount, float wetAmount)
        {
            if (_applying || !MpRuntime.Active || points == null || points.Count == 0) return;
            try
            {
                var w = new NetWriter();
                int n = Math.Min(points.Count, 200);
                w.F32(radius); w.F32(dustAmount); w.F32(wetAmount); w.U8((byte)n);
                for (int i = 0; i < n; i++) { var p = points[i]; w.F32(p.x); w.F32(p.y); w.F32(p.z); }
                if (MpRuntime.Host is { } h) h.Broadcast(MsgId.MopStrokes, w.Span);
                else if (MpRuntime.Client is { State: ClientState.Playing } c) c.Send(MsgId.MopStrokes, w.Span);
                StrokesOut++;
            }
            catch (Exception e) { Plugin.L.LogWarning($"[clean] stroke: {e.Message}"); }
        }
    }

    static void ApplyStroke(ref NetReader r)
    {
        float radius = r.F32(), dust = r.F32(), wet = r.F32();
        int n = r.U8();
        var list = new Il2CppSystem.Collections.Generic.List<Vector3>(n);
        for (int i = 0; i < n; i++) list.Add(new Vector3(r.F32(), r.F32(), r.F32()));
        var field = PotatoPeeler.CleanupDuty.Instance?.dust ?? UnityEngine.Object.FindObjectOfType<PotatoPeeler.DustField>();
        if (field == null) return;
        _applying = true;
        try { field.Stamp(list, radius, dust, wet); }
        finally { _applying = false; }
        StrokesIn++;
    }

    // ---------------- duty ----------------

    public static void UpdateHost(HostSession host)
    {
        var now = Time.realtimeSinceStartupAsDouble;
        if (now < _next) return;
        _next = now + 0.5;
        try
        {
            var d = PotatoPeeler.CleanupDuty.Instance;
            if (d == null) return;
            var f = d.dust;
            var lockText = PotatoPeeler.PeelSession.Instance?._startLock;
            var w = new NetWriter();
            byte flags = (byte)((d._IsActive_k__BackingField ? 1 : 0) | (d._settling ? 2 : 0) | (d._measuring ? 4 : 0)
                | (f != null && f._Growing_k__BackingField ? 8 : 0) | (lockText != null ? 16 : 0));
            w.U8(flags);
            w.I32(d._completed);
            w.F32(d._startDirt);
            w.F32(Time.time - d._calledAt);
            w.F32(f?._FillSeconds_k__BackingField ?? 0);
            w.I32(f?._growCursor ?? 0);
            w.Str8(lockText ?? "");
            var bytes = w.Span.ToArray();
            // Send on change, and the growth cursor creeps, so at most every 2 s while only it moves.
            bool changed = _last.Length == 0 || bytes[0] != _last[0] || !bytes.AsSpan(1, 4).SequenceEqual(_last.AsSpan(1, 4));
            if (!changed && now < _nextRefresh) return;
            _nextRefresh = now + 2.0;
            _last = bytes;
            host.Broadcast(MsgId.CleanupState, bytes);
            StateOut++;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[clean] poll: {e.Message}"); _next = now + 5; }
    }

    static void ApplyState(ref NetReader r)
    {
        byte flags = r.U8();
        int completed = r.I32();
        float startDirt = r.F32(), age = r.F32(), fill = r.F32();
        int cursor = r.I32();
        string lockText = r.Str8();
        var d = PotatoPeeler.CleanupDuty.Instance;
        if (d == null) return;
        bool active = (flags & 1) != 0;
        _applying = true;
        try
        {
            if (_wasCompleted < 0) _wasCompleted = d._completed;
            if (completed > _wasCompleted && _wasActive) { try { d.Complete(); } catch (Exception e) { Plugin.L.LogWarning($"[clean] complete: {e.Message}"); } Last = "cleanup done"; }
            else if (active && !_wasActive) { try { d.Call(); } catch (Exception e) { Plugin.L.LogWarning($"[clean] call: {e.Message}"); } Last = "cleanup called"; }
            else if (!active && _wasActive) { try { d.Release(); } catch { } Last = "cleanup ended"; }
            d._IsActive_k__BackingField = active;
            d._settling = (flags & 2) != 0;
            d._measuring = (flags & 4) != 0;
            d._completed = completed;
            d._startDirt = startDirt;
            d._calledAt = Time.time - age;
            if (d.dust is { } f)
            {
                f._Growing_k__BackingField = (flags & 8) != 0;
                f._FillSeconds_k__BackingField = fill;
            }
            if (PotatoPeeler.PeelSession.Instance is { } ps) ps._startLock = (flags & 16) != 0 ? lockText : null;
            _wasActive = active; _wasCompleted = completed;
        }
        finally { _applying = false; }
        StateIn++;
    }

    /// <summary>Client: the cleanup cycle is the host's. Our copy only shows hints while a cleanup is on.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.CleanupDuty), "Update")]
    static class DutyUpdateGate
    {
        static bool Prefix(PotatoPeeler.CleanupDuty __instance)
        {
            if (!MpRuntime.IsClient) return true;
            try { if (__instance._IsActive_k__BackingField && __instance._completed < __instance.guidedCleanups) __instance.Guide(); } catch { }
            return false;
        }
    }

    // ---------------- shavings money ----------------

    [HarmonyPatch(typeof(PotatoPeeler.Mop), "SweepShavings")]
    static class SweepScope
    {
        static void Prefix() => _inSweep = true;
        static void Postfix() => _inSweep = false;
        static Exception? Finalizer(Exception? __exception) { _inSweep = false; return __exception; }
    }

    /// <summary>Client: income from its own sweep (gated locally by EconSync) goes to the team wallet.</summary>
    public static void ForwardIncome(long amount, Vector3 at, PotatoPeeler.IncomeSource source)
    {
        if (amount == 0 || MpRuntime.Client is not { State: ClientState.Playing } c) return;
        var w = new NetWriter(); w.U8(EvIncome); w.I64(amount); w.F32(at.x); w.F32(at.y); w.F32(at.z); w.U8((byte)source);
        c.Send(MsgId.CleanupEvent, w.Span);
        Income += amount;
        Last = $"sweep income {amount} sent";
    }

    /// <summary>Client: a potato ruined at its station counts for the team (tracker + waste fines on the host).</summary>
    public static void ForwardRuin(PotatoPeeler.PotatoInstance potato)
    {
        if (MpRuntime.Client is not { State: ClientState.Playing } c) return;
        var id = potato != null ? BodySync.IdOf(potato) : Core.Bodies.NetId.None;
        var w = new NetWriter(); w.U8(EvRuin); w.U16(id.Value);
        c.Send(MsgId.CleanupEvent, w.Span);
        Ruins++;
    }

    static void OnEvent(PlayerInfo from, ref NetReader r)
    {
        byte ev = r.U8();
        if (ev == EvIncome)
        {
            long amount = r.I64(); var at = new Vector3(r.F32(), r.F32(), r.F32());
            var source = r.Remaining > 0 ? (PotatoPeeler.IncomeSource)r.U8() : PotatoPeeler.IncomeSource.Mop;
            if (amount > 0 && amount < 100_000) PotatoPeeler.EconomyManager.Instance?.AddIncome(amount, at, source);
            Income += amount;
            Last = $"slot {from.Slot} swept shavings: +{amount}";
        }
        else if (ev == EvRuin)
        {
            var nid = new Core.Bodies.NetId(r.U16());
            var p = BodySync.Get(nid);
            try
            {
                if (p != null) PotatoPeeler.PotatoTracker.Instance?.HandleRuined(p);
                var we = UnityEngine.Object.FindObjectOfType<PotatoPeeler.WasteEnforcer>();
                if (we != null && p != null) we.OnRuined(p);
            }
            catch (Exception e) { Plugin.L.LogWarning($"[clean] ruin: {e.Message}"); }
            BodySync.HostRemove(nid); // the client's copy shattered: no ghost left hanging over the table
            Ruins++;
            Last = $"slot {from.Slot} ruined a potato";
        }
        Plugin.L.LogMessage($"[clean] {Last}");
    }

    // ---------------- water and enforcers (host only) ----------------

    [HarmonyPatch(typeof(PotatoPeeler.WaterChannelFlow), "FixedUpdate")]
    static class ChannelGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.StillWaterVolume), "FixedUpdate")]
    static class StillWaterGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.WasteEnforcer), nameof(PotatoPeeler.WasteEnforcer.OnRuined))]
    static class WasteGate { static bool Prefix() => !MpRuntime.IsClient; }

    // ---------------- mop bucket water: one bucket, everyone's rinsing dirties it ----------------

    static bool _bucketApplying;
    public static long BucketOut, BucketIn;

    static void SendBucket(byte op, float amount)
    {
        if (_bucketApplying || !MpRuntime.Active) return;
        var w = new NetWriter(); w.U8(op); w.F32(amount);
        if (MpRuntime.Host is { } h) h.Broadcast(MsgId.BucketDirt, w.Span);
        else if (MpRuntime.Client is { State: ClientState.Playing } c) c.Send(MsgId.BucketDirt, w.Span);
        BucketOut++;
    }

    [HarmonyPatch(typeof(PotatoPeeler.MopBucket), nameof(PotatoPeeler.MopBucket.AddDirt))]
    static class BucketDirtPatch { static void Postfix(float amount) => SendBucket(0, amount); }

    [HarmonyPatch(typeof(PotatoPeeler.MopBucket), nameof(PotatoPeeler.MopBucket.ResetWater))]
    static class BucketResetPatch { static void Postfix() => SendBucket(1, 0); }

    static void ApplyBucket(ref NetReader r)
    {
        byte op = r.U8(); float amount = r.F32();
        var b = PotatoPeeler.MopBucket.Active ?? UnityEngine.Object.FindObjectOfType<PotatoPeeler.MopBucket>();
        if (b == null) return;
        _bucketApplying = true;
        try { if (op == 0) b.AddDirt(amount); else b.ResetWater(); BucketIn++; }
        finally { _bucketApplying = false; }
    }

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if ((byte)id < 0x80 || (byte)id > 0x8F) return false;
        try
        {
            var r = new NetReader(payload);
            if (id == MsgId.MopStrokes)
            {
                ApplyStroke(ref r);
                if (from != null && MpRuntime.Host is { } h) h.Broadcast(MsgId.MopStrokes, payload, exceptSlot: from.Slot);
            }
            else if (id == MsgId.CleanupState && from == null) ApplyState(ref r);
            else if (id == MsgId.BucketDirt)
            {
                ApplyBucket(ref r);
                if (from != null && MpRuntime.Host is { } h2) h2.Broadcast(MsgId.BucketDirt, payload, exceptSlot: from.Slot);
            }
            else if (id == MsgId.CleanupEvent && from != null && MpRuntime.IsHost) OnEvent(from, ref r);
        }
        catch (WireFormatException) { }
        catch (Exception e) { Plugin.L.LogWarning($"[clean] {id}: {e.Message}"); }
        return true;
    }

    public static object Describe()
    {
        var d = PotatoPeeler.CleanupDuty.Instance;
        return new
        {
            StrokesOut, StrokesIn, StateOut, StateIn, Income, Ruins, last = Last,
            BucketOut, BucketIn, bucketDirt = (PotatoPeeler.MopBucket.Active ?? UnityEngine.Object.FindObjectOfType<PotatoPeeler.MopBucket>())?.Dirt,
            active = d?._IsActive_k__BackingField, completed = d?._completed, dirtiness = SafeDirt(d),
            growing = d?.dust?._Growing_k__BackingField, startLock = PotatoPeeler.PeelSession.Instance?._startLock,
        };
    }

    static float? SafeDirt(PotatoPeeler.CleanupDuty? d) { try { return d?.Dirtiness; } catch { return null; } }
}
