using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Injection;

namespace PotatoPleaseMP.Net.Steam;

/// <summary>
/// Receives Steam callbacks through the game's own Steamworks.NET dispatcher (spec 17 §6): the game pumps callbacks with
/// manual dispatch and drops any id nobody registered, so we inject our own Callback/CallResult subclasses into
/// CallbackDispatcher's tables. Whether Il2CppInterop puts our overrides into the il2cpp vtable is proven at startup with a
/// dry run before anything is injected.
/// </summary>
public sealed class MpCallback : Steamworks.Callback
{
    public static readonly IntPtr Sentinel = new(0x5050_4D50);
    public MpCallback(IntPtr ptr) : base(ptr) { }
    public MpCallback() : base(ClassInjector.DerivedConstructorPointer<MpCallback>()) => ClassInjector.DerivedConstructorBody(this);

    internal int Id;
    internal Action<IntPtr>? Handler;
    internal int Hits, DryHits;

    public override void OnRunCallback(IntPtr pvParam)
    {
        if (pvParam == Sentinel) { DryHits++; return; }
        Hits++;
        try { Handler?.Invoke(pvParam); }
        catch (Exception e) { SteamEvents.Fault(Id, e); } // never let it reach RunFrame: it would skip the game's handlers
    }

    public override bool IsGameServer => false;
    public override Il2CppSystem.Type GetCallbackType() => null!;
    public override void SetUnregistered() { }
    internal void DryRun() => base.OnRunCallback(Sentinel);
}

public sealed class MpCallResult : Steamworks.CallResult
{
    public MpCallResult(IntPtr ptr) : base(ptr) { }
    public MpCallResult() : base(ClassInjector.DerivedConstructorPointer<MpCallResult>()) => ClassInjector.DerivedConstructorBody(this);

    internal ulong Call;
    internal int ExpectedId;
    internal Action<IntPtr, bool>? Handler;
    internal bool Done;
    internal int DryHits;

    public override void OnRunCallResult(IntPtr pvParam, bool bFailed, ulong hSteamAPICall)
    {
        if (pvParam == MpCallback.Sentinel) { DryHits++; return; }
        if (Done || hSteamAPICall != Call) return;
        Done = true;
        try { Handler?.Invoke(pvParam, bFailed); }
        catch (Exception e) { SteamEvents.Fault(ExpectedId, e); }
        finally { SteamEvents.Forget(Call); }
    }

    public override Il2CppSystem.Type GetCallbackType() => null!;
    public override void SetUnregistered() { }
    internal void DryRun() => base.OnRunCallResult(MpCallback.Sentinel, false, 0);
}

public enum SteamMode { Unknown, Full, PollResults, RichPresence, Unavailable }

internal static class SteamEvents
{
    static readonly Dictionary<int, MpCallback> Callbacks = new();
    static readonly Dictionary<ulong, (MpCallResult Cr, float Deadline)> Pending = new();
    public static readonly Queue<(int Id, ulong A, ulong B, uint U)> Queue = new();
    public static int Faults;
    static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        ClassInjector.RegisterTypeInIl2Cpp<MpCallback>();
        ClassInjector.RegisterTypeInIl2Cpp<MpCallResult>();
    }

    public static string DryRun()
    {
        try
        {
            Register();
            var cb = new MpCallback();
            cb.DryRun();
            var cr = new MpCallResult();
            cr.DryRun();
            return cb.DryHits == 1 && cr.DryHits == 1 ? "ok" : $"overrides not hit (callback {cb.DryHits}, result {cr.DryHits})";
        }
        catch (Exception e) { return "exception " + (e.InnerException ?? e).Message; }
    }

    public static bool On(int id, Action<IntPtr> handler)
    {
        var reg = Steamworks.CallbackDispatcher.m_registeredCallbacks;
        if (reg == null) return false;
        var cb = new MpCallback { Id = id, Handler = handler };
        Il2CppSystem.Collections.Generic.List<Steamworks.Callback> list;
        if (reg.ContainsKey(id)) list = reg[id];
        else { list = new Il2CppSystem.Collections.Generic.List<Steamworks.Callback>(); reg[id] = list; }
        list.Add(cb);
        Callbacks[id] = cb;
        return reg.ContainsKey(id) && reg[id].Contains(cb);
    }

    public static int Hits(int id) => Callbacks.TryGetValue(id, out var cb) ? cb.Hits : -1;

    public static bool Await(ulong call, int expectedId, Action<IntPtr, bool> handler, float timeoutS)
    {
        if (call == 0) { handler(IntPtr.Zero, true); return false; }
        var cr = new MpCallResult { Call = call, ExpectedId = expectedId, Handler = handler };
        Pending[call] = (cr, UnityEngine.Time.realtimeSinceStartup + timeoutS);
        Steamworks.CallbackDispatcher.Register(new Steamworks.SteamAPICall_t(call), cr);
        return true;
    }

    public static void Update()
    {
        if (Pending.Count == 0) return;
        var now = UnityEngine.Time.realtimeSinceStartup;
        foreach (var (call, (cr, deadline)) in Pending.ToArray())
        {
            if (now < deadline || cr.Done) continue;
            cr.Done = true;
            Pending.Remove(call);
            try { Steamworks.CallbackDispatcher.Unregister(new Steamworks.SteamAPICall_t(call), cr); } catch { }
            cr.Handler?.Invoke(IntPtr.Zero, true);
        }
    }

    public static void Forget(ulong call) => Pending.Remove(call);

    public static void Fault(int id, Exception e)
    {
        Faults++;
        if (Faults < 20) Plugin.L.LogError($"Steam callback {id}: {e}");
    }

    internal static ulong R64(IntPtr p, int off) => (ulong)Marshal.ReadInt64(p, off);
}

/// <summary>Brings Steam up once the game's SteamService is initialised and runs the callback self-test (spec 17 §6).</summary>
internal static class SteamBoot
{
    public static SteamMode Mode { get; private set; } = SteamMode.Unknown;
    public static readonly List<string> Log = new();
    static int _stage;
    static float _deadline;
    static ulong _testCall;
    static bool _resultOk, _resultFailed;

    static void Step(string s) { Log.Add(s); Plugin.L.LogMessage("[steam] " + s); }

    public static void Update()
    {
        SteamEvents.Update();
        switch (_stage)
        {
            case 0:
                if (!Steamworks.SteamAPI.IsSteamRunning() || !Steamworks.CallbackDispatcher.IsInitialized) return;
                if (!PotatoPeeler.Platform.SteamService.Initialized) return;
                if (!SteamNative.TryInit(out var err)) { Step("native init failed: " + err); Mode = SteamMode.Unavailable; _stage = 9; return; }
                Step($"native ok: {SteamNative.PersonaName} ({SteamNative.LocalSteamId}), app {SteamNative.AppId}, build {SteamNative.BuildId}, logged on {SteamNative.LoggedOn}");
                var dry = SteamEvents.DryRun();
                Step("dry run: " + dry);
                // The dry run goes through the interop wrapper, which may call the base body directly; the real proof is
                // a live call result below, so carry on either way (an unhit override is harmless: the base body is empty).
                bool inj = SteamEvents.On(1281, _ => { }) & SteamEvents.On(1251, p => SteamEvents.Queue.Enqueue((1251, SteamEvents.R64(p, 8), 0, 0)))
                    & SteamEvents.On(333, p => SteamEvents.Queue.Enqueue((333, SteamEvents.R64(p, 0), SteamEvents.R64(p, 8), 0)));
                Step("inject: " + (inj ? "ok" : "FAILED"));
                _testCall = SteamNative.RequestNumberOfCurrentPlayers();
                SteamEvents.Await(_testCall, 1107, (p, failed) =>
                {
                    _resultFailed = failed || p == IntPtr.Zero;
                    _resultOk = !_resultFailed && System.Runtime.InteropServices.Marshal.ReadByte(p, 0) == 1;
                    if (_resultOk) Step($"call result 1107 ok: {System.Runtime.InteropServices.Marshal.ReadInt32(p, 4)} players");
                }, 10f);
                _deadline = UnityEngine.Time.realtimeSinceStartup + 11f;
                _stage = 1;
                return;
            case 1:
                if (!_resultOk && !_resultFailed && UnityEngine.Time.realtimeSinceStartup < _deadline) return;
                Mode = _resultOk ? SteamMode.Full : SteamMode.PollResults;
                Step($"mode {Mode} (relay {SteamNative.RelayAvailability()}, relay callbacks {SteamEvents.Hits(1281)})");
                _stage = 9;
                return;
        }
    }

    public static object Describe() => new
    {
        mode = Mode.ToString(),
        alive = SteamNative.Alive,
        steamId = SteamNative.LocalSteamId.ToString(),
        name = SteamNative.PersonaName,
        relay = SteamNative.RelayAvailability(),
        relayCallbacks = SteamEvents.Hits(1281),
        faults = SteamEvents.Faults,
        log = Log,
    };
}
