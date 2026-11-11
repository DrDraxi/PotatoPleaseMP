using System.Collections;
using System.Globalization;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace PotatoPleaseMP.Dev;

/// <summary>
/// A tiny reflection evaluator over the interop assemblies, for driving and inspecting the game from tests
/// (UnityExplorer's C# console can't load some Unity 6 types). Paths look like
/// <c>PotatoPeeler.SaveManager.Instance.player.transform.position</c>, <c>#123.name</c> (an object id from
/// <c>list</c>), <c>PotatoPeeler.Workstation.slots[0]</c>. A leading component type with a non-static member
/// resolves to <c>FindObjectOfType</c>.
/// </summary>
internal static class Reflect
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Dictionary<int, UnityEngine.Object> Ids = new();
    static Dictionary<string, Type>? _types;

    /// <summary>
    /// Looks a type up by exact name, assembly by assembly. Never enumerates assemblies with GetTypes(): touching
    /// every interop type crashes this game natively (some class inits fault).
    /// </summary>
    public static Type? FindType(string name)
    {
        _types ??= new Dictionary<string, Type>(StringComparer.Ordinal);
        if (_types.TryGetValue(name, out var cached)) return cached;
        Type? found = null;
        var asms = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var candidate in new[] { name, "PotatoPeeler." + name, "UnityEngine." + name, "PotatoPeeler.Platform." + name, "UnityEngine.UI." + name })
        {
            var clr = candidate.Replace('/', '+');
            foreach (var asm in asms)
            {
                if (asm.IsDynamic) continue;
                try { found = asm.GetType(clr, false); } catch { found = null; }
                if (found != null) break;
            }
            if (found != null) break;
        }
        if (found != null) _types[name] = found;
        return found;
    }

    public static int Remember(UnityEngine.Object o)
    {
        var id = o.GetInstanceID();
        Ids[id] = o;
        return id;
    }

    public static UnityEngine.Object[] FindAll(Type t, bool includeInactive = false)
    {
        var arr = includeInactive
            ? Resources.FindObjectsOfTypeAll(Il2CppType.From(t))
            : UnityEngine.Object.FindObjectsOfType(Il2CppType.From(t));
        var list = new List<UnityEngine.Object>();
        foreach (var o in arr) list.Add((UnityEngine.Object)Cast(o, t)!);
        return list.ToArray();
    }

    public static object? Cast(object? o, Type t)
    {
        if (o is Il2CppObjectBase b && t.IsSubclassOf(typeof(Il2CppObjectBase)) && !t.IsInstanceOfType(o))
        {
            var m = typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.TryCast))!.MakeGenericMethod(t);
            return m.Invoke(b, null);
        }
        return o;
    }

    /// <summary>Evaluates a path; the last segment may be a method call when <paramref name="args"/> is given.</summary>
    public static object? Eval(string path, string[]? args = null, string? setValue = null)
    {
        var segs = Split(path);
        int i = 0;
        object? cur = null;
        Type? curType = null;

        if (segs[0].StartsWith("#"))
        {
            var id = int.Parse(segs[0][1..]);
            if (!Ids.TryGetValue(id, out var o)) throw new Exception($"unknown object id {id} (run list first)");
            cur = o; curType = o.GetType(); i = 1;
        }
        else
        {
            // Longest prefix that names a type.
            for (int n = segs.Count; n >= 1; n--)
            {
                var t = FindType(string.Join(".", segs.Take(n)));
                if (t == null) continue;
                curType = t; i = n;
                break;
            }
            if (curType == null) throw new Exception($"no type in '{path}'");
            if ((i == segs.Count || !IsStatic(curType, Bare(segs[i]))) && typeof(UnityEngine.Object).IsAssignableFrom(curType))
            {
                var all = FindAll(curType);
                if (all.Length == 0) throw new Exception($"no live {curType.Name}");
                cur = all[0];
            }
        }

        for (; i < segs.Count; i++)
        {
            bool last = i == segs.Count - 1;
            var (name, index) = ParseIndex(segs[i]);
            var t = cur?.GetType() ?? curType!;
            if (last && args != null)
                return Call(cur, t, name, args);
            if (last && setValue != null && index == null)
            {
                Set(cur, t, name, setValue);
                return Get(cur, t, name);
            }
            cur = Get(cur, t, name);
            if (index != null) cur = Index(cur, index.Value);
            curType = cur?.GetType();
            if (cur == null && !last) throw new Exception($"'{name}' is null");
        }
        return cur;
    }

    static bool IsStatic(Type t, string member)
    {
        var p = t.GetProperty(member, Any);
        if (p != null) return (p.GetGetMethod(true) ?? p.GetSetMethod(true))!.IsStatic;
        var f = t.GetField(member, Any);
        if (f != null) return f.IsStatic;
        var m = t.GetMethods(Any).FirstOrDefault(x => x.Name == member);
        return m?.IsStatic ?? false;
    }

    static string Bare(string seg) => ParseIndex(seg).name;

    static (string name, int? index) ParseIndex(string seg)
    {
        var b = seg.IndexOf('[');
        if (b < 0) return (seg, null);
        return (seg[..b], int.Parse(seg[(b + 1)..seg.IndexOf(']')]));
    }

    static List<string> Split(string path)
    {
        var res = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '[') depth++;
            else if (path[i] == ']') depth--;
            else if (path[i] == '.' && depth == 0) { res.Add(path[start..i]); start = i + 1; }
        }
        res.Add(path[start..]);
        return res;
    }

    static object? Get(object? obj, Type t, string name)
    {
        for (var tt = t; tt != null; tt = tt.BaseType)
        {
            var p = tt.GetProperty(name, Any | BindingFlags.DeclaredOnly);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(p.GetGetMethod(true)!.IsStatic ? null : obj);
            var f = tt.GetField(name, Any | BindingFlags.DeclaredOnly);
            if (f != null) return f.GetValue(f.IsStatic ? null : obj);
        }
        // Components by type name: obj.GetComponent<T>()
        if (obj is Component c || obj is GameObject)
        {
            var ct = FindType(name);
            if (ct != null && typeof(Component).IsAssignableFrom(ct))
            {
                var go = obj is Component cc ? cc.gameObject : (GameObject)obj;
                var comp = go.GetComponent(Il2CppType.From(ct));
                return comp == null ? null : Cast(comp, ct);
            }
        }
        var m0 = t.GetMethods(Any).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 0);
        if (m0 != null) return m0.Invoke(m0.IsStatic ? null : obj, null);
        throw new Exception($"{t.Name} has no member '{name}'");
    }

    static void Set(object? obj, Type t, string name, string value)
    {
        for (var tt = t; tt != null; tt = tt.BaseType)
        {
            var p = tt.GetProperty(name, Any | BindingFlags.DeclaredOnly);
            if (p != null && p.CanWrite) { p.SetValue(p.GetSetMethod(true)!.IsStatic ? null : obj, Convert(value, p.PropertyType)); return; }
            var f = tt.GetField(name, Any | BindingFlags.DeclaredOnly);
            if (f != null) { f.SetValue(f.IsStatic ? null : obj, Convert(value, f.FieldType)); return; }
        }
        throw new Exception($"{t.Name} has no settable member '{name}'");
    }

    static object? Index(object? o, int idx)
    {
        if (o == null) return null;
        if (o is Array a) return a.GetValue(idx);
        if (o is IList l) return l[idx];
        var item = o.GetType().GetProperty("Item", Any);
        if (item != null) return item.GetValue(o, new object[] { idx });
        var get = o.GetType().GetMethod("get_Item", Any);
        if (get != null) return get.Invoke(o, new object[] { idx });
        throw new Exception($"{o.GetType().Name} is not indexable");
    }

    static object? Call(object? obj, Type t, string name, string[] args)
    {
        var cands = t.GetMethods(Any).Where(m => m.Name == name && m.GetParameters().Length == args.Length && !m.ContainsGenericParameters).ToList();
        if (cands.Count == 0) throw new Exception($"{t.Name} has no method {name} with {args.Length} argument(s)");
        Exception? last = null;
        foreach (var m in cands)
        {
            try
            {
                var ps = m.GetParameters();
                var vals = new object?[ps.Length];
                for (int k = 0; k < ps.Length; k++) vals[k] = Convert(args[k], ps[k].ParameterType);
                var r = m.Invoke(m.IsStatic ? null : obj, vals);
                return m.ReturnType == typeof(void) ? "(void)" : r;
            }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
            catch (Exception e) { last = e; }
        }
        throw last ?? new Exception("no overload matched");
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static object? Convert(string s, Type t)
    {
        if (t.IsByRef) t = t.GetElementType()!;
        if (s == "null") return null;
        if (t == typeof(string)) return s;
        if (t == typeof(float)) return float.Parse(s, Inv);
        if (t == typeof(double)) return double.Parse(s, Inv);
        if (t == typeof(int)) return int.Parse(s, Inv);
        if (t == typeof(long)) return long.Parse(s, Inv);
        if (t == typeof(bool)) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (t.IsEnum) return Enum.Parse(t, s, true);
        if (s.StartsWith("#"))
            return Ids.TryGetValue(int.Parse(s[1..]), out var byId) ? Cast(byId, t) : throw new Exception($"unknown object id {s}");
        var f = s.Split(';').Select(x => float.Parse(x, Inv)).ToArray();
        if (t == typeof(Vector3)) return new Vector3(f[0], f[1], f[2]);
        if (t == typeof(Vector2)) return new Vector2(f[0], f[1]);
        if (t == typeof(Quaternion)) return f.Length == 4 ? new Quaternion(f[0], f[1], f[2], f[3]) : Quaternion.Euler(f[0], f[1], f[2]);
        if (s.StartsWith("#") && Ids.TryGetValue(int.Parse(s[1..]), out var o)) return Cast(o, t);
        throw new Exception($"can't convert '{s}' to {t.Name}");
    }

    /// <summary>A readable, JSON-safe description of a value.</summary>
    public static object? Describe(object? v, int depth = 0)
    {
        switch (v)
        {
            case null: return null;
            case string or bool or int or long or float or double or byte or short or uint or ulong: return v;
            case Enum e: return e.ToString();
            case Vector3 v3: return new[] { v3.x, v3.y, v3.z };
            case Vector2 v2: return new[] { v2.x, v2.y };
            case Quaternion q: return new { euler = new[] { q.eulerAngles.x, q.eulerAngles.y, q.eulerAngles.z } };
            case UnityEngine.Object uo:
                if (uo == null) return "(destroyed)";
                var id = Remember(uo);
                var go = uo as GameObject ?? (uo as Component)?.gameObject;
                return new { id = "#" + id, type = uo.GetIl2CppType().FullName, name = uo.name, active = go?.activeInHierarchy, pos = go != null ? Describe(go.transform.position) : null };
        }
        if (depth < 1 && v is Il2CppSystem.Object io)
        {
            var count = v.GetType().GetProperty("Count", Any)?.GetValue(v);
            if (count is int n)
            {
                var items = new List<object?>();
                for (int k = 0; k < Math.Min(n, 50); k++)
                {
                    try { items.Add(Describe(Index(v, k), depth + 1)); } catch { break; }
                }
                return new { count = n, items };
            }
            var len = v.GetType().GetProperty("Length", Any)?.GetValue(v);
            if (len is int ln)
            {
                var items = new List<object?>();
                for (int k = 0; k < Math.Min(ln, 50); k++) items.Add(Describe(Index(v, k), depth + 1));
                return new { length = ln, items };
            }
        }
        return v.ToString();
    }

    /// <summary>Properties of a type (interop exposes fields as properties), with values for an instance.</summary>
    public static object Members(object? obj, Type t, string? filter)
    {
        var res = new List<object>();
        foreach (var p in t.GetProperties(Any).Where(p => p.DeclaringType == t && p.GetIndexParameters().Length == 0))
        {
            if (p.Name.StartsWith("NativeFieldInfoPtr") || p.Name.StartsWith("NativeMethodInfoPtr")) continue;
            if (filter != null && p.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            object? val;
            try { val = Describe(p.GetValue(p.GetGetMethod(true)!.IsStatic ? null : obj)); } catch (Exception e) { val = "ERR " + (e.InnerException ?? e).GetType().Name; }
            res.Add(new { p.Name, type = p.PropertyType.Name, value = val });
        }
        var methods = t.GetMethods(Any).Where(m => m.DeclaringType == t && !m.IsSpecialName && !m.Name.StartsWith("Native"))
            .Where(m => filter == null || m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name))})")
            .Distinct().ToArray();
        return new { type = t.FullName, members = res, methods };
    }
}
