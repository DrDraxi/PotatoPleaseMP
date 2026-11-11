using HarmonyLib;
using PotatoPleaseMP.Core.Net;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Claw crane driving for every player. The host's crane is the only one that simulates (grabs, carries, drops into the
/// hopper). A client that sits in the seat mounts locally (camera, controls, hints), reads its own controls each frame and
/// streams the aim + grip to the host (DevicePose sub 4, ~20 Hz); the host drives its crane with them, and the arm pose comes
/// back in the normal crane stream (which also says who drives it, so a second player gets "in use").
/// Seat requests: DeviceRequest kind 10, op 1 sit / op 0 leave.
/// </summary>
internal static class ClawSync
{
    const byte None = 0xFF, HostSlot = 0;

    // host
    static byte _driver = None;
    static double _lastCtl;
    static Vector3 _remoteTarget;
    static int _remoteGrip;
    public static long CtlIn, Mounts;

    // client
    public static bool DrivingLocally;
    public static int? DevGrip; // dev test: hold the grip button
    static byte _knownDriver = None;
    static double _mountedAt, _leavingSince, _nextSend;
    public static long CtlOut;

    static byte LocalSlot => MpRuntime.Client?.LocalSlot ?? None;

    // ---------------- host ----------------

    public static byte DriverByte(PotatoPeeler.ClawCrane claw)
    {
        if (_driver != None) return _driver;
        try { return claw.IsOccupied ? HostSlot : None; } catch { return None; }
    }

    public static void OnRequest(PlayerInfo from, byte op)
    {
        var claw = UnityEngine.Object.FindObjectOfType<PotatoPeeler.ClawCrane>();
        if (claw == null) return;
        if (op == 1)
        {
            if (_driver == None && !claw.IsOccupied)
            {
                _driver = from.Slot; _lastCtl = Time.realtimeSinceStartupAsDouble; _remoteTarget = claw._target; _remoteGrip = claw._gripDir;
                try { claw.SuspendAuto(); } catch { }
                Mounts++;
                MachineSync.Last = $"slot {from.Slot} drives the claw crane";
            }
        }
        else if (op == 0 && _driver == from.Slot) Release(claw);
    }

    static void Release(PotatoPeeler.ClawCrane? claw)
    {
        _driver = None;
        try { if (claw != null && claw._gripDir != 0) claw.SetGripDir(0); } catch { }
    }

    public static void OnPlayerLeft(byte slot) { if (_driver == slot) Release(UnityEngine.Object.FindObjectOfType<PotatoPeeler.ClawCrane>()); }

    public static void OnControls(PlayerInfo from, ref NetReader r)
    {
        if (r.U8() != 4 || _driver != from.Slot) return;
        _remoteTarget = new Vector3(r.F32(), r.F32(), r.F32());
        _remoteGrip = (sbyte)r.U8();
        _lastCtl = Time.realtimeSinceStartupAsDouble;
        CtlIn++;
    }

    public static void HostBeforeUpdate(PotatoPeeler.ClawCrane claw)
    {
        if (_driver == None) return;
        if (Time.realtimeSinceStartupAsDouble - _lastCtl > 3.0) { Release(claw); return; }
        claw._target = _remoteTarget;
        try { if (claw._gripDir != _remoteGrip) claw.SetGripDir(_remoteGrip); } catch { }
        _posBefore = claw._pos; _frameBefore = Time.frameCount;
    }

    static Vector3 _posBefore; static int _frameBefore;
    public static long ManualSims;

    /// <summary>Host: the crane's own Update only simulates with a local driver; for a remote driver run the arm ourselves.</summary>
    public static void HostAfterUpdate(PotatoPeeler.ClawCrane claw)
    {
        if (_driver == None || _frameBefore != Time.frameCount || claw.IsOccupied) return;
        if (claw._pos != _posBefore) return; // the game simulated it after all
        try { if (claw._gripDir != _remoteGrip) claw.SetGripDir(_remoteGrip); claw.Simulate(Time.deltaTime); ManualSims++; } catch (Exception e) { Plugin.L.LogWarning($"[claw] sim: {e.Message}"); }
    }

    [HarmonyPatch(typeof(PotatoPeeler.ClawCrane), nameof(PotatoPeeler.ClawCrane.TickAuto))]
    static class NoAutoWhileDriven { static bool Prefix() => !(MpRuntime.IsHost && _driver != None); }

    [HarmonyPatch(typeof(PotatoPeeler.ClawCrane), nameof(PotatoPeeler.ClawCrane.TickControls))]
    static class NoHostControlsWhileDriven { static bool Prefix() => !(MpRuntime.IsHost && _driver != None); }

    [HarmonyPatch(typeof(PotatoPeeler.ClawCraneSeat), nameof(PotatoPeeler.ClawCraneSeat.OnInteract))]
    static class SeatGate
    {
        static bool Prefix()
        {
            if (MpRuntime.IsHost && _driver != None) { UI.Notice.Show("A crewmate is driving the claw crane."); return false; }
            if (MpRuntime.IsClient && _knownDriver != None && _knownDriver != LocalSlot) { UI.Notice.Show("Someone is already driving the claw crane."); return false; }
            return true;
        }
    }

    // ---------------- client ----------------

    [HarmonyPatch(typeof(PotatoPeeler.ClawCrane), nameof(PotatoPeeler.ClawCrane.Mount))]
    static class MountPatch
    {
        static void Postfix()
        {
            if (!MpRuntime.IsClient || MpRuntime.Client is not { State: ClientState.Playing } c) return;
            DrivingLocally = true; _mountedAt = Time.realtimeSinceStartupAsDouble; _leavingSince = 0;
            var w = new NetWriter(); w.U8(10); w.U8(0); w.U8(1); c.Send(MsgId.DeviceRequest, w.Span);
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.ClawCrane), nameof(PotatoPeeler.ClawCrane.FinishDismount))]
    static class DismountPatch
    {
        static void Postfix()
        {
            if (!DrivingLocally) return;
            DrivingLocally = false;
            if (MpRuntime.Client is { State: ClientState.Playing } c) { var w = new NetWriter(); w.U8(10); w.U8(0); w.U8(0); c.Send(MsgId.DeviceRequest, w.Span); }
        }
    }

    static readonly System.Random Rng = new();
    public static long ShellsMade;

    /// <summary>Client: fill our crane's (never simulated) load with as many potatoes as the host's claw holds, and draw them.</summary>
    static void ClientLoadView(PotatoPeeler.ClawCrane claw)
    {
        try
        {
            var held = claw._held;
            if (held == null) return;
            int want = HostLoad, have = held.Count;
            if (have > want) held.RemoveRange(want, have - want);
            else if (have < want && claw.claw != null)
            {
                var at = claw.claw.position;
                for (int i = 0, n = Math.Min(want - have, 120); i < n; i++)
                {
                    claw.AddShell(at, UnityEngine.Random.rotation, 0.9f + (float)Rng.NextDouble() * 0.2f, Rng.Next(0, 4));
                    ShellsMade++;
                }
            }
            if (held.Count == 0) return;
            // keep them in the claw (the host's StepLoad does this; ours never runs)
            var tip = claw._pos; var rot = claw.claw != null ? claw.claw.rotation : Quaternion.identity;
            for (int i = 0; i < held.Count; i++) { var sh = held[i]; sh.Pos = tip + rot * sh.Slot; sh.Rot = rot * sh.SlotRot; }
            claw.DrawLoad();
        }
        catch (Exception e) { Plugin.L.LogWarning($"[claw] load view: {e.Message}"); HostLoad = 0; }
    }

    /// <summary>Client, each frame instead of the crane's Update: show the host's load; when driving, read our controls and send them.</summary>
    public static void ClientTick(PotatoPeeler.ClawCrane claw)
    {
        ClientFollow(claw);
        ClientLoadView(claw);
        if (!DrivingLocally) return;
        var now = Time.realtimeSinceStartupAsDouble;
        try
        {
            if (claw._leaving)
            {
                if (_leavingSince == 0) _leavingSince = now;
                if (now - _leavingSince > 0.6) claw.FinishDismount();
                return;
            }
            claw.TickControls();
            if (DevGrip is { } dg && claw._gripDir != dg) claw.SetGripDir(dg);
            if (now < _nextSend || MpRuntime.Client is not { State: ClientState.Playing } c) return;
            _nextSend = now + 0.05;
            var w = new NetWriter();
            w.U8(4); w.F32(claw._target.x); w.F32(claw._target.y); w.F32(claw._target.z); w.U8((byte)(sbyte)claw._gripDir);
            c.Send(MsgId.DevicePose, w.Span, delivery: Delivery.Unreliable);
            CtlOut++;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[claw] tick: {e.Message}"); DrivingLocally = false; }
    }

    /// <summary>Client: who drives the host's crane. Someone else got it first: leave the seat.</summary>
    public static void OnDriver(PotatoPeeler.ClawCrane claw, byte driver)
    {
        _knownDriver = driver;
        if (!DrivingLocally || driver == LocalSlot) return;
        if (Time.realtimeSinceStartupAsDouble - _mountedAt < 1.5) return; // our sit request is still on its way
        UI.Notice.Show("Someone else is driving the claw crane.");
        try { claw.RequestDismount(); } catch { DrivingLocally = false; }
    }

    public static int HostLoad;

    // client: the host's latest arm position and velocity, and when it arrived
    static Vector3 _netPos, _netVel;
    static double _netAt;
    static bool _hasNet;
    public static long Snaps;

    /// <summary>Client: a crane pose from the host (20 Hz).</summary>
    public static void OnHostPose(PotatoPeeler.ClawCrane claw, Vector3 pos, Vector3 vel)
    {
        _netPos = pos; _netVel = vel; _netAt = Time.realtimeSinceStartupAsDouble;
        if (!_hasNet) { claw._pos = pos; _hasNet = true; }
        claw._vel = vel;
    }

    /// <summary>Client, every frame: move the arm toward where the host's arm is now (last pose + velocity for its age),
    /// so it glides instead of stepping at the packet rate and does not trail the host's.</summary>
    static void ClientFollow(PotatoPeeler.ClawCrane claw)
    {
        if (!_hasNet) return;
        var age = (float)Math.Min(Time.realtimeSinceStartupAsDouble - _netAt, 0.15);
        var goal = _netPos + _netVel * age;
        var cur = claw._pos;
        if ((goal - cur).sqrMagnitude > 16f) { claw._pos = goal; Snaps++; } // > 4 m off (join, reload): jump
        else claw._pos = Vector3.Lerp(cur, goal, 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime));
        // The crane's Update (skipped here: the host simulates) is what poses the boom, stick and claw. Pose them from the
        // arm position, with the cable swing worked out locally from that motion, like Simulate does on the host.
        try
        {
            var tip = claw._pos + Vector3.up * claw.hangLength;
            float dt = Time.deltaTime;
            if (dt > 0f) claw.StepSwing(tip, dt);
            claw.PoseArm(tip);
            claw.PoseClaw(tip);
            Poses++;
        }
        catch (Exception e) { if (PoseErrors++ == 0) Plugin.L.LogWarning($"[claw] pose: {e.Message}"); }
    }
    public static long Poses, PoseErrors;

    /// <summary>Client: the crane's load (HUD, autopilot checks) is the host's: our copy never grabs.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.ClawCrane), nameof(PotatoPeeler.ClawCrane.Load), MethodType.Getter)]
    static class LoadView { static void Postfix(ref int __result) { if (MpRuntime.IsClient) __result = HostLoad; } }

    public static void ResetSession() { _driver = None; _knownDriver = None; DrivingLocally = false; _hasNet = false; }

    public static object Describe()
    {
        var claw = UnityEngine.Object.FindObjectOfType<PotatoPeeler.ClawCrane>();
        return new
        {
            driver = _driver, ManualSims, Snaps, Poses, PoseErrors, claw = claw?.claw == null ? null : Dev.Reflect.Describe(claw.claw.position), knownDriver = _knownDriver, DrivingLocally, CtlIn, CtlOut, Mounts,
            occupied = claw != null && claw.IsOccupied, target = claw == null ? null : Dev.Reflect.Describe(claw._target),
            pos = claw == null ? null : Dev.Reflect.Describe(claw._pos), grip = claw?._grip, load = claw?.Load, HostLoad, ShellsMade, held = claw?._held?.Count,
            sample = claw?._held != null && claw._held.Count > 0 ? new { claw._held[0].Scale, claw._held[0].Variant, slot = Dev.Reflect.Describe(claw._held[0].Slot), pos = Dev.Reflect.Describe(claw._held[0].Pos) } : null,
        };
    }
}
