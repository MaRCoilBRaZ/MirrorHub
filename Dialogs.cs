using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace MirrorHub;

static class DialogHelpers
{
    public static WindowIcon LoadAppIcon()
    {
        using var s = AssetLoader.Open(new Uri("avares://MirrorHub/app.ico"));
        return new WindowIcon(s);
    }
}

/// <summary>Cross-platform replacement for WPF MessageBox.</summary>
static class MessageDialog
{
    public static async Task<bool> ShowAsync(Window? owner, string text, string title, bool yesNo = false)
    {
        var result = false;
        var hasOwner = owner is { IsVisible: true };

        var body = new SelectableTextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 490,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
        };

        var win = new Window
        {
            Title = title,
            Icon = owner?.Icon ?? DialogHelpers.LoadAppIcon(),
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            MinWidth = 300,
            MaxWidth = 520,
            WindowStartupLocation = hasOwner ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Topmost = !hasOwner,
        };

        if (yesNo)
        {
            var yes = new Button { Content = "Yes", IsDefault = true, MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
            var no = new Button { Content = "No", IsCancel = true, MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
            yes.Click += (_, _) => { result = true; win.Close(true); };
            no.Click += (_, _) => { result = false; win.Close(false); };
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
        }
        else
        {
            var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Click += (_, _) => { result = true; win.Close(true); };
            buttons.Children.Add(ok);
        }

        win.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children = { body, buttons },
        };

        if (hasOwner)
            return await win.ShowDialog<bool>(owner!);

        var tcs = new TaskCompletionSource<bool>();
        win.Closed += (_, _) => tcs.TrySetResult(result);
        win.Show();
        win.Activate();
        return await tcs.Task;
    }
}

/// <summary>Minimal modal text input.</summary>
static class Prompt
{
    public static async Task<string?> AskAsync(Window owner, string title, string label)
    {
        var box = new TextBox { Margin = new Thickness(0, 6, 0, 10), MinWidth = 300 };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
        var win = new Window
        {
            Title = title,
            Icon = owner.Icon ?? DialogHelpers.LoadAppIcon(),
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = label }, box, ok } },
        };
        ok.Click += (_, _) => win.Close(box.Text?.Trim() ?? "");
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; win.Close(null); } };
        win.Opened += (_, _) => box.Focus();
        return await win.ShowDialog<string?>(owner);
    }
}

/// <summary>Searchable list of the apps installed on a device.</summary>
static class AppPicker
{
    public static async Task<AndroidApp?> PickAsync(Window owner, Device device)
    {
        var status = new TextBlock { Text = "Loading apps (this may take a few seconds)…", Margin = new Thickness(0, 0, 0, 6) };
        var filter = new TextBox { Margin = new Thickness(0, 0, 0, 6), IsEnabled = false };
        var list = new ListBox { Height = 380 };
        var ok = new Button { Content = "Launch", IsDefault = true, Width = 90, Margin = new Thickness(0, 8, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Margin = new Thickness(0, 8, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var win = new Window
        {
            Title = $"Launch app on {device.Model}",
            Icon = owner.Icon ?? DialogHelpers.LoadAppIcon(),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Children =
                {
                    status,
                    new TextBlock { Text = "Search:", Margin = new Thickness(0, 0, 0, 2) },
                    filter,
                    list,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
                },
            },
        };

        List<AndroidApp> all = [];
        filter.TextChanged += (_, _) =>
        {
            var q = filter.Text ?? "";
            list.ItemsSource = all
                .Where(a => a.Label.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                            a.Package.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        };
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is AndroidApp app) win.Close(app); };
        ok.Click += (_, _) => { if (list.SelectedItem is AndroidApp app) win.Close(app); };
        cancel.Click += (_, _) => win.Close(null);
        win.Loaded += async (_, _) =>
        {
            all = await Scrcpy.ListAppsAsync(device.Serial);
            status.Text = all.Count > 0 ? $"{all.Count} apps. Double-click to launch." : "No apps found (see log).";
            list.ItemsSource = all;
            filter.IsEnabled = true;
            filter.Focus();
        };
        return await win.ShowDialog<AndroidApp?>(owner);
    }
}
