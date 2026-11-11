using HarmonyLib;
using PotatoPleaseMP.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PotatoPleaseMP.UI;

/// <summary>
/// Pause-menu entries during a session (spec 16): INVITE FRIENDS (host over Steam: the overlay invite dialog for our lobby)
/// and LEAVE SESSION. Cloned from the game's own SAVES button; visibility is refreshed whenever the menu opens.
/// </summary>
internal static class PauseButtons
{
    static Button? _invite, _leave;

    [HarmonyPatch(typeof(PotatoPeeler.PauseUI), nameof(PotatoPeeler.PauseUI.OnShowStarted))]
    static class ShowPatch
    {
        static void Postfix(PotatoPeeler.PauseUI __instance)
        {
            try { Ensure(__instance); Refresh(); }
            catch (Exception e) { Plugin.L.LogError($"Pause buttons: {e}"); }
        }
    }

    static void Ensure(PotatoPeeler.PauseUI ui)
    {
        if (_invite != null && _leave != null) return;
        var src = ui.savesButton ?? ui.settingsButton;
        if (src == null) return;
        _invite = Clone(src, "PPMP_Invite", "Invite Friends", () => Net.Steam.SteamLobby.OpenInviteDialog());
        _leave = Clone(src, "PPMP_Leave", "Leave Session", () => { MpRuntime.Stop(); Refresh(); });
    }

    static Button Clone(Button src, string name, string label, Action onClick)
    {
        var go = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent, false);
        go.name = name;
        go.transform.SetSiblingIndex(src.transform.GetSiblingIndex() + 1);
        foreach (var c in go.GetComponentsInChildren<Component>(true))
            if (c != null && c.GetIl2CppType().Name == "LocalizeStringEvent") UnityEngine.Object.Destroy(c);
        foreach (var t in go.GetComponentsInChildren<TMP_Text>(true)) t.text = label;
        var b = go.GetComponent<Button>();
        b.onClick = new Button.ButtonClickedEvent();
        b.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
        {
            try { onClick(); } catch (Exception e) { Plugin.L.LogError($"Pause button {name}: {e}"); }
        }));
        return b;
    }

    static void Refresh()
    {
        if (_invite != null) _invite.gameObject.SetActive(MpRuntime.IsHost && Net.Steam.SteamLobby.Lobby != 0);
        if (_leave != null) _leave.gameObject.SetActive(MpRuntime.Active);
    }
}
