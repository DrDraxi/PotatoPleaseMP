using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Session;

/// <summary>
/// What the host session needs from the game (spec 02 §3.4). The plugin's <c>HostWorld</c> implements it over the real
/// game (BuildSnapshot, gates); tests use fakes. All calls on the main thread.
/// </summary>
public interface IHostWorld
{
    /// <summary>Versions compared at the handshake; <see cref="VersionInfo.ManifestHash"/> is re-checked at ClientReady.</summary>
    VersionInfo Version { get; }
    /// <summary>Game difficulty sent in Welcome.</summary>
    byte Difficulty { get; }
    /// <summary>Game-side join gate (cinematics, story choice, execution, final scene); null = open (spec 02 §3.3).</summary>
    QueueReason? JoinGate();
    /// <summary>The ending is showing: joins are refused with ShiftOver.</summary>
    bool IsShiftOver { get; }
    /// <summary>
    /// Captures the world in this frame (the gather point). Builds the snapshot synchronously; the file bytes may be
    /// produced later (the save write runs on a worker), see <see cref="HostSnapshot.Save"/>.
    /// </summary>
    HostSnapshot CaptureSnapshot(SnapshotReason reason);
}

/// <summary>A captured world: metadata now, the save file bytes when <see cref="Save"/> completes.</summary>
public sealed class HostSnapshot
{
    public ulong SaveUtcTicks { get; init; }
    public ushort SaveVersion { get; init; }
    public Task<byte[]> Save { get; init; } = Task.FromResult(Array.Empty<byte>());
}

public enum LoadState : byte { Loading, Done, Failed }

/// <summary>Progress of a client world load.</summary>
public readonly record struct ClientLoadStatus(LoadState State, byte Percent, string? Error = null)
{
    public static ClientLoadStatus Done => new(LoadState.Done, 100);
    public static ClientLoadStatus Failed(string why) => new(LoadState.Failed, 0, why);
}

/// <summary>A fully received snapshot handed to the client world.</summary>
public sealed class ClientSnapshot
{
    public SnapshotInfo Info { get; init; }
    public byte[] Save { get; init; } = Array.Empty<byte>();
    public uint SaveCrc { get; init; }
    /// <summary>Join extras by module id (already applied before load by the registered providers).</summary>
    public IReadOnlyDictionary<byte, byte[]> Extras { get; init; } = new Dictionary<byte, byte[]>();
}

/// <summary>
/// What the client session needs from the game (spec 02 §3.4): the plugin's <c>ClientWorld</c> (menu detour, Saves
/// hand-off, ShiftLoader), or a bot's in-memory world. All calls on the main thread.
/// </summary>
public interface IClientWorld
{
    VersionInfo Version { get; }
    /// <summary>Starts loading a received world (writes the save, loads the scene). Called once per snapshot.</summary>
    void BeginLoad(ClientSnapshot snapshot);
    /// <summary>Polled every update while loading.</summary>
    ClientLoadStatus PollLoad(double now);
    /// <summary>Scene manifest hash of the loaded world (ClientReady).</summary>
    uint ManifestHash { get; }
    /// <summary>The session ended (any reason): restore whatever the join changed.</summary>
    void OnSessionEnded(LeaveReason reason);
}

/// <summary>
/// Per-module join-in-progress data (spec 02 §6). Registered at plugin load, applied in registration order. The extras
/// blob is repeated <c>{u8 moduleId, u32 len, bytes}</c>; unknown ids are skipped by length.
/// </summary>
public interface IJoinStateProvider
{
    byte ModuleId { get; }
    /// <summary>Host, main thread, right after the snapshot is built (same frame).</summary>
    void Capture(BinaryWriter w);
    /// <summary>Client, before the world load starts (PlayerPrefs, settings).</summary>
    void ApplyBeforeLoad(BinaryReader r);
    /// <summary>Client, after the world loaded, before the buffered world changes are replayed.</summary>
    void ApplyAfterLoad(BinaryReader r);
}

/// <summary>Join extras container codec.</summary>
public static class JoinExtras
{
    public const int MaxModules = 64;

    public static byte[] Build(IEnumerable<IJoinStateProvider> providers)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        foreach (var p in providers)
        {
            using var part = new MemoryStream();
            using (var pw = new BinaryWriter(part, System.Text.Encoding.UTF8, leaveOpen: true)) p.Capture(pw);
            bw.Write(p.ModuleId);
            bw.Write((uint)part.Length);
            bw.Write(part.GetBuffer(), 0, (int)part.Length);
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Splits an extras blob by module id; malformed data throws <see cref="WireFormatException"/>.</summary>
    public static Dictionary<byte, byte[]> Parse(ReadOnlySpan<byte> extras)
    {
        var map = new Dictionary<byte, byte[]>();
        var r = new NetReader(extras);
        while (!r.IsEnd)
        {
            byte id = r.U8();
            uint len = r.U32();
            if (len > (uint)r.Remaining) throw new WireFormatException($"Extras part {id} of {len} B past the end.");
            if (map.Count >= MaxModules) throw new WireFormatException("Too many extras parts.");
            map[id] = r.Raw((int)len).ToArray();
        }
        return map;
    }
}
