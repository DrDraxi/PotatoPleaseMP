using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using UnityEngine;

namespace PotatoPleaseMP.Dev;

/// <summary>
/// Dev-only video capture from inside the game (PrintWindow manages ~4 fps at 1440p). A coroutine waits for the end of each
/// frame (screen capture is only valid then), grabs the back buffer into a RenderTexture, downsamples on the GPU, reads back
/// a small frame and writes numbered JPEGs that scripts/record.sh turns into an MP4.
/// </summary>
internal static class FrameRecorder
{
    static string _dir = "";
    static int _frame, _total;
    static bool _running;
    public static MonoBehaviour? Host;

    public static object Start(string name, float seconds, int fps, int width)
    {
        if (_running) return new { error = "already recording", dir = _dir };
        if (Host == null) return new { error = "no host behaviour" };
        fps = Math.Clamp(fps, 1, 60);
        width = Math.Clamp(width, 320, Screen.width);
        int height = Mathf.RoundToInt(Screen.height * width / (float)Screen.width / 2f) * 2;
        _dir = Path.Combine(DevControl.ShotsDir, "rec_" + DateTime.Now.ToString("HHmmss") + "_" + name);
        Directory.CreateDirectory(_dir);
        _frame = 0;
        _total = Mathf.CeilToInt(seconds * fps);
        _running = true;
        Host.StartCoroutine(Routine(fps, width, height).WrapToIl2Cpp());
        return new { recording = _dir, frames = _total, fps, width, height };
    }

    static IEnumerator Routine(int fps, int width, int height)
    {
        var full = RenderTexture.GetTemporary(Screen.width, Screen.height, 0);
        var small = RenderTexture.GetTemporary(width, height, 0);
        var read = new Texture2D(width, height, TextureFormat.RGBA32, false);
        float interval = 1f / fps, next = Time.realtimeSinceStartup;
        while (_frame < _total)
        {
            yield return new WaitForEndOfFrame();
            if (Time.realtimeSinceStartup < next) continue;
            next = Math.Max(next + interval, Time.realtimeSinceStartup);
            try
            {
                ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
                Graphics.Blit(full, small); // may be upside down on D3D: record.sh flips with ffmpeg
                var prev = RenderTexture.active;
                RenderTexture.active = small;
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                read.Apply(false);
                RenderTexture.active = prev;
                // EncodeToJPG is broken in this interop (BlittableArrayWrapper): append raw RGB24 rows; ffmpeg encodes them.
                var px = read.GetPixels32();
                var raw = new byte[px.Length * 4];
                // il2cpp array header is 4 pointers on x64; elements are packed RGBA32.
                System.Runtime.InteropServices.Marshal.Copy(px.Pointer + 4 * IntPtr.Size, raw, 0, raw.Length);
                using (var fs = new FileStream(Path.Combine(_dir, "frames.rgb"), FileMode.Append)) fs.Write(raw, 0, raw.Length);
                _frame++;
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"FrameRecorder: {e}");
                break;
            }
        }
        RenderTexture.ReleaseTemporary(full); RenderTexture.ReleaseTemporary(small);
        UnityEngine.Object.Destroy(read);
        File.WriteAllText(Path.Combine(_dir, "done.txt"), $"{_frame} {width} {height} {fps}");
        _running = false;
    }

    public static object Status() => new { recording = _running, dir = _dir, frame = _frame, total = _total };
}
