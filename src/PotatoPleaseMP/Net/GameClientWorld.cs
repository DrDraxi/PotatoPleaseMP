using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Session;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PotatoPleaseMP.Net;

/// <summary>
/// Client side of a join (spec 15 §3.2, first cut): writes the host's save into the client folder (never the player's own
/// saves), switches the game's save folder there and loads slot 0 the way the menu's CONTINUE does. Mid-shift it reloads
/// the gameplay scene into that slot.
/// </summary>
internal sealed class GameClientWorld : IClientWorld
{
    readonly PeerId _host;
    float _startedAt;
    bool _loading, _sawLoadingScene;
    public static string LastStatus = "idle";

    public GameClientWorld(PeerId host) => _host = host;

    public VersionInfo Version => MpRuntime.LocalVersion;
    public uint ManifestHash => 0;

    public void BeginLoad(ClientSnapshot snapshot)
    {
        var folder = Saves.SaveContext.EnterClient(_host.Value.ToString("X16"));
        var path = PotatoPeeler.SaveSlots.PathFor(0);
        File.WriteAllBytes(path, snapshot.Save);
        LastStatus = $"wrote {snapshot.Save.Length} B to {path}";
        Plugin.L.LogMessage("[mp] " + LastStatus);
        _startedAt = Time.realtimeSinceStartup;
        _loading = true;
        _sawLoadingScene = false;

        var menu = UnityEngine.Object.FindObjectOfType<PotatoPeeler.MainMenuController>();
        if (menu != null)
        {
            menu.LoadSlot(0);
            LastStatus = "loading from the main menu";
        }
        else if (PotatoPeeler.SaveManager.Instance != null)
        {
            PotatoPeeler.SaveManager.Instance.ReloadIntoSlot(0);
            LastStatus = "reloading the shift";
        }
        else
        {
            LastStatus = "no menu or shift to load from";
            _loading = false;
        }
    }

    public ClientLoadStatus PollLoad(double now)
    {
        if (!_loading) return ClientLoadStatus.Failed(LastStatus);
        var scene = SceneManager.GetActiveScene().name;
        if (scene != "Gameplay") _sawLoadingScene = true;
        var waited = Time.realtimeSinceStartup - _startedAt;
        bool ready = scene == "Gameplay" && PotatoPeeler.SaveManager.Instance != null
                     && UnityEngine.Object.FindObjectOfType<PotatoPeeler.Player>() != null && waited > 2f
                     && (_sawLoadingScene || waited > 6f);
        if (ready)
        {
            _loading = false;
            LastStatus = $"loaded in {waited:0.0} s";
            Plugin.L.LogMessage("[mp] " + LastStatus);
            return ClientLoadStatus.Done;
        }
        if (waited > 170f) return ClientLoadStatus.Failed("the shift did not load in time");
        return new ClientLoadStatus(LoadState.Loading, (byte)Math.Min(99, waited * 4));
    }

    public void OnSessionEnded(LeaveReason reason)
    {
        LastStatus = "ended: " + reason;
        Saves.SaveContext.LeaveClient();
    }
}
