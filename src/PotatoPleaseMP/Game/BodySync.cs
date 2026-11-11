using PotatoPleaseMP.Core;
using PotatoPleaseMP.Core.Bodies;
using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Loose potatoes (spec 07, first cut). Host: every live PotatoInstance gets a NetId; poses go out through Core's
/// HostBodySync (priority + budget per client). Client: its own loose potatoes from the loaded save are put away when the
/// world is ready, and the host's bodies appear as kinematic proxies spawned through the game's own pool
/// (PotatoPileActivator.SpawnLoaded) and driven from the interpolation buffer.
/// Client grabs (spec 07 4): when the client's hand captures a proxy it turns dynamic at once (optimistic), a claim goes to
/// the host, and the client streams its pose while it owns it. After it lets go and the body settles (or 3 s), it releases
/// with the final pose and velocity and the proxy goes back to following the host. On the host, a client-owned body is
/// kinematic and follows the owner's stream.
/// Not yet: client-side spawns (sacks torn on a client), containers.
/// </summary>
internal static class BodySync
{
    static HostBodySync? _host;
    static ClientBodyMirror? _client;
    static readonly Dictionary<int, NetId> IdByObject = new();               // host: instance id -> net id
    static readonly Dictionary<ushort, PotatoPeeler.PotatoInstance> ObjById = new();
    static double _nextTick, _nextOwnedSend;
    static bool _clientCleared;
    /// <summary>Client: proxies this client simulates right now -> local time it let go (NaN while still held).</summary>
    static readonly Dictionary<ushort, double> LocalOwned = new();
    /// <summary>Host: bodies whose pose currently comes from a client.</summary>
    static readonly HashSet<ushort> RemoteDriven = new();
    static readonly Dictionary<ushort, Vector3> LastReleaseVel = new();
    public static long Claims, Releases, Denied, TableHandoffs, TablePlacements, FactorySent, FactoryApplied;
    static readonly HashSet<ushort> FactoryKnown = new();
    static readonly Dictionary<ushort, (byte f, byte c)> FrostSent = new();
    public static long FrostApplied;

    static void FrostOut(HostSession session)
    {
        NetWriter? w = null; int n = 0;
        foreach (var (idv, p) in ObjById)
        {
            if (p == null) continue;
            byte f, ch;
            try { f = (byte)Mathf.Clamp(Mathf.RoundToInt(p._Frost_k__BackingField * 100), 0, 100); ch = (byte)Mathf.Clamp(Mathf.RoundToInt(p._Char_k__BackingField * 100), 0, 100); }
            catch { continue; }
            var known = FrostSent.TryGetValue(idv, out var last);
            if (known ? (last.f == f && last.c == ch) : (f == 0 && ch == 0)) continue;
            FrostSent[idv] = (f, ch);
            if (w == null) { w = new NetWriter(); w.U8(0); }
            w.U16(idv); w.U8(f); w.U8(ch);
            if (++n >= 60) break;
        }
        if (w == null) return;
        var b = w.Span.ToArray(); b[0] = (byte)n;
        session.Broadcast(MsgId.PotatoFrost, b);
    }

    public static void ApplyFrost(ReadOnlySpan<byte> payload)
    {
        var r = new NetReader(payload);
        int n = r.U8();
        for (int i = 0; i < n; i++)
        {
            ushort id = r.U16(); float f = r.U8() / 100f, ch = r.U8() / 100f;
            if (Get(new NetId(id)) is not { } inst) continue;
            try { if (Mathf.Abs(inst._Frost_k__BackingField - f) > 0.005f) inst.SetFrost(f); if (Mathf.Abs(inst._Char_k__BackingField - ch) > 0.005f) inst.SetChar(ch); FrostApplied++; }
            catch { }
        }
    }
    /// <summary>Client: bodies held by the local peel session (granted by the host for a station); never auto-released.</summary>
    static readonly HashSet<ushort> PeelLocked = new();
    static readonly Dictionary<ushort, double> PeelLockedAt = new();
    /// <summary>Client: owned bodies picked up out of a table; they must leave it before they can be handed back to one.</summary>
    static readonly HashSet<ushort> TakenFromTable = new();
    static readonly Dictionary<byte, int> HostRx = new();

    public static int Count => ObjById.Count;
    public static PotatoPeeler.PotatoInstance? Get(NetId id) => ObjById.TryGetValue(id.Value, out var p) ? p : null;
    public static NetId IdOf(PotatoPeeler.PotatoInstance p)
    {
        if (IdByObject.TryGetValue(p.GetInstanceID(), out var id)) return id;
        foreach (var (k, v) in ObjById) if (v != null && v.Pointer == p.Pointer) return new NetId(k);
        return NetId.None;
    }
    /// <summary>Host: the loose potatoes it tracks.</summary>
    public static IEnumerable<PotatoPeeler.PotatoInstance> HostBodies()
    {
        if (_host == null) yield break;
        foreach (var p in ObjById.Values) if (p != null) yield return p;
    }

    /// <summary>Host: does this client still hold a potato for peeling (a table it was granted is still in use)?</summary>
    public static bool HoldsPeel(byte slot) => _host != null && _host.Authority.All.Any(r => r.Owner == slot && r.Mode == BodyMode.PeelHeld);

    /// <summary>Host: true while a client owns (simulates) this body.</summary>
    public static bool IsRemoteDriven(PotatoPeeler.PotatoInstance p)
    {
        var id = IdOf(p);
        return id.IsValid && RemoteDriven.Contains(id.Value);
    }

    public static string Role => _host != null ? "host" : _client != null ? "client" : "none";

    public static void StartHost()
    {
        _host = new HostBodySync();
        _client = null;
        IdByObject.Clear(); ObjById.Clear();
    }

    public static void StartClient(byte slot)
    {
        _client = new ClientBodyMirror(slot);
        _host = null;
        IdByObject.Clear(); ObjById.Clear();
        _clientCleared = false;
        _client.Spawned += OnProxySpawned;
        _client.Despawned += (p, _) => OnProxyDespawned(p.Id);
        _client.AuthChanged += OnAuthChanged;
        LocalOwned.Clear();
    }

    public static void Stop()
    {
        _host = null; _client = null;
        LocalOwned.Clear(); RemoteDriven.Clear(); LastReleaseVel.Clear(); PeelLocked.Clear(); TakenFromTable.Clear();
        IdByObject.Clear(); ObjById.Clear();
    }

    public static void OnPlayerJoined(PlayerInfo p) { _host?.AddClient(p.Slot); FactoryKnown.Clear(); FrostSent.Clear(); }

    /// <summary>Client: a host body is machine-peeled; give our copy the same look.</summary>
    static readonly Dictionary<ushort, float> PendingFactory = new();

    public static void ApplyFactory(ushort id, float depth)
    {
        if (Get(new NetId(id)) is not { } inst) { PendingFactory[id] = depth; return; } // the body's spawn is still on its way
        try { if (!inst.IsFactoryPeeled) inst.ApplyMachinePeel(depth); FactoryApplied++; }
        catch (Exception e) { Plugin.L.LogWarning($"[bodies] factory look: {e.Message}"); }
    }
    public static void OnPlayerLeft(PlayerInfo p) => _host?.RemoveClient(p.Slot, Time.realtimeSinceStartupAsDouble);

    /// <summary>Host: a client's body message (claims etc.).</summary>
    public static bool HandleHost(PlayerInfo from, MsgId id, ReadOnlySpan<byte> payload)
    {
        if ((byte)id < 0x40 || (byte)id > 0x4F) return false;
        try
        {
            if (id == MsgId.BodyRelease) NoteRelease(payload);
            HostRx[(byte)id] = HostRx.GetValueOrDefault((byte)id) + 1;
            _host?.Receive(from.Slot, id, payload, Time.realtimeSinceStartupAsDouble);
        }
        catch (Exception e) { Plugin.L.LogWarning($"[bodies] from slot {from.Slot} {id}: {e.Message}"); }
        return true;
    }

    /// <summary>Client: the host's body messages.</summary>
    public static bool HandleClient(MsgId id, ReadOnlySpan<byte> payload, ushort stamp)
    {
        if ((byte)id < 0x40 || (byte)id > 0x4F) return false;
        if (_client == null) return true;
        ClearLocalOnce();
        _client.Receive(id, payload, stamp, Time.realtimeSinceStartupAsDouble);
        return true;
    }

    public static void SetReceiver(byte slot, Vector3 eye, float yaw, float pitch)
        => _host?.SetReceiver(new ReceiverView(slot, new Vec3(eye.x, eye.y, eye.z), yaw, pitch));

    static Vec3 V(Vector3 v) => new(v.x, v.y, v.z);
    static Quat Q(Quaternion q) => new(q.x, q.y, q.z, q.w);

    /// <summary>Host, every frame: track spawns/despawns, and 30 Hz poses + sends.</summary>
    public static void UpdateHost(HostSession session)
    {
        if (_host == null) return;
        var now = Time.realtimeSinceStartupAsDouble;
        if (now >= _nextScan) { _nextScan = now + 0.05; ScanHost(now); }
        DriveRemoteOwned();
        HostTail(session, now);
    }

    static double _nextScan;

    /// <summary>Host: find new and gone potatoes (20 Hz; FindObjectsOfType over hundreds of bodies is not free).</summary>
    static void ScanHost(double now)
    {
        if (_host == null) return;
        var live = new HashSet<int>();
        foreach (var p in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.PotatoInstance>())
        {
            int key = p.GetInstanceID();
            live.Add(key);
            var t = p.transform;
            if (!IdByObject.TryGetValue(key, out var id))
            {
                var template = new SpawnEntry { Variant = (byte)p.Variant, TrueScale = TrueScale(p) };
                id = _host.Spawn(V(t.position), Q(t.rotation), now, SpawnOrigin.Activate, template, default, false);
                IdByObject[key] = id;
                ObjById[id.Value] = p;
            }
        }
        foreach (var (key, id) in IdByObject.ToList())
            if (!live.Contains(key))
            {
                _host.Despawn(id, DespawnReason.Pooled, now);
                IdByObject.Remove(key);
                ObjById.Remove(id.Value);
                RemoteDriven.Remove(id.Value);
                FactoryKnown.Remove(id.Value);
            }
    }

    static void HostTail(HostSession session, double now)
    {
        if (_host == null) return;
        if (now < _nextTick) return;
        _nextTick = now + 1.0 / 30;
        // Frost and char (storm, flamethrower): send changes.
        FrostOut(session);
        // Machine-peeled potatoes (peel machine ejects) carry a "factory" peel look: tell clients once per body.
        foreach (var (idv, p) in ObjById)
        {
            if (p == null || FactoryKnown.Contains(idv)) continue;
            bool peeled; float depth;
            try { peeled = p.IsFactoryPeeled; depth = p.FactoryPeelDepth; } catch { continue; }
            if (!peeled) continue;
            FactoryKnown.Add(idv);
            var fw = new NetWriter(); fw.U16(idv); fw.F32(depth);
            session.Broadcast(MsgId.HopperEject, fw.Span);
            FactorySent++;
        }
        foreach (var (idv, p) in ObjById)
        {
            if (p == null || RemoteDriven.Contains(idv)) continue;
            var t = p.transform;
            var rb = p.Body;
            // Kinematic bodies moved by game code (table slots settling, carried, tweened) report "asleep" while they move:
            // only a dynamic body's sleep means it is at rest.
            _host.SetPose(new NetId(idv), V(t.position), Q(t.rotation), rb != null ? V(rb.linearVelocity) : null, rb != null && !rb.isKinematic && rb.IsSleeping());
            _host.SetHints(new NetId(idv), held: p.IsHeld || p.IsCarried);
        }
        foreach (var o in _host.Tick(now))
            session.SendTo(o.Slot, o.Id, o.Payload, o.Reliable ? Delivery.Reliable : Delivery.Unreliable);
    }

    /// <summary>Host: bodies a client owns follow its stream (kinematic); when it hands them back they get its velocity.</summary>
    static void DriveRemoteOwned()
    {
        if (_host == null) return;
        foreach (var (idv, p) in ObjById)
        {
            if (p == null) continue;
            var rb = p.Body;
            var id = new NetId(idv);
            var (pos, rot) = _host.PoseOf(id);
            var target = new Vector3(pos.X, pos.Y, pos.Z);
            var trot = rot.X == 0 && rot.Y == 0 && rot.Z == 0 && rot.W == 0 ? Quaternion.identity : new Quaternion(rot.X, rot.Y, rot.Z, rot.W);
            if (_host.Authority.Get(id) is { IsRemoteOwned: true })
            {
                if (RemoteDriven.Add(idv) && rb != null) rb.isKinematic = true;
                var t = p.transform;
                var np = Vector3.Lerp(t.position, target, 0.5f);
                var nr = Quaternion.Slerp(t.rotation, trot, 0.5f);
                if (rb != null) { rb.position = np; rb.rotation = nr; }
                t.SetPositionAndRotation(np, nr);
            }
            else if (RemoteDriven.Remove(idv))
            {
                p.transform.SetPositionAndRotation(target, trot);
                // Handed back inside a table's tray: the table takes it now (free-falling, it would drop through the
                // tray before the table's capture saw it).
                if (TableAt(target) is { } st)
                {
                    if (rb != null) { rb.position = target; rb.rotation = trot; rb.isKinematic = false; }
                    LastReleaseVel.Remove(idv);
                    try { st.PlaceRaw(p); TablePlacements++; } catch (Exception e) { Plugin.L.LogWarning($"[bodies] place on table: {e.Message}"); }
                    continue;
                }
                if (rb != null)
                {
                    rb.position = target; rb.rotation = trot;
                    rb.isKinematic = false;
                    if (LastReleaseVel.Remove(idv, out var v)) rb.linearVelocity = v;
                    rb.WakeUp();
                }
            }
        }
    }

    static PotatoPeeler.Workstation? TableAt(Vector3 pos)
    {
        var all = PotatoPeeler.Workstation.All;
        if (all == null) return null;
        for (int i = 0; i < all.Count; i++)
            if (all[i] is { } w && w.ZoneContains(pos)) return w;
        return null;
    }

    /// <summary>Host: note the release velocity before Core hands authority back.</summary>
    static void NoteRelease(ReadOnlySpan<byte> payload)
    {
        try
        {
            var m = BodyMessages.Decode<BodyRelease>(payload);
            LastReleaseVel[m.Body.Value] = new Vector3(m.Vel.X, m.Vel.Y, m.Vel.Z);
        }
        catch (Exception) { }
    }

    static float TrueScale(PotatoPeeler.PotatoInstance p)
    {
        try { return p.BaseScale; } catch { return 1f; }
    }

    // ---------------- client ----------------

    /// <summary>The client's own loose potatoes came from its copy of the save: the host's bodies replace them.</summary>
    static void ClearLocalOnce()
    {
        if (_clientCleared) return;
        _clientCleared = true;
        int n = 0;
        foreach (var p in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.PotatoInstance>())
        {
            p.gameObject.SetActive(false);
            n++;
        }
        Plugin.L.LogMessage($"[bodies] client: put away {n} local loose potatoes; the host's bodies take over.");
    }

    static void OnProxySpawned(ClientBodyMirror.Proxy proxy)
    {
        try
        {
            var act = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PotatoPileActivator>();
            if (act == null) return;
            var s = proxy.Spawn;
            var inst = act.SpawnLoaded(new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z), new Quaternion(s.Rot.X, s.Rot.Y, s.Rot.Z, s.Rot.W),
                s.TrueScale > 0 ? s.TrueScale : 1f, true, false, s.Variant);
            if (inst == null) return;
            if (inst.Body != null) inst.Body.isKinematic = true;
            ObjById[proxy.Id.Value] = inst;
            if (PendingFactory.Remove(proxy.Id.Value, out var depth)) ApplyFactory(proxy.Id.Value, depth);
        }
        catch (Exception e) { Plugin.L.LogWarning($"[bodies] proxy spawn failed: {e.Message}"); }
    }

    static void OnProxyDespawned(NetId id)
    {
        LocalOwned.Remove(id.Value); PeelLocked.Remove(id.Value); TakenFromTable.Remove(id.Value);
        if (!ObjById.Remove(id.Value, out var inst) || inst == null) return;
        // Back into the game's pool (not just hidden), so instances are reused instead of leaking.
        var act = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PotatoPileActivator>();
        ApplyingPool = true;
        try { if (act == null || !act.ConsumeActive(inst)) inst.gameObject.SetActive(false); }
        catch { inst.gameObject.SetActive(false); }
        finally { ApplyingPool = false; }
    }

    [ThreadStatic] public static bool ApplyingPool;

    /// <summary>Client: a local tool (vacuum, bucket, barrow...) took this proxy into a container. Stop driving it.</summary>
    public static NetId ForgetProxy(PotatoPeeler.PotatoInstance inst)
    {
        var id = IdOf(inst);
        if (!id.IsValid) return id;
        ObjById.Remove(id.Value);
        LocalOwned.Remove(id.Value); PeelLocked.Remove(id.Value); TakenFromTable.Remove(id.Value);
        return id;
    }

    /// <summary>Host: a body leaves the world for good (ruined on a client's table): stop tracking and pool it.</summary>
    public static void HostRemove(NetId id)
    {
        if (_host == null || !ObjById.TryGetValue(id.Value, out var p) || p == null) return;
        RemoteDriven.Remove(id.Value);
        HostTakeBack(id);
        var act = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PotatoPileActivator>();
        try { if (act != null) act.NoteLost(p); } catch { }
        if (p != null && p.gameObject.activeSelf) p.gameObject.SetActive(false);
    }

    /// <summary>Host: a client gave this body back (station hand-back): the host places it now, so stop following the client.</summary>
    public static void HostForgetRemote(NetId id) => RemoteDriven.Remove(id.Value);

    /// <summary>Client, every frame: drive proxies from the interpolation buffers.</summary>
    public static void UpdateClient()
    {
        if (_client == null) return;
        var hostNow = _client.EstimatedHostSeconds(Time.realtimeSinceStartupAsDouble) - _client.DelaySeconds;
        UpdateLocalOwned();
        foreach (var (idv, inst) in ObjById)
        {
            if (inst == null || LocalOwned.ContainsKey(idv)) continue;
            var s = _client.Sample(new NetId(idv), hostNow);
            if (!s.HasPose) continue;
            var pos = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
            var rot = new Quaternion(s.Rot.X, s.Rot.Y, s.Rot.Z, s.Rot.W);
            var rb = inst.Body;
            if (rb != null)
            {
                if (!rb.isKinematic) rb.isKinematic = true;
                rb.position = pos; rb.rotation = rot;
            }
            inst.transform.SetPositionAndRotation(pos, rot);
            // The host's tables park the potatoes in their slots, and the hand never pulls parked potatoes. Our copies
            // carry the same mark so the client's hand leaves a table's potatoes alone too.
            bool parked = PotatoPeeler.Workstation.AnyZoneContains(pos);
            if (inst.IsParked != parked) inst._IsParked_k__BackingField = parked;
        }
        var nack = _client.TakeNack();
        if (nack is { } n && MpRuntime.Client is { } c)
        {
            var w = new NetWriter();
            n.Write(w);
            c.Send(MsgId.BodyAudit, w.Span);
        }
    }

    // ---------------- station grants (spec 14) ----------------

    public static void HostGrant(NetId id, byte slot) => _host?.GrantTo(id, slot, ClaimReason.Peel, Time.realtimeSinceStartupAsDouble);
    public static bool HostTakeBack(NetId id) => _host != null && _host.TakeBack(id, Time.realtimeSinceStartupAsDouble);

    /// <summary>Client: the host granted this body to our peel session; we simulate it until we hand it back.</summary>
    public static void LockForPeel(NetId id)
    {
        if (Get(id) is { } p) p._IsParked_k__BackingField = false;
        LocalOwned[id.Value] = double.NaN;
        PeelLocked.Add(id.Value);
        PeelLockedAt[id.Value] = Time.realtimeSinceStartupAsDouble;
    }

    /// <summary>Client: the body went back to the host (placed on a table); follow the host again without a release message.</summary>
    public static void UnlockPeel(NetId id)
    {
        PeelLocked.Remove(id.Value);
        LocalOwned.Remove(id.Value);
        if (Get(id) is { } inst && inst.Body != null) inst.Body.isKinematic = true;
    }

    // ---------------- client claims ----------------

    /// <summary>Client: the local hand is capturing <paramref name="inst"/>. Makes it ours at once and asks the host.</summary>
    public static void OnLocalCapture(PotatoPeeler.PotatoInstance inst)
    {
        if (_client == null || inst == null || MpRuntime.Client is not { State: ClientState.Playing } c) return;
        var id = IdOf(inst);
        if (!id.IsValid) return;
        if (_client.Get(id) is { } px && px.Owner != _client.Slot && px.Mode is BodyMode.ClientOwned or BodyMode.PeelHeld) return;
        bool had = LocalOwned.ContainsKey(id.Value);
        if (!had && PotatoPeeler.Workstation.AnyZoneContains(inst.transform.position)) TakenFromTable.Add(id.Value);
        LocalOwned[id.Value] = double.NaN;
        if (inst.Body != null) { inst.Body.isKinematic = false; inst.Body.WakeUp(); }
        if (had && (_client.OwnsLocally(id) || _client.IsClaimPending(id))) return;
        c.Send(new BodyClaim { Entries = new List<ClaimEntry> { _client.Claim(id, ClaimReason.Hand) } }, Delivery.Reliable);
        Claims++;
    }

    static void OnAuthChanged(AuthEntry e)
    {
        if (_client == null || !LocalOwned.ContainsKey(e.Id.Value) || PeelLocked.Contains(e.Id.Value)) return;
        if (e.Result is AuthResult.Denied or AuthResult.Revoked || (e.Result == AuthResult.Granted && e.Owner != _client.Slot))
        {
            LocalOwned.Remove(e.Id.Value);
            Denied++;
            if (Get(e.Id) is { } inst && inst.Body != null) inst.Body.isKinematic = true;
            Plugin.L.LogMessage($"[bodies] claim on {e.Id.Value}: {e.Result} (owner {e.Owner}); back to the host's copy.");
        }
    }

    static void UpdateLocalOwned()
    {
        if (_client == null || LocalOwned.Count == 0 || MpRuntime.Client is not { State: ClientState.Playing } c) return;
        var now = Time.realtimeSinceStartupAsDouble;
        bool send = now >= _nextOwnedSend;
        if (send) _nextOwnedSend = now + 1.0 / 30;
        var states = new List<OwnedEntry>();
        foreach (var (idv, letGo) in LocalOwned.ToList())
        {
            var inst = Get(new NetId(idv));
            if (inst == null) { LocalOwned.Remove(idv); continue; }
            var rb = inst.Body;
            var t = inst.transform;
            // A peel grant whose session is over (dropped, ruined, ended without placing it): it becomes a normal held body
            // again, so the usual let-go path hands it back to the host.
            if (PeelLocked.Contains(idv))
            {
                var ps = PotatoPeeler.PeelSession.Instance;
                bool inSession = ps != null && ps.IsActive && ps._potato != null && ps._potato.Pointer == inst.Pointer;
                if (!inSession && now - PeelLockedAt.GetValueOrDefault(idv) > 1.5) { PeelLocked.Remove(idv); if (rb != null && rb.isKinematic) rb.isKinematic = false; }
            }
            // A table's raw area has no floor: the table snaps potatoes into slots, and only the host's tables do that.
            // So a potato we carried into any table's raw area goes straight back to the host, whose table catches it.
            bool inZone = !PeelLocked.Contains(idv) && PotatoPeeler.Workstation.AnyZoneContains(t.position);
            if (!inZone) TakenFromTable.Remove(idv);
            bool atTable = inZone && !TakenFromTable.Contains(idv);
            bool held = !atTable && (PeelLocked.Contains(idv) || inst.IsHeld || inst.IsCarried || PotatoPeeler.PotatoPuller.IsAnyPulling(inst));
            if (held) LocalOwned[idv] = double.NaN;
            else if (double.IsNaN(letGo) && !atTable) LocalOwned[idv] = now;
            else
            {
                bool settled = rb == null || rb.IsSleeping() || rb.linearVelocity.sqrMagnitude < 0.01f;
                if (atTable || (settled && now - letGo > 0.4) || now - letGo > 3.0)
                {
                    var v = rb != null ? rb.linearVelocity : Vector3.zero;
                    // Into a table: drop straight down so the host's table catches it where we let go.
                    if (atTable) v = new Vector3(0, Math.Min(v.y, 0), 0);
                    var av = rb != null ? rb.angularVelocity : Vector3.zero;
                    c.Send(new BodyRelease
                    {
                        Body = new NetId(idv), Reason = v.sqrMagnitude > 4f ? ReleaseReason.Throw : ReleaseReason.Drop,
                        Pos = V(t.position), Rot = Q(t.rotation), Vel = V(v), AngVel = V(av),
                    }, Delivery.Reliable);
                    LocalOwned.Remove(idv);
                    TakenFromTable.Remove(idv);
                    if (rb != null) rb.isKinematic = true;
                    Releases++;
                    if (atTable) TableHandoffs++;
                    continue;
                }
            }
            if (send) states.Add(new OwnedEntry(new NetId(idv), V(t.position), Q(t.rotation), rb != null ? V(rb.linearVelocity) : default));
        }
        if (states.Count > 0) c.Send(new BodyOwnedState { Entries = states }, Delivery.Unreliable);
    }

    public static object Describe() => new
    {
        claims = Claims, releases = Releases, denied = Denied, factorySent = FactorySent, factoryApplied = FactoryApplied, tableHandoffs = TableHandoffs, tablePlacements = TablePlacements,
        localOwned = LocalOwned.Keys.ToArray(), remoteDriven = RemoteDriven.ToArray(),
        hostRx = HostRx.ToDictionary(k => $"0x{k.Key:X2}", k => k.Value),
        hostRemoteOwned = _host?.Authority.All.Count(r => r.IsRemoteOwned),
        denies = _host?.DenyCounts.ToDictionary(k => k.Key.ToString(), k => k.Value), lastDeny = _host?.LastDeny,
        role = Role,
        bodies = ObjById.Count,
        hostLive = _host?.LiveIds.Count(),
        clientProxies = _client?.Count,
        clientDelay = _client?.DelaySeconds,
    };
}

/// <summary>Client: capturing a networked potato with the hand claims it (spec 07 4).</summary>
[HarmonyLib.HarmonyPatch(typeof(PotatoPeeler.PotatoPuller), nameof(PotatoPeeler.PotatoPuller.Capture))]
static class ClientCaptureClaim
{
    static bool Prefix(PotatoPeeler.PotatoInstance potato)
    {
        if (!PotatoPleaseMP.Net.MpRuntime.IsClient) return true;
        try
        {
            // A potato parked in a host table's tray stays there (the host's own hand never pulls parked potatoes).
            if (potato != null && potato.IsParked) return false;
            BodySync.OnLocalCapture(potato!);
        }
        catch (Exception e) { Plugin.L.LogWarning($"[bodies] capture claim: {e.Message}"); }
        return true;
    }
}

/// <summary>A client never activates pile potatoes on its own: loose bodies come from the host (spec 06/07).</summary>
[HarmonyLib.HarmonyPatch(typeof(PotatoPeeler.PotatoPileActivator), nameof(PotatoPeeler.PotatoPileActivator.Update))]
static class ClientActivatorGate
{
    static bool Prefix() => !PotatoPleaseMP.Net.MpRuntime.IsClient;
}
