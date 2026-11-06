using System.Windows;
using Forms = System.Windows.Forms;

namespace MirrorHub;

/// <summary>Notification-area icon: right-click management menu and notifications.</summary>
public partial class MainWindow
{
    Forms.NotifyIcon? _tray;
    Action? _balloonAction;
    bool _trayHintShown;

    void InitTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, e) =>
        {
            BuildTrayMenu(menu);
            e.Cancel = false;
        };
        _tray = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "MirrorHub",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
        _tray.BalloonTipClicked += (_, _) => (_balloonAction ?? ShowFromTray)();
    }

    static System.Drawing.Icon LoadAppIcon()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    void DisposeTray()
    {
        if (_tray == null) return;
        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
    }

    /// <summary>Windows notification (shown as a toast on Windows 10/11). Thread-safe.</summary>
    void Notify(bool enabled, string title, string text, Action? onClick = null)
    {
        if (!enabled) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_tray == null) return;
            _balloonAction = onClick;
            _tray.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
        });
    }

    void ShowTrayHintOnce()
    {
        if (_trayHintShown) return;
        _trayHintShown = true;
        Notify(true, "MirrorHub is still running",
            "It is in the notification area. To quit: right-click the icon → Exit.");
    }

    string HotkeyText(string action) => _settings.Hotkeys.GetValueOrDefault(action) ?? "";

    void BuildTrayMenu(Forms.ContextMenuStrip m)
    {
        m.Items.Clear();
        var online = _devices.Where(d => d.State == "device").ToList();

        var open = new Forms.ToolStripMenuItem("Open MirrorHub", null, (_, _) => ShowFromTray())
        {
            ShortcutKeyDisplayString = HotkeyText(HotkeyActions.ToggleWindow),
        };
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        m.Items.Add(open);
        m.Items.Add(new Forms.ToolStripSeparator());

        var android = new Forms.ToolStripMenuItem("Android mirroring")
        {
            ShortcutKeyDisplayString = HotkeyText(HotkeyActions.ToggleMirror),
        };
        if (online.Count == 0)
            android.DropDownItems.Add(new Forms.ToolStripMenuItem("No device connected") { Enabled = false });
        foreach (var d in online)
        {
            var device = d;
            android.DropDownItems.Add(new Forms.ToolStripMenuItem(d.Display, null, (_, _) => ToggleMirror(device))
            {
                Checked = IsMirroring(d) && !IsAudioOnly(d),
            });
        }
        m.Items.Add(android);

        var audio = new Forms.ToolStripMenuItem("Android audio only (no video)")
        {
            ShortcutKeyDisplayString = HotkeyText(HotkeyActions.ToggleAudioOnly),
        };
        if (online.Count == 0)
            audio.DropDownItems.Add(new Forms.ToolStripMenuItem("No device connected") { Enabled = false });
        foreach (var d in online)
        {
            var device = d;
            audio.DropDownItems.Add(new Forms.ToolStripMenuItem(d.Display, null, (_, _) => ToggleAudioOnly(device))
            {
                Checked = IsAudioOnly(d),
            });
        }
        m.Items.Add(audio);

        m.Items.Add(new Forms.ToolStripMenuItem("AirPlay receiver (iPhone / iPad)", null, (_, _) => ToggleAirPlay())
        {
            Checked = _airplay != null,
            Enabled = Tools.UxPlay != null,
            ShortcutKeyDisplayString = HotkeyText(HotkeyActions.ToggleAirPlay),
        });
        m.Items.Add(new Forms.ToolStripMenuItem("Phone camera…", null, (_, _) => OpenCamera())
        {
            Checked = _camera?.IsRunning == true,
            ShortcutKeyDisplayString = HotkeyText(HotkeyActions.ToggleCamera),
        });
        m.Items.Add(new Forms.ToolStripMenuItem("Android screenshot", null, async (_, _) => await TakeScreenshot())
        {
            Enabled = online.Count > 0,
            ShortcutKeyDisplayString = HotkeyText(HotkeyActions.Screenshot),
        });

        var tools = new Forms.ToolStripMenuItem("Android tools") { Enabled = online.Count > 0 };
        tools.DropDownItems.Add("Install APK…", null, (_, _) => { ShowFromTray(); InstallApk_Click(this, new RoutedEventArgs()); });
        tools.DropDownItems.Add("Send files…", null, (_, _) => { ShowFromTray(); SendFiles_Click(this, new RoutedEventArgs()); });
        tools.DropDownItems.Add("Launch app…", null, (_, _) => { ShowFromTray(); StartApp_Click(this, new RoutedEventArgs()); });
        m.Items.Add(tools);

        m.Items.Add(new Forms.ToolStripSeparator());
        m.Items.Add("Open output folder", null, (_, _) => OpenOutputFolder());
        m.Items.Add("Settings", null, (_, _) => { ShowFromTray(); Tabs.SelectedItem = SettingsTab; });
        m.Items.Add(new Forms.ToolStripSeparator());
        m.Items.Add("Exit", null, (_, _) => ExitApp());
    }
}
