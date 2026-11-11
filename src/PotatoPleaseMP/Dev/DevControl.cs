using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace PotatoPleaseMP.Dev;

/// <summary>
/// Test automation: a small HTTP API on 127.0.0.1 (only when [Dev] ControlPort is set) so scripts and Claude can
/// drive the game without focus, mouse or keyboard. Requests are read on a background thread and run on the main
/// thread. GET http://127.0.0.1:port/&lt;command&gt;?args. Commands: see <see cref="Handle"/>.
/// </summary>
internal static class DevControl
{
    sealed class Pending
    {
        public TcpClient Client = null!;
        public string Path = "";
        public Dictionary<string, string> Query = new();
    }

    sealed class Job
    {
        public Pending Reply = null!;
        public Func<object?> Step = null!;
    }

    static TcpListener? _listener;
    static readonly ConcurrentQueue<Pending> Queue = new();
    static readonly List<Job> Jobs = new();
    public static int Port { get; private set; }

    public static string ShotsDir => System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "screenshots");

    public static void Start(int port)
    {
        if (port <= 0) return;
        for (int p = port; p < port + 4; p++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, p);
                l.Start();
                _listener = l;
                Port = p;
                break;
            }
            catch (SocketException) { }
        }
        if (_listener == null) { Plugin.L.LogWarning($"Dev control: no free port from {port}."); return; }
        Plugin.L.LogWarning($"Dev control listening on http://127.0.0.1:{Port}/ (test setups only).");
        new Thread(Accept) { IsBackground = true, Name = "PPMP dev control" }.Start();
    }

    static void Accept()
    {
        while (_listener != null)
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                client.ReceiveTimeout = 5000;
                var reader = new StreamReader(client.GetStream(), Encoding.ASCII, false, 1024, leaveOpen: true);
                var line = reader.ReadLine() ?? "";
                // Browsers can reach 127.0.0.1 too: refuse anything a web page could send (Origin/Referer, or a
                // Host other than loopback, which also stops DNS rebinding). curl sends neither header.
                bool foreign = false;
                string? h;
                while (!string.IsNullOrEmpty(h = reader.ReadLine()))
                {
                    var colon = h.IndexOf(':');
                    if (colon < 0) continue;
                    var name = h[..colon].Trim().ToLowerInvariant();
                    var value = h[(colon + 1)..].Trim().ToLowerInvariant();
                    if (name is "origin" or "referer" or "sec-fetch-site" or "sec-fetch-mode") foreign = true;
                    if (name == "host" && !(value.StartsWith("127.0.0.1") || value.StartsWith("localhost"))) foreign = true;
                }
                if (foreign)
                {
                    var deny = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    client.GetStream().Write(deny, 0, deny.Length);
                    client.Close();
                    continue;
                }
                var target = line.Split(' ').Skip(1).FirstOrDefault() ?? "/";
                var qi = target.IndexOf('?');
                var path = (qi >= 0 ? target[..qi] : target).Trim('/').ToLowerInvariant();
                var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (qi >= 0)
                    foreach (var pair in target[(qi + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = pair.IndexOf('=');
                        var k = Uri.UnescapeDataString(eq >= 0 ? pair[..eq] : pair);
                        q[k] = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
                    }
                Queue.Enqueue(new Pending { Client = client, Path = path, Query = q });
            }
            catch (Exception e) { Plugin.L.LogDebug($"Dev control: {e.Message}"); }
        }
    }

    public static void Pump()
    {
        if (Port == 0) return;
        foreach (var job in Jobs.ToList())
        {
            object? r;
            try { r = job.Step(); }
            catch (Exception e) { r = new { error = Err(e) }; }
            if (r == null) continue;
            Jobs.Remove(job);
            Reply(job.Reply, r);
        }
        while (Queue.TryDequeue(out var p))
        {
            object? result;
            try { result = Handle(p.Path, p.Query); }
            catch (Exception e) { result = new { error = Err(e) }; }
            if (result is Func<object?> step) { Jobs.Add(new Job { Reply = p, Step = step }); continue; }
            Reply(p, result);
        }
    }

    static string Err(Exception e)
    {
        while (e is System.Reflection.TargetInvocationException { InnerException: not null } t) e = t.InnerException!;
        return e.GetType().Name + ": " + e.Message;
    }

    static void Reply(Pending p, object? result)
    {
        try
        {
            string json;
            try { json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }); }
            catch (Exception e) { json = JsonSerializer.Serialize(new { error = "serialize: " + e.Message, text = result?.ToString() }); }
            var body = Encoding.UTF8.GetBytes(json);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            var s = p.Client.GetStream();
            s.Write(head, 0, head.Length);
            s.Write(body, 0, body.Length);
            p.Client.Close();
        }
        catch (Exception e) { Plugin.L.LogDebug($"Dev control reply: {e.Message}"); }
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string A(Dictionary<string, string> q, string k, string d = "") => q.TryGetValue(k, out var v) ? v : d;
    static float F(Dictionary<string, string> q, string k, float d = 0) => q.TryGetValue(k, out var v) && v != "" ? float.Parse(v, Inv) : d;

    static object? Handle(string path, Dictionary<string, string> q)
    {
        switch (path)
        {
            case "":
            case "status": return Status();
            case "eval":
            {
                // eval?p=Type.member.member  [&set=value]  [&args=a|b|c  to call the last segment]
                string[]? args = q.ContainsKey("args") ? (A(q, "args") == "" ? Array.Empty<string>() : A(q, "args").Split('|')) : null;
                var v = Reflect.Eval(A(q, "p"), args, q.ContainsKey("set") ? A(q, "set") : null);
                return new { value = Reflect.Describe(v) };
            }
            case "members":
            {
                // members?p=path (an object) or ?type=Name, &filter=
                var filter = q.ContainsKey("filter") ? A(q, "filter") : null;
                if (q.ContainsKey("type"))
                {
                    var t = Reflect.FindType(A(q, "type")) ?? throw new Exception("no such type");
                    object? inst = typeof(UnityEngine.Object).IsAssignableFrom(t) ? Reflect.FindAll(t).FirstOrDefault() : null;
                    return Reflect.Members(inst, t, filter);
                }
                var o = Reflect.Eval(A(q, "p")) ?? throw new Exception("null");
                return Reflect.Members(o, o.GetType(), filter);
            }
            case "list":
            {
                // list?type=PotatoPeeler.PatatoSack [&inactive=1]
                var t = Reflect.FindType(A(q, "type")) ?? throw new Exception("no such type");
                var all = Reflect.FindAll(t, A(q, "inactive") == "1");
                return new { count = all.Length, items = all.Take(200).Select(o => Reflect.Describe(o)).ToArray() };
            }
            case "buttons": return Buttons();
            case "tree":
            {
                // tree?p=path-to-object&depth=3 : child hierarchy with components, active flags and texts
                var o = Reflect.Eval(A(q, "p"));
                var tr = (o as Component)?.transform ?? (o as GameObject)?.transform ?? throw new Exception("not a GameObject/Component");
                var lines = new List<string>();
                void Walk(Transform t, int d)
                {
                    var comps = string.Join(",", t.GetComponents<Component>().Select(c => c.GetIl2CppType().Name).Where(n => n != "Transform" && n != "RectTransform" && n != "CanvasRenderer"));
                    var txt = t.GetComponent<TMPro.TMP_Text>()?.text ?? t.GetComponent<UnityEngine.UI.Text>()?.text;
                    var rt = t.TryCast<RectTransform>();
                    var size = rt != null ? $" {rt.rect.width:0}x{rt.rect.height:0}" : "";
                    lines.Add($"{new string(' ', d * 2)}{t.name}{(t.gameObject.activeSelf ? "" : " (off)")}{size} [{comps}]{(txt != null ? " \"" + txt.Replace("\n", " ") + "\"" : "")}");
                    if (d < (int)F(q, "depth", 3))
                        for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), d + 1);
                }
                Walk(tr, 0);
                return lines;
            }
            case "click": return Click(A(q, "name"));
            case "press":
                // press?a=Interact&s=0.1 (an InputAction name)
                VirtualInput.Press(A(q, "a"), F(q, "s", 0.1f));
                return new { pressed = A(q, "a"), seconds = F(q, "s", 0.1f) };
            case "steam": return Net.Steam.SteamBoot.Describe();
            case "peelcap":
                // peelcap?mirror=<path or #id of a VoxelPotato> replays every captured cut on it too; mirror= (empty) clears
                if (q.ContainsKey("mirror"))
                    Game.PeelCapture.Mirror = A(q, "mirror") == "" ? null
                        : (PotatoPeeler.VoxelPotato?)Reflect.Cast(Reflect.Eval(A(q, "mirror")), typeof(PotatoPeeler.VoxelPotato));
                return new { Game.PeelCapture.Status, Game.PeelCapture.Ops, Game.PeelCapture.Last, mirror = Reflect.Describe(Game.PeelCapture.Mirror) };
            case "host": return Net.MpRuntime.StartHost(q.ContainsKey("name") ? A(q, "name") : null);
            case "join": return Net.MpRuntime.Join(A(q, "to"));
            case "leave": return Net.MpRuntime.Stop();
            case "endhost": return Net.MpRuntime.Stop();
            case "session": return Net.MpRuntime.Describe();
            case "bodies": return Game.BodySync.Describe();
            case "peelsync": return Game.PeelSync.Describe();
            case "sacks": return Game.SackSync.Describe();
            case "stations": return q.ContainsKey("handin") ? Game.StationSync.DevHandIn(long.Parse(A(q, "handin"))) : Game.StationSync.Describe();
            case "econ": return Game.EconSync.Describe();
            case "story": return Game.StorySync.Describe();
            case "firebeat":
            {
                // firebeat?kind=SmokeBreak : fires the first timeline entry of that kind (host test)
                var tl = PotatoPeeler.ProgressionTimeline.Instance;
                var beats = UnityEngine.Object.FindObjectOfType<PotatoPeeler.StoryBeats>();
                var want = A(q, "kind", "SmokeBreak");
                for (int i = 0; tl != null && beats != null && i < tl.entries.Length; i++)
                    if (tl.entries[i] != null && tl.entries[i].kind.ToString() == want) { beats.Fire(tl.entries[i]); return new { fired = i, id = tl.entries[i].id }; }
                return new { error = "no such entry" };
            }
            case "clean": return Game.CleanSync.Describe();
            case "storm": return Game.StormSync.Describe();
            case "pile": return Game.PileSync.Describe();
            case "switches":
            {
                // switches?toggle=<saveKey or label part> : flip that power switch as if the local player used it
                if (q.ContainsKey("toggle"))
                {
                    var want = A(q, "toggle");
                    foreach (var s in UnityEngine.Object.FindObjectsOfType<PotatoPeeler.PowerSwitch>())
                    {
                        string label; try { label = s.Label ?? ""; } catch { label = s.gameObject.name; }
                        if (s.saveKey == want || label.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0 || s.gameObject.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var it = s.GetComponentInChildren<PotatoPeeler.PowerSwitchInteractable>(true) ?? s.GetComponentInParent<PotatoPeeler.PowerSwitchInteractable>();
                            if (it != null) it.OnInteract(UnityEngine.Object.FindObjectOfType<PotatoPeeler.Interactor>()); else s.Toggle();
                            return new { flipped = s.saveKey, via = it != null ? "interactable" : "toggle", on = s.IsOn };
                        }
                    }
                    return new { error = "no such switch" };
                }
                return Game.DoorSync.DescribeSwitches();
            }
            case "barrows": return Game.BarrowSync.Describe();
            case "shavings": return Game.ShavingSync.Describe();
            case "endchoice":
            {
                // endchoice?pick=continue|menu : press that button of the post-credits choice
                var ui = UnityEngine.Object.FindObjectOfType<PotatoPeeler.EndingSequenceUI>(true);
                if (q.ContainsKey("pick") && ui == null) ui = new GameObject("MP_TestEndingUI").AddComponent<PotatoPeeler.EndingSequenceUI>(); // test stand-in
                if (q.ContainsKey("pick") && ui != null) { if (A(q, "pick") == "continue") ui.ContinuePlaying(); else ui.ReturnToMenu(); }
                return new { ui = ui != null, slot = PotatoPeeler.EndingSequenceUI.ContinueSlot(), info = Game.EndingChoiceSync.Describe() };
            }
            case "pilerefill":
            {
                // pilerefill?count=N : the host refills the pile like the State telephone does
                if (q.ContainsKey("count")) { var act = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PotatoPileActivator>(); return new { got = act?.RefillPile(int.Parse(A(q, "count"))), info = Game.PileRefillSync.Describe() }; }
                if (q.ContainsKey("phone")) { var ph = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PileRefillPhone>(); var it = UnityEngine.Object.FindObjectOfType<PotatoPeeler.Interactor>(); ph?.OnInteract(it); return new { phone = ph != null }; }
                return Game.PileRefillSync.Describe();
            }
            case "claw":
            {
                // claw?sit=1 : sit in the claw crane seat (as if pressing it); claw?leave=1 ; claw?aim=x,y,z&grip=-1|0|1 (driver test)
                var claw = UnityEngine.Object.FindObjectOfType<PotatoPeeler.ClawCrane>();
                if (claw == null) return new { error = "no claw crane" };
                if (q.ContainsKey("sit"))
                {
                    var seat = UnityEngine.Object.FindObjectOfType<PotatoPeeler.ClawCraneSeat>();
                    var inter = UnityEngine.Object.FindObjectOfType<PotatoPeeler.Interactor>();
                    if (seat == null || inter == null) return new { error = "no seat/interactor" };
                    seat.OnInteract(inter);
                }
                if (q.ContainsKey("leave")) claw.RequestDismount();
                if (q.ContainsKey("aim")) { var a = A(q, "aim").Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); claw._target = new Vector3(a[0], a[1], a[2]); }
                if (q.ContainsKey("grip")) { Game.ClawSync.DevGrip = A(q, "grip") == "none" ? null : int.Parse(A(q, "grip")); if (Game.ClawSync.DevGrip is { } g) claw.SetGripDir(g); }
                return Game.ClawSync.Describe();
            }
            case "speaker":
            {
                // speaker?say=1 : the waste enforcer speaks its fine line (PA relay test)
                if (q.ContainsKey("say"))
                {
                    var we = UnityEngine.Object.FindObjectOfType<PotatoPeeler.WasteEnforcer>();
                    if (we == null || we.fineLine == null) return new { error = "no waste enforcer" };
                    return new { said = we.Speak(we.fineLine) };
                }
                return Game.SpeakerSync.Describe();
            }
            case "endgame": return q.ContainsKey("send") ? Game.EndSync.DevSendEnding(A(q, "send", "Varenka"), A(q, "pavel", "State")) : Game.EndSync.Describe();
            case "actors": return Game.StoryActors.Describe();
            case "machines": return Game.MachineSync.Describe();
            case "achievements": return new { guard = AchievementGuard.Describe(), account = AchievementGuard.Unlocked() };
            case "overlap":
            {
                // overlap?x=&y=&z=&r=0.3 : colliders around a point (incl. triggers), with their hierarchy path
                var hits = Physics.OverlapSphere(new Vector3(F(q, "x"), F(q, "y"), F(q, "z")), F(q, "r", 0.3f), ~0, QueryTriggerInteraction.Collide);
                var list = new List<object>();
                foreach (var c in hits)
                {
                    var hp = c.name; var t = c.transform.parent;
                    while (t != null) { hp = t.name + "/" + hp; t = t.parent; }
                    list.Add(new { id = "#" + Reflect.Remember(c), type = c.GetIl2CppType().Name, path = hp, trigger = c.isTrigger, layer = c.gameObject.layer, bounds = c.bounds.ToString() });
                }
                return list;
            }
            case "spawnsack":
            {
                // spawnsack?x=&y=&z=&n=12 : a fresh sack from the (inactive) sack template, as if the save had it there
                PotatoPeeler.PatatoSackInteractable? tpl = null;
                foreach (var o in Reflect.FindAll(typeof(PotatoPeeler.PatatoSackInteractable), true))
                    if (o is PotatoPeeler.PatatoSackInteractable si) { tpl = si; if (!si.gameObject.activeInHierarchy) break; }
                if (tpl == null) throw new Exception("no sack template");
                var go = UnityEngine.Object.Instantiate(tpl.gameObject, new Vector3(F(q, "x"), F(q, "y"), F(q, "z")), Quaternion.identity);
                go.SetActive(true);
                var sack = go.GetComponent<PotatoPeeler.PatatoSackInteractable>();
                sack.Initialize((int)F(q, "n", 12), UnityEngine.Object.FindObjectOfType<PotatoPeeler.PotatoPileActivator>());
                return new { sack = "#" + Reflect.Remember(sack), pos = Reflect.Describe(go.transform.position) };
            }
            case "bot":
                // bot?add=Name[&walk=1] | bot?do=leave|remove|walk|stop&who=Name
                return q.ContainsKey("add") ? Net.MpRuntime.AddBot(A(q, "add"), A(q, "walk") == "1") : Net.MpRuntime.BotCommand(A(q, "do"), A(q, "who"));
            case "record":
                // record?name=&s=10&fps=20&w=1280 ; record (no args) = status
                return q.ContainsKey("s") ? FrameRecorder.Start(A(q, "name", "clip"), F(q, "s", 10), (int)F(q, "fps", 20), (int)F(q, "w", 1280)) : FrameRecorder.Status();
            case "avatar":
            {
                // avatar?slot=1&name=Bot&x=&y=&z=&yaw=&walk=1&r=1.5 (defaults: 2.5 m in front of the player);  avatar?clear=1
                if (q.ContainsKey("clear")) return Presence.DemoAvatars.Clear();
                var cam = Camera.main!.transform;
                var fwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
                var me = PlayerObj()!.transform.position;
                var at = q.ContainsKey("x") ? new Vector3(F(q, "x"), F(q, "y"), F(q, "z")) : me + fwd * F(q, "dist", 2.5f);
                var yaw = q.ContainsKey("yaw") ? F(q, "yaw") : Mathf.Atan2(-fwd.x, -fwd.z) * Mathf.Rad2Deg;
                return Presence.DemoAvatars.Spawn((int)F(q, "slot", 1), A(q, "name", "Comrade"), at, yaw, A(q, "walk") == "1", F(q, "r", 1.5f));
            }
            case "release": VirtualInput.Release(A(q, "a")); return new { released = A(q, "a") };
            case "pad":
            {
                // pad?lx=&ly=&rx=&ry=&s=  sticks;  pad?lt=&rt=&s=  triggers;  pad?btn=South,RightShoulder&s=  buttons
                var s = F(q, "s", 0.15f);
                if (q.ContainsKey("lx") || q.ContainsKey("ly") || q.ContainsKey("rx") || q.ContainsKey("ry"))
                    VirtualPad.SetSticks(new Vector2(F(q, "lx"), F(q, "ly")), new Vector2(F(q, "rx"), F(q, "ry")), s);
                if (q.ContainsKey("lt") || q.ContainsKey("rt")) VirtualPad.SetTriggers(F(q, "lt"), F(q, "rt"), s);
                if (q.ContainsKey("btn"))
                    foreach (var b in A(q, "btn").Split(',', StringSplitOptions.RemoveEmptyEntries))
                        VirtualPad.Press(Enum.Parse<UnityEngine.InputSystem.LowLevel.GamepadButton>(b.Trim(), true), s);
                return VirtualPad.Describe();
            }
            case "bindings":
            {
                // bindings?filter=  lists the player's actions and their binding paths
                var reader = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PlayerInputReader>();
                var map = reader._map;
                var res = new List<object>();
                var f = A(q, "filter");
                for (int i = 0; i < map.actions.Count; i++)
                {
                    var act = map.actions[i];
                    if (f != "" && act.name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var paths = new List<string>();
                    for (int k = 0; k < act.bindings.Count; k++) paths.Add(act.bindings[k].effectivePath);
                    res.Add(new { act.name, paths });
                }
                return res;
            }
            case "move":
            {
                // move?x=0&y=1&s=1 walks with the stick vector for s seconds
                var until = Time.realtimeSinceStartup + F(q, "s", 1);
                VirtualInput.Move = new Vector2(F(q, "x"), F(q, "y"));
                return (Func<object?>)(() =>
                {
                    if (Time.realtimeSinceStartup < until) return null;
                    VirtualInput.Move = null;
                    return new { moved = true, pos = Reflect.Describe(PlayerObj()?.transform.position) };
                });
            }
            case "look":
                VirtualInput.LookDelta += new Vector2(F(q, "dx"), F(q, "dy"));
                return new { look = "queued" };
            case "pointer":
                // pointer?dx=&dy= one-shot, or &hold=1 to keep adding every frame (&hold=0 stops)
                if (q.ContainsKey("hold")) VirtualInput.PointerDeltaHold = A(q, "hold") == "0" ? null : new Vector2(F(q, "dx"), F(q, "dy"));
                else VirtualInput.PointerDelta += new Vector2(F(q, "dx"), F(q, "dy"));
                return new { pointer = "ok" };
            case "warp":
            {
                var mv = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PlayerMove>();
                mv.Warp(new Vector3(F(q, "x"), F(q, "y"), F(q, "z")));
                return new { pos = Reflect.Describe(PlayerObj()?.transform.position) };
            }
            case "aim": return Aim(new Vector3(F(q, "x"), F(q, "y"), F(q, "z")));
            case "aimat":
            {
                // aimat?p=path-to-object [&dist=1.5] walks next to it and looks at its collider centre
                var o = Reflect.Eval(A(q, "p"));
                var go = (o as Component)?.gameObject ?? o as GameObject ?? throw new Exception("not a GameObject/Component");
                var col = go.GetComponentInChildren<Collider>();
                var target = col != null ? col.bounds.center : go.transform.position;
                var dist = F(q, "dist", 1.4f);
                var me = PlayerObj()!.transform.position;
                var flat = new Vector3(me.x - target.x, 0, me.z - target.z);
                if (flat.sqrMagnitude < 0.01f) flat = Vector3.forward;
                var stand = target + flat.normalized * dist;
                stand.y = me.y;
                if (A(q, "walk") != "0") UnityEngine.Object.FindObjectOfType<PotatoPeeler.PlayerMove>().Warp(stand);
                int frames = 0;
                return (Func<object?>)(() =>
                {
                    if (frames++ < 2) return null; // let the camera follow the warp
                    return Aim(target);
                });
            }
            case "focus": return Focus();
            case "interact":
            {
                // interact?i=0 runs the focused interactable's option i (as if its key were pressed)
                var it = UnityEngine.Object.FindObjectOfType<PotatoPeeler.Interactor>();
                var opts = it._options;
                int i = (int)F(q, "i");
                if (opts == null || i >= opts.Count) return new { error = "no such option", focus = Focus() };
                var opt = opts[i];
                opt.Execute.Invoke(it);
                return new { executed = opt.Label };
            }
            case "shot":
            case "screenshot":
            {
                Directory.CreateDirectory(ShotsDir);
                var name = new string(A(q, "name", "shot").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
                var file = System.IO.Path.Combine(ShotsDir, $"{DateTime.Now:HHmmss}_{name}.png");
                ScreenCapture.CaptureScreenshot(file);
                var until = Time.realtimeSinceStartup + 10;
                return (Func<object?>)(() =>
                {
                    if (File.Exists(file) && new FileInfo(file).Length > 0) return new { file };
                    return Time.realtimeSinceStartup > until ? new { error = "screenshot not written", file } : null;
                });
            }
            case "wait":
            {
                var until = Time.realtimeSinceStartup + F(q, "s", 1);
                return (Func<object?>)(() => Time.realtimeSinceStartup < until ? null : new { waited = F(q, "s", 1) });
            }
            case "log":
                Plugin.L.LogMessage("[dev] " + A(q, "m"));
                return new { logged = true };
            case "quit":
                Application.Quit();
                return new { quitting = true };
            default:
                return new { error = $"unknown command '{path}'", commands = "status eval members list types buttons click press release move look pointer warp aim aimat focus interact shot wait log quit" };
        }
    }

    static GameObject? PlayerObj() => UnityEngine.Object.FindObjectOfType<PotatoPeeler.Player>()?.gameObject;

    static object Status()
    {
        var p = UnityEngine.Object.FindObjectOfType<PotatoPeeler.Player>();
        var look = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PlayerLook>();
        return new
        {
            scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            frame = Time.frameCount,
            timeScale = Time.timeScale,
            focused = Application.isFocused,
            player = p == null ? null : new { pos = Reflect.Describe(p.transform.position), cam = Reflect.Describe(Camera.main?.transform.position), fwd = Reflect.Describe(Camera.main?.transform.forward) },
            focus = p == null ? null : Focus(),
            virtualInput = VirtualInput.Active,
        };
    }

    static object Focus()
    {
        var it = UnityEngine.Object.FindObjectOfType<PotatoPeeler.Interactor>();
        if (it == null) return new { error = "no Interactor" };
        var opts = new List<object>();
        var list = it._options;
        if (list != null)
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                opts.Add(new { i, o.Label, o.Enabled, o.HoldDuration, action = o.Action?.action?.name });
            }
        return new { name = it._focusedName, options = opts };
    }

    static object Aim(Vector3 target)
    {
        var look = UnityEngine.Object.FindObjectOfType<PotatoPeeler.PlayerLook>();
        var cam = Camera.main!.transform.position;
        var d = target - cam;
        var yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        var pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
        look.SetRotation(yaw, pitch);
        var it = UnityEngine.Object.FindObjectOfType<PotatoPeeler.Interactor>();
        string? hit = null;
        if (Physics.Raycast(cam, d.normalized, out var h, 6f, it.layerMask, it.triggerInteraction)) hit = h.collider.name + " @" + h.distance.ToString("0.00", Inv);
        return new { yaw, pitch, hit, focus = Focus() };
    }

    static object Buttons()
    {
        var res = new List<object>();
        foreach (var b in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Selectable>())
        {
            if (!b.isActiveAndEnabled) continue;
            var label = b.GetComponentInChildren<TMPro.TMP_Text>()?.text ?? b.GetComponentInChildren<UnityEngine.UI.Text>()?.text;
            res.Add(new { type = b.GetIl2CppType().Name, path = PathOf(b.transform), label, b.interactable });
        }
        return res;
    }

    static object Click(string name)
    {
        var matches = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>()
            .Where(b => b.isActiveAndEnabled && b.interactable)
            .Where(b => b.name.Equals(name, StringComparison.OrdinalIgnoreCase) || PathOf(b.transform).EndsWith(name, StringComparison.OrdinalIgnoreCase)
                        || (b.GetComponentInChildren<TMPro.TMP_Text>()?.text ?? b.GetComponentInChildren<UnityEngine.UI.Text>()?.text ?? "").Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1) return new { error = matches.Count == 0 ? "no match" : "ambiguous", candidates = matches.Select(m => PathOf(m.transform)).ToArray() };
        matches[0].onClick.Invoke();
        return new { clicked = PathOf(matches[0].transform) };
    }

    static string PathOf(Transform t)
    {
        var s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }
}
