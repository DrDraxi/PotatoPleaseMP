using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace PotatoPleaseMP;

[BepInPlugin(Guid, "Potato Please Multiplayer", Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "drdraxi.potatopleasemp";
    public const string Version = "0.2.3";

    internal static ManualLogSource L = null!;
    internal static Harmony Harmony = null!;
    internal static ConfigEntry<int> ControlPort = null!;

    public override void Load()
    {
        L = Log;
        ControlPort = Config.Bind("Dev", "ControlPort", 0,
            "Test setups only: a local HTTP API on 127.0.0.1:<port> that drives the game (0 = off).");
        Dev.AchievementGuard.Init(Config, ControlPort.Value > 0);
        Saves.Isolation.Init(Config);
        Net.Steam.NativeResolver.Install();
        Net.MpRuntime.Init(Config);
        UI.MultiplayerPanel.Init();
        Game.PeelCapture.Install();
        Game.PeelSync.Init();
        Harmony = new Harmony(Guid);
        PatchEachClass();
        AddComponent<MpBehaviour>();
        Dev.DevControl.Start(ControlPort.Value);
        L.LogMessage($"Potato Please Multiplayer {Version} loaded.");
    }

    /// <summary>One try/catch per patch class: a bad target disables only its own feature (spec 20).</summary>
    static void PatchEachClass()
    {
        int ok = 0, failed = 0;
        foreach (var t in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try { Harmony.CreateClassProcessor(t).Patch(); ok++; }
            catch (Exception e) { failed++; L.LogError($"Patch {t.FullName} failed: {e.InnerException?.Message ?? e.Message}"); }
        }
        L.LogMessage($"Patches: {ok} applied, {failed} failed.");
    }
}

/// <summary>Persistent behaviour that pumps the mod every frame.</summary>
public class MpBehaviour : MonoBehaviour
{
    public MpBehaviour(IntPtr ptr) : base(ptr) { }

    float _nextFrameCap;
    bool _selfTested;
    int _steamErrors;

    void Update()
    {
        if (!_selfTested) { _selfTested = true; Saves.Isolation.SelfTest(); Dev.FrameRecorder.Host = this; }
        // Test copies run behind other windows; with vsync on, an occluded window drops to ~4 fps.
        if (Plugin.ControlPort.Value > 0 && Time.realtimeSinceStartup >= _nextFrameCap)
        {
            _nextFrameCap = Time.realtimeSinceStartup + 2f;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != 60) Application.targetFrameRate = 60;
        }
        Saves.Isolation.Prefs.Tick();
        try { Saves.SaveContext.Tick(); UI.Notice.Tick(); UI.MainMenuEntry.KeepLabel(); UI.MultiplayerPanel.Update(); Net.MpRuntime.Update(); Game.StationSync.Update(); Game.BarrowSync.Update(); Game.FlameView.Update(); Game.EndingChoiceSync.Update(); if (Net.MpRuntime.IsClient) Game.StorySync.HookLetters(); Game.TimeGuard.Update(); }
        catch (Exception e) { if (_steamErrors++ < 20) Plugin.L.LogError($"Session: {e}"); }
        try { Net.Steam.SteamBoot.Update(); Net.Steam.SteamLobby.Update(); }
        catch (Exception e) { if (_steamErrors++ < 5) Plugin.L.LogError($"Steam: {e}"); }
        Dev.VirtualInput.Tick();
        try { Dev.VirtualPad.Tick(); }
        catch (Exception e) { Plugin.L.LogError($"VirtualPad: {e}"); }
        try { Dev.DevControl.Pump(); }
        catch (Exception e) { Plugin.L.LogError($"DevControl: {e}"); }
    }

    void LateUpdate()
    {
        Dev.VirtualInput.EndFrame();
        try { Presence.DemoAvatars.LateUpdate(); Net.MpRuntime.LateUpdate(); }
        catch (Exception e) { if (_steamErrors++ < 5) Plugin.L.LogError($"Avatars: {e}"); }
    }

    void OnApplicationQuit()
    {
        // Tell the others we are gone (otherwise they wait for the timeout).
        try { if (Net.MpRuntime.Active) Net.MpRuntime.Stop(); } catch { }
        Saves.Isolation.Prefs.Flush();
    }
}
