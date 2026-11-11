using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace PotatoPleaseMP.UI;

/// <summary>
/// Adds a native MULTIPLAYER command to the main menu's paper card (spec 16 / doc 15 §3.1): a clone of 04_SETTINGS (and its
/// separator) inserted after LOAD SAVE, created in a prefix on MainMenuSelectionFX.Awake so the menu's own arrow, scale and
/// sound effects pick it up when it builds its item list.
/// </summary>
internal static class MainMenuEntry
{
    public const string Label = "MULTIPLAYER";
    public static Button? Button { get; private set; }
    public static event Action? Clicked;

    [HarmonyPatch(typeof(PotatoPeeler.MainMenuSelectionFX), nameof(PotatoPeeler.MainMenuSelectionFX.Awake))]
    static class AwakePatch
    {
        static void Prefix(PotatoPeeler.MainMenuSelectionFX __instance)
        {
            try { Add(__instance); }
            catch (Exception e) { Plugin.L.LogError($"Main menu entry: {e}"); }
        }
    }

    static void Add(PotatoPeeler.MainMenuSelectionFX fx)
    {
        var orders = (fx.commandRail != null ? fx.commandRail.Find("Orders") : null) ?? fx.mainPanel?.Find("CommandRail/Orders");
        if (orders == null) { Plugin.L.LogWarning("Main menu entry: Orders not found."); return; }
        if (orders.Find(Name()) != null) return;
        var settings = orders.Find("04_SETTINGS");
        if (settings == null) { Plugin.L.LogWarning("Main menu entry: 04_SETTINGS not found."); return; }

        // Insert after LOAD SAVE's separator so the order reads CONTINUE / NEW SHIFT / LOAD SAVE / MULTIPLAYER / SETTINGS.
        int at = settings.GetSiblingIndex();
        var sep = at > 0 ? orders.GetChild(at - 1) : null;
        var clone = UnityEngine.Object.Instantiate(settings.gameObject, orders, false);
        clone.name = Name();
        clone.transform.SetSiblingIndex(at);
        if (sep != null && sep.name.StartsWith("---"))
        {
            var sepClone = UnityEngine.Object.Instantiate(sep.gameObject, orders, false);
            sepClone.transform.SetSiblingIndex(at + 1);
        }

        // The card was laid out for six commands: shrink the rows so seven still fit inside the paper.
        var group = orders.GetComponent<VerticalLayoutGroup>();
        if (group != null) group.spacing = Mathf.Min(group.spacing, 2f);
        // Rows size themselves (childControlHeight off), so scale the whole list towards its top edge instead.
        var rt = orders.Cast<RectTransform>();
        var top = rt.TransformPoint(new Vector3(0, rt.rect.yMax, 0));
        rt.localScale = rt.localScale * 0.88f;
        rt.position += top - rt.TransformPoint(new Vector3(0, rt.rect.yMax, 0));
        for (int i = 0; i < orders.childCount; i++)
        {
            var le = orders.GetChild(i).GetComponent<LayoutElement>();
            if (le != null && le.preferredHeight > 20) le.preferredHeight *= 0.8f;
        }

        // The cloned row carries the game's localization driver, which would put "SETTINGS" back once the strings load
        // (on a fresh profile that happens after this runs). Drop it, then keep the label in place.
        foreach (var c in clone.GetComponentsInChildren<Component>(true))
            if (c != null && c.GetIl2CppType().Name.Contains("Localize")) UnityEngine.Object.DestroyImmediate(c);
        foreach (var t in clone.GetComponentsInChildren<Text>(true)) t.text = Label;
        _labels.Clear();
        foreach (var t in clone.GetComponentsInChildren<TMPro.TMP_Text>(true)) { t.text = Label; _labels.Add(t); }
        // Inspector listeners survive RemoveAllListeners: replace the whole event.
        var button = clone.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
        {
            Plugin.L.LogMessage("MULTIPLAYER clicked.");
            Clicked?.Invoke();
        }));
        Button = button;
        Plugin.L.LogMessage($"Main menu entry added as {clone.name} (main command: {PotatoPeeler.MainMenuSelectionFX.IsMainCommand(clone.name)}).");
    }

    static string? _name;
    static readonly List<TMPro.TMP_Text> _labels = new();

    /// <summary>Called every frame: puts the label back if anything rewrote it.</summary>
    public static void KeepLabel()
    {
        for (int i = _labels.Count - 1; i >= 0; i--)
        {
            var t = _labels[i];
            if (t == null) { _labels.RemoveAt(i); continue; }
            if (t.text != Label) t.text = Label;
        }
    }

    /// <summary>A name the menu FX accepts as a main command (it keys its effects off the object name).</summary>
    static string Name()
    {
        if (_name != null) return _name;
        foreach (var n in new[] { "035_MULTIPLAYER", "03_MULTIPLAYER", "04_MULTIPLAYER", "MULTIPLAYER" })
            if (PotatoPeeler.MainMenuSelectionFX.IsMainCommand(n)) return _name = n;
        return _name = "03_MULTIPLAYER";
    }
}
