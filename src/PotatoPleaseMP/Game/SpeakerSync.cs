using HarmonyLib;
using PotatoPleaseMP.Core.Session;
using PotatoPleaseMP.Core.Wire;
using PotatoPleaseMP.Net;
using UnityEngine;

namespace PotatoPleaseMP.Game;

/// <summary>
/// The facility loudspeaker (radio broadcasts, waste/dip enforcer warnings) is one shared PA: a line any peer's game starts
/// is played on everyone's speaker. Lines go as cue keys + voice clip name (SpeakerLine 0x7D, any -> host -> others).
/// Tutorial lines stay personal. The same line arriving twice within a few seconds (both peers' games started it, or a
/// replayed story beat) plays once.
/// </summary>
internal static class SpeakerSync
{
    static bool _applying;
    static readonly Dictionary<string, float> Recent = new();
    static Dictionary<string, AudioClip>? _clips;
    public static long Out, In, Dupes, NotList;
    public static string Last = "";

    static string KeyOf(PotatoPeeler.SubtitleCue c)
    {
        try { var e = c.text.TableEntryReference; return string.IsNullOrEmpty(e.Key) ? e.KeyId.ToString() : e.Key; } catch { return ""; }
    }

    static string TableOf(UnityEngine.Localization.LocalizedString s)
    {
        try { return s.TableReference.TableCollectionName ?? ""; } catch { return ""; }
    }

    static string SpeakerKeyOf(PotatoPeeler.SubtitleCue c)
    {
        try { var e = c.speaker.TableEntryReference; return string.IsNullOrEmpty(e.Key) ? (e.KeyId != 0 ? e.KeyId.ToString() : "") : e.Key; } catch { return ""; }
    }

    /// <summary>True when this line was already played moments ago (then the caller drops it).</summary>
    static bool SeenRecently(string sig)
    {
        var now = Time.realtimeSinceStartup;
        if (Recent.TryGetValue(sig, out var at) && now - at < 6f) return true;
        Recent[sig] = now;
        if (Recent.Count > 64) foreach (var k in Recent.Where(kv => now - kv.Value > 30f).Select(kv => kv.Key).ToList()) Recent.Remove(k);
        return false;
    }

    [HarmonyPatch(typeof(PotatoPeeler.Loudspeaker), nameof(PotatoPeeler.Loudspeaker.Enqueue))]
    static class EnqueuePatch
    {
        static bool Prefix(Il2CppSystem.Collections.Generic.IEnumerable<PotatoPeeler.SubtitleCue> cues, AudioClip voice,
            PotatoPeeler.LoudspeakerPriority priority, float volume, ref float __result)
        {
            if (!MpRuntime.Active || priority == PotatoPeeler.LoudspeakerPriority.Tutorial) return true;
            try
            {
                var list = new List<PotatoPeeler.SubtitleCue>();
                // Read by index (never enumerate: the game may hand us a one-shot sequence it enumerates itself).
                var asList = cues.TryCast<Il2CppSystem.Collections.Generic.List<PotatoPeeler.SubtitleCue>>();
                if (asList != null) { for (int i = 0; i < asList.Count; i++) if (asList[i] != null) list.Add(asList[i]); }
                else
                {
                    var arr = cues.TryCast<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<PotatoPeeler.SubtitleCue>>();
                    if (arr == null) { NotList++; return true; }
                    for (int i = 0; i < arr.Length; i++) if (arr[i] != null) list.Add(arr[i]);
                }
                var sig = (voice != null ? voice.name : "") + "|" + string.Join(",", list.Select(KeyOf));
                if (SeenRecently(sig) && !_applying) { Dupes++; __result = 0f; return false; }
                if (_applying) return true;
                var w = new NetWriter();
                w.U8((byte)priority); w.F32(volume); w.Str8(voice != null ? voice.name : "");
                w.U8((byte)Math.Min(list.Count, 32));
                foreach (var c in list.Take(32)) { w.Str8(TableOf(c.text)); w.Str8(KeyOf(c)); w.Str8(TableOf(c.speaker)); w.Str8(SpeakerKeyOf(c)); w.F32(c.duration); }
                if (MpRuntime.Host is { } h) h.Broadcast(MsgId.SpeakerLine, w.Span);
                else if (MpRuntime.Client is { State: ClientState.Playing } cl) cl.Send(MsgId.SpeakerLine, w.Span);
                Out++;
                Last = "out " + sig;
            }
            catch (Exception e) { Plugin.L.LogWarning($"[speaker] out: {e.Message}"); }
            return true;
        }
    }

    static AudioClip? FindClip(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_clips == null || !_clips.TryGetValue(name, out var c) || c == null)
        {
            _clips = new Dictionary<string, AudioClip>();
            foreach (var a in Resources.FindObjectsOfTypeAll<AudioClip>()) if (a != null && !_clips.ContainsKey(a.name)) _clips[a.name] = a;
            _clips.TryGetValue(name, out c);
        }
        return c;
    }

    public static bool Handle(MsgId id, ReadOnlySpan<byte> payload, PlayerInfo? from)
    {
        if (id != MsgId.SpeakerLine) return false;
        try
        {
            if (from != null && MpRuntime.Host is { } h) h.Broadcast(MsgId.SpeakerLine, payload, exceptSlot: from.Slot);
            var r = new NetReader(payload);
            var prio = (PotatoPeeler.LoudspeakerPriority)r.U8(); float vol = r.F32(); var clipName = r.Str8();
            int n = r.U8();
            var cues = new Il2CppSystem.Collections.Generic.List<PotatoPeeler.SubtitleCue>();
            var keys = new List<string>();
            for (int i = 0; i < n; i++)
            {
                var tt = r.Str8(); var tk = r.Str8(); var st = r.Str8(); var sk = r.Str8(); float d = r.F32();
                var cue = new PotatoPeeler.SubtitleCue(tk, sk, d);
                try { if (!string.IsNullOrEmpty(tt)) cue.text.TableReference = tt; } catch { }
                try { if (!string.IsNullOrEmpty(st)) cue.speaker.TableReference = st; } catch { }
                cues.Add(cue); keys.Add(tk);
            }
            In++;
            var sig = clipName + "|" + string.Join(",", keys);
            if (SeenRecently(sig)) { Dupes++; return true; }
            var sp = PotatoPeeler.Loudspeaker.Instance;
            if (sp == null) return true;
            _applying = true;
            try { sp.Enqueue(sp, cues.Cast<Il2CppSystem.Collections.Generic.IEnumerable<PotatoPeeler.SubtitleCue>>(), FindClip(clipName), prio, vol); }
            finally { _applying = false; }
            Last = "in " + sig;
        }
        catch (WireFormatException) { }
        catch (Exception e) { Plugin.L.LogWarning($"[speaker] in: {e.Message}"); }
        return true;
    }

    public static object Describe() => new { Out, In, Dupes, NotList, Last, busy = PotatoPeeler.Loudspeaker.IsVoiceActive };
}
