namespace PotatoPleaseMP.Core.Net;

public enum LinkSend : byte { Ok, Busy, Failed }

[Flags]
public enum LinkFlags : byte
{
    Unreliable = 0,
    Reliable = 1,
    /// <summary>Re-open a broken session. Only Connect uses it: on an established peer a silent restart would hide lost data.</summary>
    RestartBroken = 2,
    /// <summary>Send on the calling thread now (a Bye that must leave before we stop).</summary>
    Urgent = 4,
}

/// <summary>Steam's <c>ESteamNetworkingConnectionState</c> values for a messages session.</summary>
public enum LinkState : int { None = 0, Connecting = 1, FindingRoute = 2, Connected = 3, ClosedByPeer = 4, ProblemDetectedLocally = 5 }

/// <summary>Per-peer link status as <c>GetSessionConnectionInfo</c> reports it (spec 17 §8).</summary>
public struct LinkStatus
{
    public LinkState State;
    public int PingMs;
    public float QualityLocal, QualityRemote;
    public float OutBytesPerSec, InBytesPerSec;
    public int SendRate;
    public int PendingReliable, PendingUnreliable, UnackedReliable;
    public long QueueTimeUs;
    public bool Relayed;
    public string? Pop;
    public int EndReason;
    public string? EndDebug;
}

/// <summary>
/// The slice of a connectionless P2P message API that <see cref="P2PTransport"/> needs (spec 17 §1): implemented by
/// the plugin's <c>SteamLink</c> over ISteamNetworkingMessages, and by <see cref="LoopbackLink"/> in-process.
/// Channels 0 (control), 1 (reliable data) and 2 (unreliable data); all reliable traffic to one peer is one ordered stream.
/// </summary>
public interface IP2PLink
{
    ulong LocalId { get; }
    LinkSend Send(ulong to, ReadOnlySpan<byte> data, int channel, LinkFlags flags);
    /// <summary>Appends every message waiting on <paramref name="channel"/>.</summary>
    void Receive(int channel, List<(ulong From, byte[] Data)> into);
    /// <summary>Accepts (or opens) a session with a peer that sent first.</summary>
    bool Accept(ulong peer);
    /// <summary>Closes the session; queued, unsent data is discarded.</summary>
    void Close(ulong peer);
    LinkStatus Status(ulong peer);
}

/// <summary>Per-peer transport statistics (spec 17 §8).</summary>
public sealed class PeerStats
{
    public int RttMs = -1;
    public double LastHeardAgoS;
    public long MsgsSent, MsgsRecv, BytesSent, BytesRecv;
    public long QueuedReliableBytes;
    public long BusyCount, UnreliableDropped, Oversize;
    public LinkStatus Link;

    internal void OnSent(int bytes) { MsgsSent++; BytesSent += bytes; }
    internal void OnRecv(int bytes) { MsgsRecv++; BytesRecv += bytes; }

    public PeerStats Clone() => (PeerStats)MemberwiseClone();
}

/// <summary>Steam relay POP id decode: chars <c>&gt;&gt;16, &gt;&gt;8, &amp;0xFF, &gt;&gt;24</c> ("fra", "iad", 4-char ids).</summary>
public static class PopId
{
    public static string? Decode(uint pop)
    {
        if (pop == 0) return null;
        Span<char> c = stackalloc char[4];
        int n = 0;
        foreach (var b in new[] { (byte)(pop >> 16), (byte)(pop >> 8), (byte)pop, (byte)(pop >> 24) })
            if (b != 0) c[n++] = (char)b;
        return new string(c[..n]);
    }
}
