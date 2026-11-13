using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace MirrorHub;

/// <summary>
/// "iPhone / iPad" tab: one or more UxPlay AirPlay receivers (one iPhone/iPad each, every one with its
/// own name and window), with checks and readable errors.
/// </summary>
public partial class MainWindow
{
    static readonly IBrush Gray = Brushes.DimGray;
    static readonly IBrush Orange = new SolidColorBrush(Color.FromRgb(0xB5, 0x6A, 0x00));
    static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34));
    static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(0xC0, 0x1C, 0x28));

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
        public string Key = "";            // UxPlay.SettingsKey it was started with
        public readonly Queue<string> Tail = new();         // last output lines, shown when something fails
        public readonly List<string> EarlyLines = new();    // first lines before Ready, for early failure diagnosis
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
    bool _airSelectAgain;           // restarted for new settings while an iPhone/iPad was connected: it must pick the receiver again

    string LogPrefix(AirReceiver r) => _air.Count > 1 || r.Slot > 1 ? $"[AirPlay {r.Slot}] " : "[AirPlay] ";

    void SetAirStatus(string text, IBrush color)
    {
        AirStatus.Text = text;
        AirStatus.Foreground = color;
    }

    /// <summary>Status line(s) from the state of every receiver.</summary>
    void UpdateAirStatus()
    {
        InvalidateTray();
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
                    ? (_airSelectAgain ? "New settings applied. " : "") +
                      $"Audio only, ready. On the iPhone/iPad: Control Center → Screen Mirroring → \"{r.Name}\" (all sounds), " +
                      $"or the AirPlay icon in a music/video app → \"{r.Name}\" (best quality)"
                    : _airSelectAgain
                        ? $"New settings applied. On the iPhone/iPad select it again: Control Center → Screen Mirroring → \"{r.Name}\""
                        : $"Ready. On the iPhone/iPad: Control Center → Screen Mirroring → \"{r.Name}\"";
            SetAirStatus(text, color);
            return;
        }

        var header = (_airSelectAgain ? "New settings applied: select the receiver again. " : "") + (_airAudioOnly
            ? "Audio only. On each iPhone/iPad: Control Center → Screen Mirroring (or the AirPlay icon in a music app) → a free name:"
            : "On each iPhone/iPad: Control Center → Screen Mirroring → a free name:");
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
        AirProblemActions.IsVisible = actions.Length > 0;
        AirProblem.IsVisible = true;
    }

    void HideAirProblem() => AirProblem.IsVisible = false;

    Task CheckAirPlayNetwork(bool interactive) =>
        Os.RunAirPlayPreflightAsync(this, interactive, Log, ShowAirProblem, HideAirProblem, () => AirStatus.Foreground != Red);

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
            ShowAirProblem(Tools.MissingUxPlayHint);
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
            if (Os.HasFirewallSetup) SetAirStatus("Checking network and firewall…", Gray);
            await CheckAirPlayNetwork(interactive);
        }
        finally { _airBusy = false; }

        UxPlay.LogUnsupportedOptions(_settings, audioOnly, Log);
        _airAudioOnly = audioOnly;
        _airRecord = _settings.AirPlayRecord && !audioOnly && UxPlay.SupportsRecording();
        _airSink = audioOnly ? "auto" : _settings.AirPlayVideoSink;   // no renderer fallback without video

        for (var slot = 1; slot <= count; slot++)
        {
            var r = new AirReceiver(slot, UxPlay.NameFor(_settings, slot)) { Key = UxPlay.SettingsKey(_settings, slot, audioOnly) };
            var session = UxPlay.Create(_settings, slot, audioOnly, () => r.Ready);
            session.Log += m =>
            {
                if (m.Contains(UxPlay.ReadyLine)) r.Ready = true;
                Log(LogPrefix(r) + m);
                Dispatcher.UIThread.Post(() => OnAirPlayLog(r, m));
            };
            session.Restarting += _ =>
            {
                r.Ready = false;
                Dispatcher.UIThread.Post(() => { r.Restarting = true; r.Client = null; UpdateAirStatus(); });
            };
            session.Ended += unexpected => Dispatcher.UIThread.Post(() => OnAirPlayEnded(r, unexpected));
            r.Process = session;
            _air.Add(r);
        }
        UpdateAirStatus();
        foreach (var r in _air.ToList()) r.Process!.Start();
    }

    void OnAirPlayLog(AirReceiver r, string line)
    {
        if (!r.EverReady && r.EarlyLines.Count < 100) r.EarlyLines.Add(line);
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
            _airSelectAgain = false;
            UpdateAirStatus();
            Notify(_settings.Notify.AirPlayClient, "iPhone / iPad connected", $"{r.Client} ({m.Groups[2].Value}) → \"{r.Name}\"");
            if (!_airAudioOnly)
                _ = Os.RetitleAirWindowAsync(() => r.Process?.ProcessId, $"MirrorHub - {r.Client} ({r.Name})", () => r.Client != null && _air.Contains(r));
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
        if (_air.Count == 0 && _airStartNext == null) _airSelectAgain = false;
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

        if (Os.DiagnoseAirPlayFailure(r.EarlyLines, r.Tail) is { } diagnosis)
        {
            StopAllAir();
            _airFailedStatus = diagnosis.Status;
            UpdateAirStatus();
            ShowAirProblem(diagnosis.Problem, ("Copy log", CopyLog));
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

    /// <summary>Restarts the receivers when a settings change alters how they run (quality, name, number…).</summary>
    void ApplyAirPlayChanges()
    {
        if (!AirRunning || _airBusy || _airStartNext != null) return;
        if (_settings.AirPlayPin.Length > 0 && !UxPlay.IsValidPin(_settings.AirPlayPin)) return;   // still typing it
        if (_air.Count == UxPlay.ReceiverCount(_settings) &&
            _air.All(r => r.Key == UxPlay.SettingsKey(_settings, r.Slot, _airAudioOnly))) return;

        var connected = _air.FirstOrDefault(r => r.Client != null);
        Log("AirPlay: settings changed, restarting the receivers with the new settings." +
            (connected != null ? $" If the mirroring does not resume, select \"{connected.Name}\" again on the iPhone/iPad." : ""));
        _airSelectAgain = connected != null;
        _airStartNext = _airAudioOnly;
        StopAllAir();
    }

    void StopAllAir()
    {
        foreach (var r in _air.ToList()) r.Process?.Stop();
    }

    /// <summary>Stops the receivers if they run in this mode, otherwise starts them (or switches to this mode).</summary>
    public void ToggleAirPlay(bool audioOnly = false)
    {
        if (AirRunning && _airAudioOnly == audioOnly) { _airStartNext = null; StopAllAir(); }
        else _ = StartAirPlay(audioOnly: audioOnly);
    }

    async void AirStart_Click(object? sender, RoutedEventArgs e) => await StartAirPlay();

    async void AirAudio_Click(object? sender, RoutedEventArgs e) => await StartAirPlay(audioOnly: true);

    void AirStop_Click(object? sender, RoutedEventArgs e) { _airStartNext = null; StopAllAir(); }

    async void Firewall_Click(object? sender, RoutedEventArgs e)
    {
        if (Tools.UxPlay == null) { ShowAirProblem(Tools.MissingUxPlayHint); return; }
        if (await UxPlay.FixAsync([], firewall: true))
        {
            Log("Firewall rule \"MirrorHub AirPlay (UxPlay)\" set (Private networks only).");
            await CheckAirPlayNetwork(false);
        }
        else Log("Firewall setup cancelled.");
    }
}
