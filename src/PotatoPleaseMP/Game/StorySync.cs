using HarmonyLib;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Story and quests (spec 09), first cut: one quest chain for the whole team, run on the host.
/// Host: unchanged game code; its quest state (active page, completed pages, progress/baseline/done) is polled at 4 Hz
/// and sent as QuestState (0x60) when it changes; letters it mails go out as LetterEvent Delivered (0x64).
/// Client: never decides. Quest recompute/completion/rewards, the peel-count timeline, story beats and the mailbox are
/// skipped; quest events its player causes (Bump, ReportFlag, a letter read) go to the host as StoryIntent (0x61).
/// Applying QuestState writes the chain state and raises the game's own quest events, so the objective HUD, gates and
/// pop-ups react like in single player.
/// Not yet: story choices, cinematics, puppy/villager/boss actors, keys and flags, reading locks.
/// </summary>
internal static class StorySync
{
    [ThreadStatic] static bool _replaying;
    static double _next;
    static byte[] _last = Array.Empty<byte>();
    public static long Sent, Applied, Intents, Letters;
    public static string Last = "";
    static bool _letterHooked;

    const byte OpBump = 0, OpFlag = 1, OpLetterRead = 2, OpItemFound = 3, OpZone = 4, OpKeyMark = 5, OpKeyUnmark = 6, OpCrateClaim = 7;
    const byte KdItemAdd = 1, KdItemSpend = 2, KdKeyMark = 3, KdKeyUnmark = 4, KdFlag = 5;

    // ---------------- zones: "reach X" objectives count any player ----------------

    static readonly Dictionary<byte, string> RemoteZones = new();
    static string _lastZoneSent = "";

    /// <summary>Client: report our zone when it changes.</summary>
    public static void UpdateClientZone()
    {
        if (MpRuntime.Client is not { State: ClientState.Playing }) return;
        string z;
        try { z = PotatoPeeler.PlayerZoneTracker.CurrentId ?? ""; } catch { return; }
        if (z == _lastZoneSent) return;
        _lastZoneSent = z;
        SendIntent(w => { w.U8(OpZone); w.Str8(z); });
    }

    public static void OnPlayerLeft(byte slot) => RemoteZones.Remove(slot);

    /// <summary>Host: a ZoneReached objective is met when any player is in that zone.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.QuestManager), nameof(PotatoPeeler.QuestManager.Value))]
    static class ZoneValue
    {
        static void Postfix(PotatoPeeler.QuestObjective objective, ref long __result)
        {
            if (!MpRuntime.IsHost || objective == null || objective.kind != PotatoPeeler.ObjectiveKind.ZoneReached || __result >= 1) return;
            var want = objective.itemId;
            if (!string.IsNullOrEmpty(want) && RemoteZones.Values.Any(z => z == want)) __result = 1;
        }
    }

    // ---------------- story keys and flags ----------------

    static HashSet<string>? _personal;

    /// <summary>The first-use guides (and smoke cravings) are per player; every other done-key is shared story state.</summary>
    static bool IsPersonal(string key)
    {
        if (_personal == null)
        {
            _personal = new HashSet<string>();
            foreach (var pr in typeof(PotatoPeeler.FirstUseGuides).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                if (pr.PropertyType == typeof(string)) try { if (pr.GetValue(null) is string v) _personal.Add(v); } catch { }
        }
        return key.StartsWith("smoke_break.") || _personal.Contains(key);
    }

    [HarmonyPatch(typeof(PotatoPeeler.FirstUseGuides), nameof(PotatoPeeler.FirstUseGuides.MarkDone))]
    static class KeyMark
    {
        static void Postfix(string id)
        {
            if (_replaying || string.IsNullOrEmpty(id) || IsPersonal(id) || !MpRuntime.Active) return;
            if (MpRuntime.Host is { } h) { var w = new NetWriter(); w.U8(KdKeyMark); w.Str8(id); w.I32(0); h.Broadcast(MsgId.KeyDelta, w.Span); }
            else SendIntent(w => { w.U8(OpKeyMark); w.Str8(id); });
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.FirstUseGuides), nameof(PotatoPeeler.FirstUseGuides.Unmark))]
    static class KeyUnmark
    {
        static void Postfix(string id)
        {
            if (_replaying || string.IsNullOrEmpty(id) || IsPersonal(id) || !MpRuntime.Active) return;
            if (MpRuntime.Host is { } h) { var w = new NetWriter(); w.U8(KdKeyUnmark); w.Str8(id); w.I32(0); h.Broadcast(MsgId.KeyDelta, w.Span); }
            else SendIntent(w => { w.U8(OpKeyUnmark); w.Str8(id); });
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.StoryFlags), nameof(PotatoPeeler.StoryFlags.Set))]
    static class FlagSet
    {
        static void Postfix(string flag, bool on)
        {
            if (_replaying || string.IsNullOrEmpty(flag) || !MpRuntime.IsHost || MpRuntime.Host is not { } h) return;
            var w = new NetWriter(); w.U8(KdFlag); w.Str8(flag); w.I32(on ? 1 : 0);
            h.Broadcast(MsgId.KeyDelta, w.Span);
        }
    }

    /// <summary>Host: a joiner gets every shared key and flag (its save copy may be older than our live state).</summary>
    public static void SendKeysTo(byte slot)
    {
        if (MpRuntime.Host is not { } h) return;
        try
        {
            foreach (var k in PotatoPeeler.FirstUseGuides.Export())
                if (!IsPersonal(k)) { var w = new NetWriter(); w.U8(KdKeyMark); w.Str8(k); w.I32(0); h.SendTo(slot, MsgId.KeyDelta, w.Span); }
            var flags = PotatoPeeler.StoryFlags.Set_;
            if (flags != null) foreach (var f in flags) { var w = new NetWriter(); w.U8(KdFlag); w.Str8(f); w.I32(1); h.SendTo(slot, MsgId.KeyDelta, w.Span); }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[story] keys to joiner: {e.Message}"); }
    }
    public static long ItemsIn, ItemsOut;

    static PotatoPeeler.HiddenItemDefinition? FindItem(string id)
    {
        foreach (var o in Resources.FindObjectsOfTypeAll<PotatoPeeler.HiddenItemDefinition>()) if (o != null && o.id == id) return o;
        return null;
    }

    /// <summary>
    /// Hidden items (notes, stones, the bunker key) live in the host's inventory. A client that finds one asks the host to
    /// add it; every add on the host goes to all clients (KeyDelta 0x63, op ItemAdd), so journals and the key agree.
    /// </summary>
    [HarmonyPatch(typeof(PotatoPeeler.HiddenItemInventory), nameof(PotatoPeeler.HiddenItemInventory.Add))]
    static class ItemAdd
    {
        static bool Prefix(PotatoPeeler.HiddenItemDefinition item, int amount)
        {
            if (!MpRuntime.IsClient || _replaying || item == null) return true;
            SendIntent(w => { w.U8(OpItemFound); w.Str8(item.id ?? ""); w.U8((byte)Math.Clamp(amount, 0, 255)); });
            ItemsOut++;
            return false;
        }

        static void Postfix(PotatoPeeler.HiddenItemDefinition item, int amount)
        {
            if (!MpRuntime.IsHost || item == null || MpRuntime.Host is not { } h) return;
            var w = new NetWriter(); w.U8(1 /*ItemAdd*/); w.Str8(item.id ?? ""); w.I32(amount);
            h.Broadcast(MsgId.KeyDelta, w.Span);
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.HiddenItemInventory), nameof(PotatoPeeler.HiddenItemInventory.TrySpend))]
    static class ItemSpend
    {
        static void Postfix(string id, int amount, bool __result)
        {
            if (!MpRuntime.IsHost || !__result || MpRuntime.Host is not { } h) return;
            var w = new NetWriter(); w.U8(2 /*ItemSpend*/); w.Str8(id ?? ""); w.I32(amount);
            h.Broadcast(MsgId.KeyDelta, w.Span);
        }
    }

    static void ApplyItem(ref NetReader r)
    {
        byte op = r.U8(); var id = r.Str8(); int amount = r.I32();
        _replaying = true;
        try
        {
            if (op == KdItemAdd) { var def = FindItem(id); if (def != null) PotatoPeeler.HiddenItemInventory.Add(def, amount); }
            else if (op == KdItemSpend) PotatoPeeler.HiddenItemInventory.TrySpend(id, amount);
            else if (op == KdKeyMark) { if (!PotatoPeeler.FirstUseGuides.IsDone(id)) PotatoPeeler.FirstUseGuides.MarkDone(id); }
            else if (op == KdKeyUnmark) { if (PotatoPeeler.FirstUseGuides.IsDone(id)) PotatoPeeler.FirstUseGuides.Unmark(id); }
            else if (op == KdFlag) PotatoPeeler.StoryFlags.Set(id, amount != 0);
        }
        finally { _replaying = false; }
        ItemsIn++;
        Last = $"item {id} {(op == 1 ? "+" : "-")}{amount}";
    }

    static bool ClientLocked => MpRuntime.IsClient && !_replaying;

    public static void OnPlayerJoined() => _last = Array.Empty<byte>();
    public static void OnPlayerJoined(byte slot) { _last = Array.Empty<byte>(); SendKeysTo(slot); }

    // ---------------- host ----------------

    public static void UpdateHost(HostSession host)
    {
        var now = Time.realtimeSinceStartupAsDouble;
        if (now < _next) return;
        _next = now + 0.25;
        try
        {
            var qm = PotatoPeeler.QuestManager.Instance;
            var main = qm?._main;
            if (main == null) return;
            var w = new NetWriter();
            w.U8((byte)Math.Clamp(main.ActiveIndex, 0, 255));
            uint mask = 0;
            var pages = main.Chain?.pages;
            if (pages != null)
                for (int i = 0; i < pages.Length && i < 32; i++)
                    if (pages[i] != null && main.Completed.Contains(pages[i].id)) mask |= 1u << i;
            w.U32(mask);
            int n = main.Progress?.Length ?? 0;
            w.U8((byte)n);
            for (int i = 0; i < n; i++)
            {
                w.I64(main.Progress![i]);
                w.I64(main.Baseline != null && i < main.Baseline.Length ? main.Baseline[i] : 0);
                w.Bool(main.Done != null && i < main.Done.Length && main.Done[i]);
            }
            var bytes = w.Span.ToArray();
            if (bytes.AsSpan().SequenceEqual(_last)) return;
            _last = bytes;
            host.Broadcast(MsgId.QuestState, bytes);
            Sent++;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[story] poll: {e.Message}"); _next = now + 5; }
    }

    static void OnIntent(PlayerInfo from, ref NetReader r)
    {
        byte op = r.U8();
        var qm = PotatoPeeler.QuestManager.Instance;
        switch (op)
        {
            case OpBump:
            {
                var kind = (PotatoPeeler.ObjectiveKind)r.U8(); long amount = r.I32();
                qm?.Bump(kind, amount);
                Last = $"slot {from.Slot}: {kind} +{amount}";
                break;
            }
            case OpFlag:
            {
                var flag = r.Str8();
                qm?.ReportFlag(flag);
                Last = $"slot {from.Slot}: flag {flag}";
                break;
            }
            case OpCrateClaim:
            {
                var crate = UnityEngine.Object.FindObjectOfType<PotatoPeeler.VegetableCrate>();
                var ev = UnityEngine.Object.FindObjectOfType<PotatoPeeler.VegetableCrateEvent>();
                if (crate != null && ev != null) ev.HandleClaimed(crate);
                Last = $"slot {from.Slot} claimed the vegetable crate";
                break;
            }
            case OpZone:
            {
                var z = r.Str8();
                RemoteZones[from.Slot] = z;
                try { qm?.Recompute(); } catch { }
                Last = $"slot {from.Slot} is in zone {z}";
                break;
            }
            case OpKeyMark:
            {
                var k = r.Str8();
                if (!IsPersonal(k) && !PotatoPeeler.FirstUseGuides.IsDone(k)) PotatoPeeler.FirstUseGuides.MarkDone(k); // postfix tells everyone
                Last = $"slot {from.Slot} marked {k}";
                break;
            }
            case OpKeyUnmark:
            {
                var k = r.Str8();
                if (!IsPersonal(k) && PotatoPeeler.FirstUseGuides.IsDone(k)) PotatoPeeler.FirstUseGuides.Unmark(k);
                break;
            }
            case OpItemFound:
            {
                var id = r.Str8(); int amount = r.U8();
                var def = FindItem(id);
                if (def != null) PotatoPeeler.HiddenItemInventory.Add(def, Math.Max(1, amount)); // postfix tells everyone
                Last = $"slot {from.Slot} found {id}";
                break;
            }
            case OpLetterRead:
            {
                var id = r.Str8(); var q = (PotatoPeeler.LetterQuality)r.U8();
                var def = PotatoPeeler.LetterMailbox.FindAny(id);
                if (def != null && !PotatoPeeler.LetterArchive.IsRead(id)) PotatoPeeler.LetterArchive.MarkRead(def, q);
                Last = $"slot {from.Slot} read letter {id}";
                break;
            }
        }
        Intents++;
        Plugin.L.LogMessage($"[story] {Last}");
        _next = 0;
    }

    // ---------------- client ----------------

    static void SendIntent(Action<NetWriter> body)
    {
        if (MpRuntime.Client is not { State: ClientState.Playing } c) return;
        var w = new NetWriter();
        body(w);
        c.Send(MsgId.StoryIntent, w.Span);
        Intents++;
    }

    static void ApplyQuestState(ref NetReader r)
    {
        var qm = PotatoPeeler.QuestManager.Instance;
        var main = qm?._main;
        if (qm == null || main == null) return;
        int active = r.U8();
        uint mask = r.U32();
        int n = r.U8();
        var prog = new long[n]; var bas = new long[n]; var done = new bool[n];
        for (int i = 0; i < n; i++) { prog[i] = r.I64(); bas[i] = r.I64(); done[i] = r.Bool(); }

        int prevActive = main.ActiveIndex;
        var pages = main.Chain?.pages;
        var prevPage = pages != null && prevActive >= 0 && prevActive < pages.Length ? pages[prevActive] : null;
        bool[] prevDone = new bool[main.Done?.Length ?? 0];
        for (int i = 0; i < prevDone.Length; i++) prevDone[i] = main.Done![i];

        _replaying = true;
        try
        {
            main.ActiveIndex = active;
            if (pages != null)
            {
                main.Completed.Clear();
                for (int i = 0; i < pages.Length && i < 32; i++)
                    if ((mask & (1u << i)) != 0 && pages[i] != null) main.Completed.Add(pages[i].id);
            }
            if (main.Progress == null || main.Progress.Length != n) main.Progress = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<long>(n);
            if (main.Baseline == null || main.Baseline.Length != n) main.Baseline = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<long>(n);
            if (main.Done == null || main.Done.Length != n) main.Done = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<bool>(n);
            for (int i = 0; i < n; i++) { main.Progress[i] = prog[i]; main.Baseline[i] = bas[i]; main.Done[i] = done[i]; }

            var page = pages != null && active < pages.Length ? pages[active] : null;
            if (active == prevActive && page != null)
                for (int i = 0; i < n; i++)
                    if (done[i] && (i >= prevDone.Length || !prevDone[i])) qm.OnObjectiveCompleted?.Invoke(page, i);
            if (active != prevActive)
            {
                // every page finished since the last state, not just the previous one (gates listen for each)
                if (pages != null && active > prevActive + 1)
                    for (int pi = prevActive + 1; pi < active && pi < pages.Length; pi++)
                        if (pages[pi] != null) try { qm.OnQuestCompleted?.Invoke(pages[pi]); } catch { }
                if (prevPage != null) { qm.OnQuestCompleted?.Invoke(prevPage); try { qm.Announce(prevPage); } catch { } }
                qm.OnActiveQuestChanged?.Invoke();
                Last = $"quest page {prevActive} -> {active}";
            }
            qm.OnProgressChanged?.Invoke();
        }
        finally { _replaying = false; }
    }

    static void ApplyLetter(ref NetReader r)
    {
        byte op = r.U8();
        var id = r.Str8();
        bool quiet = r.Bool();
        if (op != 0) return;
        var def = PotatoPeeler.LetterMailbox.FindAny(id);
        var box = PotatoPeeler.LetterMailbox.Owner(id) ?? PotatoPeeler.LetterMailbox.Instance;
        if (def == null || box == null) { Last = $"letter {id} not found"; return; }
        _replaying = true;
        try { box.Deliver(def, quiet); }
        finally { _replaying = false; }
        Letters++;
        Last = $"letter {id} delivered";
    }

    /// <summary>Client: a letter its player read counts for the team (host marks it read).</summary>
    public static void HookLetters()
    {
        if (_letterHooked) return;
        _letterHooked = true;
        try
        {
            PotatoPeeler.LetterArchive.add_OnLetterRead((Action<PotatoPeeler.LetterDefinition, PotatoPeeler.LetterQuality>)((def, q) =>
            {
                if (!MpRuntime.IsClient || _replaying || def == null) return;
                SendIntent(w => { w.U8(OpLetterRead); w.Str8(def.id); w.U8((byte)q); });
            }));
        }
        catch (Exception e) { Plugin.L.LogWarning($"[story] letter hook: {e.Message}"); }
    }

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if ((byte)id < 0x60 || (byte)id > 0x6F) return false;
        try
        {
            var r = new NetReader(payload);
            if (from != null) { if (id == MsgId.StoryIntent && MpRuntime.IsHost) OnIntent(from, ref r); }
            else if (id == MsgId.QuestState) { ApplyQuestState(ref r); Applied++; }
            else if (id == MsgId.LetterEvent) { ApplyLetter(ref r); Applied++; }
            else if (id == MsgId.KeyDelta) ApplyItem(ref r);
            else if (id == MsgId.StoryBeat) ApplyBeat(ref r);
        }
        catch (WireFormatException) { }
        catch (Exception e) { Plugin.L.LogWarning($"[story] {id}: {e.Message}"); }
        return true;
    }

    // ---------------- patches ----------------

    [HarmonyPatch(typeof(PotatoPeeler.QuestManager), nameof(PotatoPeeler.QuestManager.Recompute))]
    static class RecomputeGate { static bool Prefix() => !ClientLocked; }

    [HarmonyPatch(typeof(PotatoPeeler.QuestManager), nameof(PotatoPeeler.QuestManager.CompletePage))]
    static class CompleteGate { static bool Prefix() => !ClientLocked; }

    [HarmonyPatch(typeof(PotatoPeeler.QuestManager), "GrantRewards")]
    static class RewardsGate { static bool Prefix() => !ClientLocked; }

    [HarmonyPatch(typeof(PotatoPeeler.QuestManager), "CatchUpPassedPages")]
    static class CatchUpGate { static bool Prefix() => !ClientLocked; }

    [HarmonyPatch(typeof(PotatoPeeler.QuestManager), nameof(PotatoPeeler.QuestManager.Bump))]
    static class BumpForward
    {
        static bool Prefix(PotatoPeeler.ObjectiveKind kind, long amount)
        {
            if (!ClientLocked) return true;
            SendIntent(w => { w.U8(OpBump); w.U8((byte)kind); w.I32((int)Math.Clamp(amount, int.MinValue, int.MaxValue)); });
            return false;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.QuestManager), nameof(PotatoPeeler.QuestManager.ReportFlag))]
    static class FlagForward
    {
        static bool Prefix(string flagId)
        {
            if (!ClientLocked) return true;
            SendIntent(w => { w.U8(OpFlag); w.Str8(flagId ?? ""); });
            return false;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.PeelCountEvents), nameof(PotatoPeeler.PeelCountEvents.Evaluate))]
    static class EvaluateGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.PeelCountEvents), "Fire")]
    static class FireGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.StoryBeats), "Update")]
    static class BeatsUpdateGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.StoryBeats), "FireQuota")]
    static class BeatsQuotaGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.StoryBeats), "Fire")]
    static class BeatsFireGate
    {
        static bool Prefix() => !MpRuntime.IsClient || _beatReplay;

        /// <summary>Host: every timeline beat reaches the clients, which replay the ones that are per player or visual.</summary>
        static void Postfix(PotatoPeeler.ProgressionTimeline.Entry entry)
        {
            if (!MpRuntime.IsHost || entry == null || MpRuntime.Host is not { } h) return;
            try
            {
                var entries = PotatoPeeler.ProgressionTimeline.Instance?.entries;
                if (entries == null) return;
                for (int i = 0; i < entries.Length; i++)
                    if (entries[i] != null && entries[i].Pointer == entry.Pointer)
                    {
                        var w = new NetWriter(); w.U8((byte)i); w.U8((byte)(int)entry.kind);
                        h.Broadcast(MsgId.StoryBeat, w.Span);
                        Beats++;
                        break;
                    }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[story] beat: {e.Message}"); }
        }
    }

    [ThreadStatic] static bool _beatReplay;
    public static long Beats, BeatsIn;

    /// <summary>
    /// Client: a host beat. Smoke breaks (each player gets a craving), the radio broadcast and the vegetable crate (a world
    /// object players use) replay locally; letters, notes, stones, the key, the puppy and villager crates arrive through their
    /// own sync instead, so they are not replayed here.
    /// </summary>
    static void ApplyBeat(ref NetReader r)
    {
        int idx = r.U8(); var kind = (PotatoPeeler.ProgressionTimeline.Kind)r.U8();
        var entries = PotatoPeeler.ProgressionTimeline.Instance?.entries;
        if (entries == null || idx >= entries.Length || entries[idx] == null) return;
        var e = entries[idx];
        try
        {
            switch (kind)
            {
                case PotatoPeeler.ProgressionTimeline.Kind.SmokeBreak:
                    UnityEngine.Object.FindObjectOfType<PotatoPeeler.SmokeBreak>()?.Request(e.id);
                    break;
                case PotatoPeeler.ProgressionTimeline.Kind.RadioOnAir:
                    UnityEngine.Object.FindObjectOfType<PotatoPeeler.Radio>()?.StartBroadcast();
                    break;
                case PotatoPeeler.ProgressionTimeline.Kind.VegetableCrate:
                {
                    var beats = UnityEngine.Object.FindObjectOfType<PotatoPeeler.StoryBeats>();
                    _beatReplay = true; _replaying = true;
                    try { beats?.Fire(e); } finally { _beatReplay = false; _replaying = false; }
                    break;
                }
            }
            Last = $"beat {idx} ({kind})";
            BeatsIn++;
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[story] beat {idx} {kind}: {ex.Message}"); }
    }

    /// <summary>Client: claiming the vegetable crate is the host's (money, fines, story).</summary>
    [HarmonyPatch(typeof(PotatoPeeler.VegetableCrateEvent), nameof(PotatoPeeler.VegetableCrateEvent.HandleClaimed))]
    static class CrateClaim
    {
        static bool Prefix()
        {
            if (!MpRuntime.IsClient || _replaying) return true;
            SendIntent(w => w.U8(OpCrateClaim));
            return false;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.LetterMailbox), nameof(PotatoPeeler.LetterMailbox.TickLostLetters))]
    static class LostLettersGate { static bool Prefix() => !MpRuntime.IsClient; }

    /// <summary>Host: letters it mails reach every client. Client: only the host mails.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.LetterMailbox), nameof(PotatoPeeler.LetterMailbox.Send))]
    static class SendPatch
    {
        static bool Prefix(ref bool __result)
        {
            if (!ClientLocked) return true;
            __result = false;
            return false;
        }

        static void Postfix(PotatoPeeler.LetterDefinition definition, bool quiet, bool __result)
        {
            if (!__result || !MpRuntime.IsHost || definition == null || MpRuntime.Host is not { } h) return;
            try
            {
                var w = new NetWriter(); w.U8(0); w.Str8(definition.id ?? ""); w.Bool(quiet);
                h.Broadcast(MsgId.LetterEvent, w.Span);
                Letters++;
                Last = $"mailed letter {definition.id}";
            }
            catch (Exception e) { Plugin.L.LogWarning($"[story] letter send: {e.Message}"); }
        }
    }

    public static object Describe()
    {
        var main = PotatoPeeler.QuestManager.Instance?._main;
        return new
        {
            Sent, Applied, Intents, Letters, Beats, BeatsIn, last = Last,
            remoteZones = RemoteZones.ToDictionary(k => k.Key.ToString(), k => k.Value),
            activePage = main?.ActiveIndex,
            title = main?.Active?.title,
            completed = main?.Completed?.Count,
            progress = main?.Progress == null ? null : Enumerable.Range(0, main.Progress.Length).Select(i => $"{main.Progress[i]}{(main.Done != null && i < main.Done.Length && main.Done[i] ? "!" : "")}").ToArray(),
        };
    }
}
