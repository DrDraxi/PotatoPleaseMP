using BepInEx.Configuration;
using PotatoPleaseMP.Core;
using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Presence;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Testing;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Presence;
using UnityEngine;

namespace PotatoPleaseMP.Net;

/// <summary>
/// The live multiplayer session in the game, host or client. Host: HostSession over Steam (or the direct TCP link) plus the
/// in-process bot hub. Client: ClientSession over the same links. Both sides render every remote PoseUpdate as an avatar.
/// </summary>
internal static class MpRuntime
{
    public static ConfigEntry<string> TransportMode = null!;
    public static ConfigEntry<int> DirectPort = null!;
    public static ConfigEntry<string> HostAddress = null!;

    public static HostSession? Host { get; private set; }
    public static ClientSession? Client { get; private set; }
    public static BotHub? Bots { get; private set; }
    static LoopbackHub? _hub;
    static ITransport? _transport;
    static TcpLink? _tcp;
    static readonly Dictionary<byte, RemotePlayer> Remotes = new();
    static GameObject? _avatarRoot;
    static byte _poseSeq;
    static double _nextPose;

    public static bool IsHost => Host != null;
    public static bool IsClient => Client != null;
    public static bool Active => Host != null || Client != null;
    static double Now => Time.realtimeSinceStartupAsDouble;

    sealed class RemotePlayer
    {
        public AvatarRig Rig = null!;
        public PoseUpdate Last, Prev;
        public double LastAt, PrevAt;
    }

    public static void Init(ConfigFile cfg)
    {
        TransportMode = cfg.Bind("Net", "Transport", "Steam", "Steam (lobbies, friends) or Direct (TCP to HostAddress:DirectPort, for LAN/VPN or two copies on one PC).");
        DirectPort = cfg.Bind("Net", "DirectPort", 27250, "Direct: TCP port.");
        HostAddress = cfg.Bind("Net", "HostAddress", "127.0.0.1", "Direct: the host's address (clients).");
    }

    static bool Direct => TransportMode.Value.Equals("Direct", StringComparison.OrdinalIgnoreCase) || !Steam.SteamNative.Alive;

    public static VersionInfo LocalVersion => new(Envelope.ProtocolVersion, Plugin.Version, Application.version, (uint)Steam.SteamNative.BuildId, 44);

    static string LocalName => Steam.SteamNative.Alive ? Steam.SteamNative.PersonaName : "Player";

    // ---------------- host ----------------

    public static object StartHost(string? name = null)
    {
        if (Active) return new { error = "a session is already running" };
        ITransport primary;
        if (Direct)
        {
            _tcp = new TcpLink(UdpIds.Make(0).Value);
            _tcp.Listen(DirectPort.Value, any: true);
            primary = new P2PTransport(_tcp, () => Now);
        }
        else primary = new P2PTransport(new Steam.SteamLink(), () => Now);
        _hub = new LoopbackHub(() => Now);
        _transport = new CompositeTransport(primary, _hub.CreateTransport(primary.LocalId));
        Host = new HostSession(_transport, new GameHostWorld(), new HostConfig
        {
            HostSteamId = Steam.SteamNative.Alive ? Steam.SteamNative.LocalSteamId : 0,
            HostName = name ?? LocalName,
            MaxPlayers = 4,
        }, now: Now);
        Host.MessageHandler = OnHostMessage;
        Host.PlayerJoined += p => Game.BodySync.OnPlayerJoined(p);
        Host.PlayerJoined += p => Game.PileSync.OnPlayerJoined(p);
        Host.PlayerJoined += p => Game.StorySync.SendKeysTo(p.Slot);
        Game.DoorSync.ResetSwitches();
        Host.PlayerJoined += p => Game.DoorSync.SendAllTo(p.Slot);
        Host.PlayerLeft += (p, why) => Game.StorySync.OnPlayerLeft(p.Slot);
        Host.PlayerLeft += (p, why) => Game.ClawSync.OnPlayerLeft(p.Slot);
        Host.PlayerJoined += _ => { Game.EconSync.OnPlayerJoined(); Game.StorySync.OnPlayerJoined(); Game.StoryActors.OnPlayerJoined(); Game.EndSync.OnPlayerJoined(); Game.SackSync.OnPlayerJoined(); Game.CleanSync.OnPlayerJoined(); Game.StormSync.OnPlayerJoined(); };
        Host.PlayerLeft += (p, why) => Game.BodySync.OnPlayerLeft(p);
        Host.PlayerJoined += p => Plugin.L.LogMessage($"[mp] {p.Name} joined as slot {p.Slot}.");
        Host.PlayerLeft += (p, why) => { Plugin.L.LogMessage($"[mp] {p.Name} left ({why})."); DropRemote(p.Slot); };
        Host.Warning += w => Plugin.L.LogWarning("[mp] " + w);
        Game.BodySync.StartHost();
        Game.PileSync.Reset();
        ResetModules();
        Host.Start(Now, worldReady: true);
        Bots = new BotHub(_hub, primary.LocalId, () => new BotOptions { Version = LocalVersion, LoadSeconds = 1.5 });
        if (!Direct) Steam.SteamLobby.Create(4);
        Plugin.L.LogMessage($"[mp] hosting as {Host.Local?.Name} ({primary.LocalId}) over {(Direct ? $"direct TCP :{_tcp!.Port}" : "Steam")}.");
        return new { hosting = true, id = primary.LocalId.ToString(), transport = Direct ? "direct" : "steam", port = _tcp?.Port };
    }

    // ---------------- client ----------------

    /// <summary>Joins a host: Direct = "address[:port]", Steam = the host's SteamID64.</summary>
    /// <summary>What the last Join connected to (for rejoining the same host).</summary>
    public static string LastJoinTarget = "";

    /// <summary>End the session without the "session ended" trip to the menu (the caller is leaving the world itself).</summary>
    public static void StopQuiet()
    {
        if (!Active) return;
        _inMenuLoad = true;
        try { Stop(); } catch (Exception e) { Plugin.L.LogWarning($"[mp] stop: {e.Message}"); }
        finally { _inMenuLoad = false; }
    }

    public static object Join(string target)
    {
        if (Active) return new { error = "a session is already running" };
        PeerId hostId;
        ITransport t;
        if (Direct)
        {
            var parts = (string.IsNullOrEmpty(target) ? HostAddress.Value : target).Split(':');
            int port = parts.Length > 1 && int.TryParse(parts[1], out var pp) ? pp : DirectPort.Value;
            _tcp = new TcpLink(UdpIds.Make(1 + UnityEngine.Random.Range(1, 30000)).Value);
            try { hostId = new PeerId(_tcp.ConnectTo(parts[0], port)); }
            catch (Exception e) { _tcp.Dispose(); _tcp = null; return new { error = "connect failed: " + e.Message }; }
            t = new P2PTransport(_tcp, () => Now);
        }
        else
        {
            if (!ulong.TryParse(target, out var sid)) return new { error = "no Steam host: accept a friend's invite, or use Join Game in the Steam friends list" };
            hostId = new PeerId(sid);
            t = new P2PTransport(new Steam.SteamLink(), () => Now);
        }
        _transport = t;
        Client = new ClientSession(t, new GameClientWorld(hostId), new ClientConfig
        {
            Name = LocalName,
            SteamId = Steam.SteamNative.Alive && !Direct ? Steam.SteamNative.LocalSteamId : 0,
        });
        Client.MessageHandler = OnClientMessage;
        Client.Connect(hostId, Now);
        LastJoinTarget = Direct ? (string.IsNullOrEmpty(target) ? HostAddress.Value : target) : target;
        Plugin.L.LogMessage($"[mp] joining {hostId} over {(Direct ? "direct TCP" : "Steam")}.");
        return new { joining = hostId.ToString() };
    }

    // ---------------- shared ----------------

    public static object Stop()
    {
        if (Host != null)
        {
            Host.Shutdown(LeaveReason.HostLeft);
            Host.Update(Now);
        }
        bool wasClient = Client != null;
        if (Client != null)
        {
            Client.Leave(LeaveReason.Left);
            Client.Update(Now);
        }
        Bots?.Dispose();
        _transport?.Dispose();
        _tcp?.Dispose();
        Host = null; Client = null; Bots = null; _hub = null; _transport = null; _tcp = null;
        foreach (var s in Remotes.Keys.ToList()) DropRemote(s);
        Game.BodySync.Stop();
        Steam.SteamLobby.Leave();
        // A client was playing in the host's world: that world is gone, so go back to the menu (nothing is saved).
        if (wasClient) BackToMenu();
        Saves.SaveContext.LeaveClient();
        return new { stopped = true };
    }

    /// <summary>Per-session state of the sync modules (a new session in the same game run starts clean).</summary>
    static void ResetModules()
    {
        Application.runInBackground = true; // an alt-tabbed host must keep the session running
        Game.EndSync.ResetSession();
        Game.CleanSync.ResetSession();
        Game.StormSync.ResetSession();
        Game.ClawSync.ResetSession();
        Game.FlameView.ResetSession();
        Game.PileSync.ResetPending();
        Game.SackSync.Reset();
    }

    [ThreadStatic] static bool _inMenuLoad;

    /// <summary>Quit to menu from the pause menu during a session: end the session too (host closes it, client leaves).</summary>
    [HarmonyLib.HarmonyPatch(typeof(PotatoPeeler.PauseUI), nameof(PotatoPeeler.PauseUI.LoadMainMenu))]
    static class QuitToMenu
    {
        static void Prefix()
        {
            if (!Active || _inMenuLoad) return;
            _inMenuLoad = true;
            try { Stop(); } catch (Exception e) { Plugin.L.LogWarning($"[mp] quit to menu: {e.Message}"); }
            finally { _inMenuLoad = false; }
        }
    }

    static void BackToMenu()
    {
        if (_inMenuLoad) return; // already on the way (quit to menu)
        try
        {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Gameplay") return;
            PotatoPleaseMP.UI.Notice.Show("The co-op session ended.");
            var pause = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PauseUI>(true);
            if (pause != null) pause.LoadMainMenu(false);
            else Plugin.L.LogWarning("[mp] no pause menu to leave through");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[mp] back to menu: {e.Message}"); }
    }

    public static object AddBot(string name, bool walk)
    {
        if (Bots == null) return new { error = "not hosting" };
        var me = PlayerPos();
        Bots.SpawnCentre = new Vec3(me.x, me.y, me.z);
        var bot = Bots.Add(name, Now);
        if (walk) WalkBot(bot);
        return new { bot = bot.Name, index = bot.Index, id = bot.Id.ToString() };
    }

    static void WalkBot(BotClient bot)
    {
        var me = PlayerPos();
        float r = 1.5f + bot.Index * 0.7f;
        var path = new List<Vec3>();
        for (int lap = 0; lap < 50; lap++)
        {
            path.Add(new Vec3(me.x + r, me.y, me.z + r));
            path.Add(new Vec3(me.x - r, me.y, me.z + r));
            path.Add(new Vec3(me.x - r, me.y, me.z - r));
            path.Add(new Vec3(me.x + r, me.y, me.z - r));
        }
        bot.Walk(path);
    }

    public static object BotCommand(string what, string who)
    {
        if (Bots == null) return new { error = "not hosting" };
        var bot = Bots.Find(who) ?? Bots.Bots.LastOrDefault();
        if (bot == null) return new { error = "no bot" };
        switch (what)
        {
            case "leave": Bots.Leave(bot); break;
            case "remove": Bots.Remove(bot); break;
            case "walk": WalkBot(bot); break;
            case "stop": bot.Walker.Stop(); break;
            default: return new { error = "unknown bot command" };
        }
        return new { bot = bot.Name, did = what };
    }

    static Vector3 PlayerPos() => UnityEngine.Object.FindObjectOfType<PotatoPeeler.Player>()?.transform.position ?? Vector3.zero;

    public static void Update()
    {
        if (!Active) return;
        var now = Now;
        Bots?.Update(now);
        Host?.Update(now);
        Client?.Update(now);
        if (Client != null && Game.BodySync.Role == "none" && Client.LocalSlot != 0xFF) { Game.BodySync.StartClient(Client.LocalSlot); ResetModules(); }
        if (Host != null) { Game.BodySync.UpdateHost(Host); Game.EconSync.UpdateHost(Host); Game.StorySync.UpdateHost(Host); Game.StoryActors.UpdateHost(Host); Game.CleanSync.UpdateHost(Host); Game.StormSync.UpdateHost(Host); Game.PileSync.UpdateHost(Host); Game.SackSync.UpdateHost(Host); Game.MachineSync.UpdateHost(Host); Game.EndSync.UpdateHost(Host); Game.DoorSync.UpdateHost(Host); }
        if (Client != null && Client.State == ClientState.Playing) Game.BodySync.UpdateClient(); Game.PileSync.UpdateClient(); Game.SackSync.UpdateClient(); Game.StorySync.UpdateClientZone();
        Bots?.Prune();
        P2PTransport.PumpDeferredCloses();
        if (Client != null && Client.State == ClientState.Ended) { Plugin.L.LogMessage("[mp] session ended."); Stop(); return; }
        if (now >= _nextPose) { _nextPose = now + 0.05; SendOwnPose(); }
    }

    static void SendOwnPose()
    {
        var cam = Camera.main;
        var player = UnityEngine.Object.FindObjectOfType<PotatoPeeler.Player>();
        if (cam == null || player == null) return;
        var p = player.transform.position;
        var e = cam.transform.eulerAngles;
        var pose = new PoseUpdate
        {
            Slot = Host != null ? (byte)0 : Client?.LocalSlot ?? 0, Seq = _poseSeq++, TimeMs = (ushort)(Now * 1000),
            Pos = new Vec3(p.x, p.y, p.z), YawDeg = e.y, PitchDeg = e.x > 180 ? e.x - 360 : e.x, Warmth = 1,
        };
        var w = new NetWriter();
        pose.Write(w);
        if (Host != null)
        {
            foreach (var pl in Host.Players.Players)
                if (!pl.IsLocal && pl.InGame) Host.SendTo(pl.Slot, MsgId.PoseUpdate, w.Span, Delivery.Unreliable);
        }
        else if (Client != null && Client.State == ClientState.Playing)
            Client.Send(MsgId.PoseUpdate, w.Span, Delivery.Unreliable);
    }

    static void OnHostMessage(PlayerInfo from, MsgId id, ReadOnlySpan<byte> payload, Delivery delivery, ushort stamp)
    {
        if (Game.EndSync.Handle(id, payload, from)) return;
        if (Game.MachineSync.Handle(id, payload, from)) return;
        if (Game.PileSync.Handle(id, payload, from)) return;
        if (Game.StormSync.Handle(id, payload, from)) return;
        if (Game.ShavingSync.Handle(id, payload, from)) return;
        if (Game.CleanSync.Handle(id, payload, from)) return;
        if (Game.StoryActors.Handle(id, payload, from)) return;
        if (Game.StorySync.Handle(id, payload, from)) return;
        if (Game.EconSync.Handle(id, payload, from)) return;
        if (Game.StationSync.Handle(id, payload, from)) return;
        if (Game.BarrowSync.Handle(id, payload, from)) return;
        if (Game.SpeakerSync.Handle(id, payload, from)) return;
        if (Game.SackSync.Handle(id, payload, from)) return;
        if (Game.DoorSync.Handle(id, payload, from)) return;
        if (Game.BodySync.HandleHost(from, id, payload)) return;
        if (Game.PeelSync.Handle(id, payload, from)) return;
        if (id != MsgId.PoseUpdate) return;
        if (!TryReadPose(payload, out var pose)) return;
        pose.Slot = from.Slot; // never trust the sender's slot byte
        Game.BodySync.SetReceiver(from.Slot, new Vector3(pose.Pos.X, pose.Pos.Y + 1.6f, pose.Pos.Z), pose.YawDeg, pose.PitchDeg);
        ShowPose(pose, from.Name);
        Host?.Relay(from, id, payload, PoseUpdate.SlotOffset);
    }

    static void OnClientMessage(MsgId id, ReadOnlySpan<byte> payload, Delivery delivery, ushort stamp)
    {
        if (Game.EndSync.Handle(id, payload, null)) return;
        if (Game.MachineSync.Handle(id, payload, null)) return;
        if (Game.PileSync.Handle(id, payload, null)) return;
        if (Game.StormSync.Handle(id, payload, null)) return;
        if (Game.ShavingSync.Handle(id, payload, null)) return;
        if (Game.CleanSync.Handle(id, payload, null)) return;
        if (Game.StoryActors.Handle(id, payload, null)) return;
        if (Game.StorySync.Handle(id, payload, null)) return;
        if (Game.EconSync.Handle(id, payload, null)) return;
        if (Game.StationSync.Handle(id, payload, null)) return;
        if (Game.BarrowSync.Handle(id, payload, null)) return;
        if (Game.SpeakerSync.Handle(id, payload, null)) return;
        if (Game.SackSync.Handle(id, payload, null)) return;
        if (Game.DoorSync.Handle(id, payload, null)) return;
        if (Game.BodySync.HandleClient(id, payload, stamp)) return;
        if (Game.PeelSync.Handle(id, payload, null)) return;
        if (id != MsgId.PoseUpdate || Client == null) return;
        if (!TryReadPose(payload, out var pose) || pose.Slot == Client.LocalSlot) return;
        ShowPose(pose, Client.Players.BySlot(pose.Slot)?.Name ?? $"Player {pose.Slot}");
    }

    static bool TryReadPose(ReadOnlySpan<byte> payload, out PoseUpdate pose)
    {
        pose = new PoseUpdate();
        try { var r = new NetReader(payload); pose.Read(ref r); return true; }
        catch (WireFormatException) { return false; }
    }

    static void ShowPose(PoseUpdate pose, string name)
    {
        if (!Remotes.TryGetValue(pose.Slot, out var rp) || rp.Rig.Root == null)
        {
            if (_avatarRoot == null) _avatarRoot = new GameObject("PPMP_Remotes");
            rp = new RemotePlayer { Rig = AvatarBuilder.Build(pose.Slot, name, _avatarRoot.transform) };
            Remotes[pose.Slot] = rp;
            rp.Last = rp.Prev = pose;
            rp.LastAt = rp.PrevAt = Now;
        }
        rp.Prev = rp.Last; rp.PrevAt = rp.LastAt;
        rp.Last = pose; rp.LastAt = Now;
    }

    /// <summary>Avatar root transforms of the remote players.</summary>
    public static IEnumerable<Transform> RemoteRoots()
    {
        foreach (var rp in Remotes.Values) if (rp.Rig.Root != null) yield return rp.Rig.Root;
    }

    /// <summary>Feet positions of the remote players (last pose received).</summary>
    public static IEnumerable<Vector3> RemoteFeet()
    {
        foreach (var rp in Remotes.Values) yield return new Vector3(rp.Last.Pos.X, rp.Last.Pos.Y, rp.Last.Pos.Z);
    }

    static void DropRemote(byte slot)
    {
        if (!Remotes.TryGetValue(slot, out var rp)) return;
        if (rp.Rig.Root != null) UnityEngine.Object.Destroy(rp.Rig.Root.gameObject);
        Remotes.Remove(slot);
    }

    /// <summary>Renders remote avatars 100 ms in the past, interpolated between the last two poses.</summary>
    public static void LateUpdate()
    {
        if (Remotes.Count == 0) return;
        var now = Now;
        foreach (var rp in Remotes.Values)
        {
            if (rp.Rig.Root == null) continue;
            var span = Math.Max(0.001, rp.LastAt - rp.PrevAt);
            var t = (float)Math.Clamp((now - 0.1 - rp.PrevAt) / span, 0, 1.25);
            var a = rp.Prev.Pos; var b = rp.Last.Pos;
            var pos = new Vector3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
            var yaw = Mathf.LerpAngle(rp.Prev.YawDeg, rp.Last.YawDeg, Mathf.Clamp01(t));
            rp.Rig.Apply(pos, yaw, rp.Last.PitchDeg, Time.deltaTime);
        }
    }

    public static object Describe()
    {
        if (Host != null)
            return new
            {
                role = "host",
                phase = Host.Phase.ToString(),
                players = Host.Players.Players.Select(p => { var l = Host.Net.Link(p.Peer); return new { p.Slot, p.Name, phase = p.Phase.ToString(), p.RttMs, peer = p.Peer.ToString(), envIn = l?.EnvelopesIn, poses = l?.InCountById[0x10] }; }).ToArray(),
                bots = Bots?.Bots.Select(b => new { b.Index, b.Name, state = b.State.ToString(), b.SnapshotBytes, b.PosesSent, b.JoinMs }).ToArray(),
                avatars = Remotes.Keys.Select(k => (int)k).ToArray(),
                tcpPort = _tcp?.Port,
            };
        if (Client != null)
            return new
            {
                role = "client",
                state = Client.State.ToString(),
                slot = Client.LocalSlot,
                players = Client.Players.Players.Select(p => new { p.Slot, p.Name, phase = p.Phase.ToString() }).ToArray(),
                avatars = Remotes.Keys.Select(k => (int)k).ToArray(),
                load = GameClientWorld.LastStatus,
                tcpError = _tcp?.LastError,
            };
        return new { role = "none" };
    }
}

/// <summary>The host's world for joiners: the current save file (spec 15 builds a fresh snapshot; this is the first cut).</summary>
internal sealed class GameHostWorld : IHostWorld
{
    public VersionInfo Version => MpRuntime.LocalVersion;
    public byte Difficulty => 0;
    public QueueReason? JoinGate() => null;
    public bool IsShiftOver => false;

    public HostSnapshot CaptureSnapshot(SnapshotReason reason)
    {
        var path = PotatoPeeler.SaveSlots.PathFor(0);
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        return new HostSnapshot { SaveUtcTicks = (ulong)DateTime.UtcNow.ToBinary(), SaveVersion = 44, Save = Task.FromResult(bytes) };
    }
}
