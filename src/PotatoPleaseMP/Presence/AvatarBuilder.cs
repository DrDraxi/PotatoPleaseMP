using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;

namespace PotatoPleaseMP.Presence;

/// <summary>
/// Builds the placeholder avatar for remote players (spec 04 §5): a figure made from Unity primitives with the game's own
/// URP material, tinted per slot, plus a world-space name tag in the game's font. No colliders, no game components, no
/// Player tag, layer 0, so nothing in the game mistakes it for the player.
/// </summary>
internal static class AvatarBuilder
{
    public static readonly Color[] Coat =
    {
        Hex(0xB8332A), Hex(0x2F6FA8), Hex(0xD49A22), Hex(0x3C8C4A), Hex(0x7A4FA0), Hex(0x2A9C9A), Hex(0x9C5A2A), Hex(0x6E6E6E),
    };
    static readonly Color Skin = new(0.86f, 0.70f, 0.58f), Dark = new(0.13f, 0.13f, 0.14f);

    static Mesh? _capsule, _sphere, _cylinder, _cube;
    static Material? _base;
    static Material? _skin, _dark;
    static TMP_FontAsset? _font;
    static readonly Dictionary<int, Material> CoatMats = new();

    static Color Hex(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

    /// <summary>Scene-scoped resources; call again after a scene load (meshes/materials die with their scene objects).</summary>
    static void EnsureResources()
    {
        if (_capsule == null)
        {
            _capsule = Prim(PrimitiveType.Capsule);
            _sphere = Prim(PrimitiveType.Sphere);
            _cylinder = Prim(PrimitiveType.Cylinder);
            _cube = Prim(PrimitiveType.Cube);
        }
        if (_base == null)
        {
            _base = FindBaseMaterial();
            _skin = Tinted(Skin);
            _dark = Tinted(Dark);
            CoatMats.Clear();
        }
        if (_font == null)
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_FontAsset>()))
            {
                var f = o.Cast<TMP_FontAsset>();
                if (f.name == "RussoOne-Regular SDF outline") { _font = f; break; }
                if (_font == null && (f.name == "RussoOne-Regular SDF" || f.name == "CourierPrime-Regular SDF")) _font = f;
            }
        }
    }

    static Mesh Prim(PrimitiveType t)
    {
        var go = GameObject.CreatePrimitive(t);
        var mesh = go.GetComponent<MeshFilter>().sharedMesh;
        UnityEngine.Object.Destroy(go);
        return mesh;
    }

    /// <summary>The mop handle's material (Shader Graphs/S_Basic, untextured white): guaranteed to render in this URP build.</summary>
    static Material FindBaseMaterial()
    {
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<PotatoPeeler.Mop>()))
        {
            var mop = o.Cast<PotatoPeeler.Mop>();
            if (mop.handleMaterial != null) return mop.handleMaterial;
            if (mop.strandMaterial != null) return mop.strandMaterial;
        }
        Plugin.L.LogWarning("Avatar: no mop material found, using URP/Lit.");
        return new Material(Shader.Find("Universal Render Pipeline/Lit"));
    }

    static Material Tinted(Color c)
    {
        var m = new Material(_base!);
        m.color = c;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        return m;
    }

    static Material CoatFor(int slot)
    {
        if (!CoatMats.TryGetValue(slot, out var m) || m == null) CoatMats[slot] = m = Tinted(Coat[slot & 7]);
        return m;
    }

    public static AvatarRig Build(int slot, string name, Transform parent)
    {
        EnsureResources();
        var coat = CoatFor(slot);
        var root = new GameObject($"Avatar_{slot}");
        root.transform.SetParent(parent, false);
        var rig = new AvatarRig { Root = root.transform };

        rig.Hips = Pivot("Hips", root.transform, new Vector3(0, 0.95f, 0));
        Part("Torso", _capsule!, coat, rig.Hips, new Vector3(0, 0.20f, 0), new Vector3(0.50f, 0.40f, 0.34f));
        Part("Belt", _cylinder!, _dark!, rig.Hips, new Vector3(0, -0.12f, 0), new Vector3(0.52f, 0.03f, 0.36f));
        rig.LegL = Pivot("LegL", rig.Hips, new Vector3(-0.11f, 0, 0));
        rig.LegR = Pivot("LegR", rig.Hips, new Vector3(0.11f, 0, 0));
        Part("Leg", _capsule!, _dark!, rig.LegL, new Vector3(0, -0.475f, 0), new Vector3(0.16f, 0.475f, 0.16f));
        Part("Leg", _capsule!, _dark!, rig.LegR, new Vector3(0, -0.475f, 0), new Vector3(0.16f, 0.475f, 0.16f));
        rig.Neck = Pivot("Neck", rig.Hips, new Vector3(0, 0.60f, 0));
        Part("Head", _sphere!, _skin!, rig.Neck, new Vector3(0, 0.10f, 0), Vector3.one * 0.24f);
        Part("EyeL", _sphere!, _dark!, rig.Neck, new Vector3(-0.045f, 0.12f, 0.105f), Vector3.one * 0.035f);
        Part("EyeR", _sphere!, _dark!, rig.Neck, new Vector3(0.045f, 0.12f, 0.105f), Vector3.one * 0.035f);
        Part("Hat", _cylinder!, coat, rig.Neck, new Vector3(0, 0.20f, 0), new Vector3(0.30f, 0.07f, 0.30f));
        Part("HatFur", _cylinder!, _dark!, rig.Neck, new Vector3(0, 0.16f, 0), new Vector3(0.32f, 0.03f, 0.32f));
        Part("FlapL", _cube!, _dark!, rig.Neck, new Vector3(-0.13f, 0.08f, 0), new Vector3(0.05f, 0.12f, 0.14f));
        Part("FlapR", _cube!, _dark!, rig.Neck, new Vector3(0.13f, 0.08f, 0), new Vector3(0.05f, 0.12f, 0.14f));
        rig.ArmL = Pivot("ShoulderL", rig.Hips, new Vector3(-0.27f, 0.42f, 0));
        rig.ArmR = Pivot("ShoulderR", rig.Hips, new Vector3(0.27f, 0.42f, 0));
        Part("Arm", _capsule!, coat, rig.ArmL, new Vector3(0, -0.25f, 0), new Vector3(0.12f, 0.27f, 0.12f));
        Part("Arm", _capsule!, coat, rig.ArmR, new Vector3(0, -0.25f, 0), new Vector3(0.12f, 0.27f, 0.12f));
        Part("Mitt", _sphere!, _dark!, rig.ArmL, new Vector3(0, -0.52f, 0), Vector3.one * 0.11f);
        Part("Mitt", _sphere!, _dark!, rig.ArmR, new Vector3(0, -0.52f, 0), Vector3.one * 0.11f);
        rig.ToolSocket = Pivot("ToolSocket", rig.ArmR, new Vector3(0, -0.55f, 0.05f));

        var tagGo = new GameObject("Tag");
        tagGo.transform.SetParent(root.transform, false);
        tagGo.transform.localPosition = new Vector3(0, 2.05f, 0);
        var tmp = tagGo.AddComponent<TextMeshPro>();
        if (_font != null) tmp.font = _font;
        tmp.richText = false;
        tmp.enableWordWrapping = false;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSize = 1.6f;
        tmp.color = Color.Lerp(Coat[slot & 7], Color.white, 0.35f);
        tmp.text = name.Length > 24 ? name[..24] : name;
        rig.Tag = tmp;
        return rig;
    }

    static Transform Pivot(string name, Transform parent, Vector3 local)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = local;
        return go.transform;
    }

    static void Part(string name, Mesh mesh, Material mat, Transform parent, Vector3 local, Vector3 scale)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = local;
        go.transform.localScale = scale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
    }
}

/// <summary>Transforms of one avatar plus a tiny procedural walk (the full solver lives in Core later).</summary>
internal sealed class AvatarRig
{
    public Transform Root = null!, Hips = null!, LegL = null!, LegR = null!, Neck = null!, ArmL = null!, ArmR = null!, ToolSocket = null!;
    public TextMeshPro Tag = null!;
    float _phase;
    Vector3 _last;
    bool _hasLast;

    /// <summary>Feet position, body yaw, view pitch (degrees, + = down like the game).</summary>
    public void Apply(Vector3 feet, float yaw, float pitch, float dt)
    {
        var moved = _hasLast ? Vector3.ProjectOnPlane(feet - _last, Vector3.up).magnitude : 0f;
        _last = feet; _hasLast = true;
        Root.position = feet;
        Root.rotation = Quaternion.Euler(0, yaw, 0);
        _phase += moved / 1.2f;
        var speed = dt > 0 ? moved / dt : 0;
        var amp = Mathf.Clamp01(speed / 2f);
        var swing = Mathf.Sin(_phase * Mathf.PI) * 25f * amp;
        LegL.localRotation = Quaternion.Euler(swing, 0, 0);
        LegR.localRotation = Quaternion.Euler(-swing, 0, 0);
        ArmL.localRotation = Quaternion.Euler(-swing * 0.8f, 0, 0);
        ArmR.localRotation = Quaternion.Euler(swing * 0.8f, 0, 0);
        Hips.localPosition = new Vector3(0, 0.95f + 0.03f * Mathf.Abs(Mathf.Sin(_phase * Mathf.PI)) * amp, 0);
        Neck.localRotation = Quaternion.Euler(Mathf.Clamp(pitch, -60, 60), 0, 0);
        var cam = Camera.main;
        if (cam != null) Tag.transform.rotation = cam.transform.rotation;
    }
}
