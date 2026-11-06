using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace MirrorHub;

/// <summary>iPhone/iPad mirroring: UxPlay as an AirPlay receiver.</summary>
static class UxPlay
{
    const string FirewallRule = "MirrorHub AirPlay (UxPlay)";

    public const int MaxReceivers = 4;

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

    public static WatchedProcess Create(Settings s, int slot, bool audioOnly, Func<bool> restartOnCrash)
    {
        // UxPlay keeps its key in $HOME: a stable HOME keeps the server identity
        // (and PIN pairings) across restarts. One folder per receiver.
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MirrorHub",
                                slot == 1 ? "uxplay" : $"uxplay-{slot}");
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
        return new WatchedProcess(Tools.UxPlay!, () => Args(s, slot, audioOnly), restartOnCrash, env);
    }

    /// <summary>UxPlay prints this once it is listening.</summary>
    public const string ReadyLine = "Initialized server socket";

    public static bool IsValidPin(string? pin) => pin != null && Regex.IsMatch(pin, @"^\d{4}$") && pin != "0000";

    static IEnumerable<string> Args(Settings s, int slot, bool audioOnly)
    {
        yield return "-n"; yield return NameFor(s, slot);
        yield return "-nh";
        if (slot > 1) { yield return "-m"; yield return MacFor(slot); }
        if (audioOnly)
        {
            // No window: in Screen Mirroring the iPhone sends only 1 frame/s, which is not shown;
            // AirPlay audio from a music app (Apple Lossless) works as usual.
            yield return "-vs"; yield return "0";
            if (s.AirPlayNoHold) yield return "-nohold";
            if (IsValidPin(s.AirPlayPin)) { yield return "-pin"; yield return s.AirPlayPin; }
            yield break;
        }
        yield return "-s"; yield return s.AirPlayResolution;
        yield return "-fps"; yield return s.AirPlayFps.ToString();
        if (s.AirPlayH265) yield return "-h265";
        if (s.AirPlayVideoSink != "auto") { yield return "-vs"; yield return s.AirPlayVideoSink; }
        if (s.AirPlayFullscreen) yield return "-fs";
        if (s.AirPlayLowLatency) { yield return "-vsync"; yield return "no"; }
        else if (s.AirPlayAudioDelayMs != 0)
        {
            // Timestamp sync (the default) with an audio offset; UxPlay accepts less than one second.
            yield return "-vsync"; yield return Math.Clamp(s.AirPlayAudioDelayMs, -999, 999).ToString();
        }
        if (s.AirPlayNoHold) yield return "-nohold";
        if (IsValidPin(s.AirPlayPin)) { yield return "-pin"; yield return s.AirPlayPin; }
        if (s.AirPlayRecord)
        {
            Directory.CreateDirectory(s.OutputDir);
            // UxPlay appends ".<n>.<format>.mp4".
            yield return "-mp4"; yield return Path.Combine(s.OutputDir, $"iPhone{(slot > 1 ? "-" + slot : "")}_{DateTime.Now:yyyyMMdd_HHmmss}");
        }
    }

    static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>What could stop the iPhone from finding this PC.</summary>
    public sealed record Preflight(List<string> PublicNetworks, bool FirewallOk, string Networks);

    /// <summary>
    /// Checks the networks that reach the router (VPNs such as Tailscale are ignored) and the
    /// firewall: our Allow rule must point to this uxplay.exe and no Block rule may exist for it
    /// (Windows creates those when its first-run prompt is dismissed). No admin rights needed.
    /// </summary>
    public static async Task<Preflight> CheckAsync()
    {
        var exe = Q(Tools.UxPlay ?? "");
        var script =
            "$n = @(Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetProfile } | " +
            "  ForEach-Object { $_.InterfaceAlias + '=' + $_.NetProfile.NetworkCategory }) -join '|'; " +
            $"$allow = @(Get-NetFirewallRule -DisplayName {Q(FirewallRule)} -ErrorAction SilentlyContinue | " +
            "  Where-Object { $_.Enabled -eq 'True' -and $_.Action -eq 'Allow' } | Get-NetFirewallApplicationFilter | " +
            $"  Where-Object {{ $_.Program -eq {exe} }}).Count; " +
            $"$block = @(Get-NetFirewallApplicationFilter -Program {exe} -ErrorAction SilentlyContinue | Get-NetFirewallRule | " +
            "  Where-Object { $_.Enabled -eq 'True' -and $_.Action -eq 'Block' }).Count; " +
            "\"$n`n$allow`n$block\"";
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        try
        {
            using var p = Process.Start(psi)!;
            var lines = (await p.StandardOutput.ReadToEndAsync()).Replace("\r", "").Split('\n');
            await p.WaitForExitAsync();
            var nets = lines[0].Trim();
            var pub = nets.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x.EndsWith("=Public")).Select(x => x[..x.LastIndexOf('=')]).ToList();
            var ok = lines.Length > 2 && lines[1].Trim() != "0" && lines[2].Trim() == "0";
            return new Preflight(pub, ok, nets.Replace("|", "; ").Replace("=", ": "));
        }
        catch (Exception ex) { return new Preflight([], true, "unknown (" + ex.Message + ")"); }
    }

    /// <summary>
    /// One UAC prompt that sets the given networks to Private and/or replaces every firewall rule
    /// for uxplay.exe with one inbound Allow rule for Private networks. False if the user said no.
    /// </summary>
    public static async Task<bool> FixAsync(IEnumerable<string> makePrivate, bool firewall)
    {
        var cmd = string.Concat(makePrivate.Select(a =>
            $"Set-NetConnectionProfile -InterfaceAlias {Q(a)} -NetworkCategory Private -ErrorAction SilentlyContinue; "));
        if (firewall)
        {
            var exe = Q(Tools.UxPlay!);
            cmd += $"Get-NetFirewallApplicationFilter -Program {exe} -ErrorAction SilentlyContinue | Get-NetFirewallRule | Remove-NetFirewallRule; " +
                   $"Remove-NetFirewallRule -DisplayName {Q(FirewallRule)} -ErrorAction SilentlyContinue; " +
                   $"New-NetFirewallRule -DisplayName {Q(FirewallRule)} -Direction Inbound -Action Allow " +
                   $"-Profile Private -Program {exe} | Out-Null";
        }
        if (cmd.Length == 0) return true;
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(cmd);
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }   // UAC prompt declined
    }
}
