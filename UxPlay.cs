using System.IO;
using System.Text.RegularExpressions;

namespace MirrorHub;

/// <summary>iPhone/iPad mirroring: UxPlay as an AirPlay receiver.</summary>
static class UxPlay
{
    public const string FirewallRule = "MirrorHub AirPlay (UxPlay)";

    public const int MaxReceivers = 4;

    static readonly Version MinH265Version = new(1, 70, 0);
    static readonly Version MinRecordVersion = new(1, 73, 0);

    /// <summary>How many receivers to run: one per iPhone/iPad mirrored at the same time.</summary>
    public static int ReceiverCount(Settings s) => Math.Clamp(s.AirPlayReceivers, 1, MaxReceivers);

    /// <summary>Receiver 1 keeps the plain name; the others get "Name 2", "Name 3"…</summary>
    public static string NameFor(Settings s, int slot) => slot == 1 ? s.AirPlayName : $"{s.AirPlayName} {slot}";

    /// <summary>
    /// Every receiver needs its own device id (a MAC address). Receiver 1 uses the real one, as before;
    /// the others get a fixed, locally administered address, so the iPhone sees the same device every time.
    /// </summary>
    static string MacFor(int slot)
    {
        var h = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"MirrorHub|{Environment.MachineName}|{slot}"));
        h[0] = (byte)((h[0] & 0xFC) | 0x02);   // locally administered, unicast
        return string.Join(":", h.Take(6).Select(b => b.ToString("x2")));
    }

    static string ReceiverDir(int slot) =>
        Path.Combine(AppPaths.DataDir, slot == 1 ? "uxplay" : $"uxplay-{slot}");

    public static bool SupportsH265() =>
        Os.GetUxPlayVersion() is not { } v || v.Version >= MinH265Version;

    public static bool SupportsRecording() =>
        Os.GetUxPlayVersion() is not { } v || v.Version >= MinRecordVersion;

    public static void LogUnsupportedOptions(Settings s, bool audioOnly, Action<string> log)
    {
        if (audioOnly || Os.GetUxPlayVersion() is not { } v) return;
        if (s.AirPlayH265 && v.Version < MinH265Version)
            log($"[AirPlay] UxPlay {v.Text} does not support H.265 (needs 1.70 or newer): option ignored.");
        if (s.AirPlayRecord && v.Version < MinRecordVersion)
            log($"[AirPlay] UxPlay {v.Text} does not support recording (needs 1.73 or newer): option ignored.");
    }

    public static WatchedProcess Create(Settings s, int slot, bool audioOnly, Func<bool> restartOnCrash)
    {
        // Per-receiver folder: used for HOME on Windows and -key / gst-registry on Linux.
        var home = ReceiverDir(slot);
        Directory.CreateDirectory(home);
        var bin = Path.GetDirectoryName(Tools.UxPlay)!;
        var env = new Dictionary<string, string>
        {
            // GStreamer and the other shared libraries live next to uxplay.
            ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
        };
        Os.ConfigureUxPlayEnv(env, home);
        if (Tools.UxPlayIsBundled)
        {
            // Use only the bundled GStreamer plugins, even if another GStreamer is installed.
            var root = Path.GetDirectoryName(bin)!;
            env["GST_PLUGIN_PATH"] = "";
            env["GST_PLUGIN_PATH_1_0"] = "";
            env["GST_PLUGIN_SYSTEM_PATH_1_0"] = Path.Combine(root, "lib", "gstreamer-1.0");
            env["GST_PLUGIN_SCANNER_1_0"] = Path.Combine(root, "libexec", "gstreamer-1.0", Os.Exe("gst-plugin-scanner"));
            env["GST_REGISTRY_1_0"] = Path.Combine(home, "gst-registry.bin");
        }
        return new WatchedProcess(Tools.UxPlay!, () => Args(s, slot, audioOnly, home), restartOnCrash, env);
    }

    /// <summary>UxPlay prints this once it is listening.</summary>
    public const string ReadyLine = "Initialized server socket";

    public const int MaxFps = 60;

    public static bool IsValidPin(string? pin) => pin != null && Regex.IsMatch(pin, @"^\d{4}$") && pin != "0000";

    static IEnumerable<string> Args(Settings s, int slot, bool audioOnly, string receiverDir)
    {
        var hasPin = IsValidPin(s.AirPlayPin);
        yield return "-n"; yield return NameFor(s, slot);
        yield return "-nh";
        if (slot > 1) { yield return "-m"; yield return MacFor(slot); }
        foreach (var extra in Os.UxPlayExtraArgs(slot, receiverDir, hasPin)) yield return extra;
        if (audioOnly)
        {
            // No window: in Screen Mirroring the iPhone sends only 1 frame/s, which is not shown;
            // AirPlay audio from a music app (Apple Lossless) works as usual.
            yield return "-vs"; yield return "0";
            if (s.AirPlayNoHold) yield return "-nohold";
            if (hasPin) { yield return "-pin"; yield return s.AirPlayPin; }
            yield break;
        }
        yield return "-s"; yield return s.AirPlayResolution;
        // AirPlay screen mirroring goes up to 60 fps: with a higher maxFPS the iPhone ignores the receiver.
        yield return "-fps"; yield return Math.Clamp(s.AirPlayFps, 1, MaxFps).ToString();
        if (s.AirPlayH265 && SupportsH265()) yield return "-h265";
        if (s.AirPlayVideoSink != "auto") { yield return "-vs"; yield return s.AirPlayVideoSink; }
        if (s.AirPlayFullscreen) yield return "-fs";
        if (s.AirPlayLowLatency) { yield return "-vsync"; yield return "no"; }
        else if (s.AirPlayAudioDelayMs != 0)
        {
            // Timestamp sync (the default) with an audio offset; UxPlay accepts less than one second.
            yield return "-vsync"; yield return Math.Clamp(s.AirPlayAudioDelayMs, -999, 999).ToString();
        }
        if (s.AirPlayNoHold) yield return "-nohold";
        if (hasPin) { yield return "-pin"; yield return s.AirPlayPin; }
        if (s.AirPlayRecord && SupportsRecording())
        {
            Directory.CreateDirectory(s.OutputDir);
            // UxPlay appends ".<n>.<format>.mp4".
            yield return "-mp4"; yield return Path.Combine(s.OutputDir, $"iPhone{(slot > 1 ? "-" + slot : "")}_{DateTime.Now:yyyyMMdd_HHmmss}");
        }
    }

    /// <summary>What could stop the iPhone from finding this PC.</summary>
    public sealed record Preflight(List<string> PublicNetworks, bool FirewallOk, string Networks);

    public static Task<Preflight> CheckAsync() => Os.CheckAirPlayNetworkAsync();

    public static Task<bool> FixAsync(IEnumerable<string> makePrivate, bool firewall) =>
        Os.FixAirPlayNetworkAsync(makePrivate, firewall);
}
