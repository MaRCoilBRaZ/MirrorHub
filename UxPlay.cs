using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace MirrorHub;

/// <summary>iPhone/iPad mirroring: UxPlay as an AirPlay receiver.</summary>
static class UxPlay
{
    const string FirewallRule = "MirrorHub AirPlay (UxPlay)";

    public static WatchedProcess Create(Settings s)
    {
        // UxPlay keeps its key in $HOME: a stable HOME keeps the server identity
        // (and PIN pairings) across restarts.
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MirrorHub", "uxplay");
        Directory.CreateDirectory(home);
        var bin = Path.GetDirectoryName(Tools.UxPlay)!;
        var env = new Dictionary<string, string>
        {
            ["HOME"] = home,
            // GStreamer and the other DLLs live next to uxplay.exe.
            ["PATH"] = bin + ";" + Environment.GetEnvironmentVariable("PATH"),
        };
        if (Tools.UxPlayIsBundled)
        {
            // Use only the bundled GStreamer plugins, even if another GStreamer is installed.
            var root = Path.GetDirectoryName(bin)!;
            env["GST_PLUGIN_PATH"] = "";
            env["GST_PLUGIN_PATH_1_0"] = "";
            env["GST_PLUGIN_SYSTEM_PATH_1_0"] = Path.Combine(root, "lib", "gstreamer-1.0");
            env["GST_PLUGIN_SCANNER_1_0"] = Path.Combine(root, "libexec", "gstreamer-1.0", "gst-plugin-scanner.exe");
            env["GST_REGISTRY_1_0"] = Path.Combine(home, "gst-registry.bin");
        }
        return new WatchedProcess(Tools.UxPlay!, () => Args(s), () => true, env);
    }

    public static bool IsValidPin(string? pin) => pin != null && Regex.IsMatch(pin, @"^\d{4}$") && pin != "0000";

    static IEnumerable<string> Args(Settings s)
    {
        yield return "-n"; yield return s.AirPlayName;
        yield return "-nh";
        yield return "-s"; yield return s.AirPlayResolution;
        yield return "-fps"; yield return s.AirPlayFps.ToString();
        if (s.AirPlayH265) yield return "-h265";
        if (s.AirPlayVideoSink != "auto") { yield return "-vs"; yield return s.AirPlayVideoSink; }
        if (s.AirPlayFullscreen) yield return "-fs";
        if (s.AirPlayNoHold) yield return "-nohold";
        if (IsValidPin(s.AirPlayPin)) { yield return "-pin"; yield return s.AirPlayPin; }
        if (s.AirPlayRecord)
        {
            Directory.CreateDirectory(s.OutputDir);
            // UxPlay appends ".<n>.<format>.mp4".
            yield return "-mp4"; yield return Path.Combine(s.OutputDir, $"iPhone_{DateTime.Now:yyyyMMdd_HHmmss}");
        }
    }

    /// <summary>
    /// Replaces every firewall rule for uxplay.exe (including the Block rules Windows creates
    /// when its first-run prompt is dismissed) with one inbound Allow rule for Private networks.
    /// Shows a UAC prompt.
    /// </summary>
    public static void OpenFirewall()
    {
        var cmd = $"Get-NetFirewallApplicationFilter -Program '{Tools.UxPlay}' -ErrorAction SilentlyContinue | Get-NetFirewallRule | Remove-NetFirewallRule; " +
                  $"Remove-NetFirewallRule -DisplayName '{FirewallRule}' -ErrorAction SilentlyContinue; " +
                  $"New-NetFirewallRule -DisplayName '{FirewallRule}' -Direction Inbound -Action Allow " +
                  $"-Profile Private -Program '{Tools.UxPlay}' | Out-Null";
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(cmd);
        Process.Start(psi);
    }

    /// <summary>"Private", "Public", "DomainAuthenticated" for each active network.</summary>
    public static async Task<string> NetworkCategoriesAsync()
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("(Get-NetConnectionProfile | ForEach-Object { \"$($_.InterfaceAlias): $($_.NetworkCategory)\" }) -join '; '");
        try
        {
            using var p = Process.Start(psi)!;
            var o = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return o.Trim();
        }
        catch (Exception ex) { return "unknown (" + ex.Message + ")"; }
    }
}
