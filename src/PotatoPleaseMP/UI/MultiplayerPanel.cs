using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PotatoPleaseMP.UI;

/// <summary>
/// The MULTIPLAYER dialog in the main menu: a clone of the game's own DifficultyPanel (paper card, red header, two big
/// choice rows, GO BACK), retitled HOST SHIFT / JOIN SHIFT. The subtitle shows live join status.
/// </summary>
internal static class MultiplayerPanel
{
    static GameObject? _panel;
    static TMP_Text? _status, _hostDetail, _joinDetail;
    static float _nextRefresh;

    public static void Init() => MainMenuEntry.Clicked += Open;

    static void Open()
    {
        if (_panel == null) Build();
        if (_panel == null) return;
        _panel.SetActive(true);
        Refresh();
    }

    static bool OnMainMenu => UnityEngine.Object.FindObjectOfType<PotatoPeeler.MainMenuController>() != null;

    /// <summary>A join started (Steam invite or Join Game): show its progress in the panel, or as a notice mid-shift.</summary>
    public static void ShowJoin()
    {
        ClearJoinError();
        if (OnMainMenu) Open();
        else Notice.Show("Joining your friend's shift...", 6f);
    }

    /// <summary>A join failed before the host's shift loaded: say why.</summary>
    public static void JoinFailed(string head, string detail)
    {
        _joinError = head;
        _joinErrorDetail = detail;
        Plugin.L.LogMessage($"[ui] join failed: {head} - {detail}");
        if (OnMainMenu) Open();
        else Notice.Show($"{head}: {detail}", 8f);
    }

    public static void ClearJoinError() { _joinError = null; _joinErrorDetail = null; }

    static string? _joinError, _joinErrorDetail;

    static void Close()
    {
        if (_panel != null) _panel.SetActive(false);
    }

    static void Build()
    {
        var src = FindDifficultyPanel();
        if (src == null) { Plugin.L.LogWarning("Multiplayer panel: DifficultyPanel not found."); return; }
        // Clone while the source is inactive so no game Awake runs on the copy.
        bool wasActive = src.activeSelf;
        src.SetActive(false);
        _panel = UnityEngine.Object.Instantiate(src, src.transform.parent, false);
        src.SetActive(wasActive);
        _panel.name = "PPMP_MultiplayerPanel";
        foreach (var lse in _panel.GetComponentsInChildren<Component>(true))
            if (lse != null && lse.GetIl2CppType().Name == "LocalizeStringEvent") UnityEngine.Object.Destroy(lse);

        var body = _panel.transform.Find("Body");
        SetText(body.Find("Title"), "MULTIPLAYER");
        _status = body.Find("Subtitle")?.GetComponent<TMP_Text>();
        SetText(body.Find("Normal/Label"), "HOST SHIFT");
        _hostDetail = body.Find("Normal/Detail")?.GetComponent<TMP_Text>();
        SetText(body.Find("Hard/Label"), "JOIN SHIFT");
        _joinDetail = body.Find("Hard/Detail")?.GetComponent<TMP_Text>();
        SetText(body.Find("Cancel/Label"), "GO BACK");

        Hook(body.Find("Normal"), OnHost);
        Hook(body.Find("Hard"), OnJoin);
        Hook(body.Find("Cancel"), Close);
        Plugin.L.LogMessage("Multiplayer panel built.");
    }

    static GameObject? FindDifficultyPanel()
    {
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Transform>()))
        {
            var t = o.Cast<Transform>();
            if (t.name == "DifficultyPanel" && t.gameObject.scene.IsValid()) return t.gameObject;
        }
        return null;
    }

    static void SetText(Transform? t, string text)
    {
        var tmp = t?.GetComponent<TMP_Text>();
        if (tmp != null) tmp.text = text;
    }

    static void Hook(Transform? t, Action action)
    {
        var b = t?.GetComponent<Button>();
        if (b == null) return;
        b.onClick = new Button.ButtonClickedEvent();
        b.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
        {
            try { action(); }
            catch (Exception e) { Plugin.L.LogError($"Multiplayer panel: {e}"); }
        }));
    }

    static string Transport => MpRuntime.TransportMode.Value.Equals("Direct", StringComparison.OrdinalIgnoreCase)
        ? $"direct, port {MpRuntime.DirectPort.Value}" : "Steam friends";

    static void OnHost()
    {
        var r = MpRuntime.StartHost();
        Plugin.L.LogMessage($"[ui] host: {System.Text.Json.JsonSerializer.Serialize(r)}");
        var menu = UnityEngine.Object.FindObjectOfType<PotatoPeeler.MainMenuController>();
        if (menu == null) return;
        // Host the current shift if there is one, else start a new one (the game asks for a difficulty).
        if (PotatoPeeler.SaveSlots.Exists(0)) menu.ContinueShift(); else menu.BeginShift();
        Close();
    }

    static void OnJoin()
    {
        if (MpRuntime.TransportMode.Value.Equals("Steam", StringComparison.OrdinalIgnoreCase) && Net.Steam.SteamNative.Alive)
        {
            // Steam: you join a friend's shift from their invite, or from Join Game in the friends list.
            PotatoPleaseMP.UI.Notice.Show("Accept a friend's invite, or pick Join Game on a friend in the Steam friends list.");
            Net.Steam.SteamLobby.OpenFriends();
            return;
        }
        var r = MpRuntime.Join(MpRuntime.HostAddress.Value);
        Plugin.L.LogMessage($"[ui] join: {System.Text.Json.JsonSerializer.Serialize(r)}");
        Refresh();
    }

    static void Refresh()
    {
        if (_status == null) return;
        if (MpRuntime.Client is { } c)
            _status.text = c.State switch
            {
                ClientState.Connecting => "CONNECTING...",
                ClientState.Handshaking or ClientState.Queued => "SHAKING HANDS...",
                ClientState.Downloading => "RECEIVING THE SHIFT...",
                ClientState.Loading or ClientState.Applying => "LOADING THE SHIFT...",
                ClientState.Playing => "JOINED",
                _ => c.State.ToString().ToUpperInvariant(),
            };
        else if (MpRuntime.Host != null) _status.text = "HOSTING";
        else if (Net.Steam.SteamLobby.State == "joining lobby") _status.text = "JOINING YOUR FRIEND...";
        else if (_joinError != null) _status.text = _joinError;
        else _status.text = "WORK THE PILE TOGETHER";
        if (_hostDetail != null) _hostDetail.text = $"Comrades join your current shift ({Transport}).";
        if (_joinDetail != null) _joinDetail.text = MpRuntime.Client == null && MpRuntime.Host == null && !string.IsNullOrEmpty(_joinErrorDetail)
            ? _joinErrorDetail!
            : MpRuntime.TransportMode.Value.Equals("Direct", StringComparison.OrdinalIgnoreCase)
                ? $"Join the shift at {MpRuntime.HostAddress.Value}." : "Join a friend's shift (Steam invite).";
    }

    /// <summary>Every frame from the plugin: keep the status line live while the panel is open.</summary>
    public static void Update()
    {
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 0.25f;
        // A join that started mid-transition (an invite accepted during a shift, then quit to the menu): bring the panel
        // up once the menu is there, so the joiner sees the progress.
        bool joining = (MpRuntime.Client is { } c && c.State < ClientState.Loading) || Net.Steam.SteamLobby.State == "joining lobby";
        if (!joining) _autoOpened = false;
        else if (!_autoOpened && (_panel == null || !_panel.activeInHierarchy) && OnMainMenu) { _autoOpened = true; Open(); }
        if (_panel == null || !_panel.activeInHierarchy) return;
        Refresh();
    }

    static bool _autoOpened;
}
