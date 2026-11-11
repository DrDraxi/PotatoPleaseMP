using System.Runtime.InteropServices;
using System.Text;

namespace PotatoPleaseMP.Net.Steam;

/// <summary>
/// Direct P/Invoke into the steam_api64.dll the game already loaded (spec 17 App. A). Interface versions are the ones this
/// DLL exports (Friends v018, Apps v009, ...). Every wrapper checks <see cref="Alive"/> so nothing calls Steam after shutdown.
/// Main thread only.
/// </summary>
internal static unsafe class SteamNative
{
    const string Lib = "steam_api64";
    public static bool Alive { get; private set; }
    public static ulong LocalSteamId { get; private set; }
    static IntPtr _msgs, _nutils, _mm, _friends, _user, _utils, _apps, _stats;

    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamMatchmaking_v009();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamFriends_v018();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamUser_v023();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamUtils_v010();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamApps_v009();
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamUserStats_v013();

    [DllImport(Lib)] static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
    [DllImport(Lib)] static extern byte SteamAPI_ISteamUser_BLoggedOn(IntPtr self);
    [DllImport(Lib)] static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
    [DllImport(Lib)] static extern uint SteamAPI_ISteamUtils_GetAppID(IntPtr self);
    [DllImport(Lib)] static extern byte SteamAPI_ISteamUtils_IsAPICallCompleted(IntPtr self, ulong call, byte* failed);
    [DllImport(Lib)] static extern byte SteamAPI_ISteamUtils_GetAPICallResult(IntPtr self, ulong call, void* buf, int size, int expectedId, byte* failed);
    [DllImport(Lib)] static extern int SteamAPI_ISteamApps_GetAppBuildId(IntPtr self);
    [DllImport(Lib)] static extern ulong SteamAPI_ISteamUserStats_GetNumberOfCurrentPlayers(IntPtr self);
    [DllImport(Lib)] static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);
    [DllImport(Lib)] static extern int SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus(IntPtr self, byte* details);

    public static bool TryInit(out string error)
    {
        try
        {
            _user = SteamAPI_SteamUser_v023(); _friends = SteamAPI_SteamFriends_v018(); _utils = SteamAPI_SteamUtils_v010();
            _apps = SteamAPI_SteamApps_v009(); _mm = SteamAPI_SteamMatchmaking_v009(); _stats = SteamAPI_SteamUserStats_v013();
            _msgs = SteamAPI_SteamNetworkingMessages_SteamAPI_v002(); _nutils = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            error = "steam_api64: " + e.Message;
            return false;
        }
        if (_user == IntPtr.Zero || _friends == IntPtr.Zero || _utils == IntPtr.Zero || _apps == IntPtr.Zero || _mm == IntPtr.Zero
            || _stats == IntPtr.Zero || _msgs == IntPtr.Zero || _nutils == IntPtr.Zero)
        {
            error = "Steam isn't initialised.";
            return false;
        }
        LocalSteamId = SteamAPI_ISteamUser_GetSteamID(_user);
        if (LocalSteamId != Steamworks.SteamUser.GetSteamID().m_SteamID)
        {
            error = "steam_api64 binding mismatch";
            return false;
        }
        SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(_nutils);
        Alive = true;
        error = "";
        return true;
    }

    public static void Shutdown() => Alive = false;

    public static bool LoggedOn => Alive && SteamAPI_ISteamUser_BLoggedOn(_user) != 0;
    public static string PersonaName => Alive ? S(SteamAPI_ISteamFriends_GetPersonaName(_friends)) : "";
    public static uint AppId => Alive ? SteamAPI_ISteamUtils_GetAppID(_utils) : 0;
    public static int BuildId => Alive ? SteamAPI_ISteamApps_GetAppBuildId(_apps) : 0;
    public static ulong RequestNumberOfCurrentPlayers() => Alive ? SteamAPI_ISteamUserStats_GetNumberOfCurrentPlayers(_stats) : 0;

    /// <summary>Relay availability (ESteamNetworkingAvailability: 100 = current).</summary>
    public static int RelayAvailability()
    {
        if (!Alive) return 0;
        byte* buf = stackalloc byte[512];
        return SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus(_nutils, buf);
    }

    /// <summary>Polls a call result into <paramref name="buf"/>; true when it completed (see <paramref name="failed"/>).</summary>
    public static bool TryGetCallResult(ulong call, int expectedId, Span<byte> buf, out bool failed)
    {
        failed = true;
        if (!Alive) return false;
        byte f = 0;
        if (SteamAPI_ISteamUtils_IsAPICallCompleted(_utils, call, &f) == 0) return false;
        fixed (byte* p = buf)
        {
            var ok = SteamAPI_ISteamUtils_GetAPICallResult(_utils, call, p, buf.Length, expectedId, &f) != 0;
            failed = !ok || f != 0;
            return true;
        }
    }

    static string S(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";
    internal static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");
}

/// <summary>Resolves "steam_api64" to the copy the game already loaded (it lives in PotatoPlease_Data/Plugins/x86_64).</summary>
internal static class NativeResolver
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        NativeLibrary.SetDllImportResolver(typeof(NativeResolver).Assembly,
            (name, _, _) => name == "steam_api64" ? GetModuleHandleW("steam_api64.dll") : IntPtr.Zero);
    }
}

/// <summary>Native layouts for ISteamNetworkingMessages (spec 17 App. B, x64, pack 8).</summary>
[StructLayout(LayoutKind.Sequential, Size = 136)]
internal struct SteamNetworkingIdentity
{
    public int Type; public int Size; public ulong SteamId64;
    public static SteamNetworkingIdentity For(ulong id) => new() { Type = 16, Size = 8, SteamId64 = id };
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct SteamNetworkingMessage
{
    [FieldOffset(0)] public byte* Data;
    [FieldOffset(8)] public int Size;
    [FieldOffset(16)] public SteamNetworkingIdentity Peer;
    [FieldOffset(192)] public int Channel;
}

[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct SteamNetConnectionRealTimeStatus
{
    [FieldOffset(0)] public int State; [FieldOffset(4)] public int Ping;
    [FieldOffset(8)] public float QualityLocal; [FieldOffset(12)] public float QualityRemote;
    [FieldOffset(20)] public float OutBytesPerSec; [FieldOffset(28)] public float InBytesPerSec;
    [FieldOffset(32)] public int SendRateBytesPerSecond; [FieldOffset(36)] public int PendingUnreliable;
    [FieldOffset(40)] public int PendingReliable; [FieldOffset(44)] public int SentUnackedReliable;
}

internal static unsafe class SteamMessages
{
    const string Lib = "steam_api64";
    static IntPtr _msgs;
    [DllImport(Lib)] static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
    [DllImport(Lib)] static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, SteamNetworkingIdentity* to, void* data, uint size, int flags, int channel);
    [DllImport(Lib)] static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int channel, IntPtr* msgs, int max);
    [DllImport(Lib)] static extern byte SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, SteamNetworkingIdentity* peer);
    [DllImport(Lib)] static extern byte SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, SteamNetworkingIdentity* peer);
    [DllImport(Lib)] static extern int SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo(IntPtr self, SteamNetworkingIdentity* peer, void* info, SteamNetConnectionRealTimeStatus* status);
    [DllImport(Lib)] static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr msg);

    public const int SendUnreliable = 0, SendNoNagle = 1, SendReliable = 8, SendUseCurrentThread = 16, SendAutoRestartBrokenSession = 32;

    static IntPtr M => _msgs != IntPtr.Zero ? _msgs : (_msgs = SteamAPI_SteamNetworkingMessages_SteamAPI_v002());

    public static int Send(ulong to, ReadOnlySpan<byte> data, int flags, int channel)
    {
        if (!SteamNative.Alive) return 0;
        var id = SteamNetworkingIdentity.For(to);
        fixed (byte* p = data) return SteamAPI_ISteamNetworkingMessages_SendMessageToUser(M, &id, p, (uint)data.Length, flags, channel);
    }

    public static int Receive(int channel, IntPtr* buf, int max) => SteamNative.Alive ? SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(M, channel, buf, max) : 0;
    public static void Release(IntPtr msg) => SteamAPI_SteamNetworkingMessage_t_Release(msg);

    public static bool Accept(ulong peer)
    {
        if (!SteamNative.Alive) return false;
        var id = SteamNetworkingIdentity.For(peer);
        return SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(M, &id) != 0;
    }

    public static void Close(ulong peer)
    {
        if (!SteamNative.Alive) return;
        var id = SteamNetworkingIdentity.For(peer);
        SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(M, &id);
    }

    public static int Status(ulong peer, out SteamNetConnectionRealTimeStatus st)
    {
        st = default;
        if (!SteamNative.Alive) return 0;
        var id = SteamNetworkingIdentity.For(peer);
        byte* info = stackalloc byte[1024];
        fixed (SteamNetConnectionRealTimeStatus* s = &st) return SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo(M, &id, info, s);
    }
}
