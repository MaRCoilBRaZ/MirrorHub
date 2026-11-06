using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MirrorHub;

/// <summary>"iPhone / iPad" tab: the UxPlay AirPlay receiver, with checks and readable errors.</summary>
public partial class MainWindow
{
    static readonly Brush Gray = Brushes.DimGray;
    static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xB5, 0x6A, 0x00));
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34));
    static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xC0, 0x1C, 0x28));

    volatile bool _airReady;        // the current uxplay run printed its "ready" line
    bool _airEverReady;             // ... at least once since the user pressed Start
    bool _airBusy;                  // checks running before the launch
    bool _airTriedFallback;         // renderer already switched to "auto" once
    bool _airVideoBroken;           // UxPlay reported that it cannot build the video pipeline
    string _airName = "";
    readonly Queue<string> _airTail = new();   // last output lines, shown when something fails

    string ReadyText => $"Ready. On the iPhone/iPad: Control Center → Screen Mirroring → \"{_airName}\"";

    void SetAirStatus(string text, Brush color)
    {
        AirStatus.Text = text;
        AirStatus.Foreground = color;
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

    async Task StartAirPlay(bool interactive = true)
    {
        if (_airplay != null || _airBusy) { Log("AirPlay receiver already running."); return; }
        HideAirProblem();
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

        SaveSettings();
        _airBusy = true;
        try
        {
            SetAirStatus("Checking network and firewall…", Gray);
            await CheckAirPlayNetwork(interactive);
        }
        finally { _airBusy = false; }

        _airReady = _airEverReady = _airVideoBroken = false;
        _airTail.Clear();
        _airName = _settings.AirPlayName;
        var record = _settings.AirPlayRecord;
        var sink = _settings.AirPlayVideoSink;

        var session = UxPlay.Create(_settings, () => _airReady);
        session.Log += m =>
        {
            if (m.Contains(UxPlay.ReadyLine)) _airReady = true;
            Log("[AirPlay] " + m);
            Dispatcher.BeginInvoke(() => OnAirPlayLog(m));
        };
        session.Restarting += _ =>
        {
            _airReady = false;
            Dispatcher.BeginInvoke(() => SetAirStatus("The receiver closed, restarting it…", Orange));
        };
        session.Ended += unexpected => Dispatcher.BeginInvoke(() => OnAirPlayEnded(unexpected, record, sink));
        _airplay = session;
        SetAirStatus("Starting…", Orange);
        session.Start();
    }

    void OnAirPlayLog(string line)
    {
        _airTail.Enqueue(line);
        while (_airTail.Count > 8) _airTail.Dequeue();

        // A video pipeline that cannot be built (unknown renderer, missing plugin) does not make
        // UxPlay exit: it would accept the iPhone and then show nothing.
        if (line.Contains("failed to create video pipeline") && !_airVideoBroken)
        {
            _airVideoBroken = true;
            if (_settings.AirPlayVideoSink != "auto" && !_airTriedFallback) { _airplay?.Stop(); return; }   // → fallback
            SetAirStatus("The receiver cannot show video", Red);
            ShowAirProblem("A part of the receiver needed to show the video is missing or not supported on this PC.\n\n" +
                           "Click \"Copy log\" and send it to the developer.", ("Copy log", CopyLog));
            return;
        }
        if (line.Contains(UxPlay.ReadyLine))
        {
            if (_airVideoBroken) return;
            _airEverReady = true;
            _airTriedFallback = false;
            SetAirStatus(ReadyText, Green);
            return;
        }
        var m = Regex.Match(line, @"connection request from (.+?) \((.+?)\)");
        if (m.Success)
        {
            SetAirStatus($"Mirroring: {m.Groups[1].Value}", Green);
            Notify(_settings.Notify.AirPlayClient, "iPhone / iPad connected", $"{m.Groups[1].Value} ({m.Groups[2].Value})");
        }
        else if (line.Contains("lost connection with client"))
        {
            if (_airplay != null) SetAirStatus(ReadyText, Green);
            Notify(_settings.Notify.AirPlayClient, "AirPlay", "Connection to the iPhone / iPad lost.");
        }
    }

    async void OnAirPlayEnded(bool unexpected, bool record, string sink)
    {
        _airplay = null;
        if (record && _airEverReady) Notify(_settings.Notify.FileSaved, "AirPlay recording saved", _settings.OutputDir, OpenOutputFolder);
        var rendererFailed = _airVideoBroken && !_airEverReady;
        if (!unexpected && !rendererFailed) { SetAirStatus("Stopped", Gray); return; }

        await Task.Delay(300);   // the last output lines can arrive after the exit
        var tail = string.Join("\n", _airTail.Where(l => !l.Contains(" exited (code")).TakeLast(5));

        // 0xC000001D = STATUS_ILLEGAL_INSTRUCTION: the build uses CPU instructions this PC lacks.
        // Changing the renderer cannot help.
        if (_airTail.Any(l => l.Contains("exited (code -1073741795)")))
        {
            SetAirStatus("The receiver is not compatible with this PC's processor", Red);
            ShowAirProblem("The AirPlay receiver crashed because it uses processor instructions this PC does not support.\n\n" +
                           "Download the latest MirrorHub version. If it still happens, click \"Copy log\" and send it to the developer.",
                           ("Copy log", CopyLog));
            return;
        }

        if ((rendererFailed || !_airEverReady) && sink != "auto" && !_airTriedFallback)
        {
            // The usual cause on other PCs: the chosen renderer (e.g. D3D12) is not supported by the graphics card.
            _airTriedFallback = true;
            Log($"[AirPlay] Renderer \"{sink}\" did not start: switching to the automatic renderer and retrying.");
            _settings.AirPlayVideoSink = "auto";
            RebindAirPlay();
            await StartAirPlay(interactive: false);
            return;
        }

        Notify(_settings.Notify.MirrorEvents, "AirPlay", "The AirPlay receiver stopped unexpectedly.");
        SetAirStatus(_airEverReady ? "The receiver stopped and could not restart" : "The receiver could not start", Red);
        ShowAirProblem(
            (_airEverReady ? "The receiver stopped unexpectedly." : "The receiver closed right after starting.") +
            (tail.Length > 0 ? "\n\nLast messages:\n" + tail : "") +
            "\n\nIf it happens again, click \"Copy log\" and send it to the developer.",
            ("Try again", () => _ = StartAirPlay()), ("Copy log", CopyLog));
    }

    void ToggleAirPlay()
    {
        if (_airplay != null) _airplay.Stop();
        else _ = StartAirPlay();
    }

    async void AirStart_Click(object sender, RoutedEventArgs e) => await StartAirPlay();

    void AirStop_Click(object sender, RoutedEventArgs e) => _airplay?.Stop();

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
}
