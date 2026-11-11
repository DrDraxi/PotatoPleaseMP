using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Captures every final carve op (spec 05): a native detour on VoxelPotato.ApplyBrush(ref BrushOp, bool). Harmony cannot
/// read the by-ref non-blittable struct (the arg arrives null), so the detour reads the raw il2cpp struct through the
/// interop's native field offsets and keeps a managed copy (<see cref="CarveOp"/>). Replay builds a BrushOp again and calls
/// ApplyBrush on the target potato.
/// </summary>
internal static unsafe class PeelCapture
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate byte ApplyBrushFn(IntPtr self, IntPtr op, byte spawnChunks, IntPtr method);

    static INativeDetour? _detour;
    static ApplyBrushFn? _original, _hook;
    static readonly Dictionary<string, int> Off = new();
    [ThreadStatic] static bool _replaying;

    public static long Ops;
    public static string Last = "";
    public static string Status = "not installed";
    /// <summary>Raised for every op applied by this machine's own peeling (not replays).</summary>
    public static event Action<PotatoPeeler.VoxelPotato, CarveOp>? OpApplied;

    /// <summary>Dev mirror: every captured op is replayed on this potato too (proves replay works without networking).</summary>
    public static PotatoPeeler.VoxelPotato? Mirror;

    public static void Install()
    {
        if (_detour != null) return;
        try
        {
            var mi = typeof(PotatoPeeler.VoxelPotato).GetMethod(nameof(PotatoPeeler.VoxelPotato.ApplyBrush))!;
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(mi)
                        ?? throw new Exception("no method info field");
            var methodInfo = (IntPtr)field.GetValue(null)!;
            var fn = *(IntPtr*)methodInfo;
            foreach (var f in typeof(PotatoPeeler.BrushOp).GetFields(BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (!f.Name.StartsWith("NativeFieldInfoPtr_")) continue;
                var fi = (IntPtr)f.GetValue(null)!;
                // Offsets of a value type's fields count the object header; the raw struct starts after it.
                Off[f.Name.Substring("NativeFieldInfoPtr_".Length)] = (int)IL2CPP.il2cpp_field_get_offset(fi) - 2 * IntPtr.Size;
            }
            _hook = Hook;
            _detour = INativeDetour.CreateAndApply(fn, _hook, out _original);
            Status = $"installed at 0x{fn.ToInt64():X}, {Off.Count} fields";
            Plugin.L.LogMessage("[peel] ApplyBrush detour " + Status);
        }
        catch (Exception e)
        {
            Status = "failed: " + e.Message;
            Plugin.L.LogError("[peel] ApplyBrush detour failed: " + e);
        }
    }

    static byte Hook(IntPtr self, IntPtr op, byte spawnChunks, IntPtr method)
    {
        CarveOp? captured = null;
        if (!_replaying)
        {
            try { captured = Read(op); Ops++; Last = captured.ToString(); }
            catch (Exception e) { Last = "read failed: " + e.Message; }
        }
        var r = _original!(self, op, spawnChunks, method);
        if (captured != null)
        {
            try
            {
                var potato = new PotatoPeeler.VoxelPotato(self);
                OpApplied?.Invoke(potato, captured);
                if (Mirror != null && Mirror.Pointer != self) Apply(Mirror, captured);
            }
            catch (Exception e) { Last = "after: " + e.Message; }
        }
        return r;
    }

    static T Get<T>(IntPtr op, string name) where T : unmanaged => *(T*)(op + Off[name]);

    static Vector4[] ReadArray(IntPtr op, string name, int stride)
    {
        var arr = *(IntPtr*)(op + Off[name]);
        if (arr == IntPtr.Zero) return Array.Empty<Vector4>();
        long len = *(long*)(arr + 3 * IntPtr.Size);                 // il2cpp array: klass, monitor, bounds, max_length
        var data = (byte*)(arr + 4 * IntPtr.Size);
        var res = new Vector4[len];
        for (int i = 0; i < len; i++)
        {
            float* f = (float*)(data + i * stride);
            res[i] = stride == 16 ? new Vector4(f[0], f[1], f[2], f[3]) : new Vector4(f[0], f[1], f[2], 0);
        }
        return res;
    }

    static CarveOp Read(IntPtr op) => new()
    {
        Kind = Get<int>(op, "Kind"),
        Points = ReadArray(op, "PointsLocal", 12),
        Rights = ReadArray(op, "RightsLocal", 16),
        Radius = Get<float>(op, "Radius"),
        AffectedMin = Get<Vector3>(op, "AffectedMin"),
        AffectedMax = Get<Vector3>(op, "AffectedMax"),
        ToothPeriod = Get<float>(op, "ToothPeriod"),
        ToothDepth = Get<float>(op, "ToothDepth"),
        HalfWidth = Get<float>(op, "HalfWidth"),
        EdgeToothPeriod = Get<float>(op, "EdgeToothPeriod"),
        EdgeToothDepth = Get<float>(op, "EdgeToothDepth"),
        LatticeAxis = Get<Vector3>(op, "LatticeAxis"),
        PlanePoint = Get<Vector3>(op, "PlanePoint"),
        PlaneNormal = Get<Vector3>(op, "PlaneNormal"),
        MaxDepth = Get<float>(op, "MaxDepthBelowOriginal"),
        GuardDepth = Get<float>(op, "GuardDepthBelowOriginal"),
    };

    /// <summary>Replays an op on a potato (another machine's cut, or the dev mirror).</summary>
    public static bool Apply(PotatoPeeler.VoxelPotato target, CarveOp c)
    {
        var op = new PotatoPeeler.BrushOp
        {
            Kind = c.Kind,
            Radius = c.Radius,
            AffectedMin = c.AffectedMin,
            AffectedMax = c.AffectedMax,
            ToothPeriod = c.ToothPeriod,
            ToothDepth = c.ToothDepth,
            HalfWidth = c.HalfWidth,
            EdgeToothPeriod = c.EdgeToothPeriod,
            EdgeToothDepth = c.EdgeToothDepth,
            LatticeAxis = c.LatticeAxis,
            PlanePoint = c.PlanePoint,
            PlaneNormal = c.PlaneNormal,
            MaxDepthBelowOriginal = c.MaxDepth,
            GuardDepthBelowOriginal = c.GuardDepth,
        };
        if (c.Points.Length > 0)
        {
            var pts = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(c.Points.Length);
            for (int i = 0; i < c.Points.Length; i++) pts[i] = c.Points[i];
            op.PointsLocal = pts;
        }
        if (c.Rights.Length > 0)
        {
            var rs = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector4>(c.Rights.Length);
            for (int i = 0; i < c.Rights.Length; i++) rs[i] = c.Rights[i];
            op.RightsLocal = rs;
        }
        _replaying = true;
        try { return target.ApplyBrush(ref op, false); }
        finally { _replaying = false; }
    }
}

/// <summary>A captured carve op in potato-local space (everything ApplyBrush reads).</summary>
internal sealed class CarveOp
{
    public int Kind;
    public Vector4[] Points = Array.Empty<Vector4>(), Rights = Array.Empty<Vector4>();
    public float Radius, ToothPeriod, ToothDepth, HalfWidth, EdgeToothPeriod, EdgeToothDepth, MaxDepth, GuardDepth;
    public Vector3 AffectedMin, AffectedMax, LatticeAxis, PlanePoint, PlaneNormal;
    public override string ToString() => $"kind {Kind} pts {Points.Length} rights {Rights.Length} r {Radius:0.0000} hw {HalfWidth:0.0000} maxDepth {MaxDepth:0.0000}";
}
