using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia.Controls;

namespace MirrorHub;

/// <summary>Linux-specific platform integration.</summary>
static class Os
{
    public static string Name => "Linux";
    public static string Exe(string name) => name;
    public static string StartAtLoginLabel => "Start MirrorHub when I log in";
    public static string ConfigDir => ConfigHome();
    public static bool HasFirewallSetup => false;
    public static string SettingsPathHint => "~/.config/MirrorHub/settings.json";
    public static string AirPlayAdvancedHint =>
        "Renderer \"auto\" works everywhere; glimagesink or waylandsink can be smoother depending on your desktop. " +
        "Changing a value here turns the quality into \"Custom\".";

    public static IReadOnlyList<string> HwDecoders { get; } = ["auto", "vaapi", "disabled"];
    public static IReadOnlyList<string> AirPlaySinks { get; } = ["auto", "glimagesink", "xvimagesink", "waylandsink", "ximagesink"];

    static string ConfigHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(AppPaths.HomeDir(), ".config");
    }

    public static string? FindWinGetScrcpy() => null;

    static string EscapeDesktopExecArg(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("\"", "\\\"")
         .Replace("`", "\\`")
         .Replace("$", "\\$");

    public static void ApplyAutostart(bool enabled)
    {
        var desktopFile = Path.Combine(ConfigHome(), "autostart", "mirrorhub.desktop");
        if (!enabled)
        {
            if (File.Exists(desktopFile)) File.Delete(desktopFile);
            return;
        }
        if (Environment.ProcessPath is not { Length: > 0 } exe) return;
        Directory.CreateDirectory(Path.GetDirectoryName(desktopFile)!);
        var lines = new List<string>
        {
            "[Desktop Entry]",
            "Type=Application",
            "Name=MirrorHub",
            $"Exec=\"{EscapeDesktopExecArg(exe)}\" {Startup.AutostartArg}",
        };
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.png");
        if (File.Exists(iconPath)) lines.Add($"Icon={iconPath}");
        lines.Add("X-GNOME-Autostart-enabled=true");
        File.WriteAllLines(desktopFile, lines);
    }

    public static void OpenFolder(string path, Action<string>? log = null) => OpenWithXdg(path, log);

    public static void OpenFile(string path, Action<string>? log = null) => OpenWithXdg(path, log);

    static void OpenWithXdg(string path, Action<string>? log)
    {
        try
        {
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi);
        }
        catch
        {
            log?.Invoke($"Cannot open {path}: xdg-open not found");
        }
    }

    public static ITray CreateTray(Func<IReadOnlyList<TrayItem>> buildMenu, Action<string> log) => new AvaloniaTray(buildMenu, log);

    public static IGlobalHotkeys CreateHotkeys() => new NoHotkeys();

    sealed class SignalRegistrations(params PosixSignalRegistration[] regs) : IDisposable
    {
        public void Dispose()
        {
            foreach (var r in regs) r.Dispose();
        }
    }

    public static IDisposable? RegisterShutdownSignals(Action onShutdown)
    {
        void Handler(PosixSignalContext ctx)
        {
            ctx.Cancel = true;
            onShutdown();
        }
        return new SignalRegistrations(
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handler),
            PosixSignalRegistration.Create(PosixSignal.SIGINT, Handler),
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, Handler));
    }

    public static string? NoUsbPermissionHint(string serial) =>
        $"USB permission missing for {serial}: install the Android udev rules " +
        "(Debian/Ubuntu: sudo apt install android-sdk-platform-tools-common), then unplug and plug the phone again.";

    public static IDisposable? TryAcquireInstanceLock(bool waitForRestart)
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var lockPath = !string.IsNullOrEmpty(runtimeDir)
            ? Path.Combine(runtimeDir, "mirrorhub.lock")
            : Path.Combine(AppPaths.DataDir, "instance.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        var deadline = waitForRestart ? DateTime.UtcNow.AddSeconds(20) : DateTime.MinValue;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    // ---------- child process tracking ----------

    static readonly object ChildrenLock = new();
    static readonly List<Process> Children = [];

    public static void TrackChild(Process p)
    {
        lock (ChildrenLock)
        {
            Children.RemoveAll(c => { try { return c.HasExited; } catch { return true; } });
            Children.Add(p);
        }
    }

    public static void KillChildren()
    {
        List<Process> snapshot;
        lock (ChildrenLock)
        {
            snapshot = [.. Children];
            Children.Clear();
        }
        foreach (var p in snapshot)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }

    // ---------- UxPlay & AirPlay preflight / diagnostics ----------

    public static void ConfigureUxPlayEnv(Dictionary<string, string> env, string receiverDir)
    {
        // Do not override HOME on Linux: XAUTHORITY and PulseAudio/PipeWire depend on the real HOME.
    }

    public static IEnumerable<string> UxPlayExtraArgs(int slot, string receiverDir, bool hasValidPin)
    {
        yield return "-p";
        yield return (7000 + 3 * (slot - 1)).ToString();
        if (hasValidPin)
        {
            yield return "-key";
            yield return Path.Combine(receiverDir, "uxplay.pem");
        }
    }

    static bool _uxPlayVersionChecked;
    static (Version Version, string Text)? _uxPlayVersion;

    public static (Version Version, string Text)? GetUxPlayVersion()
    {
        if (_uxPlayVersionChecked) return _uxPlayVersion;
        _uxPlayVersionChecked = true;
        if (Tools.UxPlay == null) return null;

        try
        {
            var psi = new ProcessStartInfo(Tools.UxPlay)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-h");
            using var p = Process.Start(psi);
            if (p == null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            Task.WaitAll([outTask, errTask], 200);
            var combined = (outTask.IsCompletedSuccessfully ? outTask.Result : "") + "\n" +
                           (errTask.IsCompletedSuccessfully ? errTask.Result : "");
            var firstLine = combined.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (firstLine == null) return null;
            var m = Regex.Match(firstLine, @"UxPlay\s+(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            if (m.Success && Version.TryParse(m.Groups[1].Value, out var raw))
            {
                var norm = new Version(raw.Major, raw.Minor, Math.Max(0, raw.Build));
                _uxPlayVersion = (norm, m.Groups[1].Value);
            }
        }
        catch { }
        return _uxPlayVersion;
    }

    static async Task<(bool Ran, int ExitCode, string Out)> TryRunAsync(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (false, -1, "");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(3000);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                return (false, -1, "");
            }
            return (true, p.ExitCode, ((await stdout) + "\n" + (await stderr)).Trim());
        }
        catch { return (false, -1, ""); }
    }

    public static Task<UxPlay.Preflight> CheckAirPlayNetworkAsync() =>
        Task.FromResult(new UxPlay.Preflight([], true, ""));

    public static Task<bool> FixAirPlayNetworkAsync(IEnumerable<string> makePrivate, bool firewall) =>
        Task.FromResult(true);

    public static async Task RunAirPlayPreflightAsync(
        Window owner,
        bool interactive,
        Action<string> log,
        Action<string, (string Label, Action Action)[]> showProblem,
        Action hideProblem,
        Func<bool> canClearProblem)
    {
        var avahi = await TryRunAsync("systemctl", "is-active", "avahi-daemon");
        var avahiDown = avahi.Ran && !avahi.Out.Split('\n').Any(l => l.Trim() == "active");

        var fw = await TryRunAsync("firewall-cmd", "--state");
        var firewalldRunning = fw.Ran && fw.Out.Split('\n').Any(l => l.Trim() == "running");

        if (!avahiDown && !firewalldRunning) return;

        var cmds = new List<string>();
        if (avahiDown)
            cmds.Add("sudo systemctl enable --now avahi-daemon");
        if (firewalldRunning)
            cmds.Add("sudo firewall-cmd --permanent --add-service=mdns --add-port=7000-7011/tcp --add-port=7000-7011/udp && sudo firewall-cmd --reload");

        var reasons = new List<string>();
        if (avahiDown) reasons.Add("•  avahi-daemon is not active (needed so the iPhone/iPad can find this PC).");
        if (firewalldRunning) reasons.Add("•  firewalld is running (mDNS and ports 7000-7011 TCP/UDP must be open).");

        var msg = "AirPlay prerequisites:\n\n" + string.Join("\n", reasons) +
                  "\n\nRun:\n" + string.Join("\n", cmds);

        log("[AirPlay] " + string.Join(" | ", cmds));
        if (interactive)
            await MessageDialog.ShowAsync(owner, msg, "MirrorHub");
    }

    public static (string Status, string Problem)? DiagnoseAirPlayFailure(IReadOnlyList<string> earlyLines, IReadOnlyCollection<string> tail)
    {
        var lines = earlyLines.Concat(tail);
        if (lines.Any(l => l.Contains("Could not initialize dnssd", StringComparison.OrdinalIgnoreCase) ||
                           (l.Contains("dnssd_register_", StringComparison.OrdinalIgnoreCase) && l.Contains("failed", StringComparison.OrdinalIgnoreCase))))
        {
            return ("avahi-daemon is not running",
                    "The iPhone/iPad cannot find this PC because avahi-daemon is not running: sudo systemctl enable --now avahi-daemon");
        }
        if (lines.Any(l => l.Contains("unknown option", StringComparison.OrdinalIgnoreCase)))
        {
            var ver = GetUxPlayVersion()?.Text ?? "unknown";
            return ("The receiver could not start",
                    $"This UxPlay ({ver}) is too old for an option MirrorHub uses. Install a newer UxPlay or turn off the option.");
        }
        return null;
    }

    public static Task RetitleAirWindowAsync(Func<int?> getPid, string title, Func<bool> shouldContinue) =>
        Task.CompletedTask;
}
