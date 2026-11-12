using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MirrorHub;

/// <summary>Notification-area icon: platform-neutral menu model and notifications.</summary>
public partial class MainWindow
{
    ITray? _tray;
    bool _trayHintShown;

    void InitTray()
    {
        _tray = Os.CreateTray(BuildTrayMenu, Log);
        _tray.Activated += ShowFromTray;
    }

    void InvalidateTray() => _tray?.Invalidate();

    void DisposeTray()
    {
        _tray?.Dispose();
        _tray = null;
    }

    /// <summary>Notification (balloon/toast on Windows, log entry on Linux). Thread-safe.</summary>
    void Notify(bool enabled, string title, string text, Action? onClick = null)
    {
        if (!enabled) return;
        Dispatcher.UIThread.Post(() => _tray?.Notify(title, text, onClick));
    }

    void ShowTrayHintOnce()
    {
        if (_trayHintShown || !(_tray?.IsAvailable ?? true)) return;
        _trayHintShown = true;
        Notify(true, "MirrorHub is still running",
            "It is in the notification area. To quit: right-click the icon → Exit.");
    }

    string? HotkeyText(string action)
    {
        if (!_hotkeys.IsSupported) return null;
        var s = _settings.Hotkeys.GetValueOrDefault(action);
        return string.IsNullOrEmpty(s) ? null : s;
    }

    IReadOnlyList<TrayItem> BuildTrayMenu()
    {
        var online = _devices.Where(d => d.State == "device").ToList();

        var androidChildren = online.Count == 0
            ? new List<TrayItem> { new("No device connected", Enabled: false) }
            : online.Select(d => new TrayItem(d.Display, () => ToggleMirror(d), Checked: IsMirroring(d) && !IsAudioOnly(d))).ToList();

        var audioChildren = online.Count == 0
            ? new List<TrayItem> { new("No device connected", Enabled: false) }
            : online.Select(d => new TrayItem(d.Display, () => ToggleAudioOnly(d), Checked: IsAudioOnly(d))).ToList();

        var toolsChildren = new List<TrayItem>
        {
            new("Install APK…", () => { ShowFromTray(); InstallApk_Click(this, new RoutedEventArgs()); }),
            new("Send files…", () => { ShowFromTray(); SendFiles_Click(this, new RoutedEventArgs()); }),
            new("Launch app…", () => { ShowFromTray(); StartApp_Click(this, new RoutedEventArgs()); }),
        };

        return
        [
            new("Open MirrorHub", ShowFromTray, Bold: true, Shortcut: HotkeyText(HotkeyActions.ToggleWindow)),
            TrayItem.Separator,
            new("Android mirroring", Shortcut: HotkeyText(HotkeyActions.ToggleMirror), Children: androidChildren),
            new("Android audio only (no video)", Shortcut: HotkeyText(HotkeyActions.ToggleAudioOnly), Children: audioChildren),
            new("AirPlay receiver (iPhone / iPad)", () => ToggleAirPlay(),
                Checked: AirRunning && !_airAudioOnly, Enabled: Tools.UxPlay != null, Shortcut: HotkeyText(HotkeyActions.ToggleAirPlay)),
            new("iPhone / iPad audio only (no video)", () => ToggleAirPlay(audioOnly: true),
                Checked: AirRunning && _airAudioOnly, Enabled: Tools.UxPlay != null, Shortcut: HotkeyText(HotkeyActions.ToggleAirAudio)),
            new("Phone camera…", OpenCamera,
                Checked: _camera?.IsRunning == true, Shortcut: HotkeyText(HotkeyActions.ToggleCamera)),
            new("Android screenshot", () => _ = TakeScreenshot(),
                Enabled: online.Count > 0, Shortcut: HotkeyText(HotkeyActions.Screenshot)),
            new("Android tools", Enabled: online.Count > 0, Children: toolsChildren),
            TrayItem.Separator,
            new("Open output folder", OpenOutputFolder),
            new("Settings", () => { ShowFromTray(); Tabs.SelectedItem = SettingsTab; }),
            TrayItem.Separator,
            new("Restart", RestartApp),
            new("Exit", ExitApp),
        ];
    }
}
