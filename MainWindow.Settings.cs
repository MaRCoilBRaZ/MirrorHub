using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MirrorHub;

/// <summary>"Impostazioni" tab: startup, notification area, notifications, global hotkeys, files.</summary>
public partial class MainWindow
{
    readonly Dictionary<string, TextBox> _hotkeyBoxes = new();

    void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) { Log("Settings not saved: " + ex.Message); }
    }

    void SaveSettings_Click(object sender, RoutedEventArgs e) => SaveSettings();

    void StartupOption_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Startup.Apply(_settings.StartWithWindows);
            Log(_settings.StartWithWindows ? "Start with Windows enabled." : "Start with Windows disabled.");
        }
        catch (Exception ex) { Log("Startup setting not applied: " + ex.Message); }
        SaveSettings();
    }

    void MinimizedMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MinimizedModeBox.SelectedIndex < 0) return;
        _settings.MinimizedMode = (StartMinimizedMode)MinimizedModeBox.SelectedIndex;
        SaveSettings();
    }

    void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Folder for recordings and screenshots",
            InitialDirectory = Directory.Exists(_settings.OutputDir) ? _settings.OutputDir : "",
        };
        if (dlg.ShowDialog(this) != true) return;
        _settings.OutputDir = dlg.FolderName;
        SaveSettings();
        DataContext = null;          // Settings has no change notification: rebind
        DataContext = _settings;
    }

    // ---------- global hotkeys ----------

    void RegisterHotkeys()
    {
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
    }

    Action HotkeyAction(string id) => id switch
    {
        HotkeyActions.ToggleMirror => () => ToggleMirror(),
        HotkeyActions.ToggleAudioOnly => () => ToggleAudioOnly(),
        HotkeyActions.ToggleAirPlay => () => ToggleAirPlay(),
        HotkeyActions.ToggleAirAudio => () => ToggleAirPlay(audioOnly: true),
        HotkeyActions.Screenshot => () => _ = TakeScreenshot(),
        HotkeyActions.ToggleCamera => () => Camera.Toggle(),
        HotkeyActions.ToggleWindow => ToggleWindow,
        _ => () => { },
    };

    void BuildHotkeyEditors()
    {
        var row = 0;
        foreach (var (id, label, _) in HotkeyActions.All)
        {
            HotkeyGrid.RowDefinitions.Add(new RowDefinition());

            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 6) };
            var box = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = false, Cursor = Cursors.Hand, Tag = id };
            box.PreviewKeyDown += HotkeyBox_PreviewKeyDown;
            // While editing, global hotkeys are released so the pressed combination reaches the box.
            box.GotKeyboardFocus += (_, _) => { _hotkeys.Clear(); box.Background = Brushes.LightYellow; };
            box.LostKeyboardFocus += (_, _) => { box.ClearValue(BackgroundProperty); RegisterHotkeys(); };
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

    void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return;   // Tab still moves focus
        e.Handled = true;
        if (key == Key.Escape) { Keyboard.ClearFocus(); return; }

        var gesture = GlobalHotkeys.Format(Keyboard.Modifiers, key);
        if (gesture == null) return;   // wait for modifier + key

        var id = (string)box.Tag;
        foreach (var other in _settings.Hotkeys.Where(kv => kv.Key != id && kv.Value == gesture).Select(kv => kv.Key).ToList())
            _settings.Hotkeys[other] = "";   // a combination belongs to one action only
        _settings.Hotkeys[id] = gesture;
        SaveSettings();
        UpdateHotkeyBoxes();
        Keyboard.ClearFocus();             // re-registers on focus loss
    }

    void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (id, _, def) in HotkeyActions.All) _settings.Hotkeys[id] = def;
        SaveSettings();
        UpdateHotkeyBoxes();
        RegisterHotkeys();
    }
}
