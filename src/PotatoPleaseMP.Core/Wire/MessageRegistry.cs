namespace PotatoPleaseMP.Core.Wire;

/// <summary>Who may send an id (spec 01 §7.2). Relayed ids (C→H→all) are <see cref="Both"/>.</summary>
public enum MsgDir : byte { HostToClient = 1, ClientToHost = 2, Both = 3 }

/// <summary>Delivery class (spec 01 §5.1): R reliable, U latest-wins state, UE lossy event, RU reliable edges + unreliable refresh.</summary>
public enum DeliveryClass : byte { R, U, UE, RU }

/// <summary>
/// Router class for the session (spec 01 §5.1, spec 02 §3.2): <see cref="Session"/> always flows; <see cref="WorldChange"/>
/// goes to a joiner only after its gather point and is buffered by it until its world is loaded; <see cref="State"/> only
/// flows between <c>Playing</c> peers (also: a client may send it only once Playing).
/// </summary>
public enum SessionClass : byte { Session, WorldChange, State }

/// <summary>Description of one id.</summary>
public sealed record MsgInfo(MsgId Id, string Name, byte Owner, MsgDir Dir, DeliveryClass Delivery, SessionClass Class,
    int MaxLen, Func<IWireMessage>? Codec = null)
{
    public bool AllowsReliable => Delivery is DeliveryClass.R or DeliveryClass.RU;
    public bool AllowsUnreliable => Delivery is DeliveryClass.U or DeliveryClass.UE or DeliveryClass.RU;
}

/// <summary>
/// The id registry (spec 01 §7). <see cref="CreateDefault"/> holds the whole catalog of §8 with the roadmap's moves;
/// modules attach their codecs with <see cref="SetCodec"/>. <see cref="SelfCheck"/> is the startup self-check.
/// </summary>
public sealed class MessageRegistry
{
    /// <summary>Largest payload of one message; anything bigger goes as a blob (spec 01 §3).</summary>
    public const int MaxPayload = 60 * 1024;
    /// <summary>Largest payload that fits one unreliable datagram (1100 B minus a 7-byte header and 3 bytes of framing).</summary>
    public const int MaxUnreliablePayload = Envelope.MaxUnreliable - Envelope.HeaderSize - Envelope.AckSize - 3;

    /// <summary>Id ranges and their owning spec (spec 01 §7.1, roadmap).</summary>
    public static readonly (byte First, byte Last, byte Owner)[] Ranges =
    {
        (0x00, 0x0F, 2), (0x10, 0x1B, 4), (0x1C, 0x1F, 10), (0x20, 0x2F, 5), (0x30, 0x3F, 6), (0x40, 0x4F, 7),
        (0x50, 0x5F, 8), (0x60, 0x6F, 9), (0x70, 0x7F, 21), (0x80, 0x8F, 11), (0x90, 0x9F, 12), (0xA0, 0xAF, 13),
        (0xB0, 0xBF, 14), (0xC0, 0xCF, 15), (0xD0, 0xDF, 16), (0xE0, 0xE3, 1), (0xE4, 0xE7, 19), (0xE8, 0xEF, 20),
        (0xF0, 0xFF, 18),
    };

    readonly MsgInfo?[] _byId = new MsgInfo?[256];

    public static byte OwnerOf(byte id)
    {
        foreach (var (first, last, owner) in Ranges)
            if (id >= first && id <= last) return owner;
        return 0;
    }

    public MsgInfo? this[MsgId id] => _byId[(byte)id];
    public bool TryGet(byte id, out MsgInfo info)
    {
        info = _byId[id]!;
        return info != null;
    }

    public IEnumerable<MsgInfo> All => _byId.Where(i => i != null)!;

    /// <summary>Adds or replaces an id. Owner defaults to the range owner; <paramref name="cls"/> to the default rule.</summary>
    public MsgInfo Register(MsgId id, MsgDir dir, DeliveryClass delivery, int maxLen = 0, SessionClass? cls = null,
        string? name = null, byte owner = 0, Func<IWireMessage>? codec = null)
    {
        if (maxLen <= 0) maxLen = delivery == DeliveryClass.R ? MaxPayload : MaxUnreliablePayload;
        var info = new MsgInfo(id, name ?? id.ToString(), owner == 0 ? OwnerOf((byte)id) : owner, dir, delivery,
            cls ?? DefaultClass((byte)id, dir, delivery), maxLen, codec);
        _byId[(byte)id] = info;
        return info;
    }

    public void SetCodec(MsgId id, Func<IWireMessage> codec)
    {
        var info = _byId[(byte)id] ?? throw new InvalidOperationException($"{id} is not registered.");
        _byId[(byte)id] = info with { Codec = codec };
    }

    /// <summary>
    /// Default router class: session, blob, robustness-control and test ranges always flow; unreliable state and
    /// client intents need <c>Playing</c>; reliable host output is a world change.
    /// </summary>
    static SessionClass DefaultClass(byte id, MsgDir dir, DeliveryClass delivery)
    {
        if (id <= 0x0F || (id >= 0xE0 && id <= 0xE3) || id >= 0xF0) return SessionClass.Session;
        if (id is 0xED or 0xEE or 0xEF) return SessionClass.Session; // PeerError, Rejoin, CompatReject
        if (delivery is DeliveryClass.U or DeliveryClass.UE) return SessionClass.State;
        if (dir == MsgDir.ClientToHost) return SessionClass.State;
        return SessionClass.WorldChange;
    }

    /// <summary>
    /// Startup self-check (spec 01 §7.2): ids inside the owner's range, unique names, MaxLen within 60 KB (and within one
    /// datagram for unreliable ids), and, when <paramref name="requireCodecFor"/> matches, a codec. Returns the problems.
    /// </summary>
    public List<string> SelfCheck(Func<MsgInfo, bool>? requireCodecFor = null)
    {
        var problems = new List<string>();
        var names = new HashSet<string>();
        foreach (var info in All)
        {
            byte id = (byte)info.Id;
            if (OwnerOf(id) != info.Owner) problems.Add($"0x{id:X2} {info.Name}: owner {info.Owner} outside its range (owner {OwnerOf(id)}).");
            if (!names.Add(info.Name)) problems.Add($"0x{id:X2}: duplicate name {info.Name}.");
            if (info.MaxLen > MaxPayload) problems.Add($"0x{id:X2} {info.Name}: MaxLen {info.MaxLen} > {MaxPayload}.");
            if (info.Delivery is DeliveryClass.U or DeliveryClass.UE && info.MaxLen > MaxUnreliablePayload)
                problems.Add($"0x{id:X2} {info.Name}: unreliable MaxLen {info.MaxLen} > {MaxUnreliablePayload}.");
            if (requireCodecFor != null && requireCodecFor(info) && info.Codec == null)
                problems.Add($"0x{id:X2} {info.Name}: no codec.");
        }
        return problems;
    }

    /// <summary>The full catalog of spec 01 §8 (directions, delivery, caps); session and blob codecs attached.</summary>
    public static MessageRegistry CreateDefault()
    {
        var g = new MessageRegistry();
        const MsgDir H = MsgDir.HostToClient, C = MsgDir.ClientToHost, B = MsgDir.Both;
        const DeliveryClass R = DeliveryClass.R, U = DeliveryClass.U, UE = DeliveryClass.UE, RU = DeliveryClass.RU;

        // session (02)
        g.Register(MsgId.Hello, C, R, 512);
        g.Register(MsgId.Welcome, H, R, 4096);
        g.Register(MsgId.Reject, H, R, 300);
        g.Register(MsgId.Queued, H, R, 16);
        g.Register(MsgId.SnapshotInfo, H, R, 64);
        g.Register(MsgId.LoadProgress, C, R, 8);
        g.Register(MsgId.ClientReady, C, R, 32);
        g.Register(MsgId.PlayerAdded, H, R, 300);
        g.Register(MsgId.PlayerRemoved, H, R, 8);
        g.Register(MsgId.PlayerUpdate, H, R, 16);
        g.Register(MsgId.Heartbeat, B, U, 32);
        g.Register(MsgId.Goodbye, B, R, 300);
        g.Register(MsgId.SessionState, H, R, 16);
        g.Register(MsgId.SetReady, C, R, 8);
        g.Register(MsgId.Resync, B, R, 16);
        // presence (04) and deaths (10)
        g.Register(MsgId.PoseUpdate, B, U, 64);
        g.Register(MsgId.KnifePose, B, U, 64);
        g.Register(MsgId.PresenceEvent, B, R, 16);
        g.Register(MsgId.PresenceInfo, H, R, 2048);
        g.Register(MsgId.ForceTeleport, H, R, 64);
        g.Register(MsgId.PresenceReady, C, R, 16);
        g.Register(MsgId.PlayerDowned, B, R, 256);
        g.Register(MsgId.Respawn, H, R, 128);
        g.Register(MsgId.PlayerStatus, B, R, 16);
        g.Register(MsgId.LifeEvent, H, R, 128);
        // peeling (05)
        g.Register(MsgId.PeelLockReq, C, R, 64);
        g.Register(MsgId.PeelLockReply, H, R, 64);
        g.Register(MsgId.PeelOwnerSet, H, R);
        g.Register(MsgId.CarveRecords, B, R);
        g.Register(MsgId.CarveCheck, B, R, 64);
        g.Register(MsgId.CarveSnapshotReq, B, R, 32);
        g.Register(MsgId.CarveSnapshotInfo, B, R, 128);
        g.Register(MsgId.PeelEnd, C, R);
        g.Register(MsgId.PeelResult, C, R, 128);
        g.Register(MsgId.PeelVerdict, H, R, 32);
        g.Register(MsgId.HiddenItem, B, R, 64);
        g.Register(MsgId.PeelStrictness, H, R, 16);
        g.Register(MsgId.CarveLogReq, C, R, 16);
        g.Register(MsgId.CarveLogSlice, H, R);
        // pile (06)
        g.Register(MsgId.PileFullState, H, R);
        g.Register(MsgId.PileDelta, H, R);
        g.Register(MsgId.PileDigests, H, U);
        g.Register(MsgId.PileCellNack, C, R);
        g.Register(MsgId.PileCellState, H, R);
        g.Register(MsgId.PileDigIntent, C, R);
        g.Register(MsgId.PileDigReject, H, R, 16);
        g.Register(MsgId.PileFullRequest, C, R, 16);
        g.Register(MsgId.PileCounters, H, R, 64);
        g.Register(MsgId.PileFrostSnapshot, H, R);
        g.Register(MsgId.PileProbe, B, R, 128);
        g.Register(MsgId.PileRefill, H, R, 32);
        // bodies (07)
        g.Register(MsgId.BodySpawn, H, R);
        g.Register(MsgId.BodyDespawn, H, R);
        g.Register(MsgId.BodyState, H, U);
        g.Register(MsgId.BodyOwnedState, C, U);
        g.Register(MsgId.BodyRest, H, R);
        g.Register(MsgId.BodyClaim, C, R);
        g.Register(MsgId.BodyAuth, H, R);
        g.Register(MsgId.BodyRelease, C, R, 64);
        g.Register(MsgId.ClientSpawn, C, R, 64);
        g.Register(MsgId.SpawnAdopt, H, R, 16);
        g.Register(MsgId.BodyConsume, C, R);
        g.Register(MsgId.ContainerCount, H, R, 64);
        g.Register(MsgId.BodySlot, H, R);
        g.Register(MsgId.BodyFx, B, UE);
        g.Register(MsgId.BodyScalar, H, R);
        g.Register(MsgId.BodyAudit, B, RU, MaxUnreliablePayload);
        // economy (08)
        g.Register(MsgId.Balance, H, R, 64);
        g.Register(MsgId.Counters, H, R, 128);
        g.Register(MsgId.Pacing, H, R, 128);
        g.Register(MsgId.Quota, H, R, 32);
        g.Register(MsgId.Skill, H, R, 32);
        g.Register(MsgId.UpgradeLevels, H, R);
        g.Register(MsgId.PurchaseReq, C, R, 32);
        g.Register(MsgId.PurchaseRes, H, R, 32);
        g.Register(MsgId.Scoreboard, H, R);
        g.Register(MsgId.AchCredit, H, R, 128);
        g.Register(MsgId.EconNotice, H, R, 32);
        g.Register(MsgId.ShiftSummary, H, R);
        // story (09)
        g.Register(MsgId.QuestState, H, R);
        g.Register(MsgId.StoryIntent, C, R, 128);
        g.Register(MsgId.StoryBeat, H, R, 16);
        g.Register(MsgId.KeyDelta, H, R, 128);
        g.Register(MsgId.LetterEvent, H, R, 128);
        g.Register(MsgId.StoryJoin, H, R);
        g.Register(MsgId.LockReq, C, R, 128);
        g.Register(MsgId.LockState, H, R, 128);
        g.Register(MsgId.ChoiceIntent, C, R, 128);
        g.Register(MsgId.ChoiceResolved, H, R);
        g.Register(MsgId.Cinematic, H, R, 128);
        g.Register(MsgId.CinematicAck, C, R, 16);
        g.Register(MsgId.ActorState, H, U);
        g.Register(MsgId.ActorEvent, H, R, 128);
        g.Register(MsgId.ActorIntent, C, R, 128);
        g.Register(MsgId.PaLine, B, R);
        // interactables (gamemap 08 proposal)
        g.Register(MsgId.InteractReq, C, R, 64);
        g.Register(MsgId.InteractRes, H, R, 32);
        g.Register(MsgId.ObjState, H, R, 64);
        g.Register(MsgId.ObjLock, H, R, 32);
        g.Register(MsgId.DragState, B, U);
        g.Register(MsgId.DartThrow, B, R, 64);
        g.Register(MsgId.DartsRecord, H, R, 16);
        g.Register(MsgId.ObjSpawn, H, R, 64);
        g.Register(MsgId.ObjDespawn, H, R, 16);
        g.Register(MsgId.SackState, H, R);
        g.Register(MsgId.SackIntent, C, R, 64);
        g.Register(MsgId.SackOwned, C, U, 64);
        g.Register(MsgId.BarrowPose, B, U, 64);
        g.Register(MsgId.SpeakerLine, B, R);
        // cleaning (11)
        g.Register(MsgId.MopStrokes, B, R);
        g.Register(MsgId.DustDigest, H, R);
        g.Register(MsgId.DustRepairReq, C, R);
        g.Register(MsgId.DustRepair, H, R);
        g.Register(MsgId.CleanupEvent, B, R, 64);
        g.Register(MsgId.CleanupState, H, U);
        g.Register(MsgId.ShavingSpawn, B, R);
        g.Register(MsgId.ShavingSweepReq, B, R);
        g.Register(MsgId.ShavingUpdate, H, R);
        g.Register(MsgId.BucketDirt, B, R, 16);
        g.Register(MsgId.CarpetFold, B, RU, 16);
        g.Register(MsgId.EnforcerLine, H, R, 16);
        g.Register(MsgId.DipOffence, C, R, 32);
        g.Register(MsgId.DipVerdict, H, R, 16);
        g.Register(MsgId.WaterEvents, H, U);
        g.Register(MsgId.CleanupConfig, H, R, 64);
        // storm (12)
        g.Register(MsgId.StormStage, H, R, 32);
        g.Register(MsgId.StormProgress, H, R, 32);
        g.Register(MsgId.StormCut, H, R);
        g.Register(MsgId.StormFrost, H, R);
        g.Register(MsgId.FrostDigest, H, U, 32);
        g.Register(MsgId.StormRequest, C, R, 16);
        g.Register(MsgId.FlameState, B, R, 64);
        g.Register(MsgId.FlameOps, B, R);
        g.Register(MsgId.PotatoFrost, H, R);
        g.Register(MsgId.TempStatus, B, U, 16);
        g.Register(MsgId.SnowDissolve, H, R, 16);
        g.Register(MsgId.StormJoinState, H, R, 256);
        g.Register(MsgId.SnowOpsResync, H, R);
        // machines (13)
        g.Register(MsgId.DeviceSnapshot, H, R);
        g.Register(MsgId.HopperAdd, H, R);
        g.Register(MsgId.HopperEject, H, R, 32);
        g.Register(MsgId.MachRun, H, RU, MaxUnreliablePayload);
        g.Register(MsgId.MachJam, H, R, 32);
        g.Register(MsgId.DeviceRequest, C, R, 32);
        g.Register(MsgId.DeviceLock, H, R, 32);
        g.Register(MsgId.GantryState, H, RU, 64);
        g.Register(MsgId.CablewayState, H, RU, 32);
        g.Register(MsgId.CablewayHook, H, R, 32);
        g.Register(MsgId.DevicePose, B, U, 64);
        g.Register(MsgId.ClawEvent, H, R);
        g.Register(MsgId.DropUnitEvent, H, R, 32);
        g.Register(MsgId.ThrowerState, H, R, 32);
        g.Register(MsgId.Package, B, R, 32);
        g.Register(MsgId.WorldEvent, H, R, 64);
        // stations (14)
        g.Register(MsgId.StationSet, H, R);
        g.Register(MsgId.StationAssign, H, R);
        g.Register(MsgId.StationTakeReq, C, R, 32);
        g.Register(MsgId.StationTakeGrant, H, R, 32);
        g.Register(MsgId.StationPlaceClean, C, R, 512);
        g.Register(MsgId.StationRelease, C, R, 16);
        g.Register(MsgId.StationState, H, R, 16);
        g.Register(MsgId.StationVerify, C, R, 16);
        // saves (15): the join-time ones flow while the client is still loading
        g.Register(MsgId.WorldManifest, H, R, 256, SessionClass.Session);
        g.Register(MsgId.WorldLoaded, C, R, 32, SessionClass.Session);
        g.Register(MsgId.WorldRejected, C, R, 256, SessionClass.Session);
        g.Register(MsgId.WorldResyncRequest, C, R, 8, SessionClass.Session);
        g.Register(MsgId.PlayerPersist, C, R, 2048);
        g.Register(MsgId.HostSaved, H, R, 32);
        // UI (16)
        g.Register(MsgId.ChatSay, C, R, 400);
        g.Register(MsgId.ChatLine, H, R, 400);
        g.Register(MsgId.UiNotice, H, R, 300);
        g.Register(MsgId.ReadyState, C, R, 8);
        g.Register(MsgId.ReadyBoard, H, R, 64);
        g.Register(MsgId.ReadyCheckStart, H, R, 300);
        g.Register(MsgId.ReadyCheckVote, C, R, 8);
        g.Register(MsgId.ReadyCheckEnd, H, R, 8);
        g.Register(MsgId.ChatTyping, B, U, 8);
        // wire blobs (01)
        g.Register(MsgId.BlobBegin, B, R, 32, codec: () => new BlobBegin());
        g.Register(MsgId.BlobChunk, B, R, BlobSender.ChunkSize + 16, codec: () => new BlobChunkMsg());
        g.Register(MsgId.BlobDone, B, R, 8, codec: () => new BlobDone());
        g.Register(MsgId.BlobCancel, B, R, 8, codec: () => new BlobCancel());
        // end game (19)
        g.Register(MsgId.EndState, H, R, 64);
        g.Register(MsgId.EndIntent, C, R, 8);
        g.Register(MsgId.EndingResult, H, R, 512);
        g.Register(MsgId.EndRoster, H, U, 64);
        // robustness (20)
        g.Register(MsgId.StateHashes, H, R);
        g.Register(MsgId.DesyncReport, C, R, 1100);
        g.Register(MsgId.ResyncRequest, C, R, 16);
        g.Register(MsgId.ResyncData, H, R);
        g.Register(MsgId.PerfReport, C, U, 32);
        g.Register(MsgId.PeerError, B, R, 128);
        g.Register(MsgId.Rejoin, H, R, 8);
        g.Register(MsgId.CompatReject, H, R, 512);
        // test harness (18)
        g.Register(MsgId.TestMark, H, R, 128);
        g.Register(MsgId.DigestRequest, H, R, 16);
        g.Register(MsgId.DigestReport, C, R, 1300);
        g.Register(MsgId.RemoteCmd, H, R, 600);
        g.Register(MsgId.RemoteCmdResult, C, R, 17 * 1024);
        g.Register(MsgId.ClockProbe, B, U, 16);

        Session.SessionMessages.AttachCodecs(g);
        g.SetCodec(MsgId.PoseUpdate, () => new Presence.PoseUpdate());
        Bodies.BodyMessages.AttachCodecs(g);
        return g;
    }
}
