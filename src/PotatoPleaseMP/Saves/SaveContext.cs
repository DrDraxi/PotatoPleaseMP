namespace PotatoPleaseMP.Saves;

/// <summary>
/// Where the game's save folder points (spec 15 §1): normal (or the isolated test-copy folder), or a client's folder for the
/// host's world while joined. The client folder is outside saves/, so Steam Cloud never sees it, and the client never
/// writes its own saves while joined.
/// </summary>
internal static class SaveContext
{
    public static string? ClientFolder { get; private set; }
    public static bool IsClient => ClientFolder != null;

    static string ProfileRoot => Isolation.Active ? Isolation.ProfileRoot : UnityEngine.Application.persistentDataPath;

    public static string EnterClient(string tag)
    {
        var safe = new string(tag.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        _pendingLeave = false;
        ClientFolder = Path.Combine(ProfileRoot, "multiplayer", "client", safe);
        Directory.CreateDirectory(ClientFolder);
        Plugin.L.LogMessage($"[saves] client context: {ClientFolder}");
        return ClientFolder;
    }

    static bool _pendingLeave;

    /// <summary>
    /// Ends the redirect. While the host's world is still loaded (Gameplay scene) the redirect, and with it the write block,
    /// stays on until the scene changes: an autosave or unload save in that window would otherwise land in the player's own slot.
    /// </summary>
    public static void LeaveClient()
    {
        if (ClientFolder == null) return;
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Gameplay")
        {
            if (!_pendingLeave) Plugin.L.LogMessage("[saves] leaving the host's world: saving stays blocked until it is unloaded.");
            _pendingLeave = true;
            return;
        }
        _pendingLeave = false;
        Plugin.L.LogMessage("[saves] client context left; own saves are untouched.");
        ClientFolder = null;
    }

    /// <summary>Every frame: finish a pending leave once the host's world is gone.</summary>
    public static void Tick()
    {
        if (_pendingLeave && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Gameplay") LeaveClient();
    }
}
