using HarmonyLib;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Spec 10: in a session time never stops (the pause menu and tutorial popups set timeScale 0 in single player) and a death
/// never reloads the shared world. All patch targets are unique native bodies (spec 03/10); single player is untouched.
/// </summary>
internal static class TimeGuard
{
    public static int DeathsPrevented;

    [HarmonyPatch(typeof(PotatoPeeler.PauseUI), nameof(PotatoPeeler.PauseUI.OnShowStarted))]
    static class PausePatch
    {
        static void Postfix() { if (MpRuntime.Active) Keep(); }
    }

    [HarmonyPatch(typeof(PotatoPeeler.InfoPopupPanel), nameof(PotatoPeeler.InfoPopupPanel.OnShowStarted))]
    static class PopupPatch
    {
        static void Postfix() { if (MpRuntime.Active) Keep(); }
    }

    static void Keep()
    {
        Time.timeScale = 1f;
        AudioListener.pause = false;
    }

    /// <summary>Every frame while a session runs: anything else that freezes time is undone too.</summary>
    public static void Update()
    {
        if (MpRuntime.Active && Time.timeScale != 1f) Time.timeScale = 1f;
    }

    /// <summary>A death would reload the last save for everyone: respawn this player indoors at the last safe pose instead.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.DeathReload), nameof(PotatoPeeler.DeathReload.ToLastSave))]
    static class DeathPatch
    {
        static bool Prefix(string cause)
        {
            if (!MpRuntime.Active) return true;
            DeathsPrevented++;
            try
            {
                var sm = PotatoPeeler.SaveManager.Instance;
                var mv = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PlayerMove>();
                var spot = sm != null && sm._hasSafePose ? sm._safePosition : (Vector3?)null;
                // After the storm strike the yard kills: never respawn outside.
                var storm = PotatoPeeler.SnowStorm.Instance;
                if (spot is { } sp && storm != null && storm._Struck_k__BackingField && storm.IsOutside(sp) && storm.door != null)
                    spot = storm.door.transform.position + storm.insideDirection.normalized * 3f;
                if (mv != null && spot is { } s2) mv.Warp(s2);
                try { if (PotatoPeeler.BodyTemperature.Instance is { } bt) { bt._warmth = 1f; bt._Dying_k__BackingField = false; bt.WakeUp(); } } catch { }
                try { PotatoPeeler.CinematicBars.Instance?.Hide(0.5f); } catch { }
                Keep();
                var player = UnityEngine.Object.FindObjectOfType<PotatoPeeler.Player>();
                player?.SetControlsEnabled(true, true);
                // The death routine faded the screen to black before asking for the reload: bring the picture back.
                try { PotatoPeeler.ScreenFade.Instance?.FadeTo(0f, 0.8f); } catch { }
                Plugin.L.LogMessage($"[mp] death ({cause}): respawned at the last safe spot instead of reloading the shift.");
            }
            catch (Exception e) { Plugin.L.LogError($"[mp] respawn failed: {e}"); }
            return false;
        }
    }
}
