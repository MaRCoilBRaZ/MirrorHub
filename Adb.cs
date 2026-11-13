using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace MirrorHub;

public sealed record Device(string Serial, string State, string Model)
{
    public bool IsWifi => Serial.Contains(':') || Serial.Contains("._adb-tls-connect");
    public string Display => $"{Model} ({(IsWifi ? "Wi-Fi" : "USB")})";
}

public sealed record MdnsService(string Name, string Type, string Address);

/// <summary>Runs a command line tool and captures its output.</summary>
static class ProcessRunner
{
    public static async Task<(int Code, string Out)> RunAsync(
        string exe, IEnumerable<string> args, int timeoutMs, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // The tools print UTF-8; the default is the system code page.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        Process p;
        try { p = Process.Start(psi)!; }
        catch (Win32Exception ex) { return (-1, $"{Path.GetFileName(exe)} could not be started: {ex.Message}"); }
        Os.TrackChild(p);

        using (p)
        {
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return (-1, $"timeout: {Path.GetFileName(exe)} {string.Join(' ', args)}");
            }
            return (p.ExitCode, ((await stdout) + (await stderr)).Trim());
        }
    }
}

/// <summary>Thin async wrapper around the adb command line.</summary>
static class Adb
{
    public static Task<(int Code, string Out)> RunAsync(params string[] args) => RunAsync(15000, args);

    public static Task<(int Code, string Out)> RunAsync(int timeoutMs, params string[] args) =>
        ProcessRunner.RunAsync(Tools.Adb, args, timeoutMs);

    /// <summary>
    /// On exit: stops the background adb server if it is MirrorHub's own copy, so no adb.exe keeps
    /// the folder locked. An adb installed elsewhere (e.g. Android Studio's) is left alone.
    /// </summary>
    public static void StopBundledServer()
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Tools.Adb.StartsWith(AppContext.BaseDirectory, cmp)) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Tools.Adb, "kill-server") { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(3000);
        }
        catch { }
    }

    public static async Task<List<Device>> DevicesAsync()
    {
        var (_, o) = await RunAsync("devices", "-l");
        var list = new List<Device>();
        foreach (var raw in o.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of") || line.StartsWith('*')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var state = parts[1] == "no" && parts.Length > 2 && parts[2].StartsWith("permissions", StringComparison.Ordinal)
                ? "no permissions"
                : parts[1];
            var model = parts.FirstOrDefault(x => x.StartsWith("model:"))?[6..].Replace('_', ' ') ?? parts[0];
            list.Add(new Device(parts[0], state, model));
        }
        return list;
    }

    /// <summary>Stable id of the phone: the same over USB and Wi-Fi, unlike the adb serial.</summary>
    public static async Task<string?> HardwareIdAsync(string serial)
    {
        var (rc, o) = await RunAsync("-s", serial, "shell", "getprop", "ro.serialno");
        return rc == 0 && o.Length > 0 && !o.Contains(' ') ? o : null;
    }

    /// <summary>Android API level (30 = Android 11, 33 = Android 13), or null if unknown.</summary>
    public static async Task<int?> SdkAsync(string serial)
    {
        var (rc, o) = await RunAsync("-s", serial, "shell", "getprop", "ro.build.version.sdk");
        return rc == 0 && int.TryParse(o.Trim(), out var sdk) ? sdk : null;
    }

    public static async Task<List<MdnsService>> MdnsServicesAsync()
    {
        var (_, o) = await RunAsync("mdns", "services");
        var list = new List<MdnsService>();
        foreach (var raw in o.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[1].Contains("_adb"))
                list.Add(new MdnsService(parts[0], parts[1], parts[^1]));
        }
        return list;
    }

    public static async Task<string?> WifiIpAsync(string serial)
    {
        var (_, o) = await RunAsync("-s", serial, "shell", "ip", "-f", "inet", "addr", "show", "wlan0");
        var m = Regex.Match(o, @"inet (\d+\.\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static Task<(int Code, string Out)> InstallAsync(string serial, string apk) =>
        RunAsync(10 * 60_000, "-s", serial, "install", "-r", apk);

    public const string PushTarget = "/sdcard/Download/";

    public static Task<(int Code, string Out)> PushAsync(string serial, string path) =>
        RunAsync(60 * 60_000, "-s", serial, "push", path, PushTarget);

    /// <summary>Starts an app's launcher activity (used when a mirror is already running).</summary>
    public static Task<(int Code, string Out)> LaunchAppAsync(string serial, string package) =>
        RunAsync("-s", serial, "shell", "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1");

    /// <summary>Saves a PNG screenshot of the device screen.</summary>
    public static async Task<bool> ScreencapAsync(string serial, string file)
    {
        var psi = new ProcessStartInfo(Tools.Adb)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-s", serial, "exec-out", "screencap", "-p" }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        Os.TrackChild(p);
        await using (var fs = File.Create(file))
            await p.StandardOutput.BaseStream.CopyToAsync(fs);
        await p.WaitForExitAsync();
        return p.ExitCode == 0 && new FileInfo(file).Length > 0;
    }
}
