using HarmonyLib;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Shared money, counters, quota and upgrades (spec 08), first cut: one team wallet and one progression, all on the host.
/// The host polls its state at 4 Hz and sends what changed (Balance 0x50, Counters 0x51, Quota 0x53, UpgradeLevels 0x55);
/// clients apply it through the game's own setters, so HUDs react as in single player. Clients never change these
/// themselves: their mutators, the delivery zone and the quota ticks are skipped, and a purchase on a client becomes a
/// PurchaseReq (0x56) that the host runs with the shared balance.
/// Not yet: reasons/actors and popups for income, per-player stats, skill points, fines' voice lines, achievements.
/// </summary>
internal static class EconSync
{
    [ThreadStatic] static bool _applying;
    static double _next;
    static long _lastBalance = long.MinValue;
    static int[] _lastCounters = Array.Empty<int>();
    static int[] _lastQuota = Array.Empty<int>();
    static byte[] _lastLevels = Array.Empty<byte>();
    static int _upgradeCount = -1;
    static int _lastGranted = int.MinValue;
    public static long SentMsgs, Applied, Purchases;
    public static string Last = "";

    static bool ClientLocked => MpRuntime.IsClient && !_applying;

    static int UpgradeCount
    {
        get
        {
            if (_upgradeCount < 0) _upgradeCount = Enum.GetValues(typeof(PotatoPeeler.UpgradeId)).Length;
            return _upgradeCount;
        }
    }

    public static void Reset()
    {
        _lastBalance = long.MinValue; _lastGranted = int.MinValue;
        _lastCounters = Array.Empty<int>(); _lastQuota = Array.Empty<int>(); _lastLevels = Array.Empty<byte>();
    }

    /// <summary>A new client gets everything on its next poll.</summary>
    public static void OnPlayerJoined() => Reset();

    // ---------------- host ----------------

    public static void UpdateHost(HostSession host)
    {
        var now = Time.realtimeSinceStartupAsDouble;
        if (now < _next) return;
        _next = now + 0.25;
        try
        {
            if (PotatoPeeler.EconomyManager.Instance is { } eco && eco.Balance != _lastBalance)
            {
                _lastBalance = eco.Balance;
                var w = new NetWriter(); w.I64(_lastBalance);
                host.Broadcast(MsgId.Balance, w.Span); SentMsgs++;
            }
            if (PotatoPeeler.PotatoTracker.Instance is { } t)
            {
                var c = new[] { t.PeeledCount, t.SoldCount, (int)Math.Min(int.MaxValue, t.TotalEarned), t.RuinedCount, t.MachinePeeledCount, t.FoundItemCount, t.SackRippedCount, t.PuppyPeeledCount };
                if (!c.SequenceEqual(_lastCounters))
                {
                    _lastCounters = c;
                    var w = new NetWriter();
                    w.I32(c[0]); w.I32(c[1]); w.I64(t.TotalEarned); for (int i = 3; i < 8; i++) w.I32(c[i]);
                    host.Broadcast(MsgId.Counters, w.Span); SentMsgs++;
                }
            }
            if (UnityEngine.Object.FindObjectOfType<PotatoPeeler.EndGameDirector>() is { } d)
            {
                var q = new[] { (d._quotaStarted ? 1 : 0) | (d._quotaFinished ? 2 : 0) | (d._quotaMigrate ? 4 : 0), d._quotaPeeledAtCut, d._quotaTarget, d._quotaLost };
                if (!q.SequenceEqual(_lastQuota))
                {
                    _lastQuota = q;
                    var w = new NetWriter(); w.U8((byte)q[0]); w.I32(q[1]); w.I32(q[2]); w.I32(q[3]);
                    host.Broadcast(MsgId.Quota, w.Span); SentMsgs++;
                }
            }
            if (PotatoPeeler.SkillPointBank.Instance is { } bank && bank._GrantedPoints_k__BackingField != _lastGranted)
            {
                _lastGranted = bank._GrantedPoints_k__BackingField;
                var w = new NetWriter(); w.I32(_lastGranted);
                host.Broadcast(MsgId.Skill, w.Span); SentMsgs++;
            }
            if (PotatoPeeler.UpgradeManager.Instance is { } um)
            {
                var lv = new byte[UpgradeCount];
                for (int i = 0; i < lv.Length; i++) lv[i] = (byte)Math.Clamp(um.GetLevel((PotatoPeeler.UpgradeId)i), 0, 255);
                if (!lv.SequenceEqual(_lastLevels))
                {
                    _lastLevels = lv;
                    var w = new NetWriter(); w.U8(5 /*Resync*/); w.U8(0xFF); w.I32(0); w.U8((byte)lv.Length);
                    for (int i = 0; i < lv.Length; i++) { w.U8((byte)i); w.U8(lv[i]); }
                    host.Broadcast(MsgId.UpgradeLevels, w.Span); SentMsgs++;
                }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[econ] poll: {e.Message}"); _next = now + 5; }
    }

    static void OnPurchaseReq(PlayerInfo from, ref NetReader r)
    {
        ushort req = r.U16();
        var id = (PotatoPeeler.UpgradeId)r.U8();
        byte expected = r.U8();
        var um = PotatoPeeler.UpgradeManager.Instance;
        byte result = 9; // Failed
        int level = 0;
        if (um != null)
        {
            level = um.GetLevel(id);
            if (level != expected) result = 3; // Stale: someone bought it first
            else result = um.TryPurchase(id) ? (byte)0 : (byte)1;
            level = um.GetLevel(id);
        }
        var w = new NetWriter(); w.U16(req); w.U8(result); w.U8((byte)id); w.U8((byte)level); w.I32(0);
        MpRuntime.Host?.SendTo(from.Slot, MsgId.PurchaseRes, w.Span);
        Purchases++;
        Last = $"slot {from.Slot} bought {id}: {(result == 0 ? "ok" : result == 3 ? "stale" : "refused")} (level {level})";
        Plugin.L.LogMessage($"[econ] {Last}");
        _next = 0; // send the new balance and levels now
    }

    // ---------------- client ----------------

    static void Apply(MsgId id, ref NetReader r)
    {
        _applying = true;
        try
        {
            switch (id)
            {
                case MsgId.Balance:
                {
                    long b = r.I64();
                    if (PotatoPeeler.EconomyManager.Instance is { } eco && eco.Balance != b) eco.SetBalance(b);
                    break;
                }
                case MsgId.Counters:
                {
                    int peeled = r.I32(), sold = r.I32(); long earned = r.I64();
                    int ruined = r.I32(), machine = r.I32(), found = r.I32(), sacks = r.I32(), puppy = r.I32();
                    PotatoPeeler.PotatoTracker.Instance?.RestoreCounters(peeled, sold, earned, ruined, machine, found, sacks, puppy);
                    break;
                }
                case MsgId.Quota:
                {
                    byte f = r.U8(); int cut = r.I32(), target = r.I32(), lost = r.I32();
                    if (UnityEngine.Object.FindObjectOfType<PotatoPeeler.EndGameDirector>() is { } d)
                    {
                        d._quotaStarted = (f & 1) != 0; d._quotaFinished = (f & 2) != 0; d._quotaMigrate = (f & 4) != 0;
                        d._quotaPeeledAtCut = cut; d._quotaTarget = target; d._quotaLost = lost;
                    }
                    break;
                }
                case MsgId.UpgradeLevels:
                {
                    r.U8(); r.U8(); r.I32();
                    int n = r.U8();
                    var um = PotatoPeeler.UpgradeManager.Instance;
                    bool lowered = false;
                    for (int i = 0; i < n; i++)
                    {
                        var uid = (PotatoPeeler.UpgradeId)r.U8(); int lvl = r.U8();
                        if (um == null) continue;
                        int cur = um.GetLevel(uid);
                        if (lvl > cur) um.GrantLevel(uid, lvl - cur);
                        else if (lvl < cur) { um._levels[uid] = lvl; lowered = true; } // (1.0.4 removed DebugSetLevel)
                    }
                    if (lowered && um != null) try { um.ApplyAll(); } catch { }
                    break;
                }
                case MsgId.Skill:
                {
                    int granted = r.I32();
                    if (PotatoPeeler.SkillPointBank.Instance is { } bank && bank._GrantedPoints_k__BackingField != granted)
                    {
                        bank._GrantedPoints_k__BackingField = granted;
                        try { bank.NotifyChanged(); } catch { }
                    }
                    break;
                }
                case MsgId.PurchaseRes:
                {
                    r.U16(); byte res = r.U8(); var uid = (PotatoPeeler.UpgradeId)r.U8();
                    Last = $"purchase of {uid}: {(res == 0 ? "ok" : res == 3 ? "someone bought it first" : "refused")}";
                    break;
                }
            }
            Applied++;
        }
        finally { _applying = false; }
    }

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if ((byte)id < 0x50 || (byte)id > 0x5F) return false;
        try
        {
            var r = new NetReader(payload);
            if (from != null) { if (id == MsgId.PurchaseReq && MpRuntime.IsHost) OnPurchaseReq(from, ref r); }
            else Apply(id, ref r);
        }
        catch (WireFormatException) { }
        catch (Exception e) { Plugin.L.LogWarning($"[econ] {id}: {e.Message}"); }
        return true;
    }

    static ushort _req;

    // ---------------- client guards ----------------

    [HarmonyPatch(typeof(PotatoPeeler.EconomyManager), nameof(PotatoPeeler.EconomyManager.AddIncome))]
    static class AddIncomeGate
    {
        static bool Prefix(long amount, Vector3 worldPos, PotatoPeeler.IncomeSource source)
        {
            if (!ClientLocked) return true;
            if (CleanSync.InSweep) CleanSync.ForwardIncome(amount, worldPos, source); // swept shavings pay into the team wallet
            return false;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.EconomyManager), nameof(PotatoPeeler.EconomyManager.Fine))]
    static class FineGate { static bool Prefix() => !ClientLocked; }

    [HarmonyPatch(typeof(PotatoPeeler.EconomyManager), nameof(PotatoPeeler.EconomyManager.TrySpend))]
    static class TrySpendGate
    {
        static bool Prefix(ref bool __result) { if (!ClientLocked) return true; __result = false; return false; }
    }

    [HarmonyPatch(typeof(PotatoPeeler.PotatoTracker), nameof(PotatoPeeler.PotatoTracker.HandlePeeled), typeof(PotatoPeeler.PotatoInstance), typeof(PotatoPeeler.PeelSource))]
    static class PeeledGate { static bool Prefix() => !ClientLocked; }

    [HarmonyPatch(typeof(PotatoPeeler.PotatoTracker), nameof(PotatoPeeler.PotatoTracker.HandleRuined))]
    static class RuinedGate
    {
        static bool Prefix(PotatoPeeler.PotatoInstance potato)
        {
            if (!ClientLocked) return true;
            CleanSync.ForwardRuin(potato); // counted (and fined) on the host
            return false;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.PotatoTracker), nameof(PotatoPeeler.PotatoTracker.HandleDelivered))]
    static class DeliveredGate { static bool Prefix() => !ClientLocked; }

    [HarmonyPatch(typeof(PotatoPeeler.SkillPointBank), nameof(PotatoPeeler.SkillPointBank.NoteGranted))]
    static class GrantGate { static bool Prefix() => !ClientLocked; }

    /// <summary>Client: selling happens on the host (its copy of the potato reaches its zone; the proxy then despawns).</summary>
    [HarmonyPatch(typeof(PotatoPeeler.PotatoDeliveryZone), nameof(PotatoPeeler.PotatoDeliveryZone.Accept))]
    static class AcceptGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.EndGameDirector), nameof(PotatoPeeler.EndGameDirector.TickQuotaBaseline))]
    static class QuotaBaselineGate { static bool Prefix() => !MpRuntime.IsClient; }

    [HarmonyPatch(typeof(PotatoPeeler.EndGameDirector), nameof(PotatoPeeler.EndGameDirector.TickQuotaResync))]
    static class QuotaResyncGate { static bool Prefix() => !MpRuntime.IsClient; }

    /// <summary>Client: buying goes to the host, which spends the team's money.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.UpgradeManager), nameof(PotatoPeeler.UpgradeManager.TryPurchase))]
    static class PurchaseGate
    {
        static bool Prefix(PotatoPeeler.UpgradeManager __instance, PotatoPeeler.UpgradeId id, ref bool __result)
        {
            if (!ClientLocked) return true;
            try
            {
                if (MpRuntime.Client is { State: ClientState.Playing } c)
                {
                    var w = new NetWriter();
                    w.U16(++_req); w.U8((byte)id); w.U8((byte)__instance.GetLevel(id)); w.U8(0); w.I32(0); w.U8(0);
                    c.Send(MsgId.PurchaseReq, w.Span);
                    Last = $"asked the host to buy {id}";
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[econ] purchase request: {e.Message}"); }
            // Predict like the game would decide, so the shop plays the right feedback; levels and money come from the host.
            try { __result = !__instance.IsMaxed(id) && __instance.ArePrerequisitesMet(id) && __instance.CanAffordNext(id); }
            catch { __result = false; }
            return false;
        }
    }

    public static object Describe() => new
    {
        SentMsgs, Applied, Purchases, last = Last,
        balance = PotatoPeeler.EconomyManager.Instance?.Balance,
        peeled = PotatoPeeler.PotatoTracker.Instance?.PeeledCount,
        sold = PotatoPeeler.PotatoTracker.Instance?.SoldCount,
        quota = UnityEngine.Object.FindObjectOfType<PotatoPeeler.EndGameDirector>() is { } d ? new { d._quotaStarted, d._quotaTarget, d._quotaPeeledAtCut, d._quotaLost } : null,
        levelsNonZero = PotatoPeeler.UpgradeManager.Instance is { } um
            ? Enumerable.Range(0, UpgradeCount).Where(i => um.GetLevel((PotatoPeeler.UpgradeId)i) > 0).Select(i => $"{(PotatoPeeler.UpgradeId)i}={um.GetLevel((PotatoPeeler.UpgradeId)i)}").ToArray()
            : null,
    };
}
