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

/// <summary>
/// Which video codecs a device can encode in hardware (scrcpy --list-encoders). Without --video-encoder,
/// scrcpy takes the first encoder Android offers for the codec, even a software one: far too slow for
/// mirroring (e.g. AV1 on phones without an AV1 encoder chip).
/// </summary>
public sealed class VideoEncoders
{
    readonly Dictionary<string, bool> _hardware;   // codec -> has an encoder that is not software-only

    VideoEncoders(Dictionary<string, bool> hardware) => _hardware = hardware;

    public bool HasHardware(string codec) => _hardware.GetValueOrDefault(codec);

    /// <summary>
    /// The codec to use instead of <paramref name="codec"/> when the device lacks it in hardware: H.265 if it has
    /// that in hardware, otherwise H.264. Null = keep it (H.264 is never replaced: there is nothing lighter).
    /// </summary>
    public string? FallbackFor(string codec)
    {
        if (codec == "h264" || HasHardware(codec)) return null;
        return codec != "h265" && HasHardware("h265") ? "h265" : "h264";
    }

    /// <summary>Null when the output holds no encoder list (device gone, scrcpy error).</summary>
    public static VideoEncoders? Parse(string listEncodersOutput)
    {
        if (!listEncodersOutput.Contains("List of video encoders")) return null;
        var hardware = new Dictionary<string, bool>();
        foreach (var line in listEncodersOutput.Split('\n'))
        {
            // --video-codec=h264 --video-encoder=OMX.Exynos.AVC.Encoder      (hw) [vendor]
            var m = Regex.Match(line, @"--video-codec=(\w+)\s+--video-encoder='?([^'\s]+)'?\s*(?:\((hw|sw|hybrid)\))?");
            if (!m.Success) continue;
            var name = m.Groups[2].Value;
            // scrcpy prints hw/sw/hybrid on Android 10+ only. On older versions the Android software
            // encoders are recognized by name; any other encoder counts as hardware, as before this check.
            var software = m.Groups[3].Success
                ? m.Groups[3].Value == "sw"
                : name.StartsWith("c2.android.", StringComparison.OrdinalIgnoreCase) ||
                  name.StartsWith("OMX.google.", StringComparison.OrdinalIgnoreCase);
            hardware[m.Groups[1].Value] = hardware.GetValueOrDefault(m.Groups[1].Value) || !software;
        }
        return new VideoEncoders(hardware);
    }
}

/// <summary>Android screen and camera mirroring through scrcpy.</summary>
static class Scrcpy
{
    static Dictionary<string, string> Env => new() { ["ADB"] = Tools.Adb };

    public static string CodecLabel(string codec) => codec switch
    {
        "h264" => "H.264",
        "h265" => "H.265",
        "av1" => "AV1",
        _ => codec.ToUpperInvariant(),
    };

    // Hardware id -> video encoders. A device's encoders don't change, so they are listed once per run
    // (about 1 s over USB). Keyed by hardware id: the adb serial changes between USB and Wi-Fi.
    static readonly Dictionary<string, Task<VideoEncoders?>> _encoders = new();

    /// <summary>The device's video encoders, listed on the first call. Null if they could not be listed.</summary>
    public static Task<VideoEncoders?> VideoEncodersAsync(string serial, string hwId)
    {
        if (Tools.Scrcpy == null) return Task.FromResult<VideoEncoders?>(null);
        // A listing in progress is shared; a failed one is tried again.
        if (_encoders.TryGetValue(hwId, out var t) && (!t.IsCompleted || (t.IsCompletedSuccessfully && t.Result != null)))
            return t;
        return _encoders[hwId] = ListVideoEncodersAsync(serial);
    }

    /// <summary>The encoders already listed for this device, without listing them.</summary>
    public static VideoEncoders? CachedVideoEncoders(string hwId) =>
        _encoders.TryGetValue(hwId, out var t) && t.IsCompletedSuccessfully ? t.Result : null;

    static async Task<VideoEncoders?> ListVideoEncodersAsync(string serial)
    {
        var (_, o) = await ProcessRunner.RunAsync(Tools.Scrcpy!, ["--serial=" + serial, "--list-encoders"], 30_000, Env);
        return VideoEncoders.Parse(o);
    }

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

    /// <param name="videoCodec">Used instead of the chosen codec when the device lacks that one in hardware.</param>
    public static WatchedProcess CreateCamera(Device device, CameraOptions c, string outputDir, string videoCodec) => new(
        Tools.Scrcpy!,
        () => CameraArgs(device, c, outputDir, videoCodec),
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
        foreach (var a in PhoneTooArgs(o)) yield return a;
    }

    /// <summary>
    /// Sound on the PC and on the phone at the same time. Needs the "playback" source (Android 13+):
    /// unlike the default "output" source it does not mute the phone, but apps that opt out of
    /// capture are not heard on the PC.
    /// </summary>
    static IEnumerable<string> PhoneTooArgs(MirrorOptions o)
    {
        if (!o.AudioOnPhoneToo) yield break;
        yield return "--audio-source=playback";
        yield return "--audio-dup";
    }

    /// <summary>
    /// The options a mirror runs with, to tell whether a settings change needs a restart: the same arguments
    /// as the launch, without the recording file name and the app to launch.
    /// </summary>
    public static string SettingsKey(Device device, MirrorOptions o, bool audioOnly) =>
        string.Join('\n', audioOnly ? AudioOnlyArgs(device, o) : MirrorArgs(device, o, outputDir: null, startApp: null));

    /// <param name="outputDir">Null only for <see cref="SettingsKey"/>: no recording file is named.</param>
    static IEnumerable<string> MirrorArgs(Device device, MirrorOptions o, string? outputDir, string? startApp)
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
            foreach (var a in PhoneTooArgs(o)) yield return a;
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
        if (o.Record) yield return outputDir == null ? "--record" : "--record=" + RecordPath(outputDir, device.Model);
    }

    static IEnumerable<string> CameraArgs(Device device, CameraOptions c, string outputDir, string videoCodec)
    {
        yield return "--serial=" + device.Serial;
        yield return "--window-title=MirrorHub Camera - " + device.Model;
        yield return "--video-source=camera";
        yield return c.Camera.StartsWith("id:") ? "--camera-id=" + c.Camera[3..] : "--camera-facing=" + c.Camera;
        if (c.MaxSize > 0) yield return $"--max-size={c.MaxSize}";
        if (c.Fps > 0) yield return $"--camera-fps={c.Fps}";
        if (Math.Abs(c.Zoom - 1.0) > 0.001) yield return "--camera-zoom=" + c.Zoom.ToString(CultureInfo.InvariantCulture);
        if (c.Torch) yield return "--camera-torch";
        yield return "--video-codec=" + videoCodec;
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
