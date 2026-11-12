using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Microsoft.Win32;

namespace MirrorHub;

/// <summary>Windows-specific platform integration.</summary>
static class Os
{
    public static string Name => "Windows";
    public static string Exe(string name) => name + ".exe";
    public static string StartAtLoginLabel => "Start MirrorHub when Windows starts";
    public static string ConfigDir => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static bool HasFirewallSetup => true;
    public static string SettingsPathHint => @"%AppData%\MirrorHub\settings.json";
    public static string AirPlayAdvancedHint =>
        "Renderer \"auto\" works everywhere; d3d12videosink can be smoother on recent graphics cards. " +
        "The network must be Private in Windows (no guest networks with client isolation). " +
        "Changing a value here turns the quality into \"Custom\".";

    public static IReadOnlyList<string> HwDecoders { get; } = ["auto", "d3d11va", "disabled"];
    public static IReadOnlyList<string> AirPlaySinks { get; } = ["auto", "d3d11videosink", "d3d12videosink"];

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "MirrorHub";

    public static void ApplyAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" {Startup.AutostartArg}");
        else key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    public static void OpenFolder(string path, Action<string>? log = null) =>
        Process.Start("explorer.exe", path)?.Dispose();

    public static void OpenFile(string path, Action<string>? log = null) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    public static string? FindWinGetScrcpy()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(root)) return null;
        foreach (var d in Directory.EnumerateDirectories(root, "Genymobile.scrcpy*"))
        {
            var f = Directory.EnumerateFiles(d, "scrcpy.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (f != null) return f;
        }
        return null;
    }

    public static ITray CreateTray(Func<IReadOnlyList<TrayItem>> buildMenu, Action<string> log) => new WinFormsTray(buildMenu);

    public static IGlobalHotkeys CreateHotkeys() => new WinHotkeys();

    public static IDisposable? RegisterShutdownSignals(Action onShutdown) => null;

    public static string? NoUsbPermissionHint(string serial) => null;

    public static IDisposable? TryAcquireInstanceLock(bool waitForRestart)
    {
        var mutex = new Mutex(true, @"Local\MirrorHub.SingleInstance", out var isFirst);
        if (!isFirst && waitForRestart)
        {
            try { isFirst = mutex.WaitOne(TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { isFirst = true; }
        }
        if (isFirst) return mutex;
        mutex.Dispose();
        return null;
    }

    // ---------- job object for child processes ----------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x800;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    // The handle is never closed by code: the OS closes it when MirrorHub's process ends.
    static readonly IntPtr Job = CreateJob();

    static IntPtr CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new ExtendedLimitInformation
        {
            // BREAKAWAY_OK: a tool that explicitly asks to outlive us still can, instead of failing to start.
            Basic = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK },
        };
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimitInformation>());
        return job;
    }

    /// <summary>Ties a just-started process (and the processes it starts from now on) to MirrorHub's lifetime.</summary>
    public static void TrackChild(Process p)
    {
        if (Job == IntPtr.Zero) return;
        try { AssignProcessToJobObject(Job, p.Handle); } catch { }
    }

    public static void KillChildren() { }

    // ---------- UxPlay & AirPlay network / firewall preflight ----------

    public static void ConfigureUxPlayEnv(Dictionary<string, string> env, string receiverDir) =>
        env["HOME"] = receiverDir;

    public static IEnumerable<string> UxPlayExtraArgs(int slot, string receiverDir, bool hasValidPin) => [];

    public static (Version Version, string Text)? GetUxPlayVersion() => null;

    static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    public static async Task<UxPlay.Preflight> CheckAirPlayNetworkAsync()
    {
        var exe = Q(Tools.UxPlay ?? "");
        var script =
            "$n = @(Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetProfile } | " +
            "  ForEach-Object { $_.InterfaceAlias + '=' + $_.NetProfile.NetworkCategory }) -join '|'; " +
            $"$allow = @(Get-NetFirewallRule -DisplayName {Q(UxPlay.FirewallRule)} -ErrorAction SilentlyContinue | " +
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
            return new UxPlay.Preflight(pub, ok, nets.Replace("|", "; ").Replace("=", ": "));
        }
        catch (Exception ex) { return new UxPlay.Preflight([], true, "unknown (" + ex.Message + ")"); }
    }

    public static async Task<bool> FixAirPlayNetworkAsync(IEnumerable<string> makePrivate, bool firewall)
    {
        var cmd = string.Concat(makePrivate.Select(a =>
            $"Set-NetConnectionProfile -InterfaceAlias {Q(a)} -NetworkCategory Private -ErrorAction SilentlyContinue; "));
        if (firewall)
        {
            var exe = Q(Tools.UxPlay!);
            cmd += $"Get-NetFirewallApplicationFilter -Program {exe} -ErrorAction SilentlyContinue | Get-NetFirewallRule | Remove-NetFirewallRule; " +
                   $"Remove-NetFirewallRule -DisplayName {Q(UxPlay.FirewallRule)} -ErrorAction SilentlyContinue; " +
                   $"New-NetFirewallRule -DisplayName {Q(UxPlay.FirewallRule)} -Direction Inbound -Action Allow " +
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

    public static async Task RunAirPlayPreflightAsync(
        Window owner,
        bool interactive,
        Action<string> log,
        Action<string, (string Label, Action Action)[]> showProblem,
        Action hideProblem,
        Func<bool> canClearProblem)
    {
        var pf = await UxPlay.CheckAsync();
        log("Networks: " + (pf.Networks.Length > 0 ? pf.Networks : "none") +
            (pf.FirewallOk ? " · firewall OK" : " · firewall rule missing or blocked"));

        if (interactive && (pf.PublicNetworks.Count > 0 || !pf.FirewallOk))
        {
            var msg = "So that the iPhone/iPad can find this PC, Windows needs:\n\n";
            if (pf.PublicNetworks.Count > 0)
                msg += $"•  the network \"{string.Join("\", \"", pf.PublicNetworks)}\" set as Private (now it is Public). " +
                       "Do this only on your home network, not on public Wi-Fi.\n";
            if (!pf.FirewallOk) msg += "•  permission in the Windows firewall for the receiver.\n";
            msg += "\nFix it now? Windows will ask for administrator permission.";
            if (await MessageDialog.ShowAsync(owner, msg, "MirrorHub", yesNo: true))
            {
                if (await UxPlay.FixAsync(pf.PublicNetworks, !pf.FirewallOk)) pf = await UxPlay.CheckAsync();
                else log("Administrator permission not given: network and firewall unchanged.");
            }
        }

        var problems = new List<string>();
        if (pf.PublicNetworks.Count > 0)
            problems.Add($"The network \"{string.Join("\", \"", pf.PublicNetworks)}\" is Public: the iPhone will not see this PC.");
        if (!pf.FirewallOk)
            problems.Add("The Windows firewall does not allow the receiver: the iPhone may not see this PC.");
        if (problems.Count > 0)
            showProblem(string.Join("\n", problems), [("Fix (administrator)", () => _ = RunAirPlayPreflightAsync(owner, true, log, showProblem, hideProblem, canClearProblem))]);
        else if (canClearProblem())
            hideProblem();
    }

    public static (string Status, string Problem)? DiagnoseAirPlayFailure(IReadOnlyList<string> earlyLines, IReadOnlyCollection<string> tail)
    {
        // 0xC000001D = STATUS_ILLEGAL_INSTRUCTION: the build uses CPU instructions this PC lacks.
        // Changing the renderer cannot help.
        if (tail.Any(l => l.Contains("exited (code -1073741795)")))
        {
            return ("The receiver is not compatible with this PC's processor",
                    "The AirPlay receiver crashed because it uses processor instructions this PC does not support.\n\n" +
                    "Download the latest MirrorHub version. If it still happens, click \"Copy log\" and send it to the developer.");
        }
        return null;
    }

    // ---------- AirPlay renderer window retitling ----------

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowText(IntPtr hWnd, string text);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static async Task RetitleAirWindowAsync(Func<int?> getPid, string title, Func<bool> shouldContinue)
    {
        for (var i = 0; i < 40 && shouldContinue(); i++)
        {
            await Task.Delay(500);
            if (getPid() is not { } pid) continue;
            var found = false;
            EnumWindows((h, _) =>
            {
                if (GetWindowThreadProcessId(h, out var p) != 0 && p == pid && IsWindowVisible(h))
                {
                    SetWindowText(h, title);
                    found = true;
                }
                return true;
            }, IntPtr.Zero);
            if (found) return;
        }
    }
}
