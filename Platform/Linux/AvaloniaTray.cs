using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MirrorHub;

/// <summary>Linux notification-area icon (TrayIcon + NativeMenu) and notify-send notifications.</summary>
sealed class AvaloniaTray : ITray
{
    readonly Func<IReadOnlyList<TrayItem>> _buildMenu;
    readonly Action<string> _log;
    readonly TrayIcon _tray;
    readonly NativeMenu _menu = new();
    string _lastSignature = "";
    bool _isAvailable;
    bool _notifyMissingLogged;

    public bool IsAvailable => _isAvailable;

    public event Action? Activated;

    public AvaloniaTray(Func<IReadOnlyList<TrayItem>> buildMenu, Action<string> log)
    {
        _buildMenu = buildMenu;
        _log = log;
        _isAvailable = CheckAvailability();
        using var stream = AssetLoader.Open(new Uri("avares://MirrorHub/app.ico"));
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(stream),
            ToolTipText = "MirrorHub",
            IsVisible = true,
            Menu = _menu,
        };
        _tray.Clicked += (_, _) => Activated?.Invoke();
        if (Application.Current != null)
            TrayIcon.SetIcons(Application.Current, [_tray]);
        Invalidate();
    }

    public bool RefreshAvailability()
    {
        _isAvailable = CheckAvailability();
        return _isAvailable;
    }

    static bool CheckAvailability()
    {
        try
        {
            var psi = new ProcessStartInfo("dbus-send")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[]
            {
                "--session",
                "--print-reply",
                "--dest=org.freedesktop.DBus",
                "/org/freedesktop/DBus",
                "org.freedesktop.DBus.NameHasOwner",
                "string:org.kde.StatusNotifierWatcher",
            }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return true;
            var outputTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return true;
            }
            if (p.ExitCode != 0) return true;
            outputTask.Wait(200);
            return outputTask.IsCompletedSuccessfully && outputTask.Result.Contains("boolean true", StringComparison.Ordinal);
        }
        catch
        {
            return true;   // dbus-send missing or failed: treat as available
        }
    }

    public void Invalidate()
    {
        var items = _buildMenu();
        var sig = Signature(items);
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        Rebuild(_menu, items);
    }

    static string Signature(IReadOnlyList<TrayItem> items)
    {
        var sb = new StringBuilder();
        Append(sb, items);
        return sb.ToString();

        static void Append(StringBuilder sb, IReadOnlyList<TrayItem> list)
        {
            foreach (var item in list)
            {
                if (ReferenceEquals(item, TrayItem.Separator)) { sb.Append("|---"); continue; }
                sb.Append('|').Append(item.Text)
                  .Append(':').Append(item.Checked ? '1' : '0')
                  .Append(item.Enabled ? '1' : '0')
                  .Append(item.Bold ? '1' : '0')
                  .Append(':').Append(item.Shortcut);
                if (item.Children != null)
                {
                    sb.Append('{');
                    Append(sb, item.Children);
                    sb.Append('}');
                }
            }
        }
    }

    static void Rebuild(NativeMenu target, IReadOnlyList<TrayItem> items)
    {
        target.Items.Clear();
        foreach (var item in items)
        {
            if (ReferenceEquals(item, TrayItem.Separator))
            {
                target.Items.Add(new NativeMenuItemSeparator());
                continue;
            }
            var mi = new NativeMenuItem(item.Text) { IsEnabled = item.Enabled };
            if (item.Checked)
            {
                mi.ToggleType = NativeMenuItemToggleType.CheckBox;
                mi.IsChecked = true;
            }
            if (!string.IsNullOrEmpty(item.Shortcut) && GlobalHotkeys.TryParse(item.Shortcut, out var mods, out var key))
                mi.Gesture = new KeyGesture(key, mods);
            if (item.Click != null)
            {
                var click = item.Click;
                mi.Click += (_, _) => click();
            }
            if (item.Children != null)
            {
                var sub = new NativeMenu();
                Rebuild(sub, item.Children);
                mi.Menu = sub;
            }
            target.Items.Add(mi);
        }
    }

    public void Notify(string title, string text, Action? onClick) =>
        _ = NotifyAsync(title, text, onClick);

    async Task NotifyAsync(string title, string text, Action? onClick)
    {
        if (!await SendNotifyAsync(title, text, onClick, withAction: true))
            await SendNotifyAsync(title, text, onClick, withAction: false);
    }

    /// <summary>Returns false only when -A failed (older libnotify) and should be retried without -A.</summary>
    async Task<bool> SendNotifyAsync(string title, string text, Action? onClick, bool withAction)
    {
        var psi = new ProcessStartInfo("notify-send")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add("MirrorHub");
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.png");
        if (File.Exists(iconPath))
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(iconPath);
        }
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("5000");
        if (withAction)
        {
            psi.ArgumentList.Add("-A");
            psi.ArgumentList.Add("default=Open");
        }
        psi.ArgumentList.Add(title);
        psi.ArgumentList.Add(text);

        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            if (!_notifyMissingLogged)
            {
                _notifyMissingLogged = true;
                _log("Notifications need notify-send (package libnotify-bin)");
            }
            return true;
        }
        catch { return true; }

        if (p == null) return true;
        using (p)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                return true;
            }
            catch { return true; }

            if (withAction && p.ExitCode != 0) return false;

            try
            {
                var stdout = await stdoutTask;
                _ = await stderrTask;
                if (stdout.Split('\n').Any(l => l.Trim() == "default"))
                    Dispatcher.UIThread.Post(() => (onClick ?? Activated)?.Invoke());
            }
            catch { }
            return true;
        }
    }

    public void Dispose()
    {
        if (Application.Current != null)
            TrayIcon.SetIcons(Application.Current, []);
        _tray.IsVisible = false;
        _tray.Dispose();
    }
}
