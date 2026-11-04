using System.IO;
using System.Text.Json;

namespace MirrorHub;

public enum StartMinimizedMode { Never, AtLogon, Always }

/// <summary>Android screen-mirroring options (default set, or one per device profile).</summary>
public sealed class MirrorOptions
{
    public string VideoCodec { get; set; } = "h264";
    public int BitRateMbps { get; set; } = 8;
    public int MaxFps { get; set; } = 60;
    public int MaxSize { get; set; } = 0;          // 0 = native resolution
    public int VideoBufferMs { get; set; } = 0;
    public bool Audio { get; set; } = true;
    public string AudioCodec { get; set; } = "opus";
    public int AudioBufferMs { get; set; } = 50;
    public string HwDec { get; set; } = "auto";
    public string RenderFit { get; set; } = "letterbox";
    public bool Control { get; set; } = true;
    public bool StayAwake { get; set; } = true;
    public bool TurnScreenOff { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool Fullscreen { get; set; }
    public bool Borderless { get; set; }
    public bool Record { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public bool AutoMirror { get; set; }           // start mirroring when the device connects

    public MirrorOptions Clone() => (MirrorOptions)MemberwiseClone();
}

/// <summary>Per-device settings, keyed by the device hardware serial (ro.serialno).</summary>
public sealed class DeviceProfile
{
    public string Name { get; set; } = "";
    public MirrorOptions Options { get; set; } = new();
}

public sealed class NotifyOptions
{
    public bool DeviceConnected { get; set; } = true;
    public bool DeviceDisconnected { get; set; } = true;
    public bool MirrorEvents { get; set; } = true;     // connection lost / reconnecting / stopped
    public bool AirPlayClient { get; set; } = true;
    public bool FileSaved { get; set; } = true;
}

public sealed class CameraOptions
{
    public string Camera { get; set; } = "back";       // "back", "front", "external" or "id:<n>"
    public int MaxSize { get; set; } = 1920;
    public int Fps { get; set; } = 30;
    public double Zoom { get; set; } = 1.0;
    public string Orientation { get; set; } = "0";     // 0, 90, 180, 270
    public bool Flip { get; set; }
    public string VideoCodec { get; set; } = "h264";
    public int BitRateMbps { get; set; } = 8;
    public bool Microphone { get; set; }
    public bool Torch { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool Borderless { get; set; }
    public bool Record { get; set; }
}

public static class HotkeyActions
{
    public const string ToggleMirror = "ToggleMirror";
    public const string ToggleAirPlay = "ToggleAirPlay";
    public const string Screenshot = "Screenshot";
    public const string ToggleCamera = "ToggleCamera";
    public const string ToggleWindow = "ToggleWindow";

    public static readonly (string Id, string Label, string Default)[] All =
    [
        (ToggleMirror, "Start / stop Android mirroring", "Ctrl+Alt+M"),
        (ToggleAirPlay, "Start / stop AirPlay receiver", "Ctrl+Alt+A"),
        (Screenshot, "Android screenshot", "Ctrl+Alt+S"),
        (ToggleCamera, "Start / stop camera", "Ctrl+Alt+C"),
        (ToggleWindow, "Show / hide MirrorHub", "Ctrl+Alt+H"),
    ];
}

/// <summary>User settings, persisted to %AppData%\MirrorHub\settings.json.</summary>
public sealed class Settings
{
    // Startup and notification area
    public bool StartWithWindows { get; set; }
    public StartMinimizedMode MinimizedMode { get; set; } = StartMinimizedMode.AtLogon;
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    public NotifyOptions Notify { get; set; } = new();
    public Dictionary<string, string> Hotkeys { get; set; } = HotkeyActions.All.ToDictionary(a => a.Id, a => a.Default);

    // Android
    public MirrorOptions Mirror { get; set; } = new();
    public Dictionary<string, DeviceProfile> Profiles { get; set; } = new();
    public CameraOptions Camera { get; set; } = new();

    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MirrorHub");

    // iPhone / iPad (AirPlay receiver)
    public string AirPlayName { get; set; } = "MirrorHub";
    public string AirPlayPin { get; set; } = "";       // 4 digits, empty = no PIN
    public string AirPlayResolution { get; set; } = "1920x1080@60";
    public int AirPlayFps { get; set; } = 60;
    public bool AirPlayH265 { get; set; }
    public string AirPlayVideoSink { get; set; } = "d3d12videosink";
    public bool AirPlayFullscreen { get; set; }
    public bool AirPlayNoHold { get; set; } = true;
    public bool AirPlayRecord { get; set; }
    public bool AirPlayAutoStart { get; set; }

    /// <summary>The device's own profile if it has one, otherwise the default options.</summary>
    public MirrorOptions OptionsFor(string? hwId) =>
        hwId != null && Profiles.TryGetValue(hwId, out var p) ? p.Options : Mirror;

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MirrorHub", "settings.json");

    public static Settings Load()
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { s = new(); }
        foreach (var a in HotkeyActions.All) s.Hotkeys.TryAdd(a.Id, a.Default);   // actions added in later versions
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static readonly Dictionary<string, Action<MirrorOptions>> Presets = new()
    {
        ["Low latency"] = o => { o.VideoCodec = "h264"; o.BitRateMbps = 8; o.MaxFps = 60; o.MaxSize = 1280; o.VideoBufferMs = 0; o.AudioBufferMs = 40; },
        ["High quality"] = o => { o.VideoCodec = "h265"; o.BitRateMbps = 20; o.MaxFps = 60; o.MaxSize = 0; o.VideoBufferMs = 0; o.AudioBufferMs = 50; },
        ["Weak Wi-Fi"] = o => { o.VideoCodec = "h265"; o.BitRateMbps = 4; o.MaxFps = 30; o.MaxSize = 1024; o.VideoBufferMs = 50; o.AudioBufferMs = 80; },
    };
}
