namespace PotatoPleaseMP.Core.Wire;

/// <summary>
/// Every message id (spec 01 §8 catalog, with the roadmap's moves: end game at 0xE4-0xE7, robustness at 0xE8-0xEF,
/// ribbons only at 0x86, interactables 0x70-0x78 as proposed by gamemap 08). This enum is the single source of truth;
/// <see cref="MessageRegistry.CreateDefault"/> describes each one. Owners may renumber inside their own range.
/// </summary>
public enum MsgId : byte
{
    // 0x00 session (spec 02)
    Hello = 0x00, Welcome = 0x01, Reject = 0x02, Queued = 0x03, SnapshotInfo = 0x04, LoadProgress = 0x05,
    ClientReady = 0x06, PlayerAdded = 0x07, PlayerRemoved = 0x08, PlayerUpdate = 0x09, Heartbeat = 0x0A,
    Goodbye = 0x0B, SessionState = 0x0C, SetReady = 0x0D, Resync = 0x0E,

    // 0x10 presence (spec 04), 0x1C-0x1F lent to deaths (spec 10)
    PoseUpdate = 0x10, KnifePose = 0x11, PresenceEvent = 0x12, PresenceInfo = 0x13, ForceTeleport = 0x14,
    PresenceReady = 0x15,
    PlayerDowned = 0x1C, Respawn = 0x1D, PlayerStatus = 0x1E, LifeEvent = 0x1F,

    // 0x20 peeling (spec 05); 0x2A ShavingSpawn moved to 0x86 (roadmap)
    PeelLockReq = 0x20, PeelLockReply = 0x21, PeelOwnerSet = 0x22, CarveRecords = 0x23, CarveCheck = 0x24,
    CarveSnapshotReq = 0x25, CarveSnapshotInfo = 0x26, PeelEnd = 0x27, PeelResult = 0x28, PeelVerdict = 0x29,
    HiddenItem = 0x2B, PeelStrictness = 0x2C, CarveLogReq = 0x2D, CarveLogSlice = 0x2E,

    // 0x30 pile (spec 06)
    PileFullState = 0x30, PileDelta = 0x31, PileDigests = 0x32, PileCellNack = 0x33, PileCellState = 0x34,
    PileDigIntent = 0x35, PileDigReject = 0x36, PileFullRequest = 0x37, PileCounters = 0x38,
    PileFrostSnapshot = 0x39, PileProbe = 0x3A, PileRefill = 0x3B,

    // 0x40 bodies (spec 07)
    BodySpawn = 0x40, BodyDespawn = 0x41, BodyState = 0x42, BodyOwnedState = 0x43, BodyRest = 0x44,
    BodyClaim = 0x45, BodyAuth = 0x46, BodyRelease = 0x47, ClientSpawn = 0x48, SpawnAdopt = 0x49,
    BodyConsume = 0x4A, ContainerCount = 0x4B, BodySlot = 0x4C, BodyFx = 0x4D, BodyScalar = 0x4E, BodyAudit = 0x4F,

    // 0x50 economy (spec 08)
    Balance = 0x50, Counters = 0x51, Pacing = 0x52, Quota = 0x53, Skill = 0x54, UpgradeLevels = 0x55,
    PurchaseReq = 0x56, PurchaseRes = 0x57, Scoreboard = 0x58, AchCredit = 0x59, EconNotice = 0x5A,
    ShiftSummary = 0x5B,

    // 0x60 story (spec 09)
    QuestState = 0x60, StoryIntent = 0x61, StoryBeat = 0x62, KeyDelta = 0x63, LetterEvent = 0x64,
    StoryJoin = 0x65, LockReq = 0x66, LockState = 0x67, ChoiceIntent = 0x68, ChoiceResolved = 0x69,
    Cinematic = 0x6A, CinematicAck = 0x6B, ActorState = 0x6C, ActorEvent = 0x6D, ActorIntent = 0x6E, PaLine = 0x6F,

    // 0x70 interactables (gamemap 08 proposal; spec 21 to be written)
    InteractReq = 0x70, InteractRes = 0x71, ObjState = 0x72, ObjLock = 0x73, DragState = 0x74, DartThrow = 0x75,
    DartsRecord = 0x76, ObjSpawn = 0x77, ObjDespawn = 0x78,
    // sacks as networked objects (mod v0.1)
    SackState = 0x79, SackIntent = 0x7A, SackOwned = 0x7B, BarrowPose = 0x7C, SpeakerLine = 0x7D,

    // 0x80 cleaning (spec 11)
    MopStrokes = 0x80, DustDigest = 0x81, DustRepairReq = 0x82, DustRepair = 0x83, CleanupEvent = 0x84,
    CleanupState = 0x85, ShavingSpawn = 0x86, ShavingSweepReq = 0x87, ShavingUpdate = 0x88, BucketDirt = 0x89,
    CarpetFold = 0x8A, EnforcerLine = 0x8B, DipOffence = 0x8C, DipVerdict = 0x8D, WaterEvents = 0x8E,
    CleanupConfig = 0x8F,

    // 0x90 storm (spec 12)
    StormStage = 0x90, StormProgress = 0x91, StormCut = 0x92, StormFrost = 0x93, FrostDigest = 0x94,
    StormRequest = 0x95, FlameState = 0x96, FlameOps = 0x97, PotatoFrost = 0x98, TempStatus = 0x99,
    SnowDissolve = 0x9A, StormJoinState = 0x9B, SnowOpsResync = 0x9C,

    // 0xA0 machines (spec 13)
    DeviceSnapshot = 0xA0, HopperAdd = 0xA1, HopperEject = 0xA2, MachRun = 0xA3, MachJam = 0xA4,
    DeviceRequest = 0xA5, DeviceLock = 0xA6, GantryState = 0xA7, CablewayState = 0xA8, CablewayHook = 0xA9,
    DevicePose = 0xAA, ClawEvent = 0xAB, DropUnitEvent = 0xAC, ThrowerState = 0xAD, Package = 0xAE, WorldEvent = 0xAF,

    // 0xB0 stations (spec 14)
    StationSet = 0xB0, StationAssign = 0xB1, StationTakeReq = 0xB2, StationTakeGrant = 0xB3,
    StationPlaceClean = 0xB4, StationRelease = 0xB5, StationState = 0xB6, StationVerify = 0xB7,

    // 0xC0 saves (spec 15)
    WorldManifest = 0xC0, WorldLoaded = 0xC1, WorldRejected = 0xC2, WorldResyncRequest = 0xC3,
    PlayerPersist = 0xC4, HostSaved = 0xC5,

    // 0xD0 UI (spec 16)
    ChatSay = 0xD0, ChatLine = 0xD1, UiNotice = 0xD2, ReadyState = 0xD3, ReadyBoard = 0xD4,
    ReadyCheckStart = 0xD5, ReadyCheckVote = 0xD6, ReadyCheckEnd = 0xD7, ChatTyping = 0xD8,

    // 0xE0-0xE3 wire blobs (spec 01)
    BlobBegin = 0xE0, BlobChunk = 0xE1, BlobDone = 0xE2, BlobCancel = 0xE3,
    // 0xE4-0xE7 end game (spec 19, moved off 0x6C-0x6F)
    EndState = 0xE4, EndIntent = 0xE5, EndingResult = 0xE6, EndRoster = 0xE7,
    // 0xE8-0xEF robustness (spec 20, moved off 0xF0-0xF7)
    StateHashes = 0xE8, DesyncReport = 0xE9, ResyncRequest = 0xEA, ResyncData = 0xEB, PerfReport = 0xEC,
    PeerError = 0xED, Rejoin = 0xEE, CompatReject = 0xEF,

    // 0xF0 dev/test (spec 18)
    TestMark = 0xF0, DigestRequest = 0xF1, DigestReport = 0xF2, RemoteCmd = 0xF3, RemoteCmdResult = 0xF4,
    ClockProbe = 0xF5,
}
