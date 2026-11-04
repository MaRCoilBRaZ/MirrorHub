using System.Windows;
using System.Windows.Controls;

namespace MirrorHub;

/// <summary>Minimal modal text input.</summary>
static class Prompt
{
    public static string? Ask(Window owner, string title, string label)
    {
        var box = new TextBox { Margin = new Thickness(0, 6, 0, 10), MinWidth = 300 };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        var win = new Window
        {
            Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = label }, box, ok } },
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => box.Focus();
        return win.ShowDialog() == true ? box.Text.Trim() : null;
    }
}

/// <summary>Searchable list of the apps installed on a device.</summary>
static class AppPicker
{
    public static AndroidApp? Pick(Window owner, Device device)
    {
        var status = new TextBlock { Text = "Loading apps (this may take a few seconds)…", Margin = new Thickness(0, 0, 0, 6) };
        var filter = new TextBox { Margin = new Thickness(0, 0, 0, 6), IsEnabled = false };
        var list = new ListBox { Height = 380 };
        var ok = new Button { Content = "Launch", IsDefault = true, Width = 90, Margin = new Thickness(0, 8, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Margin = new Thickness(0, 8, 0, 0) };
        var win = new Window
        {
            Title = $"Launch app on {device.Model}", Owner = owner, Width = 480, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
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
        filter.TextChanged += (_, _) => list.ItemsSource = all
            .Where(a => a.Label.Contains(filter.Text, StringComparison.CurrentCultureIgnoreCase) ||
                        a.Package.Contains(filter.Text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem != null) win.DialogResult = true; };
        ok.Click += (_, _) => { if (list.SelectedItem != null) win.DialogResult = true; };
        win.Loaded += async (_, _) =>
        {
            all = await Scrcpy.ListAppsAsync(device.Serial);
            status.Text = all.Count > 0 ? $"{all.Count} apps. Double-click to launch." : "No apps found (see log).";
            list.ItemsSource = all;
            filter.IsEnabled = true;
            filter.Focus();
        };
        return win.ShowDialog() == true ? list.SelectedItem as AndroidApp : null;
    }
}
