using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace PotatoPleaseMP.Dev;

/// <summary>
/// A virtual gamepad added to the Input System, fed with GamepadState events every frame. The game reads pads like
/// any player input (its own Steam Input bridge does the same with QueueStateEvent&lt;GamepadState&gt;, so that generic
/// instance exists natively). Works with the window unfocused and needs no Harmony patches on input getters, most
/// of which share native bodies with other methods and can't be patched safely.
/// </summary>
internal static class VirtualPad
{
    static Gamepad? _pad;
    public static Vector2 LeftStick, RightStick;
    public static float LeftTrigger, RightTrigger;
    static readonly Dictionary<GamepadButton, float> Buttons = new(); // button -> release time
    static float _stickUntil, _triggerUntil;
    static bool _dirty;

    public static bool Exists => _pad != null && _pad.added;

    static Gamepad Pad()
    {
        if (_pad == null || !_pad.added)
        {
            var dev = InputSystem.AddDevice("Gamepad", "PPMP Virtual Pad", null);
            _pad = dev.Cast<Gamepad>();
            Plugin.L.LogMessage($"Virtual pad added: {_pad.name} (id {_pad.deviceId}).");
        }
        return _pad;
    }

    public static void SetSticks(Vector2 left, Vector2 right, float seconds)
    {
        Pad();
        LeftStick = left; RightStick = right;
        _stickUntil = Time.realtimeSinceStartup + seconds;
        _dirty = true;
    }

    public static void SetTriggers(float left, float right, float seconds)
    {
        Pad();
        LeftTrigger = left; RightTrigger = right;
        _triggerUntil = Time.realtimeSinceStartup + seconds;
        _dirty = true;
    }

    public static void Press(GamepadButton b, float seconds)
    {
        Pad();
        Buttons[b] = Time.realtimeSinceStartup + seconds;
        _dirty = true;
    }

    public static object Describe() => new
    {
        exists = Exists,
        left = new[] { LeftStick.x, LeftStick.y },
        right = new[] { RightStick.x, RightStick.y },
        triggers = new[] { LeftTrigger, RightTrigger },
        buttons = Buttons.Keys.Select(k => k.ToString()).ToArray(),
        current = Gamepad.current?.name,
    };

    /// <summary>Every frame: expire holds and queue the current state while anything is held.</summary>
    public static void Tick()
    {
        if (_pad == null) return;
        var now = Time.realtimeSinceStartup;
        if (now > _stickUntil && (LeftStick != Vector2.zero || RightStick != Vector2.zero)) { LeftStick = RightStick = Vector2.zero; _dirty = true; }
        if (now > _triggerUntil && (LeftTrigger != 0 || RightTrigger != 0)) { LeftTrigger = RightTrigger = 0; _dirty = true; }
        foreach (var kv in Buttons.ToList())
            if (now > kv.Value) { Buttons.Remove(kv.Key); _dirty = true; }

        bool active = LeftStick != Vector2.zero || RightStick != Vector2.zero || LeftTrigger > 0 || RightTrigger > 0 || Buttons.Count > 0;
        if (!active && !_dirty) return;
        _dirty = false;

        var state = new GamepadState();
        foreach (var b in Buttons.Keys) state = state.WithButton(b, true);
        state.leftStick = LeftStick;
        state.rightStick = RightStick;
        state.leftTrigger = LeftTrigger;
        state.rightTrigger = RightTrigger;
        InputSystem.QueueStateEvent(Pad(), state, -1);
    }
}
