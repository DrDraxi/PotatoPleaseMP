using PotatoPleaseMP.Core.Net;

namespace PotatoPleaseMP.Core.Session;

/// <summary>Where one player is in the join pipeline (spec 02 §2).</summary>
public enum PlayerPhase : byte { Handshaking, Queued, Downloading, Loading, Applying, Playing, Leaving }

/// <summary>Session-wide phase (spec 02 §2).</summary>
public enum SessionPhase : byte
{
    Idle, HostArming, HostLoading, Hosting, HostingFinal, Ending, Closing,
    ClientJoining, ClientPlaying, ClientResyncing, ClientLeaving,
}

[Flags]
public enum PlayerFlags : byte { None = 0, Host = 1, Ready = 2, NotResponding = 4, Paused = 8, Busy = 16, Bot = 32 }

/// <summary>Session refusal codes (spec 02 §5).</summary>
public enum RejectCode : byte
{
    Protocol = 1, ModVersion = 2, GameBuild = 3, SaveVersion = 4, Manifest = 5, Full = 6, Kicked = 7, ShiftOver = 8,
    NotMember = 9, SnapshotFailed = 10, QueueTimeout = 11, Malformed = 12,
}

/// <summary>Why a player left (spec 02 §5).</summary>
public enum LeaveReason : byte
{
    None = 0, Left = 1, Kicked = 2, TimedOut = 3, HostLeft = 4, ShiftEnded = 5, Replaced = 6, FellBehind = 7, SaveGuard = 8,
    HostLost = 9, LoadFailed = 10, Rejected = 11,
}

/// <summary>Why a joiner waits (spec 02 §3.3). 0 on the wire means "open".</summary>
public enum QueueReason : byte { None = 0, HostLoading = 1, Cinematic = 2, StoryChoice = 3, Execution = 4, OtherJoin = 5, FinalScene = 6 }

/// <summary>Why a snapshot is sent (SnapshotInfo.reason).</summary>
public enum SnapshotReason : byte { Join = 0, Resync = 1, HostReload = 2 }

[Flags]
public enum HelloFlags : byte { None = 0, Bot = 1, Rejoin = 2, TestHarness = 4 }

[Flags]
public enum HeartbeatFlags : byte { None = 0, Paused = 1, Busy = 2, AltTabbed = 4 }

/// <summary>One registry row, identical on every peer (the host is the source).</summary>
public sealed class PlayerInfo
{
    public PeerId Peer;
    /// <summary>0 = host, 1.. clients; also the colour index and the default station.</summary>
    public byte Slot;
    public ulong SteamId;
    public string Name = "";
    public PlayerPhase Phase;
    public PlayerFlags Flags;
    public ushort RttMs;
    public byte StationIndex = 0xFF;
    public bool IsLocal;

    public bool IsHost => Slot == 0;
    public bool InGame => Phase == PlayerPhase.Playing;

    public PlayerInfo Clone() => (PlayerInfo)MemberwiseClone();
    public override string ToString() => $"#{Slot} {Name} ({Phase})";
}

/// <summary>Versions compared at the handshake (spec 02 §5, check order).</summary>
public sealed record VersionInfo(ushort Proto, string ModVersion, string GameVersion, uint SteamBuildId, ushort SaveVersion,
    uint ManifestHash = 0);
