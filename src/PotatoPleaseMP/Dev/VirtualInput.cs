using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PotatoPleaseMP.Dev;

/// <summary>
/// Input injected by tests and bots, merged into the game's own input reads. Works while the window is unfocused
/// because it never goes through the OS or the Input System's devices: Harmony postfixes on InputAction's polling
/// methods and on PlayerInputReader's vector getters. Actions are matched by name ("Interact", "Attack", ...).
/// </summary>
internal static class VirtualInput
{
    sealed class Hold { public int PressedFrame; public float ReleaseAt; public int ReleasedFrame = -1; }

    static readonly Dictionary<string, Hold> Holds = new(StringComparer.OrdinalIgnoreCase);
    public static Vector2? Move;
    public static Vector2 LookDelta;     // added once, then cleared
    public static Vector2 PointerDelta;  // added once, then cleared
    public static Vector2? PointerDeltaHold; // added every frame while set

    public static int Count => Holds.Count;

    /// <summary>Presses an action for <paramref name="seconds"/> (real time).</summary>
    public static void Press(string action, float seconds)
    {
        Holds[action] = new Hold { PressedFrame = Time.frameCount, ReleaseAt = Time.realtimeSinceStartup + Math.Max(0.02f, seconds) };
    }

    public static void Release(string action)
    {
        if (Holds.TryGetValue(action, out var h)) h.ReleaseAt = 0;
    }

    public static string[] Active => Holds.Where(kv => kv.Value.ReleasedFrame < 0).Select(kv => kv.Key).ToArray();

    /// <summary>Once per frame, early.</summary>
    public static void Tick()
    {
        var now = Time.realtimeSinceStartup;
        foreach (var kv in Holds.ToList())
        {
            var h = kv.Value;
            if (h.ReleasedFrame < 0 && now >= h.ReleaseAt) h.ReleasedFrame = Time.frameCount;
            else if (h.ReleasedFrame >= 0 && Time.frameCount > h.ReleasedFrame + 1) Holds.Remove(kv.Key);
        }
    }

    static Hold? Get(InputAction a)
    {
        if (Holds.Count == 0 || a == null) return null;
        return Holds.TryGetValue(a.name, out var h) ? h : null;
    }

    [HarmonyPatch(typeof(PotatoPeeler.PlayerInputReader), nameof(PotatoPeeler.PlayerInputReader.Held))]
    static class IsPressedPatch
    {
        static void Postfix(InputAction action, ref bool __result)
        {
            if (__result) return;
            var h = Get(action);
            if (h != null && h.ReleasedFrame < 0) __result = true;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.PlayerInputReader), nameof(PotatoPeeler.PlayerInputReader.Pressed))]
    static class WasPressedPatch
    {
        static void Postfix(InputAction action, ref bool __result)
        {
            if (__result) return;
            var h = Get(action);
            if (h != null && h.PressedFrame == Time.frameCount) __result = true;
        }
    }

    // (InputAction-level patches cost too much: Input System calls them thousands of times a frame)
    static class WasPerformedPatch
    {
        static void Postfix(InputAction __instance, ref bool __result)
        {
            if (__result) return;
            var h = Get(__instance);
            if (h != null && h.PressedFrame == Time.frameCount) __result = true;
        }
    }


    static class WasReleasedPatch
    {
        static void Postfix(InputAction __instance, ref bool __result)
        {
            if (__result) return;
            var h = Get(__instance);
            if (h != null && h.ReleasedFrame == Time.frameCount) __result = true;
        }
    }

    // (getter patch disabled: possible shared native body)
    static class MovePatch
    {
        static void Postfix(ref Vector2 __result)
        {
            if (Move.HasValue) __result = Move.Value;
        }
    }

    // (getter patch disabled: possible shared native body)
    static class LookPatch
    {
        static int _frame = -1;
        static void Postfix(ref Vector2 __result)
        {
            if (LookDelta == Vector2.zero) return;
            __result += LookDelta;
            if (_frame != Time.frameCount) _frame = Time.frameCount;
        }
    }

    // (getter patch disabled: possible shared native body)
    static class PointerPatch
    {
        static void Postfix(ref Vector2 __result)
        {
            __result += PointerDelta;
            if (PointerDeltaHold.HasValue) __result += PointerDeltaHold.Value;
        }
    }

    /// <summary>
    /// PlayerInputReader's bool getters (IsPeelHeld, IsRotateHeld, WasAutoPeelPressedThisFrame, ...) read their
    /// actions directly; press?a=IsPeelHeld holds the getter of that name ("Is*" true while held, "Was*" on the
    /// press frame).
    /// </summary>
    // Not patched: these getters share native bodies with hundreds of other methods (crashes).
    static class BoolGetters
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            typeof(PotatoPeeler.PlayerInputReader).GetProperties()
                .Where(p => p.PropertyType == typeof(bool) && (p.Name.StartsWith("Is") || p.Name.StartsWith("Was")) && p.GetGetMethod() != null)
                .Select(p => (System.Reflection.MethodBase)p.GetGetMethod()!);

        static void Postfix(System.Reflection.MethodBase __originalMethod, ref bool __result)
        {
            if (__result || Holds.Count == 0) return;
            var name = __originalMethod.Name.Substring(4); // get_
            if (!Holds.TryGetValue(name, out var h)) return;
            __result = name.StartsWith("Was") ? h.PressedFrame == Time.frameCount : h.ReleasedFrame < 0;
        }
    }

    /// <summary>Late in the frame: one-shot deltas are consumed.</summary>
    public static void EndFrame()
    {
        LookDelta = Vector2.zero;
        PointerDelta = Vector2.zero;
    }
}
