namespace PotatoPleaseMP.Core.Net;

/// <summary>
/// A transport address: a SteamID64 for Steam peers, or a universe-0 id (never a Steam user) for bots and Direct UDP
/// peers (spec 17 §3). The player's real SteamID travels in the session handshake; this is only an address.
/// </summary>
public readonly record struct PeerId(ulong Value)
{
    public static readonly PeerId None = default;
    public bool IsValid => Value != 0;
    /// <summary>Universe (top 8 bits) 0: cannot be a Steam account (bots, Direct UDP).</summary>
    public bool IsUniverseZero => (Value >> 56) == 0;
    public bool IsBot => BotIds.IsBot(this);
    public bool IsUdp => UdpIds.IsUdp(this);
    public override string ToString() => $"0x{Value:X16}";
}

public enum Delivery : byte { Reliable, Unreliable }
public enum TransportKind : byte { Steam, Udp, Loopback, Composite }
public enum NetEventKind : byte { Connected, Disconnected, Data }

public enum DisconnectReason : byte { Local, RemoteBye, Timeout, ConnectionLost, ConnectFailed, Rejected, Replaced, Stopped }

/// <summary>Transport-level refusal (control frame Reject). Session refusals use the session's Reject message instead.</summary>
public enum RejectReason : byte { None, Full, NotAllowed, Version, NotHosting }

/// <summary>
/// One transport event. For <see cref="NetEventKind.Disconnected"/>: <see cref="Data"/> is the peer's final payload
/// (Bye), <see cref="Code"/> its Bye code or the Reject reason, <see cref="Detail"/> a diagnostic text.
/// </summary>
public readonly record struct NetEvent(NetEventKind Kind, PeerId Peer, Delivery Delivery, byte[]? Data,
    DisconnectReason Reason = 0, byte Code = 0, string? Detail = null);

/// <summary>
/// The shared transport contract (spec 17 §3): Steam P2P, Direct UDP, loopback (bots/tests) and the composite all
/// implement it. Main thread only; <see cref="Poll"/> is the only place events are produced.
/// </summary>
public interface ITransport : IDisposable
{
    TransportKind Kind { get; }
    PeerId LocalId { get; }
    bool IsRunning { get; }

    void StartHost(int maxPeers, Func<PeerId, bool>? acceptFilter);
    /// <summary>Client: open a connection to the host (UDP: host = None, endpoint "ip:port").</summary>
    void Connect(PeerId host, string? endpoint = null);
    /// <summary>Host: open/accept a session to a known joiner (Steam lobby member) by sending first.</summary>
    void Expect(PeerId peer);
    /// <summary>Copies or sends before returning. False: not connected or oversize.</summary>
    bool Send(PeerId to, ReadOnlySpan<byte> data, Delivery d);
    void Poll(List<NetEvent> into);
    /// <summary>Says Bye (with <paramref name="final"/> as its payload) and drops the peer.</summary>
    void Disconnect(PeerId peer, byte code = 0, ReadOnlySpan<byte> final = default);
    /// <summary>Bye to all; sessions close after the linger.</summary>
    void Stop(byte code = 0);
    /// <summary>Our queue plus the link's unsent reliable bytes; -1 for an unknown peer.</summary>
    int PendingReliableBytes(PeerId peer);
    bool TryGetStats(PeerId peer, out PeerStats stats);
    void CollectPeers(List<PeerId> into);
}

/// <summary>Limits every transport honours (spec 17 §3).</summary>
public static class TransportLimits
{
    public const int MaxReliable = 512 * 1024;
    public const int MaxUnreliable = 1100;
}

/// <summary>
/// Bot peer ids (spec 17 §3, spec 18 §2): <c>0x00B07B07_000000nn</c>, n 1..15. Universe 0, so never a Steam user; the
/// composite transport routes them to the loopback hub only.
/// </summary>
public static class BotIds
{
    public const ulong Prefix = 0x00B07B07UL;
    /// <summary>Highest index the id range allows.</summary>
    public const int MaxIndex = 15;
    /// <summary>Most bots the harness runs at once (spec 18).</summary>
    public const int MaxBots = 7;

    public static PeerId Make(int index)
    {
        if (index < 1 || index > MaxIndex)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Bot index must be in 1..{MaxIndex}.");
        return new PeerId((Prefix << 32) | (uint)index);
    }

    public static bool IsBot(PeerId p)
    {
        if ((p.Value >> 32) != Prefix) return false;
        uint i = (uint)p.Value;
        return i >= 1 && i <= MaxIndex;
    }

    public static int IndexOf(PeerId p) => IsBot(p) ? (int)(uint)p.Value : 0;
}

/// <summary>Direct UDP peer ids: <c>0x00D1EC70_xxxxxxxx</c> (LiteNetLib peer id + 1). Universe 0.</summary>
public static class UdpIds
{
    public const ulong Prefix = 0x00D1EC70UL;
    public static PeerId Make(int liteNetPeerId) => new((Prefix << 32) | (uint)(liteNetPeerId + 1));
    public static bool IsUdp(PeerId p) => (p.Value >> 32) == Prefix && (uint)p.Value != 0;
}
