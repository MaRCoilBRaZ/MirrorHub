using System.Windows;
using System.Windows.Controls;

namespace MirrorHub;

/// <summary>Dedicated window: the phone camera mirrored to the PC (scrcpy --video-source=camera).</summary>
public partial class CameraWindow : Window
{
    sealed record CameraChoice(string Value, string Label);

    static readonly CameraChoice[] FacingChoices =
    [
        new("back", "Back"),
        new("front", "Front"),
        new("external", "External (USB)"),
    ];

    readonly Settings _settings;
    readonly Action<string> _log;
    readonly Action<string, string> _notifySaved;   // title, text
    WatchedProcess? _session;
    bool _shuttingDown;

    public bool IsRunning => _session != null;

    public CameraWindow(Settings settings, Action<string> log, Action<string, string> notifySaved)
    {
        InitializeComponent();
        _settings = settings;
        _log = m => log("[Camera] " + m);
        _notifySaved = notifySaved;
        OrientationBox.ItemsSource = new[] { "0", "90", "180", "270" };
        CodecBox.ItemsSource = new[] { "h264", "h265", "av1" };
        DataContext = settings.Camera;
        SetCameraChoices([]);
        Loaded += async (_, _) => await RefreshDevicesAsync();
    }

    Device? SelectedDevice => DeviceBox.SelectedItem as Device;

    async Task RefreshDevicesAsync()
    {
        var keep = SelectedDevice?.Serial;
        var devices = (await Adb.DevicesAsync()).Where(d => d.State == "device").ToList();
        DeviceBox.ItemsSource = devices;
        DeviceBox.SelectedItem = devices.FirstOrDefault(d => d.Serial == keep) ?? devices.FirstOrDefault();
        if (devices.Count == 0) Status.Text = "No Android device connected";
    }

    void SetCameraChoices(IEnumerable<CameraInfo> cams)
    {
        var choices = FacingChoices
            .Concat(cams.Select(c => new CameraChoice("id:" + c.Id, c.Label)))
            .ToList();
        // A saved "id:n" that is not listed (yet) is kept selectable.
        if (choices.All(c => c.Value != _settings.Camera.Camera))
            choices.Add(new CameraChoice(_settings.Camera.Camera, _settings.Camera.Camera));
        CameraBox.ItemsSource = choices;
        CameraBox.SelectedValue = _settings.Camera.Camera;
    }

    async void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedDevice is not { } d || Tools.Scrcpy == null) return;
        if (!IsRunning) Status.Text = "Detecting cameras…";
        var (cams, output) = await Scrcpy.ListCamerasAsync(d.Serial);
        if (SelectedDevice?.Serial != d.Serial) return;   // selection changed meanwhile
        SetCameraChoices(cams);
        if (!IsRunning)
            Status.Text = cams.Count > 0 ? $"Stopped · {cams.Count} cameras found" : "Stopped · no cameras detected";
        if (cams.Count == 0) _log(output.Split('\n').LastOrDefault(l => l.Contains("ERROR"))?.Trim() ?? "no cameras listed");
    }

    void Camera_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CameraBox.SelectedValue is string v) _settings.Camera.Camera = v;
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    void Start_Click(object sender, RoutedEventArgs e) => _ = StartAsync();

    void Stop_Click(object sender, RoutedEventArgs e) => Stop();

    public async Task StartAsync()
    {
        if (IsRunning) return;
        if (Tools.Scrcpy == null) { _log("scrcpy not found."); return; }
        if (SelectedDevice == null) await RefreshDevicesAsync();
        if (SelectedDevice is not { } d) { Status.Text = "No Android device connected"; return; }

        try { _settings.Save(); } catch { }
        var record = _settings.Camera.Record;
        _session = Scrcpy.CreateCamera(d, _settings.Camera, _settings.OutputDir);
        _session.Log += _log;
        _session.Ended += unexpected => Dispatcher.Invoke(() =>
        {
            _session = null;
            Status.Text = unexpected ? "Interrupted (see log)" : "Stopped";
            StartButton.IsEnabled = true;
            if (record) _notifySaved("Camera recording saved", _settings.OutputDir);
        });
        Status.Text = $"Running · {d.Display}";
        StartButton.IsEnabled = false;
        _session.Start();
    }

    public void Stop() => _session?.Stop();

    public void Toggle()
    {
        if (IsRunning) Stop();
        else _ = StartAsync();
    }

    /// <summary>Closing the window only hides it: the camera keeps running until stopped.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_shuttingDown)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        Stop();
        Close();
    }
}
