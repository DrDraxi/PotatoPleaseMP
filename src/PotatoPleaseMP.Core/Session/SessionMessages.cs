using PotatoPleaseMP.Core.Wire;

namespace PotatoPleaseMP.Core.Session;

/// <summary>Codecs of the session ids 0x00-0x0E (spec 02 §5). Little-endian; str8 = u8 length + UTF-8.</summary>
public static class SessionMessages
{
    public const int MaxNameBytes = 96;   // 24 chars of up to 4 UTF-8 bytes
    public const int MaxPlayersOnWire = 16;

    public static void AttachCodecs(MessageRegistry g)
    {
        g.SetCodec(MsgId.Hello, () => new Hello());
        g.SetCodec(MsgId.Welcome, () => new Welcome());
        g.SetCodec(MsgId.Reject, () => new Reject());
        g.SetCodec(MsgId.Queued, () => new Queued());
        g.SetCodec(MsgId.SnapshotInfo, () => new SnapshotInfo());
        g.SetCodec(MsgId.LoadProgress, () => new LoadProgress());
        g.SetCodec(MsgId.ClientReady, () => new ClientReady());
        g.SetCodec(MsgId.PlayerAdded, () => new PlayerAdded());
        g.SetCodec(MsgId.PlayerRemoved, () => new PlayerRemoved());
        g.SetCodec(MsgId.PlayerUpdate, () => new PlayerUpdate());
        g.SetCodec(MsgId.Heartbeat, () => new Heartbeat());
        g.SetCodec(MsgId.Goodbye, () => new Goodbye());
        g.SetCodec(MsgId.SessionState, () => new SessionStateMsg());
        g.SetCodec(MsgId.SetReady, () => new SetReady());
        g.SetCodec(MsgId.Resync, () => new ResyncMsg());
    }

    /// <summary>PlayerInfo on the wire: u8 slot, u64 steamId, str8 name, u8 phase, u8 flags, u8 stationIndex (13 B + name).</summary>
    public static void WritePlayer(NetWriter w, PlayerInfo p)
    {
        w.U8(p.Slot); w.U64(p.SteamId); w.Str8(p.Name); w.U8((byte)p.Phase); w.U8((byte)p.Flags); w.U8(p.StationIndex);
    }

    public static PlayerInfo ReadPlayer(ref NetReader r)
    {
        var p = new PlayerInfo { Slot = r.U8(), SteamId = r.U64(), Name = r.Str8(MaxNameBytes) };
        byte phase = r.U8();
        if (phase > (byte)PlayerPhase.Leaving) throw new WireFormatException($"Bad player phase {phase}.");
        p.Phase = (PlayerPhase)phase;
        p.Flags = (PlayerFlags)r.U8();
        p.StationIndex = r.U8();
        return p;
    }

    /// <summary>Encodes a message's payload (no id/len).</summary>
    public static byte[] Encode<T>(in T msg) where T : IWireMessage
    {
        using var w = new NetWriter(64);
        msg.Write(w);
        return w.ToArray();
    }

    /// <summary>Decodes a payload; malformed bytes throw <see cref="WireFormatException"/>.</summary>
    public static T Decode<T>(ReadOnlySpan<byte> payload) where T : struct, IWireMessage
    {
        var m = new T();
        var r = new NetReader(payload);
        m.Read(ref r);
        return m;
    }

    internal static T CheckEnum<T>(byte v, T max) where T : struct, Enum
    {
        if (v > Convert.ToByte(max)) throw new WireFormatException($"{typeof(T).Name} value {v} out of range.");
        return (T)(object)v;
    }
}

/// <summary>0x00 C->H: u16 proto, str8 modVersion, str8 gameVersion, u32 steamBuildId, u16 saveVersion, u64 steamId, str8 name, u32 manifestHash, u8 flags.</summary>
public struct Hello : IWireMessage
{
    public ushort Proto; public string ModVersion; public string GameVersion; public uint SteamBuildId; public ushort SaveVersion;
    public ulong SteamId; public string Name; public uint ManifestHash; public HelloFlags Flags;
    public MsgId Id => MsgId.Hello;

    public void Write(NetWriter w)
    {
        w.U16(Proto); w.Str8(ModVersion ?? ""); w.Str8(GameVersion ?? ""); w.U32(SteamBuildId); w.U16(SaveVersion);
        w.U64(SteamId); w.Str8(Name ?? ""); w.U32(ManifestHash); w.U8((byte)Flags);
    }

    public void Read(ref NetReader r)
    {
        Proto = r.U16(); ModVersion = r.Str8(64); GameVersion = r.Str8(64); SteamBuildId = r.U32(); SaveVersion = r.U16();
        SteamId = r.U64(); Name = r.Str8(); ManifestHash = r.U32(); Flags = (HelloFlags)r.U8();
        r.ExpectEnd();
    }
}

/// <summary>
/// 0x01 H->C: u32 sessionId, u8 yourSlot, u8 maxPlayers, u16 tickRate, u32 manifestHash, u8 sessionPhase, u8 difficulty,
/// u8 n + n x PlayerInfo, u32 hostTick (spec 01 §6.2: the joiner's clock starts from it).
/// </summary>
public struct Welcome : IWireMessage
{
    public uint SessionId; public byte YourSlot; public byte MaxPlayers; public ushort TickRate; public uint ManifestHash;
    public SessionPhase Phase; public byte Difficulty; public List<PlayerInfo> Players; public uint HostTick;
    public MsgId Id => MsgId.Welcome;

    public void Write(NetWriter w)
    {
        w.U32(SessionId); w.U8(YourSlot); w.U8(MaxPlayers); w.U16(TickRate); w.U32(ManifestHash); w.U8((byte)Phase); w.U8(Difficulty);
        var list = Players ?? new List<PlayerInfo>();
        w.U8((byte)list.Count);
        foreach (var p in list) SessionMessages.WritePlayer(w, p);
        w.U32(HostTick);
    }

    public void Read(ref NetReader r)
    {
        SessionId = r.U32(); YourSlot = r.U8(); MaxPlayers = r.U8(); TickRate = r.U16(); ManifestHash = r.U32();
        Phase = SessionMessages.CheckEnum(r.U8(), SessionPhase.ClientLeaving); Difficulty = r.U8();
        int n = r.U8();
        if (n > SessionMessages.MaxPlayersOnWire) throw new WireFormatException($"Welcome lists {n} players.");
        Players = new List<PlayerInfo>(n);
        for (int i = 0; i < n; i++) Players.Add(SessionMessages.ReadPlayer(ref r));
        HostTick = r.U32();
        r.ExpectEnd();
    }
}

/// <summary>0x02 H->C: u8 RejectCode, str8 detail. Sent as the transport's final message.</summary>
public struct Reject : IWireMessage
{
    public RejectCode Code; public string Detail;
    public MsgId Id => MsgId.Reject;
    public void Write(NetWriter w) { w.U8((byte)Code); w.Str8(Clip(Detail)); }
    public void Read(ref NetReader r) { Code = (RejectCode)r.U8(); Detail = r.Str8(); r.ExpectEnd(); }

    internal static string Clip(string? s)
    {
        s ??= "";
        while (System.Text.Encoding.UTF8.GetByteCount(s) > 255) s = s[..^1];
        return s;
    }
}

/// <summary>0x03 H->C: u8 QueueReason, u8 position, u16 waitedSec.</summary>
public struct Queued : IWireMessage
{
    public QueueReason Reason; public byte Position; public ushort WaitedSec;
    public MsgId Id => MsgId.Queued;
    public void Write(NetWriter w) { w.U8((byte)Reason); w.U8(Position); w.U16(WaitedSec); }
    public void Read(ref NetReader r) { Reason = (QueueReason)r.U8(); Position = r.U8(); WaitedSec = r.U16(); r.ExpectEnd(); }
}

/// <summary>0x04 H->C: u32 snapshotId, u8 reason, u64 saveUtcTicks, u16 saveVersion, u16 saveBlobId, u16 extrasBlobId. The gather point.</summary>
public struct SnapshotInfo : IWireMessage
{
    public uint SnapshotId; public SnapshotReason Reason; public ulong SaveUtcTicks; public ushort SaveVersion;
    public ushort SaveBlobId; public ushort ExtrasBlobId;
    public MsgId Id => MsgId.SnapshotInfo;

    public void Write(NetWriter w)
    {
        w.U32(SnapshotId); w.U8((byte)Reason); w.U64(SaveUtcTicks); w.U16(SaveVersion); w.U16(SaveBlobId); w.U16(ExtrasBlobId);
    }

    public void Read(ref NetReader r)
    {
        SnapshotId = r.U32(); Reason = SessionMessages.CheckEnum(r.U8(), SnapshotReason.HostReload); SaveUtcTicks = r.U64();
        SaveVersion = r.U16(); SaveBlobId = r.U16(); ExtrasBlobId = r.U16();
        r.ExpectEnd();
    }
}

/// <summary>0x05 C->H: u8 PlayerPhase, u8 percent.</summary>
public struct LoadProgress : IWireMessage
{
    public PlayerPhase Phase; public byte Percent;
    public MsgId Id => MsgId.LoadProgress;
    public void Write(NetWriter w) { w.U8((byte)Phase); w.U8(Percent); }
    public void Read(ref NetReader r)
    {
        Phase = SessionMessages.CheckEnum(r.U8(), PlayerPhase.Leaving); Percent = r.U8();
        if (Percent > 100) throw new WireFormatException($"Load percent {Percent}.");
        r.ExpectEnd();
    }
}

/// <summary>0x06 C->H: u32 snapshotId, u32 manifestHash, u16 replayedCount.</summary>
public struct ClientReady : IWireMessage
{
    public uint SnapshotId; public uint ManifestHash; public ushort Replayed;
    public MsgId Id => MsgId.ClientReady;
    public void Write(NetWriter w) { w.U32(SnapshotId); w.U32(ManifestHash); w.U16(Replayed); }
    public void Read(ref NetReader r) { SnapshotId = r.U32(); ManifestHash = r.U32(); Replayed = r.U16(); r.ExpectEnd(); }
}

/// <summary>0x07 H->all: PlayerInfo.</summary>
public struct PlayerAdded : IWireMessage
{
    public PlayerInfo Player;
    public MsgId Id => MsgId.PlayerAdded;
    public void Write(NetWriter w) => SessionMessages.WritePlayer(w, Player);
    public void Read(ref NetReader r) { Player = SessionMessages.ReadPlayer(ref r); r.ExpectEnd(); }
}

/// <summary>0x08 H->all: u8 slot, u8 LeaveReason.</summary>
public struct PlayerRemoved : IWireMessage
{
    public byte Slot; public LeaveReason Reason;
    public MsgId Id => MsgId.PlayerRemoved;
    public void Write(NetWriter w) { w.U8(Slot); w.U8((byte)Reason); }
    public void Read(ref NetReader r) { Slot = r.U8(); Reason = (LeaveReason)r.U8(); r.ExpectEnd(); }
}

/// <summary>0x09 H->all: u8 slot, u8 phase, u8 flags, u16 rttMs, u8 stationIndex.</summary>
public struct PlayerUpdate : IWireMessage
{
    public byte Slot; public PlayerPhase Phase; public PlayerFlags Flags; public ushort RttMs; public byte StationIndex;
    public MsgId Id => MsgId.PlayerUpdate;
    public void Write(NetWriter w) { w.U8(Slot); w.U8((byte)Phase); w.U8((byte)Flags); w.U16(RttMs); w.U8(StationIndex); }
    public void Read(ref NetReader r)
    {
        Slot = r.U8(); Phase = SessionMessages.CheckEnum(r.U8(), PlayerPhase.Leaving); Flags = (PlayerFlags)r.U8();
        RttMs = r.U16(); StationIndex = r.U8();
        r.ExpectEnd();
    }
}

/// <summary>
/// 0x0A both, unreliable: u32 sendMs, u32 echoMs, u8 flags. <c>echoMs</c> is the peer's last sendMs plus how long we held
/// it, so the receiver's RTT is simply <c>nowMs - echoMs</c>.
/// </summary>
public struct Heartbeat : IWireMessage
{
    public uint SendMs; public uint EchoMs; public HeartbeatFlags Flags;
    public MsgId Id => MsgId.Heartbeat;
    public void Write(NetWriter w) { w.U32(SendMs); w.U32(EchoMs); w.U8((byte)Flags); }
    public void Read(ref NetReader r) { SendMs = r.U32(); EchoMs = r.U32(); Flags = (HeartbeatFlags)r.U8(); r.ExpectEnd(); }
}

/// <summary>0x0B both: u8 LeaveReason, str8 text. The final message of a disconnect.</summary>
public struct Goodbye : IWireMessage
{
    public LeaveReason Reason; public string Text;
    public MsgId Id => MsgId.Goodbye;
    public void Write(NetWriter w) { w.U8((byte)Reason); w.Str8(Reject.Clip(Text)); }
    public void Read(ref NetReader r) { Reason = (LeaveReason)r.U8(); Text = r.Str8(); r.ExpectEnd(); }
}

/// <summary>0x0C H->all: u8 sessionPhase, u8 gate (QueueReason or 0), u8 maxPlayers, u8 count.</summary>
public struct SessionStateMsg : IWireMessage
{
    public SessionPhase Phase; public QueueReason Gate; public byte MaxPlayers; public byte Count;
    public MsgId Id => MsgId.SessionState;
    public void Write(NetWriter w) { w.U8((byte)Phase); w.U8((byte)Gate); w.U8(MaxPlayers); w.U8(Count); }
    public void Read(ref NetReader r)
    {
        Phase = SessionMessages.CheckEnum(r.U8(), SessionPhase.ClientLeaving); Gate = (QueueReason)r.U8();
        MaxPlayers = r.U8(); Count = r.U8();
        r.ExpectEnd();
    }
}

/// <summary>0x0D C->H: u8 context (0 general, 1 final gather, 2 depart), u8 ready.</summary>
public struct SetReady : IWireMessage
{
    public byte Context; public bool Ready;
    public MsgId Id => MsgId.SetReady;
    public void Write(NetWriter w) { w.U8(Context); w.Bool(Ready); }
    public void Read(ref NetReader r) { Context = r.U8(); Ready = r.Bool(); r.ExpectEnd(); }
}

/// <summary>0x0E: C->H u8 moduleId + u32 detail = request; H->C empty = "snapshot follows".</summary>
public struct ResyncMsg : IWireMessage
{
    public bool IsRequest; public byte ModuleId; public uint Detail;
    public MsgId Id => MsgId.Resync;

    public void Write(NetWriter w)
    {
        if (!IsRequest) return;
        w.U8(ModuleId); w.U32(Detail);
    }

    public void Read(ref NetReader r)
    {
        if (r.IsEnd) { IsRequest = false; return; }
        IsRequest = true; ModuleId = r.U8(); Detail = r.U32();
        r.ExpectEnd();
    }
}
