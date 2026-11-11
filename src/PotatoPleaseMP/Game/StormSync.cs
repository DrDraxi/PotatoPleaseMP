using HarmonyLib;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Snowstorm, frost, snow and the flamethrower (spec 12), first cut.
/// - The host decides every stage (onset, strike, reveal, thaw-all, event end, melt) and sends StormStage (0x90); clients
///   run the same stage with the game's own code inside an allow-scope, so fades, sounds, the storm look, the yard freeze
///   and the snow field build happen on every machine. Outside that scope a client never starts a stage itself (its
///   peel-count trigger, door callbacks and event ticks are skipped).
/// - Event progress (requisition / melt / thaw steps, frozen counts) goes as StormProgress (0x91) at 4 Hz on change.
/// - The host does not lock the storm door while a remote player is still outside (StormGate), with a 45 s cap.
/// - Flamethrower: each player fires their own; the snow carves and thaws of the local shooter's ticks go out as FlameOps
///   (0x97, client -> host -> others) and are applied with the game's own Carve/Thaw. Each player's body temperature stays
///   local (warmth from a flame counts only for its shooter, as in the game).
/// Not yet: exact pile cut/frost bytes from the host, digests, remote flame visuals, mid-storm join state.
/// </summary>
internal static class StormSync
{
    const byte Onset = 1, Strike = 2, Reveal = 3, ThawAll = 4, EventEnd = 5, Melt = 6;
    [ThreadStatic] static byte _allow;
    [ThreadStatic] static bool _applyingNet;
    [ThreadStatic] static bool _inTick;
    static readonly List<(byte kind, Vector3 c, float r, float a)> _tickOps = new();
    static double _next, _gateSince = -1;
    static byte[] _lastProgress = Array.Empty<byte>();
    public static long Stages, OpsOut, OpsIn;
    public static string Last = "";
    public static float GateTimeout = 45f;

    static bool ClientBlocked(byte stage) => MpRuntime.IsClient && _allow != stage && _allow != 0xFF;

    static void SendStage(byte stage, byte arg = 0)
    {
        if (MpRuntime.Host is not { } h) return;
        var w = new NetWriter(); w.U8(stage); w.U8(arg);
        h.Broadcast(MsgId.StormStage, w.Span);
        Stages++;
        Last = $"stage {stage}";
        Plugin.L.LogMessage($"[storm] host stage {stage}");
    }

    static void RunStage(byte stage, byte arg)
    {
        var s = PotatoPeeler.SnowStorm.Instance;
        if (s == null) return;
        _allow = stage;
        try
        {
            switch (stage)
            {
                case Onset: s._armed = true; s.BeginOnset(); break;
                case Strike: s.Strike(false); break;
                case Reveal: s.Reveal(); break;
                case ThawAll: s.ThawAll(); break;
                case EventEnd: s.EndEvent(arg != 0); break;
                case Melt: s.Melt(); break;
            }
            Last = $"ran stage {stage}";
        }
        catch (Exception e) { Plugin.L.LogWarning($"[storm] stage {stage}: {e.Message}"); }
        finally { _allow = 0; }
        Stages++;
    }

    // ---------------- stage gates ----------------

    [HarmonyPatch(typeof(PotatoPeeler.SnowStorm), nameof(PotatoPeeler.SnowStorm.OnCountReached))]
    static class TriggerGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.SnowStorm), nameof(PotatoPeeler.SnowStorm.BeginOnset))]
    static class OnsetPatch
    {
        static bool Prefix() => !ClientBlocked(Onset);
        static void Postfix(PotatoPeeler.SnowStorm __instance) { if (MpRuntime.IsHost && __instance._onsetRunning) SendStage(Onset); }
    }

    [HarmonyPatch(typeof(PotatoPeeler.SnowStorm), nameof(PotatoPeeler.SnowStorm.Strike))]
    static class StrikePatch
    {
        static bool Prefix(bool fromLoad)
        {
            if (MpRuntime.IsHost && !fromLoad) SendStage(Strike); // before the game code, so client fades start now
            return fromLoad || !ClientBlocked(Strike);
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.SnowStorm), nameof(PotatoPeeler.SnowStorm.Reveal))]
    static class RevealPatch
    {
        static bool Prefix() => !ClientBlocked(Reveal);
        static void Postfix() { if (MpRuntime.IsHost) SendStage(Reveal); }
    }

    [HarmonyPatch(typeof(PotatoPeeler.SnowStorm), nameof(PotatoPeeler.SnowStorm.ThawAll))]
    static class ThawAllPatch
    {
        static bool Prefix() => !ClientBlocked(ThawAll);
        static void Postfix() { if (MpRuntime.IsHost) SendStage(ThawAll); }
    }

    [HarmonyPatch(typeof(PotatoPeeler.SnowStorm), nameof(PotatoPeeler.SnowStorm.EndEvent))]
    static class EndEventPatch
    {
        static bool Prefix() => !ClientBlocked(EventEnd);
        static void Postfix(bool finished) { if (MpRuntime.IsHost) SendStage(EventEnd, finished ? (byte)1 : (byte)0); }
    }

    [HarmonyPatch(typeof(PotatoPeeler.SnowStorm), nameof(PotatoPeeler.SnowStorm.Melt))]
    static class MeltPatch
    {
        static bool Prefix() => !ClientBlocked(Melt);
        static void Postfix() { if (MpRuntime.IsHost) SendStage(Melt); }
    }

    [HarmonyPatch(typeof(PotatoPeeler.SnowStorm), nameof(PotatoPeeler.SnowStorm.ReportRevealFlag))]
    static class RevealFlagGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.SnowStorm), nameof(PotatoPeeler.SnowStorm.ReportThawedFlag))]
    static class ThawedFlagGate { static bool Prefix() => !MpRuntime.IsClient; }

    /// <summary>Client: event steps come from the host.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.SnowStorm), nameof(PotatoPeeler.SnowStorm.UpdateEvent))]
    static class UpdateEventGate { static bool Prefix() => !MpRuntime.IsClient; }

    /// <summary>Host: the storm door waits for every player to be inside (capped, so a session always moves on).</summary>
    [HarmonyPatch(typeof(PotatoPeeler.SnowStorm), "get_PlayerInside")]
    static class InsideGate
    {
        static void Postfix(PotatoPeeler.SnowStorm __instance, ref bool __result)
        {
            if (!MpRuntime.IsHost || !__result || __instance._Struck_k__BackingField) return;
            try
            {
                bool allIn = MpRuntime.RemoteFeet().All(p => !__instance.IsOutside(p));
                var now = Time.realtimeSinceStartupAsDouble;
                if (allIn) { _gateSince = -1; return; }
                if (_gateSince < 0) _gateSince = now;
                if (now - _gateSince < GateTimeout) __result = false;
            }
            catch { }
        }
    }

    // ---------------- flamethrower ops ----------------

    [HarmonyPatch(typeof(PotatoPeeler.Flamethrower), "Tick")]
    static class TickScope
    {
        static void Prefix() { _inTick = true; _tickOps.Clear(); _tickNormals.Clear(); }
        static void Postfix()
        {
            _inTick = false;
            if (_tickOps.Count == 0 || !MpRuntime.Active) return;
            try
            {
                var w = new NetWriter();
                w.U8((byte)Math.Min(_tickOps.Count, 60));
                for (int i = 0; i < _tickOps.Count && i < 60; i++)
                {
                    var (k, c, rad, a) = _tickOps[i];
                    w.U8(k); w.F32(c.x); w.F32(c.y); w.F32(c.z); w.F32(rad); w.F32(a);
                    if (k == 3) { var nn = _tickNormals[i]; w.F32(nn.x); w.F32(nn.y); w.F32(nn.z); }
                }
                if (MpRuntime.Host is { } h) h.Broadcast(MsgId.FlameOps, w.Span);
                else if (MpRuntime.Client is { State: ClientState.Playing } cl) cl.Send(MsgId.FlameOps, w.Span);
                OpsOut += _tickOps.Count;
            }
            catch (Exception e) { Plugin.L.LogWarning($"[storm] flame ops: {e.Message}"); }
            _tickOps.Clear(); _tickNormals.Clear();
        }
        static Exception? Finalizer(Exception? __exception) { _inTick = false; return __exception; }
    }

    static readonly Dictionary<int, Vector3> _tickNormals = new();
    public static long ScorchIn;

    /// <summary>Scorch marks of our flame go out with the tick's ops, so everyone sees the same burnt floor.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.ScorchMarks), nameof(PotatoPeeler.ScorchMarks.Add))]
    static class ScorchCapture
    {
        static void Postfix(Vector3 point, Vector3 normal, float radius, float amount)
        {
            if (!_inTick || _applyingNet) return;
            _tickNormals[_tickOps.Count] = normal;
            _tickOps.Add((3, point, radius, amount));
        }
    }

    static PotatoPeeler.ScorchMarks? Scorch()
    {
        try
        {
            var s = PotatoPeeler.Flamethrower.s_scorch;
            if (s != null) return s;
            foreach (var f in Resources.FindObjectsOfTypeAll<PotatoPeeler.Flamethrower>()) if (f != null) return f.Scorch();
        }
        catch { }
        return null;
    }

    [HarmonyPatch(typeof(PotatoPeeler.SnowField), nameof(PotatoPeeler.SnowField.Carve))]
    static class CarveCapture
    {
        static void Postfix(Vector3 worldCenter, float radius, bool log)
        {
            if (_inTick && !_applyingNet && log) _tickOps.Add((1, worldCenter, radius, 0));
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.SnowStorm), nameof(PotatoPeeler.SnowStorm.Thaw))]
    static class ThawCapture
    {
        static void Postfix(Vector3 center, float radius, float amount)
        {
            if (_inTick && !_applyingNet) _tickOps.Add((2, center, radius, amount));
        }
    }

    static void ApplyOps(ref NetReader r)
    {
        int n = r.U8();
        var storm = PotatoPeeler.SnowStorm.Instance;
        var field = UnityEngine.Object.FindObjectOfType<PotatoPeeler.SnowField>();
        _applyingNet = true;
        try
        {
            bool carved = false;
            for (int i = 0; i < n; i++)
            {
                byte k = r.U8(); var c = new Vector3(r.F32(), r.F32(), r.F32()); float rad = r.F32(), a = r.F32();
                if (k == 3) { var nrm = new Vector3(r.F32(), r.F32(), r.F32()); Scorch()?.Add(c, nrm, rad, a); ScorchIn++; }
                else if (k == 1 && field != null) { field.Carve(c, rad, false, true); carved = true; }
                else if (k == 2 && storm != null) storm.Thaw(c, rad, a);
                OpsIn++;
            }
            if (carved) field!.Remesh();
        }
        catch (Exception e) { Plugin.L.LogWarning($"[storm] apply ops: {e.Message}"); }
        finally { _applyingNet = false; }
    }

    // ---------------- progress ----------------

    public static void UpdateHost(HostSession host)
    {
        var now = Time.realtimeSinceStartupAsDouble;
        if (now < _next) return;
        _next = now + 0.25;
        try
        {
            var s = PotatoPeeler.SnowStorm.Instance;
            if (s == null) return;
            var w = new NetWriter();
            w.I32(s._eventStep); w.I32(s._frozenNow); w.I32(s._frozenPeak);
            w.U8((byte)((s._Struck_k__BackingField ? 1 : 0) | (s._revealed ? 2 : 0) | (s._cleared ? 4 : 0) | (s._onsetRunning ? 8 : 0)));
            var b = w.Span.ToArray();
            if (b.AsSpan().SequenceEqual(_lastProgress)) return;
            _lastProgress = b;
            host.Broadcast(MsgId.StormProgress, b);
        }
        catch (Exception e) { Plugin.L.LogWarning($"[storm] poll: {e.Message}"); _next = now + 5; }
    }

    public static void OnPlayerJoined() => _lastProgress = Array.Empty<byte>();
    public static void ResetSession() { _lastProgress = Array.Empty<byte>(); _gateSince = -1; }

    /// <summary>A client restoring the storm from its save (post-storm join) runs every stage the restore needs.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.SnowStorm), nameof(PotatoPeeler.SnowStorm.Restore))]
    static class RestoreScope
    {
        static void Prefix(out byte __state) { __state = _allow; _allow = 0xFF; }
        static void Postfix(byte __state) => _allow = __state;
    }

    static void ApplyProgress(ref NetReader r)
    {
        var s = PotatoPeeler.SnowStorm.Instance;
        int step = r.I32(), now = r.I32(), peak = r.I32(); r.U8();
        if (s == null) return;
        s._eventStep = step; s._frozenNow = now; s._frozenPeak = peak;
        s._nextFrozenPoll = float.MaxValue;
    }

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if ((byte)id < 0x90 || (byte)id > 0x9F) return false;
        try
        {
            var r = new NetReader(payload);
            if (id == MsgId.StormStage && from == null) { byte st = r.U8(), arg = r.U8(); RunStage(st, arg); }
            else if (id == MsgId.StormProgress && from == null) ApplyProgress(ref r);
            else if (id == MsgId.PotatoFrost && from == null) BodySync.ApplyFrost(payload);
            else if (id == MsgId.FlameState) { FlameView.Apply(ref r, from); if (from != null && MpRuntime.Host is { } h2) h2.Broadcast(MsgId.FlameState, payload, exceptSlot: from.Slot); }
            else if (id == MsgId.FlameOps)
            {
                ApplyOps(ref r);
                if (from != null && MpRuntime.Host is { } h) h.Broadcast(MsgId.FlameOps, payload, exceptSlot: from.Slot);
            }
        }
        catch (WireFormatException) { }
        catch (Exception e) { Plugin.L.LogWarning($"[storm] {id}: {e.Message}"); }
        return true;
    }

    public static object Describe()
    {
        var s = PotatoPeeler.SnowStorm.Instance;
        return new
        {
            Stages, OpsOut, OpsIn, ScorchIn, flames = FlameView.Describe(), last = Last,
            struck = s?._Struck_k__BackingField, onset = s?._onsetRunning, revealed = s?._revealed, step = s?._eventStep,
            frozenNow = s?._frozenNow,
        };
    }
}
