using Avalonia.Platform;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace MirrorHub;

/// <summary>Windows notification-area icon backed by WinForms NotifyIcon and ContextMenuStrip.</summary>
sealed class WinFormsTray : ITray
{
    readonly Forms.NotifyIcon _tray;
    Action? _balloonAction;

    public bool IsAvailable => true;
    public bool RefreshAvailability() => true;

    public event Action? Activated;

    public WinFormsTray(Func<IReadOnlyList<TrayItem>> buildMenu)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, e) =>
        {
            Rebuild(menu.Items, buildMenu());
            e.Cancel = false;
        };

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "MirrorHub",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Activated?.Invoke(); };
        _tray.BalloonTipClicked += (_, _) => (_balloonAction ?? Activated)?.Invoke();
    }

    static Drawing.Icon LoadAppIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://MirrorHub/app.ico"));
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    static void Rebuild(Forms.ToolStripItemCollection target, IReadOnlyList<TrayItem> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            if (ReferenceEquals(item, TrayItem.Separator))
            {
                target.Add(new Forms.ToolStripSeparator());
                continue;
            }
            var mi = new Forms.ToolStripMenuItem(item.Text)
            {
                Enabled = item.Enabled,
                Checked = item.Checked,
                ShortcutKeyDisplayString = item.Shortcut ?? "",
            };
            if (item.Bold)
                mi.Font = new Drawing.Font(mi.Font, Drawing.FontStyle.Bold);
            if (item.Click != null)
            {
                var click = item.Click;
                mi.Click += (_, _) => click();
            }
            if (item.Children != null)
                Rebuild(mi.DropDownItems, item.Children);
            target.Add(mi);
        }
    }

    public void Invalidate() { }   // rebuilt on every ContextMenuStrip.Opening

    public void Notify(string title, string text, Action? onClick)
    {
        _balloonAction = onClick;
        _tray.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
    }
}
