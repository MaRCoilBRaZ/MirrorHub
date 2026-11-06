using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    readonly Dictionary<string, string> _hwIds = new();              // adb serial -> hardware id
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly GlobalHotkeys _hotkeys;
    List<Device> _devices = [];
    Dictionary<string, Device> _online = new();
    bool _refreshing, _firstRefresh = true, _exiting;
    WatchedProcess? _airplay;
    CameraWindow? _camera;

    public MainWindow()
    {
        InitializeComponent();
        VideoCodecBox.ItemsSource = new[] { "h264", "h265", "av1" };
        AudioCodecBox.ItemsSource = new[] { "opus", "aac", "flac", "raw" };
        HwDecBox.ItemsSource = new[] { "auto", "d3d11va", "disabled" };
        RenderFitBox.ItemsSource = new[] { "letterbox", "stretched", "unscaled" };
        AirResBox.ItemsSource = new[] { "1280x720@60", "1920x1080@60", "2560x1440@60", "3840x2160@60" };
        AirSinkBox.ItemsSource = new[] { "auto", "d3d11videosink", "d3d12videosink" };
        DataContext = _settings;
        MinimizedModeBox.SelectedIndex = (int)_settings.MinimizedMode;
        BuildQualityChoices();
        RebuildProfiles(null);

        // The window handle exists even while the window is hidden in the notification area.
        _hotkeys = new GlobalHotkeys(new WindowInteropHelper(this).EnsureHandle());
        BuildHotkeyEditors();
        RegisterHotkeys();
        InitTray();
        Application.Current.SessionEnding += (_, _) => _exiting = true;   // never block Windows logoff

        try { Startup.Apply(_settings.StartWithWindows); }
        catch (Exception ex) { Log("Start with Windows not updated: " + ex.Message); }

        Log($"adb: {Tools.Adb}");
        Log(Tools.Scrcpy != null
            ? $"scrcpy: {Tools.Scrcpy}"
            : "scrcpy not found. Install it with: winget install Genymobile.scrcpy");
        Log(Tools.UxPlay != null
            ? $"uxplay: {Tools.UxPlay}"
            : "uxplay not found: iPhone/iPad mirroring unavailable.");
        if (Tools.Scrcpy == null && Tools.UxPlay == null)
            Dispatcher.BeginInvoke(() => MessageBox.Show(this, Tools.MissingToolsHint, "MirrorHub",
                MessageBoxButton.OK, MessageBoxImage.Warning));

        _poll.Tick += async (_, _) => await RefreshDevices();
        Dispatcher.BeginInvoke(async () =>
        {
            await RefreshDevices();
            _poll.Start();
            if (_settings.AirPlayAutoStart) await StartAirPlay(interactive: false);
        });
    }

    // ---------- window / startup ----------

    /// <summary>Shows the window, or keeps it minimized / hidden according to the settings.</summary>
    public void ShowAtStartup()
    {
        var minimized = _settings.MinimizedMode == StartMinimizedMode.Always ||
                        (_settings.MinimizedMode == StartMinimizedMode.AtLogon && Startup.LaunchedAtLogon);
        if (!minimized) { Show(); return; }
        if (_settings.MinimizeToTray) return;          // stays in the notification area only
        WindowState = WindowState.Minimized;
        Show();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void ToggleWindow()
    {
        if (IsVisible && WindowState != WindowState.Minimized && IsActive) Hide();
        else ShowFromTray();
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray) Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            ShowTrayHintOnce();
            return;
        }

        _poll.Stop();
        _hotkeys.Clear();
        try { _settings.Save(); } catch { }
        foreach (var s in _sessions.Values.ToList()) s.Stop();
        _airplay?.Stop();
        _camera?.Shutdown();
        Adb.StopBundledServer();
        DisposeTray();
        base.OnClosing(e);
    }

    // ---------- helpers ----------

    void Log(string msg) => Dispatcher.BeginInvoke(() =>
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {msg}\n";
        if (LogBox.Text.Length > 100_000) LogBox.Text = LogBox.Text[^50_000..];
        LogBox.AppendText(line);
        LogBox.ScrollToEnd();
        LogFile.Append(line);
    });

    void CopyLog()
    {
        var header = $"MirrorHub {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} ·{Environment.OSVersion.VersionString}\n" +
                     $"Folder: {AppContext.BaseDirectory}\n";
        try
        {
            Clipboard.SetText(header + LogBox.Text);
            Log("Log copied: paste it in a message (Ctrl+V).");
        }
        catch (Exception ex) { Log("Could not copy the log: " + ex.Message); }
    }

    void CopyLog_Click(object sender, RoutedEventArgs e) => CopyLog();

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
        return id;
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
        Process.Start("explorer.exe", _settings.OutputDir);
    }

    static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    // ---------- devices ----------

    async Task RefreshDevices()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var devices = await Adb.DevicesAsync();
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
    }

    void AutoMirrorIfEnabled(Device d)
    {
        if (!_settings.OptionsFor(CachedHwId(d.Serial)).AutoMirror || IsMirroring(d)) return;
        Log($"Auto-mirror: {d.Display}");
        StartMirror(d);
    }

    void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Show the settings that apply to the selected device.
        if (DeviceList.SelectedItem is not DeviceItem item) return;
        var hw = CachedHwId(item.Device.Serial);
        var key = hw != null && _settings.Profiles.ContainsKey(hw) ? hw : null;
        if ((ProfileBox.SelectedItem as ProfileItem)?.Key != key) RebuildProfiles(key);
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
        var options = _settings.OptionsFor(CachedHwId(d.Serial));
        var session = audioOnly ? Scrcpy.CreateAudioOnly(d, options) : Scrcpy.Create(d, options, _settings.OutputDir, startApp);
        session.Log += m => Log($"[{d.Model}] {m}");
        session.Restarting += n =>
        {
            if (n == 1) Notify(_settings.Notify.MirrorEvents, "Connection lost", $"{d.Model}: reconnecting…");
        };
        session.Ended += unexpected => Dispatcher.Invoke(() =>
        {
            _sessions.Remove(d.Serial);
            _audioOnly.Remove(d.Serial);
            if (unexpected) Notify(_settings.Notify.MirrorEvents, audioOnly ? "Phone audio stopped" : "Mirroring stopped",
                $"{d.Model}: could not reconnect.");
            if (options.Record && !audioOnly) Notify(_settings.Notify.FileSaved, "Recording saved", _settings.OutputDir, OpenOutputFolder);
            _ = RefreshDevices();
        });
        _sessions[d.Serial] = session;
        if (audioOnly)
        {
            _audioOnly.Add(d.Serial);
            Log($"{d.Model}: audio only (no video, no window). Stop it with ■ Stop or from the icon menu.");
        }
        session.Start();
        _ = RefreshDevices();
        return true;
    }

    void ToggleAudioOnly(Device? d = null)
    {
        d ??= TargetDevice();
        if (d == null) return;
        if (IsAudioOnly(d)) _sessions[SessionSerialFor(d)!].Stop();
        else StartMirror(d, audioOnly: true);
    }

    void AudioOnly_Click(object sender, RoutedEventArgs e) => ToggleAudioOnly();

    void ToggleMirror(Device? d = null)
    {
        d ??= TargetDevice();
        if (d == null) return;
        if (SessionSerialFor(d) is { } serial && !IsAudioOnly(d)) _sessions[serial].Stop();
        else StartMirror(d);   // explains what to do if audio only is running
    }

    void Start_Click(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is { } d) StartMirror(d);
    }

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is { } d && SessionSerialFor(d) is { } serial) _sessions[serial].Stop();
    }

    async Task TakeScreenshot(Device? d = null)
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

    async void Screenshot_Click(object sender, RoutedEventArgs e) => await TakeScreenshot();

    void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenOutputFolder();

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

    void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ProfileItem item) return;
        MirrorOptionsPanel.DataContext = _settings.OptionsFor(item.Key);
        ProfileNote.Text = item.Key != null ? $"These are the settings of the profile \"{_settings.Profiles[item.Key].Name}\"." : "";
        ProfileNote.Visibility = item.Key != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateQualityChoices();
    }

    async void CreateProfile_Click(object sender, RoutedEventArgs e)
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

    void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ProfileItem { Key: { } key }) { Log("The default settings cannot be deleted."); return; }
        var name = _settings.Profiles[key].Name;
        if (MessageBox.Show(this, $"Delete the profile of {name}?", "MirrorHub",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
            var name = Path.GetFileName(p.TrimEnd('\\'));
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

    async void InstallApk_Click(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is not { } d) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Install APK on {d.Model}", Filter = "Android apps (*.apk)|*.apk", Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) await InstallOrPush(d, dlg.FileNames);
    }

    async void SendFiles_Click(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is not { } d) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Send files to {d.Model} (Download folder)", Filter = "All files (*.*)|*.*", Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) await InstallOrPush(d, dlg.FileNames);
    }

    void Android_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void Android_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || TargetDevice() is not { } d) return;
        await InstallOrPush(d, files);
    }

    async void StartApp_Click(object sender, RoutedEventArgs e)
    {
        if (TargetDevice() is not { } d || Tools.Scrcpy == null) return;
        if (AppPicker.Pick(this, d) is not { } app) return;
        if (IsMirroring(d))
        {
            var (rc, o) = await Adb.LaunchAppAsync(d.Serial, app.Package);
            Log(rc == 0 ? $"Launched {app.Label} on {d.Model}." : $"Failed to launch {app.Label}: {o}");
        }
        else if (StartMirror(d, app.Package)) Log($"Mirroring with {app.Label} launched.");
    }

    // ---------- camera ----------

    CameraWindow Camera => _camera ??= new CameraWindow(_settings, Log,
        (title, text) => Notify(_settings.Notify.FileSaved, title, text, OpenOutputFolder));

    void OpenCamera()
    {
        var w = Camera;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    void OpenCamera_Click(object sender, RoutedEventArgs e) => OpenCamera();

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

    async void ConnectIp_Click(object sender, RoutedEventArgs e)
    {
        var addr = Prompt.Ask(this, "Connect over Wi-Fi", "IP:port (e.g. 192.168.1.20:5555)");
        if (string.IsNullOrEmpty(addr)) return;
        if (!addr.Contains(':')) addr += ":5555";
        Log((await Adb.RunAsync("connect", addr)).Out);
        await RefreshDevices();
    }

    async void PairCode_Click(object sender, RoutedEventArgs e)
    {
        var addr = Prompt.Ask(this, "Pair with code",
            "Wireless debugging → Pair device with pairing code.\nPairing IP:port:");
        if (string.IsNullOrEmpty(addr)) return;
        var code = Prompt.Ask(this, "Pair with code", "6-digit code:");
        if (string.IsNullOrEmpty(code)) return;

        var (rc, o) = await Adb.RunAsync(20000, "pair", addr, code);
        Log(o);
        if (rc == 0 && addr.Contains(':')) await ConnectViaMdns(addr[..addr.LastIndexOf(':')]);
        await RefreshDevices();
    }

    async void PairQr_Click(object sender, RoutedEventArgs e)
    {
        // Android 11+ "Pair device with QR code" format, as used by Android Studio.
        var name = "MirrorHub-" + RandomNumberGenerator.GetHexString(6, lowercase: true);
        var password = RandomNumberGenerator.GetString("abcdefghjkmnpqrstuvwxyz23456789", 10);
        var png = new PngByteQRCode(new QRCodeGenerator()
            .CreateQrCode($"WIFI:T:ADB;S:{name};P:{password};;", QRCodeGenerator.ECCLevel.M)).GetGraphic(8);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();

        var status = new TextBlock
        {
            Text = "On the phone: Developer options → Wireless debugging → Pair device with QR code. PC and phone must be on the same Wi-Fi.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Margin = new Thickness(10),
        };
        var win = new Window
        {
            Title = "Pair with QR", Owner = this, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Children = { new Image { Source = image, Width = 320, Height = 320, Margin = new Thickness(10) }, status },
            },
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        win.Closed += (_, _) => cts.Cancel();
        win.Show();
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

    async void UsbToWifi_Click(object sender, RoutedEventArgs e)
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

    async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var d = Selected();
        if (d == null) return;
        if (!d.IsWifi) { Log("USB devices are disconnected by unplugging the cable."); return; }
        if (_sessions.TryGetValue(d.Serial, out var s)) s.Stop();
        Log((await Adb.RunAsync("disconnect", d.Serial)).Out);
        await RefreshDevices();
    }
}
