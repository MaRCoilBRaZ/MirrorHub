using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MirrorHub;

/// <summary>
/// The simple view: one "Quality" choice per tab, built from the presets. The advanced fields stay
/// editable; when they no longer match a preset the choice shows "Custom".
/// </summary>
public partial class MainWindow
{
    readonly List<(RadioButton Radio, Func<bool> Matches)> _androidChoices = [];
    readonly List<(RadioButton Radio, Func<bool> Matches)> _airChoices = [];
    RadioButton? _androidCustom, _airCustom;
    bool _updatingChoices, _choicesBuilt;

    MirrorOptions CurrentMirrorOptions => MirrorOptionsPanel.DataContext as MirrorOptions ?? _settings.Mirror;

    void BuildQualityChoices()
    {
        foreach (var p in Settings.AndroidPresets)
            _androidChoices.Add((AddChoice(AndroidQuality, "android", p.Name, p.Description, () =>
            {
                p.Apply(CurrentMirrorOptions);
                RebindAndroid();
                Log($"Android quality: {p.Name}");
            }), () => p.Matches(CurrentMirrorOptions, CurrentMirrorOptions.Clone())));
        _androidCustom = AddChoice(AndroidQuality, "android", "Custom", "Your own values (Advanced settings).", null);

        foreach (var p in Settings.AirPlayPresets)
            _airChoices.Add((AddChoice(AirQuality, "air", p.Name, p.Description, () =>
            {
                p.Apply(_settings);
                RebindAirPlay();
                Log($"iPhone quality: {p.Name}");
            }), () => p.Matches(_settings, new Settings())));
        _airCustom = AddChoice(AirQuality, "air", "Custom", "Your own values (Advanced settings).", null);

        AndroidAdvanced.IsExpanded = _settings.ShowAndroidAdvanced;
        AirAdvanced.IsExpanded = _settings.ShowAirPlayAdvanced;

        // An edit in the advanced fields can turn the choice into "Custom" (or back into a preset).
        foreach (var panel in new UIElement[] { AndroidAdvanced, AirAdvanced })
        {
            panel.AddHandler(LostFocusEvent, new RoutedEventHandler((_, _) => QueueChoiceUpdate()));
            panel.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => QueueChoiceUpdate()));
            panel.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => QueueChoiceUpdate()));
        }
        _choicesBuilt = true;
        UpdateQualityChoices();
    }

    RadioButton AddChoice(Panel parent, string group, string name, string description, Action? apply)
    {
        var radio = new RadioButton
        {
            GroupName = group,
            Margin = new Thickness(0, 0, 0, 6),
            IsEnabled = apply != null,      // "Custom" is only a state, chosen by editing the advanced values
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Inlines =
                {
                    new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold },
                    new System.Windows.Documents.Run("  —  " + description) { Foreground = System.Windows.Media.Brushes.Gray },
                },
            },
        };
        if (apply != null)
            radio.Checked += (_, _) =>
            {
                if (_updatingChoices) return;
                apply();
                SaveSettings();
                UpdateQualityChoices();
            };
        parent.Children.Add(radio);
        return radio;
    }

    void QueueChoiceUpdate() => Dispatcher.BeginInvoke(UpdateQualityChoices, System.Windows.Threading.DispatcherPriority.Background);

    void UpdateQualityChoices()
    {
        if (_androidCustom == null || _airCustom == null) return;
        _updatingChoices = true;
        try
        {
            Select(_androidChoices, _androidCustom);
            Select(_airChoices, _airCustom);
        }
        finally { _updatingChoices = false; }

        static void Select(List<(RadioButton Radio, Func<bool> Matches)> choices, RadioButton custom)
        {
            var match = choices.FirstOrDefault(c => c.Matches()).Radio ?? custom;
            match.IsChecked = true;
            custom.Visibility = match == custom ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // MirrorOptions / Settings have no change notification: rebind to show new values.
    void RebindAndroid()
    {
        var o = MirrorOptionsPanel.DataContext;
        MirrorOptionsPanel.DataContext = null;
        MirrorOptionsPanel.DataContext = o;
    }

    void RebindAirPlay()
    {
        AirPanel.DataContext = null;
        AirPanel.DataContext = _settings;
    }

    void Advanced_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_choicesBuilt || e.OriginalSource != sender) return;
        _settings.ShowAndroidAdvanced = AndroidAdvanced.IsExpanded;
        _settings.ShowAirPlayAdvanced = AirAdvanced.IsExpanded;
        SaveSettings();
    }
}
