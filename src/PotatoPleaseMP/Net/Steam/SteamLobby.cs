using System.Runtime.InteropServices;
using System.Text;

namespace PotatoPleaseMP.Net.Steam;

/// <summary>
/// Steam lobbies and invites (spec 17 §7). The host creates a friends-only lobby and sets rich presence
/// <c>connect = +connect_lobby &lt;id&gt;</c>, so friends get "Join Game" in Steam and the overlay invite dialog works. A
/// joiner (invite accepted while running = callback 333, or a cold start with +connect_lobby on the command line)
/// joins the lobby, reads its owner and connects the session to that SteamID.
/// </summary>
internal static unsafe class SteamLobby
{
    const string Lib = "steam_api64";
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamMatchmaking_v009();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamFriends_v018();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamApps_v009();
    [DllImport(Lib)] static extern ulong SteamAPI_ISteamMatchmaking_CreateLobby(IntPtr self, int type, int max);
    [DllImport(Lib)] static extern ulong SteamAPI_ISteamMatchmaking_JoinLobby(IntPtr self, ulong lobby);
    [DllImport(Lib)] static extern void SteamAPI_ISteamMatchmaking_LeaveLobby(IntPtr self, ulong lobby);
    [DllImport(Lib)] static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyOwner(IntPtr self, ulong lobby);
    [DllImport(Lib)] static extern byte SteamAPI_ISteamMatchmaking_SetLobbyData(IntPtr self, ulong lobby, byte* key, byte* value);
    [DllImport(Lib)] static extern byte SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, byte* key, byte* value);
    [DllImport(Lib)] static extern void SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(IntPtr self, ulong lobby);
    [DllImport(Lib)] static extern int SteamAPI_ISteamApps_GetLaunchCommandLine(IntPtr self, byte* buf, int size);

    static IntPtr MM => SteamAPI_SteamMatchmaking_v009();
    static IntPtr FR => SteamAPI_SteamFriends_v018();

    public static ulong Lobby { get; private set; }
    public static string State { get; private set; } = "none";
    static bool _hooked, _coldStartChecked;

    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    static void SetData(string k, string v)
    {
        fixed (byte* pk = Z(k)) fixed (byte* pv = Z(v)) SteamAPI_ISteamMatchmaking_SetLobbyData(MM, Lobby, pk, pv);
    }

    static void SetPresence(string k, string v)
    {
        fixed (byte* pk = Z(k)) fixed (byte* pv = Z(v)) SteamAPI_ISteamFriends_SetRichPresence(FR, pk, pv);
    }

    /// <summary>Host: create the lobby (friends only = 1).</summary>
    public static void Create(int maxPlayers)
    {
        if (!SteamNative.Alive || SteamBoot.Mode != SteamMode.Full) { State = "steam unavailable"; return; }
        var call = SteamAPI_ISteamMatchmaking_CreateLobby(MM, 1, maxPlayers);
        State = "creating";
        SteamEvents.Await(call, 513, (p, failed) =>
        {
            if (failed || p == IntPtr.Zero || Marshal.ReadInt32(p, 0) != 1) { State = "create failed"; return; }
            Lobby = (ulong)Marshal.ReadInt64(p, 8);
            SetData("ppmp_proto", "1");
            SetData("ppmp_mod", Plugin.Version);
            SetData("ppmp_host", SteamNative.LocalSteamId.ToString());
            SetPresence("connect", $"+connect_lobby {Lobby}");
            SetPresence("status", "Peeling potatoes together");
            SetPresence("steam_player_group", Lobby.ToString());
            State = "open";
            Plugin.L.LogMessage($"[steam] lobby {Lobby} open; friends can join from Steam.");
        }, 10f);
    }

    [DllImport(Lib, CharSet = CharSet.Ansi)] static extern void SteamAPI_ISteamFriends_ActivateGameOverlay(IntPtr self, string dialog);

    /// <summary>Opens the Steam overlay's friends list (where "Join Game" lives).</summary>
    public static void OpenFriends()
    {
        try { SteamAPI_ISteamFriends_ActivateGameOverlay(FR, "Friends"); } catch (Exception e) { Plugin.L.LogWarning($"[steam] overlay: {e.Message}"); }
    }

    public static void OpenInviteDialog()
    {
        if (Lobby != 0) SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(FR, Lobby);
    }

    public static void Leave()
    {
        if (Lobby != 0 && SteamNative.Alive)
        {
            SteamAPI_ISteamMatchmaking_LeaveLobby(MM, Lobby);
            SetPresence("connect", "");
            SetPresence("steam_player_group", "");
        }
        Lobby = 0;
        State = "none";
    }

    /// <summary>Client: join a lobby, then connect the session to its owner.</summary>
    public static void JoinLobby(ulong lobby)
    {
        if (!SteamNative.Alive) return;
        var call = SteamAPI_ISteamMatchmaking_JoinLobby(MM, lobby);
        State = "joining lobby";
        SteamEvents.Await(call, 504, (p, failed) =>
        {
            if (failed || p == IntPtr.Zero || Marshal.ReadInt32(p, 16) != 1) { State = "lobby join failed"; return; }
            Lobby = lobby;
            var owner = SteamAPI_ISteamMatchmaking_GetLobbyOwner(MM, lobby);
            State = $"in lobby, host {owner}";
            Pending = owner;
        }, 15f);
    }

    /// <summary>Set when a lobby join resolved its host; the runtime connects from the main loop (never inside a callback).</summary>
    public static ulong Pending;

    /// <summary>Main loop: register for invites once Steam is up, handle a cold-start +connect_lobby, start pending joins.</summary>
    public static void Update()
    {
        if (SteamBoot.Mode != SteamMode.Full) return;
        if (!_hooked)
        {
            _hooked = true;
            SteamEvents.On(333, p => { var lobby = (ulong)Marshal.ReadInt64(p, 0); Plugin.L.LogMessage($"[steam] invite accepted: lobby {lobby}"); _invited = lobby; });
        }
        if (!_coldStartChecked)
        {
            _coldStartChecked = true;
            byte* b = stackalloc byte[1024];
            int n = SteamAPI_ISteamApps_GetLaunchCommandLine(SteamAPI_SteamApps_v009(), b, 1024);
            var line = n > 1 ? Encoding.UTF8.GetString(b, Math.Min(n, 1024)).TrimEnd('\0') : "";
            var args = Environment.GetCommandLineArgs();
            var all = line + " " + string.Join(" ", args);
            var i = all.IndexOf("+connect_lobby", StringComparison.Ordinal);
            if (i >= 0 && ulong.TryParse(all[(i + 14)..].Trim().Split(' ')[0], out var lobby)) _invited = lobby;
        }
        if (_invited != 0 && !MpRuntime.Active)
        {
            var l = _invited; _invited = 0;
            JoinLobby(l);
        }
        if (Pending != 0 && !MpRuntime.Active)
        {
            var host = Pending; Pending = 0;
            MpRuntime.Join(host.ToString());
        }
    }

    static ulong _invited;
}
