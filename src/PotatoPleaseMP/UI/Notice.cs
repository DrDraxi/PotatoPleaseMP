using UnityEngine;

namespace PotatoPleaseMP.UI;

/// <summary>
/// On-screen notices in the game's own notice style. The game's way (UIEventBus.Publish&lt;SystemNoticeEvent&gt;) cannot be
/// called from the mod: the event is a struct holding a string, and passing it through the IL2CPP generic bridge crashes the
/// game natively. So this writes the game's SystemNoticeHUD label directly and fades it out itself.
/// </summary>
internal static class Notice
{
    static PotatoPeeler.SystemNoticeHUD? _hud;
    static float _until, _shownAt;
    static bool _showing;

    public static void Show(string text, float seconds = 4f)
    {
        Plugin.L.LogMessage("[notice] " + text);
        try
        {
            if (_hud == null) _hud = UnityEngine.Object.FindObjectOfType<PotatoPeeler.SystemNoticeHUD>();
            if (_hud == null || _hud.label == null || _hud.group == null) return;
            _hud.label.text = text;
            try { _hud.label.color = _hud.infoColor; } catch { }
            _hud.group.alpha = 1f;
            _shownAt = Time.unscaledTime;
            _until = _shownAt + seconds;
            _showing = true;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[notice] {e.Message}"); }
    }

    /// <summary>Every frame: fade our notice out.</summary>
    public static void Tick()
    {
        if (!_showing) return;
        try
        {
            if (_hud == null || _hud.group == null) { _showing = false; return; }
            float t = Time.unscaledTime;
            if (t >= _until + 0.6f) { _hud.group.alpha = 0f; _showing = false; }
            else if (t >= _until) _hud.group.alpha = 1f - (t - _until) / 0.6f;
        }
        catch { _showing = false; }
    }
}
