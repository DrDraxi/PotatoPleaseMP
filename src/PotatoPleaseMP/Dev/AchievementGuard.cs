using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace PotatoPleaseMP.Dev;

/// <summary>
/// Keeps mod development and testing from touching the player's real Steam achievements and stats.
/// On by default whenever the dev control API is on (the test copies), and can be forced with [Dev] BlockAchievements.
/// The game's own writers are skipped: SteamAchievements.Unlock (SetAchievement), PushStats (SetStat), Flush (StoreStats)
/// and FlushProgress. Each has a unique native body (XrefDump fold = 1), so nothing else is affected. (Detouring the Steam
/// flat-API exports does NOT work: steam_api64 folds identical thunks, e.g. SetStatInt32 with a SteamInput call.)
/// Reading (GetAchievement, stats) is untouched.
/// </summary>
internal static class AchievementGuard
{
    public static ConfigEntry<string> Mode = null!;
    public static bool Active { get; private set; }
    public static long Blocked;
    public static readonly List<string> Recent = new();

    public static string Status = "off";

    public static void Init(ConfigFile cfg, bool devMode)
    {
        Mode = cfg.Bind("Dev", "BlockAchievements", "Auto",
            "Auto = block Steam achievement/stat writes while the dev control API is on (test copies); On = always; Off = never.");
        var m = Mode.Value.Trim().ToLowerInvariant();
        Active = m == "on" || (m == "auto" && devMode);
        Status = Active ? "on: the game's Unlock/PushStats/Flush/FlushProgress are skipped" : "off";
        if (Active) Plugin.L.LogWarning("[achievements] dev build: Steam achievement and stat writes are blocked. Your real Steam achievements and stats stay untouched.");
    }

    static void Note(string what)
    {
        Blocked++;
        lock (Recent) { Recent.Add(what); if (Recent.Count > 30) Recent.RemoveAt(0); }
        if (Blocked <= 50) Plugin.L.LogMessage($"[achievements] blocked {what} (dev build)");
    }

    static string Str(IntPtr p) { try { return Marshal.PtrToStringUTF8(p) ?? "?"; } catch { return "?"; } }

    /// <summary>The game's own unlock path (also stops its "achievement unlocked" bookkeeping).</summary>
    [HarmonyPatch(typeof(PotatoPeeler.Platform.SteamAchievements), nameof(PotatoPeeler.Platform.SteamAchievements.Unlock))]
    static class UnlockGate
    {
        static bool Prefix(string apiName)
        {
            if (!Active) return true;
            Note("Unlock " + apiName);
            return false;
        }
    }

    [DllImport("steam_api64")] static extern IntPtr SteamAPI_SteamUserStats_v013();
    [DllImport("steam_api64")] static extern uint SteamAPI_ISteamUserStats_GetNumAchievements(IntPtr self);
    [DllImport("steam_api64")] static extern IntPtr SteamAPI_ISteamUserStats_GetAchievementName(IntPtr self, uint i);
    [DllImport("steam_api64")] [return: MarshalAs(UnmanagedType.I1)]
    static extern bool SteamAPI_ISteamUserStats_GetAchievementAndUnlockTime(IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.I1)] out bool achieved, out uint unlockTime);

    /// <summary>Achievements this Steam account has, with unlock times (read only).</summary>
    public static object Unlocked()
    {
        if (!Net.Steam.SteamNative.Alive) return new { error = "steam not running" };
        var st = SteamAPI_SteamUserStats_v013();
        uint n = SteamAPI_ISteamUserStats_GetNumAchievements(st);
        var list = new List<object>();
        for (uint i = 0; i < n; i++)
        {
            var name = Marshal.PtrToStringUTF8(SteamAPI_ISteamUserStats_GetAchievementName(st, i)) ?? "";
            if (SteamAPI_ISteamUserStats_GetAchievementAndUnlockTime(st, name, out var got, out var t) && got)
                list.Add(new { name, unlocked = DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("yyyy-MM-dd HH:mm") });
        }
        return new { total = n, unlockedCount = list.Count, unlocked = list };
    }

    [HarmonyPatch(typeof(PotatoPeeler.Platform.SteamAchievements), nameof(PotatoPeeler.Platform.SteamAchievements.PushStats))]
    static class PushGate { static bool Prefix() { if (!Active) return true; Note("PushStats"); return false; } }

    [HarmonyPatch(typeof(PotatoPeeler.Platform.SteamAchievements), nameof(PotatoPeeler.Platform.SteamAchievements.Flush))]
    static class FlushGate
    {
        static bool Prefix(PotatoPeeler.Platform.SteamAchievements __instance)
        {
            if (!Active) return true;
            Note("Flush (StoreStats)");
            try { __instance._storeDirty = false; __instance._statDirty = false; } catch { } // or the game retries every frame
            return false;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.Platform.SteamAchievements), nameof(PotatoPeeler.Platform.SteamAchievements.FlushProgress))]
    static class FlushProgressGate { static bool Prefix() { if (!Active) return true; Note("FlushProgress"); return false; } }

    public static object Describe() => new { active = Active, status = Status, blocked = Blocked, recent = Recent.ToArray() };
}
