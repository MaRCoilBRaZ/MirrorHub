using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using QRCoder;

namespace MirrorHub;

public sealed record DeviceItem(Device Device, bool Mirroring, bool AudioOnly, bool HasProfile)
{
    public string Label =>
        $"{(AudioOnly ? "♪ " : Mirroring ? "▶ " : "")}{Device.Model}  ·  {(Device.IsWifi ? "Wi-Fi" : "USB")}" +
        (HasProfile ? "  ·  profile" : "") +
        (Device.State != "device" ? $"  ·  {Device.State}" : "") +
        $"\n{Device.Serial}";
}

public sealed record ProfileItem(string? Key, string Label);

public partial class MainWindow : Window
{
    readonly Settings _settings = Settings.Load();
    readonly Dictionary<string, WatchedProcess> _sessions = new();   // adb serial -> screen mirror or audio only
    readonly HashSet<string> _audioOnly = new();                      // serials in _sessions that are audio only
    readonly Dictionary<string, (Device Device, bool AudioOnly, string Key)> _started = new();   // serial -> what the session runs with
    readonly HashSet<string> _restartWithNewSettings = new();        // serials stopped to start again with the new settings
    readonly Dictionary<string, string> _hwIds = new();              // adb serial -> hardware id
    readonly Dictionary<string, int> _sdks = new();                  // adb serial -> Android API level
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly IGlobalHotkeys _hotkeys;
    readonly IDisposable? _signalReg;
    readonly HashSet<string> _noPermLogged = new();
    List<Device> _devices = [];
    Dictionary<string, Device> _online = new();
    bool _refreshing, _firstRefresh = true, _exiting, _cleanedUp;
    CameraWindow? _camera;

    public MainWindow()
    {
        InitializeComponent();

        VideoCodecBox.ItemsSource = new[] { "h264", "h265", "av1" };
        AudioCodecBox.ItemsSource = new[] { "opus", "aac", "flac", "raw" };
        HwDecBox.ItemsSource = Os.HwDecoders;
        RenderFitBox.ItemsSource = new[] { "letterbox", "stretched", "unscaled" };
        var airResFlyout = new MenuFlyout();
        foreach (var res in new[] { "1280x720@60", "1920x1080@60", "2560x1440@60", "3840x2160@60" })
        {
            var r = res;
            var mi = new MenuItem { Header = r };
            mi.Click += (_, _) =>
            {
                _settings.AirPlayResolution = r;
                RebindAirPlay();
                SaveSettings();
                UpdateQualityChoices();
                QueueApplySettings();
            };
            airResFlyout.Items.Add(mi);
        }
        AirResPickBtn.Flyout = airResFlyout;
        AirSinkBox.ItemsSource = Os.AirPlaySinks;
        AirCountBox.ItemsSource = Enumerable.Range(1, UxPlay.MaxReceivers).ToArray();

        StartAtLoginCheck.Content = Os.StartAtLoginLabel;
        MinimizedAtLogonItem.Content = OperatingSystem.IsWindows() ? "Only when started by Windows" : "Only when started at login";
        FirewallPanel.IsVisible = Os.HasFirewallSetup;
        AirAdvancedHint.Text = Os.AirPlayAdvancedHint;
        SettingsPathHint.Text = $"Options apply immediately. Settings are stored in {Os.SettingsPathHint}.";

        AndroidDropGrid.AddHandler(DragDrop.DragOverEvent, Android_DragOver);
        AndroidDropGrid.AddHandler(DragDrop.DropEvent, Android_Drop);

        DataContext = _settings;
        MinimizedModeBox.SelectedIndex = (int)_settings.MinimizedMode;
        BuildQualityChoices();
        RebuildProfiles(null);

        _hotkeys = Os.CreateHotkeys();
        BuildHotkeyEditors();
        RegisterHotkeys();
        InitTray();
        UpdateTrayAvailabilityUi();
        _signalReg = Os.RegisterShutdownSignals(() => Dispatcher.UIThread.Post(ExitApp));

        try { Os.ApplyAutostart(_settings.StartWithWindows); }
        catch (Exception ex) { Log($"Start with {Os.Name} not updated: " + ex.Message); }

        Log($"adb: {Tools.Adb}");
        Log(Tools.Scrcpy != null
            ? $"scrcpy: {Tools.Scrcpy}"
            : OperatingSystem.IsWindows()
                ? "scrcpy not found. Install it with: winget install Genymobile.scrcpy"
                : "scrcpy not found. Install scrcpy with your package manager or place it in tools/scrcpy/.");
        Log(Tools.UxPlay != null
            ? $"uxplay: {Tools.UxPlay}"
            : "uxplay not found: iPhone/iPad mirroring unavailable.");
        if (Tools.Scrcpy == null && Tools.UxPlay == null)
        {
            var hint = OperatingSystem.IsWindows()
                ? Tools.MissingToolsHint
                : Tools.MissingToolsHint + "\n\n" + Tools.MissingUxPlayHint;
            Dispatcher.UIThread.Post(() => _ = MessageDialog.ShowAsync(this, hint, "MirrorHub"));
        }

        _poll.Tick += async (_, _) => await RefreshDevices();
        Dispatcher.UIThread.Post(async () =>
        {
            await RefreshDevices();
            _poll.Start();
            if (Startup.HasArg(Startup.AirAudioArg)) await StartAirPlay(interactive: false, audioOnly: true);
            else if (_settings.AirPlayAutoStart || Startup.HasArg(Startup.AirPlayArg)) await StartAirPlay(interactive: false);

            if (Program.PendingStartupCommand is { } cmdName && AppCommands.FindByName(cmdName) is { } cmd)
                cmd.Execute(this);
        });
    }

    // ---------- window / startup ----------

    void UpdateTrayAvailabilityUi()
    {
        var available = _tray?.IsAvailable ?? true;
        MinimizeToTrayCheck.IsEnabled = available;
        CloseToTrayCheck.IsEnabled = available;
        TrayUsageHint.IsVisible = available;
        NoTrayHint.IsVisible = !available;
    }

    bool CheckTrayAvailable()
    {
        var available = _tray?.RefreshAvailability() ?? true;
        UpdateTrayAvailabilityUi();
        return available;
    }

    /// <summary>Shows the window, or keeps it minimized / hidden according to the settings. True if Show() was called.</summary>
    public bool ShowAtStartup()
    {
        var trayAvailable = _tray?.IsAvailable ?? true;
        if (Startup.HasArg(Startup.RestartArg))
        {
            if (Startup.HasArg(Startup.ShowArg) || !_settings.MinimizeToTray || !trayAvailable) { Show(); return true; }
            return false;
        }
        var minimized = _settings.MinimizedMode == StartMinimizedMode.Always ||
                        (_settings.MinimizedMode == StartMinimizedMode.AtLogon && Startup.LaunchedAtLogon);
        if (!minimized) { Show(); return true; }
        if (_settings.MinimizeToTray && trayAvailable) return false;    // stays in the notification area only
        WindowState = WindowState.Minimized;
        Show();
        return true;
    }

    public void ShowFromTray()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d && d.MainWindow == null)
            d.MainWindow = this;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void ToggleWindow()
    {
        if (IsVisible && WindowState != WindowState.Minimized && IsActive) Hide();
        else ShowFromTray();
    }

    public void ExitApp()
    {
        _exiting = true;
        Cleanup();
        LaunchRestartIfRequested();
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    public void OnAppShutdown() => Cleanup();

    string[]? _restartArgs;   // set by RestartApp: start a new instance with these once we have closed

    /// <summary>Closes MirrorHub and opens it again, with the window and the AirPlay receiver as they are now.</summary>
    void RestartApp()
    {
        var args = new List<string> { Startup.RestartArg };
        if (IsVisible && WindowState != WindowState.Minimized) args.Add(Startup.ShowArg);
        if (AirRunning) args.Add(_airAudioOnly ? Startup.AirAudioArg : Startup.AirPlayArg);
        _restartArgs = args.ToArray();
        Log("Restarting MirrorHub…");
        ExitApp();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && WindowState == WindowState.Minimized &&
            _settings.MinimizeToTray && CheckTrayAvailable())
            Hide();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
        {
            Cleanup();
            base.OnClosing(e);
            return;
        }
        if (!_exiting && _settings.CloseToTray && CheckTrayAvailable())
        {
            e.Cancel = true;
            Hide();
            ShowTrayHintOnce();
            return;
        }

        Cleanup();
        base.OnClosing(e);
        LaunchRestartIfRequested();
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        _exiting = true;
        _poll.Stop();
        _signalReg?.Dispose();
        _hotkeys.Clear();
        (_hotkeys as IDisposable)?.Dispose();
        try { _settings.Save(); } catch { }
        foreach (var s in _sessions.Values.ToList()) s.Stop();
        StopAllAir();
        _camera?.Shutdown();
        Adb.StopBundledServer();
        Os.KillChildren();
        DisposeTray();
    }

    void LaunchRestartIfRequested()
    {
        if (_restartArgs == null || Environment.ProcessPath is not { } exe) return;
        var args = _restartArgs;
        _restartArgs = null;
        try { Process.Start(exe, args); }
        catch (Exception ex) { _ = MessageDialog.ShowAsync(null, "MirrorHub could not restart: " + ex.Message, "MirrorHub"); }
    }

    // ---------- helpers ----------

    void Log(string msg) => Dispatcher.UIThread.Post(() =>
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {msg}\n";
        var cur = LogBox.Text ?? "";
        if (cur.Length > 100_000) cur = cur[^50_000..];
        var next = cur + line;
        LogBox.Text = next;
        LogBox.CaretIndex = next.Length;
        LogFile.Append(line);
    });

    async void CopyLog()
    {
        var header = $"MirrorHub {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} ·{Environment.OSVersion.VersionString}\n" +
                     $"Folder: {AppContext.BaseDirectory}\n";
        try
        {
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip != null)
            {
                await clip.SetTextAsync(header + (LogBox.Text ?? ""));
                Log("Log copied: paste it in a message (Ctrl+V).");
            }
        }
        catch (Exception ex) { Log("Could not copy the log: " + ex.Message); }
    }

    void CopyLog_Click(object? sender, RoutedEventArgs e) => CopyLog();

    /// <summary>The selected device if online, otherwise the first online one.</summary>
    Device? TargetDevice()
    {
        if (DeviceList.SelectedItem is DeviceItem { Device.State: "device" } item) return item.Device;
        var d = _devices.FirstOrDefault(x => x.State == "device");
        if (d == null) Log("No Android device connected.");
        return d;
    }

    Device? Selected()
    {
        if (DeviceList.SelectedItem is DeviceItem item) return item.Device;
        Log("Select a device.");
        return null;
    }

    async Task<string?> HwIdAsync(Device d)
    {
        if (_hwIds.TryGetValue(d.Serial, out var id)) return id;
        if (d.State != "device") return null;
        id = await Adb.HardwareIdAsync(d.Serial);
        if (id != null) _hwIds[d.Serial] = id;
        if (await Adb.SdkAsync(d.Serial) is { } sdk) _sdks[d.Serial] = sdk;
        // Known before an automatic mirror starts with a codec the phone may lack in hardware.
        if (id != null) await VideoEncodersIfNeededAsync(d.Serial, id);
        return id;
    }

    /// <summary>
    /// Lists the phone's video encoders (once per run, about 1 s) when its options use a codec other than H.264.
    /// H.264 is never replaced, so with it nothing is listed and the connection is not slowed down.
    /// </summary>
    async Task VideoEncodersIfNeededAsync(string serial, string hwId)
    {
        if (_settings.OptionsFor(hwId).VideoCodec == "h264" || Scrcpy.CachedVideoEncoders(hwId) != null) return;
        await Scrcpy.VideoEncodersAsync(serial, hwId);
        UpdateCodecHint();
    }

    /// <summary>
    /// Warns when the options shown use a codec that the phone they apply to lacks in hardware
    /// (<see cref="EffectiveOptions"/> replaces it at start).
    /// </summary>
    void UpdateCodecHint()
    {
        string? hw = null, name = null;
        if (ProfileBox.SelectedItem is ProfileItem { Key: { } key })
            (hw, name) = (key, _settings.Profiles.GetValueOrDefault(key)?.Name);
        else if (DeviceList.SelectedItem is DeviceItem item && CachedHwId(item.Device.Serial) is { } id && !_settings.Profiles.ContainsKey(id))
            (hw, name) = (id, item.Device.Model);   // the defaults apply to the selected phone

        var codec = CurrentMirrorOptions.VideoCodec;
        var fallback = hw == null ? null : Scrcpy.CachedVideoEncoders(hw)?.FallbackFor(codec);
        CodecHint.Text = fallback == null ? "" :
            $"{name}: no hardware {Scrcpy.CodecLabel(codec)} encoder on this phone, so {Scrcpy.CodecLabel(fallback)} will be used.";
        CodecHint.IsVisible = fallback != null;
    }

    string? CachedHwId(string serial) => _hwIds.GetValueOrDefault(serial);

    /// <summary>Serial of the running mirror of this phone (also matches the same phone on USB vs Wi-Fi).</summary>
    string? SessionSerialFor(Device d)
    {
        if (_sessions.ContainsKey(d.Serial)) return d.Serial;
        var hw = CachedHwId(d.Serial);
        return hw == null ? null : _sessions.Keys.FirstOrDefault(s => CachedHwId(s) == hw);
    }

    /// <summary>True for any running session of this phone: screen mirror or audio only.</summary>
    bool IsMirroring(Device d) => SessionSerialFor(d) != null;

    bool IsAudioOnly(Device d) => SessionSerialFor(d) is { } s && _audioOnly.Contains(s);

    void OpenOutputFolder()
    {
        Directory.CreateDirectory(_settings.OutputDir);
        Os.OpenFolder(_settings.OutputDir, Log);
    }

    void OpenFile(string path) => Os.OpenFile(path, Log);

    // ---------- devices ----------

    async Task RefreshDevices()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var devices = await Adb.DevicesAsync();
            foreach (var d in devices)
            {
                if (d.State == "no permissions" && _noPermLogged.Add(d.Serial) &&
                    Os.NoUsbPermissionHint(d.Serial) is { } hint)
                    Log(hint);
            }
            _devices = devices;
            var online = devices.Where(d => d.State == "device").ToDictionary(d => d.Serial);
            foreach (var d in online.Values) await HwIdAsync(d);

            if (_firstRefresh)
            {
                foreach (var d in online.Values) AutoMirrorIfEnabled(d);   // already connected at launch
            }
            else
            {
                foreach (var d in online.Values.Where(d => !_online.ContainsKey(d.Serial))) OnDeviceConnected(d);
                foreach (var d in _online.Values.Where(d => !online.ContainsKey(d.Serial))) OnDeviceDisconnected(d);
            }
            _firstRefresh = false;
            _online = online;

            var selected = (DeviceList.SelectedItem as DeviceItem)?.Device.Serial;
            var items = devices
                .Select(d => new DeviceItem(d, IsMirroring(d), IsAudioOnly(d),
                    CachedHwId(d.Serial) is { } hw && _settings.Profiles.ContainsKey(hw)))
                .ToList();
            var current = DeviceList.ItemsSource as IEnumerable<DeviceItem> ?? [];
            if (items.SequenceEqual(current)) return;   // avoid flicker / selection loss
            DeviceList.ItemsSource = items;
            DeviceList.SelectedItem = items.FirstOrDefault(i => i.Device.Serial == selected) ?? items.FirstOrDefault();
            InvalidateTray();
        }
        finally { _refreshing = false; }
    }

    void OnDeviceConnected(Device d)
    {
        Log($"Connected: {d.Display}");
        Notify(_settings.Notify.DeviceConnected, "Phone connected", d.Display);
        AutoMirrorIfEnabled(d);
    }

    void OnDeviceDisconnected(Device d)
    {
        Log($"Disconnected: {d.Display}");
        Notify(_settings.Notify.DeviceDisconnected, "Phone disconnected", d.Display);
        _hwIds.Remove(d.Serial);
        _sdks.Remove(d.Serial);
    }

    void AutoMirrorIfEnabled(Device d)
    {
        if (!_settings.OptionsFor(CachedHwId(d.Serial)).AutoMirror || IsMirroring(d)) return;
        Log($"Auto-mirror: {d.Display}");
        StartMirror(d);
    }

    void DeviceList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Show the settings that apply to the selected device.
        if (DeviceList.SelectedItem is not DeviceItem item) return;
        var hw = CachedHwId(item.Device.Serial);
        var key = hw != null && _settings.Profiles.ContainsKey(hw) ? hw : null;
        if ((ProfileBox.SelectedItem as ProfileItem)?.Key != key) RebuildProfiles(key);
        UpdateCodecHint();
    }

    // ---------- mirroring ----------

    bool StartMirror(Device d, string? startApp = null, bool audioOnly = false)
    {
        if (Tools.Scrcpy == null) { Log("scrcpy not found."); return false; }
        if (d.State != "device")
        {
            Log($"{d.Model}: state \"{d.State}\". Unlock the phone and accept the USB debugging prompt.");
            return false;
        }
        if (IsMirroring(d))
        {
            Log(IsAudioOnly(d)
                ? $"{d.Model}: audio only is running. Stop it first to start screen mirroring (which also has sound)."
                : $"{d.Model} is already being mirrored{(audioOnly ? " (with sound, if \"Phone sound on the PC\" is on)" : "")}.");
            return false;
        }

        try { _settings.Save(); } catch { }
        if (EffectiveOptions(d, audioOnly, log: true) is not { } options) return false;
        var session = audioOnly ? Scrcpy.CreateAudioOnly(d, options) : Scrcpy.Create(d, options, _settings.OutputDir, startApp);
        session.Log += m => Log($"[{d.Model}] {m}");
        session.Restarting += n =>
        {
            if (n == 1) Notify(_settings.Notify.MirrorEvents, "Connection lost", $"{d.Model}: reconnecting…");
        };
        session.Ended += unexpected => Dispatcher.UIThread.Post(() =>
        {
            _sessions.Remove(d.Serial);
            _audioOnly.Remove(d.Serial);
            _started.Remove(d.Serial);
            if (options.Record && !audioOnly) Notify(_settings.Notify.FileSaved, "Recording saved", _settings.OutputDir, OpenOutputFolder);
            if (_restartWithNewSettings.Remove(d.Serial) && StartMirror(d, audioOnly: audioOnly)) return;
            if (unexpected) Notify(_settings.Notify.MirrorEvents, audioOnly ? "Phone audio stopped" : "Mirroring stopped",
                $"{d.Model}: could not reconnect.");
            InvalidateTray();
            _ = RefreshDevices();
        });
        _sessions[d.Serial] = session;
        _started[d.Serial] = (d, audioOnly, Scrcpy.SettingsKey(d, options, audioOnly));
        if (audioOnly)
        {
            _audioOnly.Add(d.Serial);
            Log($"{d.Model}: audio only (no video, no window). Stop it with ■ Stop or from the icon menu.");
        }
        session.Start();
        InvalidateTray();
        _ = RefreshDevices();
        return true;
    }

    /// <summary>The options a mirror of this device starts with, or null when it must not start.</summary>
    MirrorOptions? EffectiveOptions(Device d, bool audioOnly, bool log)
    {
        var hw = CachedHwId(d.Serial);
        var options = _settings.OptionsFor(hw);
        // Sound on both needs Android 13+. On older devices the option means "leave the sound on the device".
        if (options.AudioOnPhoneToo && _sdks.TryGetValue(d.Serial, out var sdk) && sdk < 33)
        {
            if (audioOnly)
            {
                if (log)
                    Log($"{d.Model}: Android older than 13 can't play sound on both, and \"Also keep the sound on the phone\" " +
                        "is on, so the sound stays on the device. Turn that option off to hear it on the PC.");
                return null;
            }
            options = options.Clone();
            options.Audio = false;
            if (log)
                Log($"{d.Model}: Android older than 13 can't play sound on both, so the sound stays on the device " +
                    "(\"Also keep the sound on the phone\" is on).");
        }
        // A codec the phone encodes only in software (or not at all) makes the mirror unusable: use one it
        // has in hardware. The saved settings are kept, for phones that do have it.
        if (!audioOnly && hw != null && Scrcpy.CachedVideoEncoders(hw)?.FallbackFor(options.VideoCodec) is { } codec)
        {
            if (log)
                Log($"{d.Model}: no hardware {Scrcpy.CodecLabel(options.VideoCodec)} encoder on this phone, " +
                    $"using {Scrcpy.CodecLabel(codec)} instead.");
            options = options.Clone();
            options.VideoCodec = codec;
        }
        return options;
    }

    /// <summary>Restarts the running mirrors whose settings changed since they started.</summary>
    void ApplyAndroidChanges()
    {
        foreach (var (serial, (d, audioOnly, key)) in _started.ToList())
        {
            var now = EffectiveOptions(d, audioOnly, log: false) is { } o ? Scrcpy.SettingsKey(d, o, audioOnly) : null;
            if (now == key || !_sessions.TryGetValue(serial, out var session) || !_restartWithNewSettings.Add(serial)) continue;
            Log($"{d.Model}: settings changed, restarting {(audioOnly ? "audio only" : "the mirror")} with the new settings.");
            session.Stop();
        }
    }

    public void ToggleAudioOnly(Device? d = null)
    {
        d ??= TargetDevice();
        if (d == null) return;
        if (IsAudioOnly(d)) _sessions[SessionSerialFor(d)!].Stop();
        else StartMirror(d, audioOnly: true);
    }

    void AudioOnly_Click(object? sender, RoutedEventArgs e) => ToggleAudioOnly();

    public void ToggleMirror(Device? d = null)
    {
        d ??= TargetDevice();
        if (d == null) return;
        if (SessionSerialFor(d) is { } serial && !IsAudioOnly(d)) _sessions[serial].Stop();
        else StartMirror(d);   // explains what to do if audio only is running
    }

    void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (TargetDevice() is { } d) StartMirror(d);
    }

    void Stop_Click(object? sender, RoutedEventArgs e)
    {
        if (TargetDevice() is { } d && SessionSerialFor(d) is { } serial) _sessions[serial].Stop();
    }

    public async Task TakeScreenshot(Device? d = null)
    {
        d ??= TargetDevice();
        if (d == null) return;
        Directory.CreateDirectory(_settings.OutputDir);
        var file = Path.Combine(_settings.OutputDir, $"{Tools.SafeName(d.Model)}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        if (await Adb.ScreencapAsync(d.Serial, file))
        {
            Log($"Screenshot: {file}");
            Notify(_settings.Notify.FileSaved, "Screenshot saved", Path.GetFileName(file), () => OpenFile(file));
        }
        else Log("Screenshot failed.");
    }

    async void Screenshot_Click(object? sender, RoutedEventArgs e) => await TakeScreenshot();

    void OpenFolder_Click(object? sender, RoutedEventArgs e) => OpenOutputFolder();

    // ---------- profiles ----------

    void RebuildProfiles(string? select)
    {
        var items = new List<ProfileItem> { new(null, "Defaults (all devices without a profile)") };
        items.AddRange(_settings.Profiles
            .OrderBy(p => p.Value.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new ProfileItem(p.Key, $"Profile: {p.Value.Name}  ·  {p.Key}")));
        ProfileBox.ItemsSource = items;
        ProfileBox.SelectedItem = items.FirstOrDefault(i => i.Key == select) ?? items[0];
    }

    void ProfileBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ProfileItem item) return;
        MirrorOptionsPanel.DataContext = _settings.OptionsFor(item.Key);
        ProfileNote.Text = item.Key != null ? $"These are the settings of the profile \"{_settings.Profiles[item.Key].Name}\"." : "";
        ProfileNote.IsVisible = item.Key != null;
        UpdateQualityChoices();
    }

    async void CreateProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (TargetDevice() is not { } d) return;
        var hw = await HwIdAsync(d);
        if (hw == null) { Log($"{d.Model}: hardware ID not available."); return; }
        if (!_settings.Profiles.ContainsKey(hw))
        {
            _settings.Profiles[hw] = new DeviceProfile { Name = d.Model, Options = _settings.Mirror.Clone() };
            _settings.Save();
            Log($"Profile created for {d.Model} (copy of the defaults).");
        }
        RebuildProfiles(hw);
        _ = RefreshDevices();
    }

    async void DeleteProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ProfileItem { Key: { } key }) { Log("The default settings cannot be deleted."); return; }
        var name = _settings.Profiles[key].Name;
        if (!await MessageDialog.ShowAsync(this, $"Delete the profile of {name}?", "MirrorHub", yesNo: true)) return;
        _settings.Profiles.Remove(key);
        _settings.Save();
        RebuildProfiles(null);
        _ = RefreshDevices();
    }

    // ---------- Android tools ----------

    async Task InstallOrPush(Device d, IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (File.Exists(p) && p.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                Log($"Installing {name} on {d.Model}…");
                var (rc, o) = await Adb.InstallAsync(d.Serial, p);
                Log(rc == 0 ? $"Installed: {name}" : $"Install failed ({name}): {o}");
            }
            else
            {
                Log($"Sending {name} to {Adb.PushTarget} on {d.Model}…");
                var (rc, o) = await Adb.PushAsync(d.Serial, p);
                Log(rc == 0 ? $"Sent: {name}" : $"Send failed ({name}): {o}");
            }
        }
    }

    async void InstallApk_Click(object? sender, RoutedEventArgs e)
    {
        if (TargetDevice() is not { } d) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Install APK on {d.Model}",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Android apps") { Patterns = ["*.apk"] }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
        if (paths.Count > 0) await InstallOrPush(d, paths);
    }

    async void SendFiles_Click(object? sender, RoutedEventArgs e)
    {
        if (TargetDevice() is not { } d) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Send files to {d.Model} (Download folder)",
            AllowMultiple = true,
            FileTypeFilter = [FilePickerFileTypes.All],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
        if (paths.Count > 0) await InstallOrPush(d, paths);
    }

    void Android_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void Android_Drop(object? sender, DragEventArgs e)
    {
        if (TargetDevice() is not { } d) return;
        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
        if (files is { Count: > 0 }) await InstallOrPush(d, files);
    }

    async void StartApp_Click(object? sender, RoutedEventArgs e)
    {
        if (TargetDevice() is not { } d || Tools.Scrcpy == null) return;
        if (await AppPicker.PickAsync(this, d) is not { } app) return;
        if (IsMirroring(d))
        {
            var (rc, o) = await Adb.LaunchAppAsync(d.Serial, app.Package);
            Log(rc == 0 ? $"Launched {app.Label} on {d.Model}." : $"Failed to launch {app.Label}: {o}");
        }
        else if (StartMirror(d, app.Package)) Log($"Mirroring with {app.Label} launched.");
    }

    // ---------- camera ----------

    CameraWindow Camera => _camera ??= new CameraWindow(_settings, Log,
        (title, text) => Notify(_settings.Notify.FileSaved, title, text, OpenOutputFolder),
        InvalidateTray);

    public void ToggleCamera() => Camera.Toggle();

    void OpenCamera()
    {
        var w = Camera;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    void OpenCamera_Click(object? sender, RoutedEventArgs e) => OpenCamera();

    // ---------- connection ----------

    async Task<MdnsService> WaitMdns(Func<MdnsService, bool> match, CancellationToken token)
    {
        while (true)
        {
            var found = (await Adb.MdnsServicesAsync()).FirstOrDefault(match);
            if (found != null) return found;
            await Task.Delay(1000, token);
        }
    }

    /// <summary>After pairing, the phone advertises _adb-tls-connect on a different port.</summary>
    async Task ConnectViaMdns(string ip)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var svc = await WaitMdns(s => s.Type.Contains("connect") && s.Address.StartsWith(ip + ":"), cts.Token);
            Log((await Adb.RunAsync("connect", svc.Address)).Out);
        }
        catch (OperationCanceledException)
        {
            Log("Connection service not found via mDNS: use \"Connect IP…\" with the IP and port shown under Wireless debugging.");
        }
    }

    async void ConnectIp_Click(object? sender, RoutedEventArgs e)
    {
        var addr = await Prompt.AskAsync(this, "Connect over Wi-Fi", "IP:port (e.g. 192.168.1.20:5555)");
        if (string.IsNullOrEmpty(addr)) return;
        if (!addr.Contains(':')) addr += ":5555";
        Log((await Adb.RunAsync("connect", addr)).Out);
        await RefreshDevices();
    }

    async void PairCode_Click(object? sender, RoutedEventArgs e)
    {
        var addr = await Prompt.AskAsync(this, "Pair with code",
            "Wireless debugging → Pair device with pairing code.\nPairing IP:port:");
        if (string.IsNullOrEmpty(addr)) return;
        var code = await Prompt.AskAsync(this, "Pair with code", "6-digit code:");
        if (string.IsNullOrEmpty(code)) return;

        var (rc, o) = await Adb.RunAsync(20000, "pair", addr, code);
        Log(o);
        if (rc == 0 && addr.Contains(':')) await ConnectViaMdns(addr[..addr.LastIndexOf(':')]);
        await RefreshDevices();
    }

    async void PairQr_Click(object? sender, RoutedEventArgs e)
    {
        // Android 11+ "Pair device with QR code" format, as used by Android Studio.
        var name = "MirrorHub-" + RandomNumberGenerator.GetHexString(6, lowercase: true);
        var password = RandomNumberGenerator.GetString("abcdefghjkmnpqrstuvwxyz23456789", 10);
        var png = new PngByteQRCode(new QRCodeGenerator()
            .CreateQrCode($"WIFI:T:ADB;S:{name};P:{password};;", QRCodeGenerator.ECCLevel.M)).GetGraphic(8);

        using var ms = new MemoryStream(png);
        var image = new Bitmap(ms);

        var status = new TextBlock
        {
            Text = "On the phone: Developer options → Wireless debugging → Pair device with QR code. PC and phone must be on the same Wi-Fi.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 320,
            Margin = new Thickness(10),
        };
        var win = new Window
        {
            Title = "Pair with QR",
            Icon = Icon ?? DialogHelpers.LoadAppIcon(),
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Children = { new Image { Source = image, Width = 320, Height = 320, Margin = new Thickness(10) }, status },
            },
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        win.Closed += (_, _) => cts.Cancel();
        win.Show(this);
        try
        {
            var svc = await WaitMdns(s => s.Name == name && s.Type.Contains("pairing"), cts.Token);
            status.Text = $"Phone found ({svc.Address}). Pairing…";
            var (rc, o) = await Adb.RunAsync(20000, "pair", svc.Address, password);
            Log(o);
            if (rc != 0 || !o.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                status.Text = "Pairing failed: " + o;
                return;
            }
            status.Text = "Paired. Connecting…";
            await ConnectViaMdns(svc.Address[..svc.Address.LastIndexOf(':')]);
            win.Close();
        }
        catch (OperationCanceledException)
        {
            if (win.IsVisible) status.Text = "Timed out. Close and try again.";
        }
        await RefreshDevices();
    }

    async void UsbToWifi_Click(object? sender, RoutedEventArgs e)
    {
        var d = Selected();
        if (d == null) return;
        if (d.IsWifi) { Log($"{d.Model} is already connected over Wi-Fi."); return; }

        var ip = await Adb.WifiIpAsync(d.Serial);
        if (ip == null) { Log("Wi-Fi IP not found: is the phone connected to Wi-Fi?"); return; }
        Log((await Adb.RunAsync("-s", d.Serial, "tcpip", "5555")).Out);
        await Task.Delay(2000);
        Log((await Adb.RunAsync("connect", ip + ":5555")).Out);
        Log("You can now unplug the USB cable.");
        await RefreshDevices();
    }

    async void Disconnect_Click(object? sender, RoutedEventArgs e)
    {
        var d = Selected();
        if (d == null) return;
        if (!d.IsWifi) { Log("USB devices are disconnected by unplugging the cable."); return; }
        if (_sessions.TryGetValue(d.Serial, out var s)) s.Stop();
        Log((await Adb.RunAsync("disconnect", d.Serial)).Out);
        await RefreshDevices();
    }
}
