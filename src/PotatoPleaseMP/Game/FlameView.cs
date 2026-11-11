using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// Other players' flamethrower fire. The shooter sends its flame's pose while firing (FlameState 0x96, ~10 Hz, and once on
/// stop); everyone else plays a copy of the game's own flame particles there. The damage side (snow carve/thaw, scorch
/// marks, charred potatoes) already travels as FlameOps and potato char.
/// Payload: U8 slot (the host fills it in), Bool firing, pos (3 F32), rot (4 F32).
/// </summary>
internal static class FlameView
{
    static readonly Dictionary<byte, (GameObject go, ParticleSystem ps, double until)> Remote = new();
    static bool _wasFiring;
    static double _nextSend;
    public static long Out, In;

    static PotatoPeeler.Flamethrower? Local()
    {
        foreach (var f in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.Flamethrower>()) if (f != null && f.player != null) return f;
        return null;
    }

    /// <summary>Every frame: send ours while it fires, fade out copies whose shooter went quiet.</summary>
    public static void Update()
    {
        if (!MpRuntime.Active) return;
        var now = Time.realtimeSinceStartupAsDouble;
        try
        {
            var f = Local();
            bool firing = f != null && f._firing;
            if ((firing && now >= _nextSend) || firing != _wasFiring)
            {
                _nextSend = now + 0.1; _wasFiring = firing;
                var t = f != null && f.flame != null ? f.flame.transform : null;
                var w = new NetWriter();
                w.U8(MpRuntime.Client?.LocalSlot ?? 0); w.Bool(firing && t != null);
                var p = t != null ? t.position : Vector3.zero; var q = t != null ? t.rotation : Quaternion.identity;
                w.F32(p.x); w.F32(p.y); w.F32(p.z); w.F32(q.x); w.F32(q.y); w.F32(q.z); w.F32(q.w);
                if (MpRuntime.Host is { } h) h.Broadcast(MsgId.FlameState, w.Span);
                else if (MpRuntime.Client is { State: ClientState.Playing } c) c.Send(MsgId.FlameState, w.Span);
                Out++;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[flame] out: {e.Message}"); _nextSend = now + 5; }
        foreach (var (slot, v) in Remote.ToList())
            if (now > v.until && v.ps != null && v.ps.isEmitting) v.ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    public static void Apply(ref NetReader r, PlayerInfo? from)
    {
        byte slot = r.U8(); bool firing = r.Bool();
        var p = new Vector3(r.F32(), r.F32(), r.F32()); var q = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
        if (from != null) slot = from.Slot; // never trust the sender's slot byte
        In++;
        if (!Remote.TryGetValue(slot, out var v) || v.go == null)
        {
            if (!firing) return;
            var src = UnityEngine.Object.FindObjectsOfType<PotatoPeeler.Flamethrower>().FirstOrDefault(x => x != null && x.flame != null)
                      ?? Resources.FindObjectsOfTypeAll<PotatoPeeler.Flamethrower>().FirstOrDefault(x => x != null && x.flame != null);
            if (src == null) return;
            var go = UnityEngine.Object.Instantiate(src.flame.gameObject);
            go.name = $"MP_RemoteFlame_{slot}";
            go.SetActive(true);
            v = (go, go.GetComponent<ParticleSystem>(), 0);
        }
        v.go.transform.SetPositionAndRotation(p, q);
        if (firing) { v.until = Time.realtimeSinceStartupAsDouble + 0.5; if (v.ps != null && !v.ps.isEmitting) v.ps.Play(true); }
        else { v.until = 0; if (v.ps != null) v.ps.Stop(true, ParticleSystemStopBehavior.StopEmitting); }
        Remote[slot] = v;
    }

    public static void ResetSession()
    {
        foreach (var v in Remote.Values) if (v.go != null) UnityEngine.Object.Destroy(v.go);
        Remote.Clear(); _wasFiring = false;
    }

    public static object Describe() => new { Out, In, remote = Remote.Select(kv => new { slot = kv.Key, playing = kv.Value.until > Time.realtimeSinceStartupAsDouble, emitting = kv.Value.ps != null && kv.Value.ps.isEmitting }).ToArray() };
}
