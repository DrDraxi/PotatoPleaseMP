using System.Text.Json;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace PotatoPleaseMP.Saves;

/// <summary>
/// Test-copy isolation (spec 15 §2): with [Isolation] DataProfile set, the game's saves, profile and PlayerPrefs live under
/// &lt;GameRoot&gt;/PPMP-Data/&lt;profile&gt; instead of the real LocalLow folder (which Steam Cloud syncs) and registry key.
/// All targets are unique native bodies (spec 03/15). With DataProfile empty nothing changes.
/// </summary>
internal static class Isolation
{
    public static ConfigEntry<string> DataProfile = null!;
    public static ConfigEntry<string> DataRoot = null!;
    public static ConfigEntry<bool> PrefsOverlay = null!;

    public static bool Active { get; private set; }
    public static string ProfileRoot { get; private set; } = "";
    public static string SaveFolder => Path.Combine(ProfileRoot, "saves");
    /// <summary>Set when a self-test failed: every game save is refused for this run.</summary>
    public static bool BlockSaves { get; private set; }

    public static void Init(ConfigFile cfg)
    {
        DataProfile = cfg.Bind("Isolation", "DataProfile", "",
            "Test copies only: a name like Host or Client keeps this copy's saves, profile and settings in its own folder " +
            "(never the real, Steam-Cloud-synced ones). Empty = the game's normal data.");
        DataRoot = cfg.Bind("Isolation", "DataRoot", "", "Folder for the isolated data; empty = <game folder>/PPMP-Data/<DataProfile>.");
        PrefsOverlay = cfg.Bind("Isolation", "PrefsOverlay", true,
            "Isolated copies read settings they never wrote from the real registry (writes always stay local).");
        if (string.IsNullOrWhiteSpace(DataProfile.Value)) return;
        ProfileRoot = string.IsNullOrWhiteSpace(DataRoot.Value)
            ? Path.Combine(BepInEx.Paths.GameRootPath, "PPMP-Data", DataProfile.Value.Trim())
            : DataRoot.Value.Trim();
        Directory.CreateDirectory(SaveFolder);
        Prefs.Load(Path.Combine(ProfileRoot, "prefs.json"));
        Active = true;
        Plugin.L.LogWarning($"Isolation: data profile '{DataProfile.Value}' at {ProfileRoot}.");
    }

    /// <summary>After patching: verify every path points into the isolated root, otherwise block saving.</summary>
    public static void SelfTest()
    {
        if (!Active) return;
        var fails = new List<string>();
        void Check(string what, string value) { if (!Norm(value).StartsWith(Norm(ProfileRoot))) fails.Add($"{what} = {value}"); }
        try
        {
            Check("SaveSlots.Folder", PotatoPeeler.SaveSlots.Folder);
            Check("SaveSlots.PathFor(0)", PotatoPeeler.SaveSlots.PathFor(0));
            Check("SaveSlots.LegacyPath", PotatoPeeler.SaveSlots.LegacyPath);
            Check("PlayerProfile.Path", PotatoPeeler.PlayerProfile.Path);
            PlayerPrefs.SetInt("ppmp.selftest", 7);
            if (PlayerPrefs.GetInt("ppmp.selftest", 0) != 7) fails.Add("PlayerPrefs round trip");
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\{Application.companyName}\{Application.productName}"))
                if (key != null && key.GetValueNames().Any(n => n.StartsWith("ppmp.selftest"))) fails.Add("PlayerPrefs reached the registry");
            PlayerPrefs.DeleteKey("ppmp.selftest");
        }
        catch (Exception e) { fails.Add("exception " + e.Message); }
        if (fails.Count == 0) { Plugin.L.LogMessage("Isolation self-test OK."); return; }
        BlockSaves = true;
        foreach (var f in fails) Plugin.L.LogError("Isolation self-test FAILED: " + f + " -- all saving is blocked this run.");
    }

    static string Norm(string p) => Path.GetFullPath(p).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    // ---------------- save paths ----------------

    [HarmonyPatch(typeof(PotatoPeeler.SaveSlots), nameof(PotatoPeeler.SaveSlots.Folder), MethodType.Getter)]
    static class FolderPatch
    {
        static bool Prefix(ref string __result)
        {
            if (SaveContext.ClientFolder != null) { __result = SaveContext.ClientFolder; return false; }
            if (!Active) return true;
            __result = SaveFolder;
            return false;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.SaveSlots), nameof(PotatoPeeler.SaveSlots.PathFor))]
    static class PathForPatch
    {
        static bool _warned;
        static void Postfix(ref string __result)
        {
            var folder = SaveContext.ClientFolder ?? (Active ? SaveFolder : null);
            if (folder == null || __result == null || Norm(__result).StartsWith(Norm(folder))) return;
            if (!_warned) { _warned = true; Plugin.L.LogWarning($"Isolation: PathFor returned {__result}; redirected."); }
            __result = Path.Combine(folder, Path.GetFileName(__result));
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.SaveSlots), nameof(PotatoPeeler.SaveSlots.LegacyPath), MethodType.Getter)]
    static class LegacyPathPatch
    {
        static bool Prefix(ref string __result)
        {
            if (!Active) return true;
            __result = Path.Combine(ProfileRoot, "save_v1.bin");
            return false;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.SaveSlots), nameof(PotatoPeeler.SaveSlots.DeleteAll))]
    static class DeleteAllPatch
    {
        static bool Prefix()
        {
            if (!Active) return true;
            for (int s = 0; s < PotatoPeeler.SaveSlots.Count; s++) PotatoPeeler.SaveSlots.Delete(s);
            PotatoPeeler.SaveSlots.TryDelete(PotatoPeeler.SaveSlots.LegacyPath);
            return false;
        }
    }

    [HarmonyPatch(typeof(PotatoPeeler.LegacyDataMigration), nameof(PotatoPeeler.LegacyDataMigration.Run))]
    static class MigrationPatch
    {
        static bool Prefix() => !Active;
    }

    [HarmonyPatch(typeof(PotatoPeeler.PlayerProfile), nameof(PotatoPeeler.PlayerProfile.Path), MethodType.Getter)]
    static class ProfilePathPatch
    {
        static bool Prefix(ref string __result)
        {
            if (!Active) return true;
            __result = Path.Combine(ProfileRoot, "profile.json");
            return false;
        }
    }

    /// <summary>A failed self-test must never let a test copy write anywhere.</summary>
    [HarmonyPatch(typeof(PotatoPeeler.SaveManager), nameof(PotatoPeeler.SaveManager.Write))]
    static class WriteGuard
    {
        // Clients never write: the world is the host's, and their own slots must stay untouched.
        static bool Prefix() => !BlockSaves && !SaveContext.IsClient;
    }

    // ---------------- PlayerPrefs ----------------

    /// <summary>Per-copy PlayerPrefs store (prefs.json), with optional read-through to the real registry.</summary>
    internal static class Prefs
    {
        [ThreadStatic] public static bool PassThrough;
        static string _file = "";
        static Dictionary<string, JsonElement> _v = new();
        static readonly HashSet<string> Deleted = new();
        static bool _dirty;
        static float _nextFlush;

        sealed class Doc
        {
            public int schema { get; set; } = 1;
            public Dictionary<string, JsonElement> v { get; set; } = new();
            public List<string> del { get; set; } = new();
        }

        public static void Load(string file)
        {
            _file = file;
            try
            {
                if (!File.Exists(file)) return;
                var d = JsonSerializer.Deserialize<Doc>(File.ReadAllText(file));
                if (d == null) return;
                _v = d.v ?? new();
                foreach (var k in d.del ?? new()) Deleted.Add(k);
            }
            catch (Exception e) { Plugin.L.LogWarning($"Isolation: prefs.json unreadable ({e.Message}); starting empty."); }
        }

        public static void Flush()
        {
            if (!_dirty || _file == "") return;
            _dirty = false;
            try
            {
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new Doc { v = _v, del = Deleted.ToList() }));
                File.Move(tmp, _file, true);
            }
            catch (Exception e) { Plugin.L.LogWarning($"Isolation: prefs flush failed: {e.Message}"); }
        }

        public static void Tick()
        {
            if (!Active || !_dirty || Time.realtimeSinceStartup < _nextFlush) return;
            _nextFlush = Time.realtimeSinceStartup + 5f;
            Flush();
        }

        static void Set(string key, object value)
        {
            _v[key] = JsonSerializer.SerializeToElement(value);
            Deleted.Remove(key);
            _dirty = true;
        }

        static bool Local(string key, out JsonElement e) => _v.TryGetValue(key, out e);
        static bool Overlay(string key) => PrefsOverlay.Value && !Deleted.Contains(key);

        [HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.SetInt))]
        static class SetIntP { static bool Prefix(string key, int value) { if (!Active || PassThrough) return true; Set(key, new { t = "i", v = value }); return false; } }

        [HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.SetFloat))]
        static class SetFloatP { static bool Prefix(string key, float value) { if (!Active || PassThrough) return true; Set(key, new { t = "f", v = value }); return false; } }

        [HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.SetString))]
        static class SetStringP { static bool Prefix(string key, string value) { if (!Active || PassThrough) return true; Set(key, new { t = "s", v = value }); return false; } }

        static bool TryTyped(string key, string type, out JsonElement value)
        {
            value = default;
            if (!Local(key, out var e)) return false;
            if (e.GetProperty("t").GetString() != type) return false;
            value = e.GetProperty("v");
            return true;
        }

        [HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.GetInt), typeof(string), typeof(int))]
        static class GetIntP
        {
            static bool Prefix(string key, int defaultValue, ref int __result)
            {
                if (!Active || PassThrough) return true;
                if (TryTyped(key, "i", out var v)) { __result = v.GetInt32(); return false; }
                if (Local(key, out _) || !Overlay(key)) { __result = defaultValue; return false; }
                PassThrough = true;
                try { __result = PlayerPrefs.GetInt(key, defaultValue); } finally { PassThrough = false; }
                return false;
            }
        }

        [HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.GetFloat), typeof(string), typeof(float))]
        static class GetFloatP
        {
            static bool Prefix(string key, float defaultValue, ref float __result)
            {
                if (!Active || PassThrough) return true;
                if (TryTyped(key, "f", out var v)) { __result = v.GetSingle(); return false; }
                if (Local(key, out _) || !Overlay(key)) { __result = defaultValue; return false; }
                PassThrough = true;
                try { __result = PlayerPrefs.GetFloat(key, defaultValue); } finally { PassThrough = false; }
                return false;
            }
        }

        [HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.GetString), typeof(string), typeof(string))]
        static class GetStringP
        {
            static bool Prefix(string key, string defaultValue, ref string __result)
            {
                if (!Active || PassThrough) return true;
                if (TryTyped(key, "s", out var v)) { __result = v.GetString() ?? defaultValue; return false; }
                if (Local(key, out _) || !Overlay(key)) { __result = defaultValue; return false; }
                PassThrough = true;
                try { __result = PlayerPrefs.GetString(key, defaultValue); } finally { PassThrough = false; }
                return false;
            }
        }

        [HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.GetString), typeof(string))]
        static class GetString1P
        {
            static bool Prefix(string key, ref string __result)
            {
                if (!Active || PassThrough) return true;
                if (TryTyped(key, "s", out var v)) { __result = v.GetString() ?? ""; return false; }
                if (Local(key, out _) || !Overlay(key)) { __result = ""; return false; }
                PassThrough = true;
                try { __result = PlayerPrefs.GetString(key); } finally { PassThrough = false; }
                return false;
            }
        }

        [HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.HasKey))]
        static class HasKeyP
        {
            static bool Prefix(string key, ref bool __result)
            {
                if (!Active || PassThrough) return true;
                if (Local(key, out _)) { __result = true; return false; }
                if (!Overlay(key)) { __result = false; return false; }
                PassThrough = true;
                try { __result = PlayerPrefs.HasKey(key); } finally { PassThrough = false; }
                return false;
            }
        }

        [HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.DeleteKey))]
        static class DeleteKeyP
        {
            static bool Prefix(string key)
            {
                if (!Active || PassThrough) return true;
                _v.Remove(key);
                Deleted.Add(key);
                _dirty = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(PlayerPrefs), nameof(PlayerPrefs.Save))]
        static class SaveP
        {
            static bool Prefix()
            {
                if (!Active || PassThrough) return true;
                Flush();
                return false;
            }
        }
    }
}
