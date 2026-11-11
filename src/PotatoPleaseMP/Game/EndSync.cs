using HarmonyLib;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// End game (spec 19), first cut.
/// - Stage (waiting for quota, find the potato, find the door, inside, done) is the host's: clients skip the director's Update
///   and apply the host's stage (EndState Stage, 0x6C).
/// - Bunker door: anyone can open it. A client's use goes to the host (EndIntent BunkerLock, 0x6D); the host opens it for real
///   (key spent once) and sends EnterFinal; every peer then plays its own walk-in and loads the final yard together.
/// - Final yard levers, departure, exit door: a client pull goes to the host, the host does it and every peer replays it.
/// - The ending is decided once on the host (CompleteEnding); clients get the ending + Pavel's fate (EndingResult 0x6E) and show
///   the same ending pages, built from their mirrored state. Clients never seal the save; nobody submits Steam leaderboards
///   from a co-op shift (or from a dev build).
/// Not yet: the gather at the door, boarding wait, crew scoreboard page, per-player outcomes (shot/returned) for the group.
/// </summary>
internal static class EndSync
{
    [ThreadStatic] static bool _authorized;
    static int _lastStage = -1;
    static bool _resultSent;
    public static string Last = "";

    const byte StStage = 1, StEnterFinal = 4, StReplay = 6, StResult = 15, StHostContinue = 20;
    const byte InBunker = 1, InRoute = 4, InDepart = 5, InLever = 6, InExit = 7;

    static PotatoPeeler.EndGameDirector? Director => UnityEngine.Object.FindObjectOfType<PotatoPeeler.EndGameDirector>();
    static PotatoPeeler.Interactor? LocalInteractor => UnityEngine.Object.FindObjectOfType<PotatoPeeler.Interactor>();

    static void Broadcast(Action<NetWriter> body)
    {
        if (MpRuntime.Host is not { } h) return;
        var w = new NetWriter(); body(w); h.Broadcast(MsgId.EndState, w.Span);
    }

    static void Intent(byte op, byte arg = 0)
    {
        if (MpRuntime.Client is not { State: ClientState.Playing } c) return;
        var w = new NetWriter(); w.U8(op); w.U8(arg); c.Send(MsgId.EndIntent, w.Span);
        Last = $"asked the host: end-game intent {op}";
    }

    // ---------------- stage ----------------

    public static void UpdateHost(HostSession host)
    {
        try
        {
            var d = Director;
            if (d == null) return;
            int st = (int)d._stage;
            if (st == _lastStage) return;
            _lastStage = st;
            Broadcast(w => { w.U8(StStage); w.U8((byte)st); });
            Last = $"stage {d._stage}";
        }
        catch { }
    }

    public static void OnPlayerJoined() => _lastStage = -1;
    public static void ResetSession() { _resultSent = false; _lastStage = -1; }

    [HarmonyPatch(typeof(PotatoPeeler.EndGameDirector), "Update")]
    static class DirectorGate { static bool Prefix() => !MpRuntime.IsClient; }

    // ---------------- doors, levers ----------------

    static bool Gate(byte op, byte arg = 0)
    {
        if (_authorized || !MpRuntime.IsClient) return true;
        Intent(op, arg);
        return false;
    }

    static void HostDid(byte op, byte arg = 0)
    {
        if (!MpRuntime.IsHost || _authorized) return;
        Broadcast(w => { w.U8(op == InBunker ? StEnterFinal : StReplay); w.U8(op); w.U8(arg); });
    }

    [HarmonyPatch(typeof(PotatoPeeler.BunkerDoorLock), nameof(PotatoPeeler.BunkerDoorLock.OnInteract))]
    static class Bunker
    {
        static bool Prefix() => Gate(InBunker);
        static void Postfix() => HostDid(InBunker);
    }

    [HarmonyPatch(typeof(PotatoPeeler.TrainRouteLever), nameof(PotatoPeeler.TrainRouteLever.OnInteract))]
    static class Route
    {
        static bool Prefix(PotatoPeeler.TrainRouteLever __instance) => Gate(InRoute, IndexOf(__instance));
        static void Postfix(PotatoPeeler.TrainRouteLever __instance) => HostDid(InRoute, IndexOf(__instance));
    }

    [HarmonyPatch(typeof(PotatoPeeler.TrainDepartControl), nameof(PotatoPeeler.TrainDepartControl.OnInteract))]
    static class Depart
    {
        static bool Prefix() => Gate(InDepart);
        static void Postfix() => HostDid(InDepart);
    }

    [HarmonyPatch(typeof(PotatoPeeler.FinalLever), nameof(PotatoPeeler.FinalLever.OnInteract))]
    static class Lever
    {
        static bool Prefix(PotatoPeeler.FinalLever __instance) => Gate(InLever, IndexOf(__instance));
        static void Postfix(PotatoPeeler.FinalLever __instance) => HostDid(InLever, IndexOf(__instance));
    }

    [HarmonyPatch(typeof(PotatoPeeler.FinalExitDoor), nameof(PotatoPeeler.FinalExitDoor.OnInteract))]
    static class Exit
    {
        static bool Prefix() => Gate(InExit);
        static void Postfix() => HostDid(InExit);
    }

    static byte IndexOf<T>(T obj) where T : Component
    {
        var all = UnityEngine.Object.FindObjectsOfType<T>().OrderBy(o => o.transform.position.x).ThenBy(o => o.transform.position.z).ToList();
        int i = all.FindIndex(o => o.Pointer == obj.Pointer);
        return (byte)Math.Max(0, i);
    }

    static T? ByIndex<T>(int i) where T : Component
    {
        var all = UnityEngine.Object.FindObjectsOfType<T>().OrderBy(o => o.transform.position.x).ThenBy(o => o.transform.position.z).ToList();
        return i < all.Count ? all[i] : null;
    }

    /// <summary>Runs a device interaction for real on this machine (host for a client's intent, client for a host replay).</summary>
    static void Run(byte op, byte arg)
    {
        var who = LocalInteractor;
        _authorized = true;
        try
        {
            switch (op)
            {
                case InBunker:
                    if (MpRuntime.IsHost) UnityEngine.Object.FindObjectOfType<PotatoPeeler.BunkerDoorLock>()?.OnInteract(who);
                    else if (Director is { } d) d.StartCoroutine(d.EnterFinalRoom());
                    break;
                case InRoute: ByIndex<PotatoPeeler.TrainRouteLever>(arg)?.OnInteract(who); break;
                case InDepart: UnityEngine.Object.FindObjectOfType<PotatoPeeler.TrainDepartControl>()?.OnInteract(who); break;
                case InLever: ByIndex<PotatoPeeler.FinalLever>(arg)?.OnInteract(who); break;
                case InExit: UnityEngine.Object.FindObjectOfType<PotatoPeeler.FinalExitDoor>()?.OnInteract(who); break;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[end] run {op}: {e.Message}"); }
        finally { _authorized = false; }
        Last = $"ran end-game step {op}";
    }

    // ---------------- ending ----------------

    [HarmonyPatch(typeof(PotatoPeeler.EndGameDirector), nameof(PotatoPeeler.EndGameDirector.CompleteEnding))]
    static class Complete
    {
        static bool Prefix() => !MpRuntime.IsClient || _authorized;
        static void Postfix(PotatoPeeler.EndGameDirector __instance)
        {
            if (!MpRuntime.IsHost || _resultSent) return;
            _resultSent = true;
            var e = __instance._ending; var p = __instance._pavel;
            Broadcast(w => { w.U8(StResult); w.U8((byte)e); w.U8((byte)p); });
            Last = $"ending {e} (Pavel: {p})";
            Plugin.L.LogMessage($"[end] host ending {e}, Pavel {p}");
        }
    }

    static void ShowEnding(PotatoPeeler.Ending ending, PotatoPeeler.PavelStatus pavel)
    {
        var d = Director;
        if (d != null) { d._ending = ending; d._pavel = pavel; d._endingDone = true; }
        try
        {
            var summary = PotatoPeeler.EndingSummary.Capture(ending, pavel);
            if (d != null) d._LastSummary_k__BackingField = summary;
            _authorized = true;
            try { PotatoPeeler.EndingSequenceUI.Show(summary); } finally { _authorized = false; }
            Last = $"showing ending {ending}";
        }
        catch (Exception e) { Plugin.L.LogWarning($"[end] show: {e.Message}"); }
    }

    [HarmonyPatch(typeof(PotatoPeeler.EndingSequenceUI), nameof(PotatoPeeler.EndingSequenceUI.Show))]
    static class ShowGate { static bool Prefix() => !MpRuntime.IsClient || _authorized; }

    [HarmonyPatch(typeof(PotatoPeeler.SaveManager), nameof(PotatoPeeler.SaveManager.MarkFinished))]
    static class SealGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.Platform.SteamLeaderboards), nameof(PotatoPeeler.Platform.SteamLeaderboards.Submit))]
    static class LeaderboardGate { static bool Prefix() => !MpRuntime.Active && !Dev.AchievementGuard.Active; }

    /// <summary>Host: we reload our finished save (Continue Playing): clients come along by joining again.</summary>
    public static void SendHostContinue() => Broadcast(w => w.U8(StHostContinue));

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if (id != MsgId.EndState && id != MsgId.EndIntent) return false;
        try
        {
            var r = new NetReader(payload);
            if (id == MsgId.EndIntent && from != null && MpRuntime.IsHost)
            {
                byte op = r.U8(), arg = r.U8();
                Run(op, arg);
                Broadcast(w => { w.U8(op == InBunker ? StEnterFinal : StReplay); w.U8(op); w.U8(arg); });
                Plugin.L.LogMessage($"[end] slot {from.Slot} end-game step {op}");
            }
            else if (id == MsgId.EndState && from == null)
            {
                byte st = r.U8();
                if (st == StStage)
                {
                    var stage = (PotatoPeeler.EndGameDirector.Stage)r.U8();
                    var d = Director;
                    if (d != null && d._stage != stage) { _authorized = true; try { d.SetStage(stage, true); } finally { _authorized = false; } }
                    Last = $"stage {stage}";
                }
                else if (st is StEnterFinal or StReplay) { byte op = r.U8(), arg = r.U8(); Run(op, arg); }
                else if (st == StResult) ShowEnding((PotatoPeeler.Ending)r.U8(), (PotatoPeeler.PavelStatus)r.U8());
                else if (st == StHostContinue) EndingChoiceSync.OnHostContinued();
            }
        }
        catch (WireFormatException) { }
        catch (Exception e) { Plugin.L.LogWarning($"[end] {id}: {e.Message}"); }
        return true;
    }

    /// <summary>Dev: send an ending to the clients without finishing the host's shift (tests the client ending pages).</summary>
    public static object DevSendEnding(string ending, string pavel)
    {
        var e = Enum.Parse<PotatoPeeler.Ending>(ending, true); var p = Enum.Parse<PotatoPeeler.PavelStatus>(pavel, true);
        Broadcast(w => { w.U8(StResult); w.U8((byte)e); w.U8((byte)p); });
        return new { sent = e.ToString(), pavel = p.ToString() };
    }

    public static object Describe()
    {
        var d = Director;
        return new { last = Last, stage = d?._stage.ToString(), ending = d?._ending.ToString(), done = d?._endingDone };
    }
}
