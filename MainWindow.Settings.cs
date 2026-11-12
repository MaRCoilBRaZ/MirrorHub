using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace MirrorHub;

/// <summary>"Settings" tab: startup, notification area, notifications, global hotkeys, files.</summary>
public partial class MainWindow
{
    readonly Dictionary<string, TextBox> _hotkeyBoxes = new();

    void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) { Log("Settings not saved: " + ex.Message); }
    }

    void SaveSettings_Click(object? sender, RoutedEventArgs e) => SaveSettings();

    void StartupOption_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Os.ApplyAutostart(_settings.StartWithWindows);
            Log(_settings.StartWithWindows ? $"Start with {Os.Name} enabled." : $"Start with {Os.Name} disabled.");
        }
        catch (Exception ex) { Log("Startup setting not applied: " + ex.Message); }
        SaveSettings();
    }

    void MinimizedMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (MinimizedModeBox.SelectedIndex < 0) return;
        _settings.MinimizedMode = (StartMinimizedMode)MinimizedModeBox.SelectedIndex;
        SaveSettings();
    }

    async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var start = Directory.Exists(_settings.OutputDir)
            ? await top.StorageProvider.TryGetFolderFromPathAsync(_settings.OutputDir)
            : null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder for recordings and screenshots",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        _settings.OutputDir = path;
        SaveSettings();
        DataContext = null;          // Settings has no change notification: rebind
        DataContext = _settings;
    }

    // ---------- global hotkeys ----------

    void RegisterHotkeys()
    {
        if (!_hotkeys.IsSupported) return;
        _hotkeys.Clear();
        var failed = new List<string>();
        foreach (var (id, label, _) in HotkeyActions.All)
        {
            var gesture = _settings.Hotkeys.GetValueOrDefault(id) ?? "";
            if (gesture.Length > 0 && !_hotkeys.Register(gesture, HotkeyAction(id)))
                failed.Add($"{label} ({gesture})");
        }
        if (failed.Count > 0)
            Log("Shortcuts not active (combination already used by another app?): " + string.Join(", ", failed));
        InvalidateTray();
    }

    Action HotkeyAction(string id) =>
        AppCommands.FindByHotkeyId(id) is { } cmd ? () => cmd.Execute(this) : () => { };

    void BuildHotkeyEditors()
    {
        if (!_hotkeys.IsSupported)
        {
            HotkeyEditorPanel.IsVisible = false;
            HotkeyCommandsPanel.IsVisible = true;
            var exe = Environment.ProcessPath ?? "MirrorHub";
            var lines = AppCommands.All
                .Where(c => c.HotkeyId != null || c.Name == "show")
                .Select(c => $"\"{exe}\" --command {c.Name}   ({c.Label})");
            HotkeyCommandsBox.Text = string.Join(Environment.NewLine, lines);
            return;
        }

        var row = 0;
        foreach (var (id, label, _) in HotkeyActions.All)
        {
            HotkeyGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 6) };
            var box = new TextBox { IsReadOnly = true, Cursor = new Cursor(StandardCursorType.Hand), Tag = id };
            box.AddHandler(KeyDownEvent, HotkeyBox_KeyDown, RoutingStrategies.Tunnel);
            // While editing, global hotkeys are released so the pressed combination reaches the box.
            box.GotFocus += (_, _) => { _hotkeys.Clear(); box.Background = Brushes.LightYellow; };
            box.LostFocus += (_, _) => { box.ClearValue(BackgroundProperty); RegisterHotkeys(); };
            var clear = new Button { Content = "Remove", Margin = new Thickness(6, 0, 0, 6) };
            clear.Click += (_, _) =>
            {
                _settings.Hotkeys[id] = "";
                SaveSettings();
                UpdateHotkeyBoxes();
                RegisterHotkeys();
            };

            Grid.SetRow(text, row); Grid.SetColumn(text, 0);
            Grid.SetRow(box, row); Grid.SetColumn(box, 1);
            Grid.SetRow(clear, row); Grid.SetColumn(clear, 2);
            HotkeyGrid.Children.Add(text);
            HotkeyGrid.Children.Add(box);
            HotkeyGrid.Children.Add(clear);
            _hotkeyBoxes[id] = box;
            row++;
        }
        UpdateHotkeyBoxes();
    }

    void UpdateHotkeyBoxes()
    {
        foreach (var (id, box) in _hotkeyBoxes)
        {
            var gesture = _settings.Hotkeys.GetValueOrDefault(id) ?? "";
            box.Text = gesture.Length > 0 ? gesture : "None";
        }
    }

    void HotkeyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        var box = (TextBox)sender!;
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None) return;   // Tab still moves focus
        e.Handled = true;
        if (e.Key == Key.Escape) { TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus(); return; }

        var gesture = GlobalHotkeys.Format(e.KeyModifiers, e.Key);
        if (gesture == null) return;   // wait for modifier + key

        var id = (string)box.Tag!;
        foreach (var other in _settings.Hotkeys.Where(kv => kv.Key != id && kv.Value == gesture).Select(kv => kv.Key).ToList())
            _settings.Hotkeys[other] = "";   // a combination belongs to one action only
        _settings.Hotkeys[id] = gesture;
        SaveSettings();
        UpdateHotkeyBoxes();
        TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();             // re-registers on focus loss
    }

    void ResetHotkeys_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var (id, _, def) in HotkeyActions.All) _settings.Hotkeys[id] = def;
        SaveSettings();
        UpdateHotkeyBoxes();
        RegisterHotkeys();
    }

    async void CopyCommands_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip != null)
            {
                await clip.SetTextAsync(HotkeyCommandsBox.Text ?? "");
                Log("Commands copied.");
            }
        }
        catch (Exception ex) { Log("Could not copy commands: " + ex.Message); }
    }
}
