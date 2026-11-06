using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MirrorHub;

/// <summary>
/// "iPhone / iPad" tab: one or more UxPlay AirPlay receivers (one iPhone/iPad each, every one with its
/// own name and window), with checks and readable errors.
/// </summary>
public partial class MainWindow
{
    static readonly Brush Gray = Brushes.DimGray;
    static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xB5, 0x6A, 0x00));
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34));
    static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xC0, 0x1C, 0x28));

    /// <summary>One running uxplay process.</summary>
    sealed class AirReceiver(int slot, string name)
    {
        public int Slot { get; } = slot;
        public string Name { get; } = name;
        public WatchedProcess? Process;
        public volatile bool Ready;        // the current run printed its "ready" line
        public bool EverReady;             // ... at least once since Start
        public bool VideoBroken;           // UxPlay reported that it cannot build the video pipeline
        public bool Restarting;
        public string? Client;             // name of the connected iPhone/iPad
        public readonly Queue<string> Tail = new();   // last output lines, shown when something fails
    }

    readonly List<AirReceiver> _air = new();
    bool AirRunning => _air.Count > 0;
    bool _airBusy;                  // checks running before the launch
    bool _airTriedFallback;         // renderer already switched to "auto" once
    bool _airAudioOnly;             // the running receivers play sound only (no video window)
    bool _airRecord;
    string _airSink = "auto";
    bool? _airStartNext;            // mode to start once every receiver has stopped (true = audio only)
    string? _airFailedStatus;       // red status shown when nothing is running after a failure

    string LogPrefix(AirReceiver r) => _air.Count > 1 || r.Slot > 1 ? $"[AirPlay {r.Slot}] " : "[AirPlay] ";

    void SetAirStatus(string text, Brush color)
    {
        AirStatus.Text = text;
        AirStatus.Foreground = color;
    }

    /// <summary>Status line(s) from the state of every receiver.</summary>
    void UpdateAirStatus()
    {
        if (_air.Count == 0)
        {
            if (_airFailedStatus != null) SetAirStatus(_airFailedStatus, Red);
            else SetAirStatus("Stopped", Gray);
            return;
        }

        string State(AirReceiver r) =>
            r.VideoBroken ? "cannot show video" :
            r.Restarting ? "closed, restarting it…" :
            !r.EverReady ? "starting…" :
            r.Client != null ? (_airAudioOnly ? "audio from " : "mirroring ") + r.Client :
            "free";
        var color = _air.Any(r => r.VideoBroken) ? Red : _air.Any(r => r.Restarting || !r.EverReady) ? Orange : Green;

        if (_air.Count == 1 && _air[0].Slot == 1)
        {
            var r = _air[0];
            var text =
                r.VideoBroken ? "The receiver cannot show video" :
                r.Restarting ? "The receiver closed, restarting it…" :
                !r.EverReady ? "Starting…" :
                r.Client != null ? $"{(_airAudioOnly ? "Audio only" : "Mirroring")}: {r.Client}" :
                _airAudioOnly
                    ? $"Audio only, ready. On the iPhone/iPad: Control Center → Screen Mirroring → \"{r.Name}\" (all sounds), " +
                      $"or the AirPlay icon in a music/video app → \"{r.Name}\" (best quality)"
                    : $"Ready. On the iPhone/iPad: Control Center → Screen Mirroring → \"{r.Name}\"";
            SetAirStatus(text, color);
            return;
        }

        var header = _airAudioOnly
            ? "Audio only. On each iPhone/iPad: Control Center → Screen Mirroring (or the AirPlay icon in a music app) → a free name:"
            : "On each iPhone/iPad: Control Center → Screen Mirroring → a free name:";
        SetAirStatus(header + "\n" + string.Join("\n", _air.Select(r => $"   •  \"{r.Name}\": {State(r)}")), color);
    }

    void ShowAirProblem(string text, params (string Label, Action Action)[] actions)
    {
        AirProblemText.Text = text;
        AirProblemActions.Children.Clear();
        foreach (var (label, action) in actions)
        {
            var b = new Button { Content = label };
            b.Click += (_, _) => action();
            AirProblemActions.Children.Add(b);
        }
        AirProblemActions.Visibility = actions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AirProblem.Visibility = Visibility.Visible;
    }

    void HideAirProblem() => AirProblem.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Network and firewall check. When <paramref name="interactive"/>, offers to fix what is wrong
    /// (one administrator prompt). Whatever is still wrong is shown in the problem box.
    /// </summary>
    async Task CheckAirPlayNetwork(bool interactive)
    {
        var pf = await UxPlay.CheckAsync();
        Log("Networks: " + (pf.Networks.Length > 0 ? pf.Networks : "none") +
            (pf.FirewallOk ? " · firewall OK" : " · firewall rule missing or blocked"));

        if (interactive && (pf.PublicNetworks.Count > 0 || !pf.FirewallOk))
        {
            var msg = "So that the iPhone/iPad can find this PC, Windows needs:\n\n";
            if (pf.PublicNetworks.Count > 0)
                msg += $"•  the network \"{string.Join("\", \"", pf.PublicNetworks)}\" set as Private (now it is Public). " +
                       "Do this only on your home network, not on public Wi-Fi.\n";
            if (!pf.FirewallOk) msg += "•  permission in the Windows firewall for the receiver.\n";
            msg += "\nFix it now? Windows will ask for administrator permission.";
            if (MessageBox.Show(this, msg, "MirrorHub", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                if (await UxPlay.FixAsync(pf.PublicNetworks, !pf.FirewallOk)) pf = await UxPlay.CheckAsync();
                else Log("Administrator permission not given: network and firewall unchanged.");
            }
        }

        var problems = new List<string>();
        if (pf.PublicNetworks.Count > 0)
            problems.Add($"The network \"{string.Join("\", \"", pf.PublicNetworks)}\" is Public: the iPhone will not see this PC.");
        if (!pf.FirewallOk)
            problems.Add("The Windows firewall does not allow the receiver: the iPhone may not see this PC.");
        if (problems.Count > 0)
            ShowAirProblem(string.Join("\n", problems), ("Fix (administrator)", () => _ = CheckAirPlayNetwork(true)));
        else if (AirStatus.Foreground != Red)
            HideAirProblem();
    }

    async Task StartAirPlay(bool interactive = true, bool audioOnly = false)
    {
        if (_airBusy) return;
        var count = UxPlay.ReceiverCount(_settings);
        if (AirRunning)
        {
            if (_airAudioOnly == audioOnly && _air.Count == count) { Log("AirPlay receiver already running."); return; }
            // Other mode or other number of receivers: restart them all.
            Log(_airAudioOnly != audioOnly
                ? (audioOnly ? "AirPlay: switching to audio only." : "AirPlay: switching to screen mirroring.")
                : "AirPlay: restarting the receivers.");
            _airStartNext = audioOnly;
            StopAllAir();
            return;
        }
        HideAirProblem();
        _airFailedStatus = null;
        if (Tools.UxPlay == null)
        {
            SetAirStatus("Receiver not available", Red);
            ShowAirProblem(Tools.MissingToolsHint);
            return;
        }
        if (_settings.AirPlayPin.Length > 0 && !UxPlay.IsValidPin(_settings.AirPlayPin))
        {
            SetAirStatus("Not started", Red);
            ShowAirProblem("The PIN (Advanced settings) must be 4 digits, 0001-9999, or empty for no PIN.");
            return;
        }
        if (!audioOnly && _settings.AirPlayFps > UxPlay.MaxFps)
            Log($"AirPlay: Max FPS {_settings.AirPlayFps} is more than iPhones send when mirroring, using {UxPlay.MaxFps}.");

        SaveSettings();
        _airBusy = true;
        try
        {
            SetAirStatus("Checking network and firewall…", Gray);
            await CheckAirPlayNetwork(interactive);
        }
        finally { _airBusy = false; }

        _airAudioOnly = audioOnly;
        _airRecord = _settings.AirPlayRecord && !audioOnly;
        _airSink = audioOnly ? "auto" : _settings.AirPlayVideoSink;   // no renderer fallback without video

        for (var slot = 1; slot <= count; slot++)
        {
            var r = new AirReceiver(slot, UxPlay.NameFor(_settings, slot));
            var session = UxPlay.Create(_settings, slot, audioOnly, () => r.Ready);
            session.Log += m =>
            {
                if (m.Contains(UxPlay.ReadyLine)) r.Ready = true;
                Log(LogPrefix(r) + m);
                Dispatcher.BeginInvoke(() => OnAirPlayLog(r, m));
            };
            session.Restarting += _ =>
            {
                r.Ready = false;
                Dispatcher.BeginInvoke(() => { r.Restarting = true; r.Client = null; UpdateAirStatus(); });
            };
            session.Ended += unexpected => Dispatcher.BeginInvoke(() => OnAirPlayEnded(r, unexpected));
            r.Process = session;
            _air.Add(r);
        }
        UpdateAirStatus();
        foreach (var r in _air.ToList()) r.Process!.Start();
    }

    void OnAirPlayLog(AirReceiver r, string line)
    {
        r.Tail.Enqueue(line);
        while (r.Tail.Count > 8) r.Tail.Dequeue();

        // A video pipeline that cannot be built (unknown renderer, missing plugin) does not make
        // UxPlay exit: it would accept the iPhone and then show nothing.
        if (line.Contains("failed to create video pipeline") && !r.VideoBroken)
        {
            r.VideoBroken = true;
            if (!_airAudioOnly && _airSink != "auto" && !_airTriedFallback) { r.Process?.Stop(); return; }   // → fallback
            UpdateAirStatus();
            ShowAirProblem("A part of the receiver needed to show the video is missing or not supported on this PC.\n\n" +
                           "Click \"Copy log\" and send it to the developer.", ("Copy log", CopyLog));
            return;
        }
        if (line.Contains(UxPlay.ReadyLine))
        {
            if (r.VideoBroken) return;
            r.EverReady = true;
            r.Restarting = false;
            _airTriedFallback = false;
            UpdateAirStatus();
            return;
        }
        var m = Regex.Match(line, @"connection request from (.+?) \((.+?)\)");
        if (m.Success)
        {
            r.Client = m.Groups[1].Value;
            UpdateAirStatus();
            Notify(_settings.Notify.AirPlayClient, "iPhone / iPad connected", $"{r.Client} ({m.Groups[2].Value}) → \"{r.Name}\"");
            if (!_airAudioOnly) _ = RetitleAirWindow(r);
        }
        else if (line.Contains("lost connection with client"))
        {
            r.Client = null;
            UpdateAirStatus();
            Notify(_settings.Notify.AirPlayClient, "AirPlay", $"Connection to the iPhone / iPad lost (\"{r.Name}\").");
        }
    }

    async void OnAirPlayEnded(AirReceiver r, bool unexpected)
    {
        _air.Remove(r);
        if (_airRecord && r.EverReady) Notify(_settings.Notify.FileSaved, "AirPlay recording saved", _settings.OutputDir, OpenOutputFolder);
        if (_airStartNext is { } next)
        {
            if (_air.Count > 0) return;   // wait for the others to stop
            _airStartNext = null;
            await StartAirPlay(interactive: false, audioOnly: next);
            return;
        }
        var rendererFailed = r.VideoBroken && !r.EverReady;
        if (!unexpected && !rendererFailed) { UpdateAirStatus(); return; }

        await Task.Delay(300);   // the last output lines can arrive after the exit
        var tail = string.Join("\n", r.Tail.Where(l => !l.Contains(" exited (code")).TakeLast(5));
        var who = _air.Count > 0 || r.Slot > 1 ? $"The receiver \"{r.Name}\"" : "The receiver";

        // 0xC000001D = STATUS_ILLEGAL_INSTRUCTION: the build uses CPU instructions this PC lacks.
        // Changing the renderer cannot help.
        if (r.Tail.Any(l => l.Contains("exited (code -1073741795)")))
        {
            StopAllAir();
            _airFailedStatus = "The receiver is not compatible with this PC's processor";
            UpdateAirStatus();
            ShowAirProblem("The AirPlay receiver crashed because it uses processor instructions this PC does not support.\n\n" +
                           "Download the latest MirrorHub version. If it still happens, click \"Copy log\" and send it to the developer.",
                           ("Copy log", CopyLog));
            return;
        }

        if ((rendererFailed || !r.EverReady) && _airSink != "auto" && !_airTriedFallback)
        {
            // The usual cause on other PCs: the chosen renderer (e.g. D3D12) is not supported by the graphics card.
            _airTriedFallback = true;
            Log($"[AirPlay] Renderer \"{_airSink}\" did not start: switching to the automatic renderer and retrying.");
            _settings.AirPlayVideoSink = "auto";
            RebindAirPlay();
            if (_air.Count == 0) await StartAirPlay(interactive: false, audioOnly: _airAudioOnly);
            else { _airStartNext = _airAudioOnly; StopAllAir(); }
            return;
        }

        Notify(_settings.Notify.MirrorEvents, "AirPlay", $"{who} stopped unexpectedly.");
        if (_air.Count == 0)
            _airFailedStatus = r.EverReady ? "The receiver stopped and could not restart" : "The receiver could not start";
        UpdateAirStatus();
        ShowAirProblem(
            (r.EverReady ? $"{who} stopped unexpectedly." : $"{who} closed right after starting.") +
            (tail.Length > 0 ? "\n\nLast messages:\n" + tail : "") +
            "\n\nIf it happens again, click \"Copy log\" and send it to the developer.",
            ("Try again", () => _ = StartAirPlay(audioOnly: _airAudioOnly)), ("Copy log", CopyLog));
    }

    void StopAllAir()
    {
        foreach (var r in _air.ToList()) r.Process?.Stop();
    }

    /// <summary>Stops the receivers if they run in this mode, otherwise starts them (or switches to this mode).</summary>
    void ToggleAirPlay(bool audioOnly = false)
    {
        if (AirRunning && _airAudioOnly == audioOnly) { _airStartNext = null; StopAllAir(); }
        else _ = StartAirPlay(audioOnly: audioOnly);
    }

    async void AirStart_Click(object sender, RoutedEventArgs e) => await StartAirPlay();

    async void AirAudio_Click(object sender, RoutedEventArgs e) => await StartAirPlay(audioOnly: true);

    void AirStop_Click(object sender, RoutedEventArgs e) { _airStartNext = null; StopAllAir(); }

    async void Firewall_Click(object sender, RoutedEventArgs e)
    {
        if (Tools.UxPlay == null) { ShowAirProblem(Tools.MissingToolsHint); return; }
        if (await UxPlay.FixAsync([], firewall: true))
        {
            Log("Firewall rule \"MirrorHub AirPlay (UxPlay)\" set (Private networks only).");
            await CheckAirPlayNetwork(false);
        }
        else Log("Firewall setup cancelled.");
    }

    // ---------- video window title ----------

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowText(IntPtr hWnd, string text);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// The renderer window opens a moment after the iPhone connects and has a generic title; name it
    /// after the receiver and the iPhone, so several windows can be told apart.
    /// </summary>
    async Task RetitleAirWindow(AirReceiver r)
    {
        for (var i = 0; i < 40 && r.Client != null && _air.Contains(r); i++)
        {
            await Task.Delay(500);
            if (r.Process?.ProcessId is not { } pid) continue;
            var title = $"MirrorHub - {r.Client} ({r.Name})";
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
