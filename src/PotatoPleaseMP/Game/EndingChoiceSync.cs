using HarmonyLib;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PotatoPleaseMP.Game;

/// <summary>
/// 1.0.4's choice after the credits: Continue Playing (back to the hangar with the finished save) or Main Menu.
/// - Host, Continue: the host reloads its finished save and keeps hosting.
/// - Host, Main Menu: the session ends for everyone.
/// - Client, Main Menu: leaves the session.
/// - Client, Continue: a client's own "finished save" is only its copy of the host's world, so instead it leaves to the menu
///   and joins the host again (retrying while the host reloads), getting the host's current world.
/// </summary>
internal static class EndingChoiceSync
{
    static string _rejoin = "";
    static double _nextTry, _giveUpAt;
    static int _tries;
    public static string Last = "";

    [HarmonyPatch(typeof(PotatoPeeler.EndingSequenceUI), nameof(PotatoPeeler.EndingSequenceUI.ContinuePlaying))]
    static class ContinuePatch
    {
        static bool Prefix(PotatoPeeler.EndingSequenceUI __instance)
        {
            if (!MpRuntime.IsClient)
            {
                if (MpRuntime.IsHost) { Last = "host continues"; EndSync.SendHostContinue(); MpRuntime.Host?.Update(Time.realtimeSinceStartupAsDouble); }
                return true;
            }
            ArmRejoin();
            Last = $"client continues: rejoining {_rejoin}";
            Plugin.L.LogMessage($"[ending] {Last}");
            MpRuntime.StopQuiet();
            try { __instance.ReturnToMenu(); } catch (Exception e) { Plugin.L.LogWarning($"[ending] menu: {e.Message}"); }
            return false;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.EndingSequenceUI), nameof(PotatoPeeler.EndingSequenceUI.ReturnToMenu))]
    static class MenuPatch
    {
        static void Prefix()
        {
            if (!MpRuntime.Active) return;
            Last = MpRuntime.IsHost ? "host left to the menu: session closed" : "client left to the menu";
            MpRuntime.StopQuiet();
        }
    }

    static void ArmRejoin()
    {
        _rejoin = MpRuntime.LastJoinTarget;
        _tries = 0; _nextTry = Time.realtimeSinceStartupAsDouble + 4; _giveUpAt = Time.realtimeSinceStartupAsDouble + 180;
    }

    static bool _hostContinued;

    /// <summary>Client: the host reloaded its finished save. Our copy of the world is the old one: leave and join again.
    /// Called from inside the session's message loop, so the leave waits for the next frame.</summary>
    public static void OnHostContinued() { if (MpRuntime.IsClient) _hostContinued = true; }

    static void HandleHostContinued()
    {
        if (!MpRuntime.IsClient) return;
        // still on our own ending screen: the Continue button does the same thing when pressed
        if (UnityEngine.Object.FindObjectOfType<PotatoPeeler.EndingSequenceUI>() is { } ui && ui.isActiveAndEnabled)
        {
            UI.Notice.Show("The host keeps playing. Choose Continue Playing to rejoin.");
            return;
        }
        ArmRejoin();
        Last = "host continued: rejoining";
        Plugin.L.LogMessage($"[ending] {Last}");
        UI.Notice.Show("The host keeps playing: rejoining their shift...");
        MpRuntime.StopQuiet();
        try { UnityEngine.Object.FindObjectOfType<PotatoPeeler.PauseUI>(true)?.LoadMainMenu(false); }
        catch (Exception e) { Plugin.L.LogWarning($"[ending] menu: {e.Message}"); }
    }

    /// <summary>Every frame: once in the main menu, join the host again until it lets us in.</summary>
    public static void Update()
    {
        if (_hostContinued) { _hostContinued = false; HandleHostContinued(); }
        if (string.IsNullOrEmpty(_rejoin)) return;
        var now = Time.realtimeSinceStartupAsDouble;
        if (now > _giveUpAt) { _rejoin = ""; UI.Notice.Show("Could not rejoin the host's shift. Join it again from MULTIPLAYER."); return; }
        if (MpRuntime.Client is { State: ClientState.Playing }) { _rejoin = ""; Last = "rejoined the host"; return; }
        if (SceneManager.GetActiveScene().name == "Gameplay" || now < _nextTry) return;
        if (MpRuntime.Client is { } c && c.State != ClientState.Ended) return; // a join is in progress
        if (MpRuntime.Client != null) MpRuntime.StopQuiet();
        _nextTry = now + 6; _tries++;
        Last = $"rejoin try {_tries}";
        Plugin.L.LogMessage($"[ending] {Last} -> {MpRuntime.Join(_rejoin)}");
    }

    public static object Describe() => new { Last, rejoin = _rejoin, tries = _tries };
}
