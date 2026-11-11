using HarmonyLib;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Story part 2 (spec 09 §7-8), first cut.
/// - Choices: whoever opens a story choice decides it. A client's pick goes to the host (ChoiceIntent 0x68), which resolves it
///   (money, upgrades, faction score) and sends the full decision list (ChoiceResolved 0x69); every peer re-imports it.
/// - Pile reveal cinematic: started by the host only; clients play it when the host does (Cinematic 0x6A).
/// - Puppy: simulated by the host (it follows whichever player is nearest); clients get its pose and animation (ActorState
///   0x6C / ActorEvent 0x6D). Feeding and petting from a client go to the host (ActorIntent 0x6E).
/// - Villager crates: the host runs the chain; the crate set-down/retire moments replay on clients; client crate outcomes
///   (send/refuse/letter answer) go to the host.
/// Not yet: the "someone is deciding" mirror panel, choice locks/timeouts, PA line relay, the boss, gathers.
/// </summary>
internal static class StoryActors
{
    [ThreadStatic] static bool _replaying;
    static double _nextPuppy;
    static Vector3 _puppyPos; static Quaternion _puppyRot = Quaternion.identity; static bool _puppyHave;
    public static long Choices, Cinematics, PuppyIn, Intents;
    public static string Last = "";

    const byte IntentFeed = 1, IntentPet = 2, IntentCrateSent = 3, IntentCrateRefused = 4, IntentCrateLetter = 5;
    const byte EvPuppyAnim = 1, EvCrateDown = 2, EvCrateRetire = 3;

    static PotatoPeeler.StoryChoiceDefinition? FindChoice(string id)
    {
        foreach (var o in Resources.FindObjectsOfTypeAll<PotatoPeeler.StoryChoiceDefinition>()) if (o != null && o.id == id) return o;
        return null;
    }

    static PotatoPeeler.VillagerRequestDefinition? FindRequest(string name)
    {
        foreach (var o in Resources.FindObjectsOfTypeAll<PotatoPeeler.VillagerRequestDefinition>()) if (o != null && o.name == name) return o;
        return null;
    }

    static void SendIntent(MsgId id, Action<NetWriter> body)
    {
        if (MpRuntime.Client is not { State: ClientState.Playing } c) return;
        var w = new NetWriter(); body(w); c.Send(id, w.Span); Intents++;
    }

    // ---------------- choices ----------------

    [HarmonyPatch(typeof(PotatoPeeler.Allegiance), nameof(PotatoPeeler.Allegiance.Resolve))]
    static class ResolvePatch
    {
        static bool Prefix(PotatoPeeler.StoryChoiceDefinition choice, int optionIndex, Vector3 rewardAt, ref int __result)
        {
            if (!MpRuntime.IsClient || _replaying || choice == null) return true;
            SendIntent(MsgId.ChoiceIntent, w => { w.Str8(choice.id ?? ""); w.U8((byte)optionIndex); w.F32(rewardAt.x); w.F32(rewardAt.y); w.F32(rewardAt.z); });
            __result = optionIndex; // the local flow (reply, fly-out) goes on; the host's result arrives as the decision list
            Last = $"asked the host to decide {choice.id} = {optionIndex}";
            return false;
        }

        static void Postfix(PotatoPeeler.StoryChoiceDefinition choice)
        {
            if (MpRuntime.IsHost && choice != null) BroadcastDecisions();
        }
    }

    static void BroadcastDecisions()
    {
        if (MpRuntime.Host is not { } h) return;
        try
        {
            // Read the game's own tables (StoryChoiceRecord structs built by mod code crash Import on a client).
            var choices = PotatoPeeler.Allegiance.s_choices;
            var factions = PotatoPeeler.Allegiance.s_factions;
            var w = new NetWriter();
            w.I32(PotatoPeeler.Allegiance._MinistryScore_k__BackingField);
            w.I32(PotatoPeeler.Allegiance._ResistanceScore_k__BackingField);
            int n = choices?.Count ?? 0;
            w.U8((byte)Math.Min(n, 200));
            int k = 0;
            if (choices != null)
                foreach (var kv in choices)
                {
                    if (k++ >= 200) break;
                    byte f = 0;
                    if (factions != null && factions.ContainsKey(kv.Key)) f = (byte)factions[kv.Key];
                    w.Str8(kv.Key ?? ""); w.U8(kv.Value); w.U8(f);
                }
            h.Broadcast(MsgId.ChoiceResolved, w.Span);
            Choices++;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[story] decisions: {e.Message}"); }
    }

    public static void OnPlayerJoined() => BroadcastDecisions();

    static void ApplyDecisions(ref NetReader r)
    {
        int ministry = r.I32(), resistance = r.I32();
        int n = r.U8();
        var choices = PotatoPeeler.Allegiance.s_choices;
        var factions = PotatoPeeler.Allegiance.s_factions;
        if (choices == null || factions == null) return;
        choices.Clear(); factions.Clear();
        for (int i = 0; i < n; i++)
        {
            var id = r.Str8(); byte option = r.U8(); byte f = r.U8();
            choices[id] = option;
            factions[id] = (PotatoPeeler.Faction)f;
        }
        PotatoPeeler.Allegiance._MinistryScore_k__BackingField = ministry;
        PotatoPeeler.Allegiance._ResistanceScore_k__BackingField = resistance;
        Choices++;
        Last = $"{n} story decisions from the host";
    }

    // ---------------- pile reveal cinematic ----------------

    [HarmonyPatch(typeof(PotatoPeeler.PileRevealCinematic), nameof(PotatoPeeler.PileRevealCinematic.Play))]
    static class RevealCinematic
    {
        static bool Prefix() => !MpRuntime.IsClient || _replaying;
        static void Postfix()
        {
            if (!MpRuntime.IsHost || MpRuntime.Host is not { } h) return;
            var w = new NetWriter(); w.U8(1 /*Start*/); w.U8(1 /*PileReveal*/);
            h.Broadcast(MsgId.Cinematic, w.Span);
            Cinematics++;
        }
    }

    static void ApplyCinematic(ref NetReader r)
    {
        byte op = r.U8(), kind = r.U8();
        if (op != 1 || kind != 1) return;
        var cin = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PileRevealCinematic>();
        if (cin == null) return;
        _replaying = true;
        try { cin.Play(); } finally { _replaying = false; }
        Cinematics++;
        Last = "pile reveal cinematic";
    }

    // ---------------- puppy ----------------

    [HarmonyPatch(typeof(PotatoPeeler.PuppyCompanion), "Update")]
    static class PuppyUpdateGate
    {
        static bool Prefix(PotatoPeeler.PuppyCompanion __instance)
        {
            if (!MpRuntime.IsClient) return true;
            try
            {
                if (__instance.agent != null && __instance.agent.enabled) __instance.agent.enabled = false;
                if (_puppyHave)
                {
                    var t = __instance.transform;
                    t.SetPositionAndRotation(Vector3.Lerp(t.position, _puppyPos, 0.2f), Quaternion.Slerp(t.rotation, _puppyRot, 0.2f));
                }
            }
            catch { }
            return false;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.PuppyCompanion), "LateUpdate")] static class PuppyLateGate { static bool Prefix() => !MpRuntime.IsClient; }
    [HarmonyPatch(typeof(PotatoPeeler.PuppyCompanion), "TickLost")] static class PuppyLostGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.PuppyCompanion), nameof(PotatoPeeler.PuppyCompanion.Play))]
    static class PuppyAnim
    {
        static void Postfix(string state, float fade)
        {
            if (!MpRuntime.IsHost || MpRuntime.Host is not { } h || state == null) return;
            var w = new NetWriter(); w.U8(EvPuppyAnim); w.Str8(state); w.F32(fade);
            h.Broadcast(MsgId.ActorEvent, w.Span);
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.PuppyCompanion), nameof(PotatoPeeler.PuppyCompanion.Feed))]
    static class PuppyFeed { static bool Prefix() { if (!MpRuntime.IsClient || _replaying) return true; SendIntent(MsgId.ActorIntent, w => w.U8(IntentFeed)); return false; } }

    [HarmonyPatch(typeof(PotatoPeeler.PuppyCompanion), nameof(PotatoPeeler.PuppyCompanion.Pet))]
    static class PuppyPet { static bool Prefix() { if (!MpRuntime.IsClient || _replaying) return true; SendIntent(MsgId.ActorIntent, w => w.U8(IntentPet)); return false; } }

    /// <summary>Host: the puppy's "player" is whichever player is nearest (host or remote avatar).</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PuppyCompanion), "get_PlayerTransform")]
    static class PuppyNearest
    {
        static void Postfix(PotatoPeeler.PuppyCompanion __instance, ref Transform __result)
        {
            if (!MpRuntime.IsHost || __result == null) return;
            try
            {
                var me = __instance.transform.position;
                float best = (__result.position - me).sqrMagnitude;
                foreach (var t in MpRuntime.RemoteRoots())
                {
                    float d = (t.position - me).sqrMagnitude;
                    if (d < best) { best = d; __result = t; }
                }
            }
            catch { }
        }
    }

    // ---------------- villager crates ----------------

    [HarmonyPatch(typeof(PotatoPeeler.VillagerCrateEvent), "Update")] static class VillagerUpdateGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.VillagerCrateEvent), nameof(PotatoPeeler.VillagerCrateEvent.SetDown))]
    static class CrateDown
    {
        static bool Prefix() => !MpRuntime.IsClient || _replaying;
        static void Postfix(PotatoPeeler.VillagerRequestDefinition def, bool announce, bool __result)
        {
            if (!MpRuntime.IsHost || !__result || def == null || MpRuntime.Host is not { } h) return;
            var w = new NetWriter(); w.U8(EvCrateDown); w.Str8(def.name ?? ""); w.Bool(announce);
            h.Broadcast(MsgId.ActorEvent, w.Span);
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.VillagerCrateEvent), nameof(PotatoPeeler.VillagerCrateEvent.Retire))]
    static class CrateRetire
    {
        static bool Prefix() => !MpRuntime.IsClient || _replaying;
        static void Postfix(bool immediately)
        {
            if (!MpRuntime.IsHost || MpRuntime.Host is not { } h) return;
            var w = new NetWriter(); w.U8(EvCrateRetire); w.Bool(immediately);
            h.Broadcast(MsgId.ActorEvent, w.Span);
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.VillagerCrateEvent), nameof(PotatoPeeler.VillagerCrateEvent.HandleSent))]
    static class CrateSent { static bool Prefix() { if (!MpRuntime.IsClient || _replaying) return true; SendIntent(MsgId.ActorIntent, w => w.U8(IntentCrateSent)); return false; } }

    [HarmonyPatch(typeof(PotatoPeeler.VillagerCrateEvent), nameof(PotatoPeeler.VillagerCrateEvent.HandleRefused))]
    static class CrateRefused { static bool Prefix() { if (!MpRuntime.IsClient || _replaying) return true; SendIntent(MsgId.ActorIntent, w => w.U8(IntentCrateRefused)); return false; } }

    [HarmonyPatch(typeof(PotatoPeeler.VillagerCrateEvent), nameof(PotatoPeeler.VillagerCrateEvent.HandleLetterChoice))]
    static class CrateLetter
    {
        static bool Prefix(int option)
        {
            if (!MpRuntime.IsClient || _replaying) return true;
            SendIntent(MsgId.ActorIntent, w => { w.U8(IntentCrateLetter); w.U8((byte)option); });
            return false;
        }
    }

    static PotatoPeeler.VillagerCrate? AnyCrate() => UnityEngine.Object.FindObjectOfType<PotatoPeeler.VillagerCrate>();

    // ---------------- host poll + routing ----------------

    public static void UpdateHost(HostSession host)
    {
        var now = Time.realtimeSinceStartupAsDouble;
        if (now < _nextPuppy) return;
        _nextPuppy = now + 0.1;
        try
        {
            var p = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PuppyCompanion>();
            if (p == null) return;
            var t = p.transform;
            var w = new NetWriter();
            w.F32(t.position.x); w.F32(t.position.y); w.F32(t.position.z);
            w.F32(t.rotation.x); w.F32(t.rotation.y); w.F32(t.rotation.z); w.F32(t.rotation.w);
            w.Bool(p.gameObject.activeInHierarchy);
            host.Broadcast(MsgId.ActorState, w.Span, delivery: Core.Net.Delivery.Unreliable);
        }
        catch { _nextPuppy = now + 5; }
    }

    static void OnIntent(PlayerInfo from, ref NetReader r)
    {
        byte op = r.U8();
        var interactor = UnityEngine.Object.FindObjectOfType<PotatoPeeler.Interactor>();
        _replaying = true;
        try
        {
            switch (op)
            {
                case IntentFeed: UnityEngine.Object.FindObjectOfType<PotatoPeeler.PuppyCompanion>()?.Feed(interactor); break;
                case IntentPet: UnityEngine.Object.FindObjectOfType<PotatoPeeler.PuppyCompanion>()?.Pet(interactor); break;
                case IntentCrateSent: if (AnyCrate() is { } c1) UnityEngine.Object.FindObjectOfType<PotatoPeeler.VillagerCrateEvent>()?.HandleSent(c1); break;
                case IntentCrateRefused: if (AnyCrate() is { } c2) UnityEngine.Object.FindObjectOfType<PotatoPeeler.VillagerCrateEvent>()?.HandleRefused(c2); break;
                case IntentCrateLetter: { int opt = r.U8(); if (AnyCrate() is { } c3) UnityEngine.Object.FindObjectOfType<PotatoPeeler.VillagerCrateEvent>()?.HandleLetterChoice(c3, opt); break; }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[story] intent {op}: {e.Message}"); }
        finally { _replaying = false; }
        Last = $"slot {from.Slot} intent {op}";
        Plugin.L.LogMessage($"[story] {Last}");
    }

    static void OnChoiceIntent(PlayerInfo from, ref NetReader r)
    {
        var id = r.Str8(); int option = r.U8(); var at = new Vector3(r.F32(), r.F32(), r.F32());
        var def = FindChoice(id);
        if (def == null) { Plugin.L.LogWarning($"[story] unknown choice {id}"); return; }
        if (!PotatoPeeler.Allegiance.HasDecided(id)) PotatoPeeler.Allegiance.Resolve(def, option, at); // postfix broadcasts
        else BroadcastDecisions();
        Last = $"slot {from.Slot} decided {id} = {option}";
        Plugin.L.LogMessage($"[story] {Last}");
    }

    static void ApplyEvent(ref NetReader r)
    {
        byte ev = r.U8();
        _replaying = true;
        try
        {
            if (ev == EvPuppyAnim)
            {
                var state = r.Str8(); float fade = r.F32();
                UnityEngine.Object.FindObjectOfType<PotatoPeeler.PuppyCompanion>()?.Play(state, fade);
            }
            else if (ev == EvCrateDown)
            {
                var name = r.Str8(); bool announce = r.Bool();
                var def = FindRequest(name);
                if (def != null) UnityEngine.Object.FindObjectOfType<PotatoPeeler.VillagerCrateEvent>()?.SetDown(def, announce);
            }
            else if (ev == EvCrateRetire)
            {
                bool imm = r.Bool();
                UnityEngine.Object.FindObjectOfType<PotatoPeeler.VillagerCrateEvent>()?.Retire(imm);
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[story] event {ev}: {e.Message}"); }
        finally { _replaying = false; }
    }

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if (id is not (MsgId.ChoiceIntent or MsgId.ChoiceResolved or MsgId.Cinematic or MsgId.ActorState or MsgId.ActorEvent or MsgId.ActorIntent)) return false;
        try
        {
            var r = new NetReader(payload);
            if (from != null && MpRuntime.IsHost)
            {
                if (id == MsgId.ChoiceIntent) OnChoiceIntent(from, ref r);
                else if (id == MsgId.ActorIntent) OnIntent(from, ref r);
            }
            else if (from == null)
            {
                if (id == MsgId.ChoiceResolved) ApplyDecisions(ref r);
                else if (id == MsgId.Cinematic) ApplyCinematic(ref r);
                else if (id == MsgId.ActorEvent) ApplyEvent(ref r);
                else if (id == MsgId.ActorState)
                {
                    _puppyPos = new Vector3(r.F32(), r.F32(), r.F32()); _puppyRot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
                    bool active = r.Bool(); _puppyHave = true; PuppyIn++;
                    var p = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PuppyCompanion>(true);
                    if (p != null && p.gameObject.activeSelf != active) p.gameObject.SetActive(active);
                }
            }
        }
        catch (WireFormatException) { }
        catch (Exception e) { Plugin.L.LogWarning($"[story] {id}: {e.Message}"); }
        return true;
    }

    public static object Describe()
    {
        var p = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PuppyCompanion>();
        return new
        {
            Choices, Cinematics, PuppyIn, Intents, last = Last,
            ministry = PotatoPeeler.Allegiance.MinistryScore, resistance = PotatoPeeler.Allegiance.ResistanceScore,
            puppy = p != null ? Dev.Reflect.Describe(p.transform.position) : null,
        };
    }
}
