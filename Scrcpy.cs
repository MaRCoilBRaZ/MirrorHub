using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MirrorHub;

public sealed record AndroidApp(string Label, string Package)
{
    public override string ToString() => $"{Label}   ({Package})";
}

public sealed record CameraInfo(string Id, string Facing, string Size, string Zoom)
{
    public string Label => $"{FacingLabel(Facing)} · id {Id} · {Size}{(Zoom.Length > 0 ? $" · zoom {Zoom}" : "")}";

    public static string FacingLabel(string facing) => facing switch
    {
        "back" => "Back",
        "front" => "Front",
        "external" => "External",
        _ => facing,
    };
}

/// <summary>Android screen and camera mirroring through scrcpy.</summary>
static class Scrcpy
{
    static Dictionary<string, string> Env => new() { ["ADB"] = Tools.Adb };

    /// <summary>Screen mirroring. <paramref name="startApp"/> is launched on the first start only.</summary>
    public static WatchedProcess Create(Device device, MirrorOptions o, string outputDir, string? startApp = null) => new(
        Tools.Scrcpy!,
        () =>
        {
            var args = MirrorArgs(device, o, outputDir, startApp);
            startApp = null;
            return args;
        },
        () => o.AutoReconnect,
        Env,
        async () => { if (device.IsWifi) await Adb.RunAsync("connect", device.Serial); });

    public static WatchedProcess CreateCamera(Device device, CameraOptions c, string outputDir) => new(
        Tools.Scrcpy!,
        () => CameraArgs(device, c, outputDir),
        () => true,
        Env,
        async () => { if (device.IsWifi) await Adb.RunAsync("connect", device.Serial); });

    /// <summary>
    /// Phone sound only, no video and no window: very light on the PC, meant to run in the background
    /// (e.g. while gaming). Requires Android 11+.
    /// </summary>
    public static WatchedProcess CreateAudioOnly(Device device, MirrorOptions o) => new(
        Tools.Scrcpy!,
        () => AudioOnlyArgs(device, o),
        () => o.AutoReconnect,
        Env,
        async () => { if (device.IsWifi) await Adb.RunAsync("connect", device.Serial); });

    static IEnumerable<string> AudioOnlyArgs(Device device, MirrorOptions o)
    {
        yield return "--serial=" + device.Serial;
        yield return "--no-video";
        yield return "--no-window";
        yield return "--no-control";
        yield return "--require-audio";     // fail visibly instead of sitting there silent (Android < 11)
        // Opus at 64 kbit/s: half the default bitrate, hard to tell apart from it in headphones.
        yield return "--audio-codec=opus";
        yield return "--audio-bit-rate=64K";
        yield return $"--audio-buffer={o.AudioBufferMs}";
    }

    static IEnumerable<string> MirrorArgs(Device device, MirrorOptions o, string outputDir, string? startApp)
    {
        yield return "--serial=" + device.Serial;
        yield return "--window-title=MirrorHub - " + device.Model;
        yield return "--video-codec=" + o.VideoCodec;
        yield return $"--video-bit-rate={o.BitRateMbps}M";
        yield return $"--max-fps={o.MaxFps}";
        if (o.MaxSize > 0) yield return $"--max-size={o.MaxSize}";
        yield return $"--video-buffer={o.VideoBufferMs}";
        yield return "--hwdec=" + o.HwDec;
        yield return "--render-fit=" + o.RenderFit;

        if (o.Audio)
        {
            yield return "--audio-codec=" + o.AudioCodec;
            yield return $"--audio-buffer={o.AudioBufferMs}";
        }
        else yield return "--no-audio";

        // --stay-awake and --turn-screen-off require control.
        if (!o.Control) yield return "--no-control";
        else
        {
            if (o.StayAwake) yield return "--stay-awake";
            if (o.TurnScreenOff) yield return "--turn-screen-off";
        }

        if (o.AlwaysOnTop) yield return "--always-on-top";
        if (o.Fullscreen) yield return "--fullscreen";
        if (o.Borderless) yield return "--window-borderless";
        if (startApp != null) yield return "--start-app=" + startApp;
        if (o.Record) yield return "--record=" + RecordPath(outputDir, device.Model);
    }

    static IEnumerable<string> CameraArgs(Device device, CameraOptions c, string outputDir)
    {
        yield return "--serial=" + device.Serial;
        yield return "--window-title=MirrorHub Camera - " + device.Model;
        yield return "--video-source=camera";
        yield return c.Camera.StartsWith("id:") ? "--camera-id=" + c.Camera[3..] : "--camera-facing=" + c.Camera;
        if (c.MaxSize > 0) yield return $"--max-size={c.MaxSize}";
        if (c.Fps > 0) yield return $"--camera-fps={c.Fps}";
        if (Math.Abs(c.Zoom - 1.0) > 0.001) yield return "--camera-zoom=" + c.Zoom.ToString(CultureInfo.InvariantCulture);
        if (c.Torch) yield return "--camera-torch";
        yield return "--video-codec=" + c.VideoCodec;
        yield return $"--video-bit-rate={c.BitRateMbps}M";
        // Applied to the captured stream, so window, OBS and recording all match
        // (--orientation would also set the record orientation, which rejects flips).
        yield return "--capture-orientation=" + (c.Flip ? "flip" : "") + c.Orientation;
        if (c.Microphone) yield return "--audio-source=mic";
        else yield return "--no-audio";
        if (c.AlwaysOnTop) yield return "--always-on-top";
        if (c.Borderless) yield return "--window-borderless";
        if (c.Record) yield return "--record=" + RecordPath(outputDir, device.Model + "_camera");
    }

    // .mkv stays playable even if the process is killed mid-recording.
    static string RecordPath(string outputDir, string name)
    {
        Directory.CreateDirectory(outputDir);
        return Path.Combine(outputDir, $"{Tools.SafeName(name)}_{DateTime.Now:yyyyMMdd_HHmmss}.mkv");
    }

    /// <summary>Installed apps with their labels (scrcpy --list-apps).</summary>
    public static async Task<List<AndroidApp>> ListAppsAsync(string serial)
    {
        var (_, o) = await ProcessRunner.RunAsync(Tools.Scrcpy!, ["--serial=" + serial, "--list-apps"], 120_000, Env);
        return o.Split('\n')
            .Select(l => Regex.Match(l, @"^\s*[*-]\s+(.+?)\s{2,}(\S+)\s*$"))
            .Where(m => m.Success)
            .Select(m => new AndroidApp(m.Groups[1].Value.Trim(), m.Groups[2].Value))
            .OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Device cameras (scrcpy --list-cameras). Requires Android 12+.</summary>
    public static async Task<(List<CameraInfo> Cameras, string Output)> ListCamerasAsync(string serial)
    {
        var (_, o) = await ProcessRunner.RunAsync(Tools.Scrcpy!, ["--serial=" + serial, "--list-cameras"], 60_000, Env);
        var cams = o.Split('\n')
            .Select(l => Regex.Match(l, @"--camera-id=(\S+)\s+\((\w+),\s*(\d+x\d+)(?:.*?zoom-range=\[([^\]]+)\])?"))
            .Where(m => m.Success)
            .Select(m => new CameraInfo(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value.Replace(", ", "-")))
            .ToList();
        return (cams, o);
    }
}
