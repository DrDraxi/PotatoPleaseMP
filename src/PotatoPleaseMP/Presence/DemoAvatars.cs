using UnityEngine;

namespace PotatoPleaseMP.Presence;

/// <summary>Dev-only: avatars driven by simple paths so the look can be checked with screenshots before networking exists.</summary>
internal static class DemoAvatars
{
    sealed class Demo { public AvatarRig Rig = null!; public Vector3 Center; public float Radius, Speed, Angle; public bool Walk; public float Yaw; }

    static readonly List<Demo> Demos = new();
    static GameObject? _root;

    static Transform Root()
    {
        if (_root == null) _root = new GameObject("PPMP_Avatars");
        return _root.transform;
    }

    public static object Spawn(int slot, string name, Vector3 at, float yaw, bool walk, float radius)
    {
        var rig = AvatarBuilder.Build(slot, name, Root());
        Demos.Add(new Demo { Rig = rig, Center = at, Radius = radius, Speed = 1.3f, Walk = walk, Yaw = yaw, Angle = slot * 1.7f });
        rig.Apply(at, yaw, 0, 0);
        return new { spawned = name, slot, count = Demos.Count };
    }

    public static object Clear()
    {
        foreach (var d in Demos) if (d.Rig.Root != null) UnityEngine.Object.Destroy(d.Rig.Root.gameObject);
        Demos.Clear();
        return new { cleared = true };
    }

    public static void LateUpdate()
    {
        if (Demos.Count == 0) return;
        var dt = Time.deltaTime;
        foreach (var d in Demos)
        {
            if (d.Rig.Root == null) continue;
            if (!d.Walk) { d.Rig.Apply(d.Center, d.Yaw, 0, dt); continue; }
            d.Angle += d.Speed / Mathf.Max(0.5f, d.Radius) * dt;
            var pos = d.Center + new Vector3(Mathf.Cos(d.Angle), 0, Mathf.Sin(d.Angle)) * d.Radius;
            var tangent = new Vector3(-Mathf.Sin(d.Angle), 0, Mathf.Cos(d.Angle));
            d.Rig.Apply(pos, Mathf.Atan2(tangent.x, tangent.z) * Mathf.Rad2Deg, 10f * Mathf.Sin(Time.time), dt);
        }
    }
}
