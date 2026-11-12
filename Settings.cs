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
    public bool AudioOnPhoneToo { get; set; }      // keep playing on the phone as well (Android 13+)
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
    public const string ToggleAudioOnly = "ToggleAudioOnly";
    public const string ToggleAirPlay = "ToggleAirPlay";
    public const string ToggleAirAudio = "ToggleAirAudio";
    public const string Screenshot = "Screenshot";
    public const string ToggleCamera = "ToggleCamera";
    public const string ToggleWindow = "ToggleWindow";

    public static readonly (string Id, string Label, string Default)[] All =
    [
        (ToggleMirror, "Start / stop Android mirroring", "Ctrl+Alt+M"),
        (ToggleAudioOnly, "Start / stop Android audio only", "Ctrl+Alt+P"),
        (ToggleAirPlay, "Start / stop AirPlay receiver", "Ctrl+Alt+A"),
        (ToggleAirAudio, "Start / stop iPhone audio only", "Ctrl+Alt+I"),
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

    // UI: advanced settings expanded
    public bool ShowAndroidAdvanced { get; set; }
    public bool ShowAirPlayAdvanced { get; set; }

    public NotifyOptions Notify { get; set; } = new();
    public Dictionary<string, string> Hotkeys { get; set; } = HotkeyActions.All.ToDictionary(a => a.Id, a => a.Default);

    // Android
    public MirrorOptions Mirror { get; set; } = new();
    public Dictionary<string, DeviceProfile> Profiles { get; set; } = new();
    public CameraOptions Camera { get; set; } = new();

    public string OutputDir { get; set; } = AppPaths.DefaultOutputDir;

    // iPhone / iPad (AirPlay receiver)
    public string AirPlayName { get; set; } = "MirrorHub";
    public string AirPlayPin { get; set; } = "";       // 4 digits, empty = no PIN
    public string AirPlayResolution { get; set; } = "1920x1080@60";
    public int AirPlayFps { get; set; } = 60;
    public bool AirPlayH265 { get; set; }
    // "auto" = UxPlay's default renderer, the safest choice on unknown hardware.
    public string AirPlayVideoSink { get; set; } = "auto";
    public bool AirPlayFullscreen { get; set; }
    public bool AirPlayNoHold { get; set; } = true;
    public bool AirPlayRecord { get; set; }
    public int AirPlayReceivers { get; set; } = 1;     // iPhones/iPads mirrored at the same time
    public int AirPlayAudioDelayMs { get; set; }       // > 0 plays the sound later (when it comes before the picture)
    public bool AirPlayLowLatency { get; set; }        // show frames as soon as they arrive, no audio/video timestamps
    public bool AirPlayAutoStart { get; set; }

    /// <summary>The device's own profile if it has one, otherwise the default options.</summary>
    public MirrorOptions OptionsFor(string? hwId) =>
        hwId != null && Profiles.TryGetValue(hwId, out var p) ? p.Options : Mirror;

    static readonly string FilePath = Path.Combine(AppPaths.DataDir, "settings.json");

    public static Settings Load()
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { s = new(); }
        foreach (var a in HotkeyActions.All) s.Hotkeys.TryAdd(a.Id, a.Default);   // actions added in later versions
        if (!Os.HwDecoders.Contains(s.Mirror.HwDec)) s.Mirror.HwDec = "auto";
        foreach (var p in s.Profiles.Values)
            if (!Os.HwDecoders.Contains(p.Options.HwDec)) p.Options.HwDec = "auto";
        if (!Os.AirPlaySinks.Contains(s.AirPlayVideoSink)) s.AirPlayVideoSink = "auto";
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------- quality presets (the simple view; the advanced fields stay editable) ----------

    // The first preset equals the defaults of MirrorOptions.
    public static readonly QualityPreset<MirrorOptions>[] AndroidPresets =
    [
        new("Balanced", "Recommended. Full resolution, 60 fps, works with every phone.",
            o => { o.VideoCodec = "h264"; o.BitRateMbps = 8; o.MaxFps = 60; o.MaxSize = 0; o.VideoBufferMs = 0; o.AudioBufferMs = 50; },
            o => (o.VideoCodec, o.BitRateMbps, o.MaxFps, o.MaxSize, o.VideoBufferMs, o.AudioBufferMs)),
        new("Low latency", "Less input lag, for games: lower resolution (1280 px). A USB cable lags less than Wi-Fi.",
            o => { o.VideoCodec = "h264"; o.BitRateMbps = 8; o.MaxFps = 60; o.MaxSize = 1280; o.VideoBufferMs = 0; o.AudioBufferMs = 40; },
            o => (o.VideoCodec, o.BitRateMbps, o.MaxFps, o.MaxSize, o.VideoBufferMs, o.AudioBufferMs)),
        new("Best quality", "Sharpest picture (H.265, 20 Mbps). Best over USB or fast Wi-Fi.",
            o => { o.VideoCodec = "h265"; o.BitRateMbps = 20; o.MaxFps = 60; o.MaxSize = 0; o.VideoBufferMs = 0; o.AudioBufferMs = 50; },
            o => (o.VideoCodec, o.BitRateMbps, o.MaxFps, o.MaxSize, o.VideoBufferMs, o.AudioBufferMs)),
        new("Weak Wi-Fi", "Fewer stutters on a slow connection (smaller picture, 30 fps).",
            o => { o.VideoCodec = "h265"; o.BitRateMbps = 4; o.MaxFps = 30; o.MaxSize = 1024; o.VideoBufferMs = 50; o.AudioBufferMs = 80; },
            o => (o.VideoCodec, o.BitRateMbps, o.MaxFps, o.MaxSize, o.VideoBufferMs, o.AudioBufferMs)),
    ];

    // The first preset equals the defaults of Settings.
    public static readonly QualityPreset<Settings>[] AirPlayPresets =
    [
        new("Full HD", "Recommended. 1080p at 60 fps.",
            s => { s.AirPlayResolution = "1920x1080@60"; s.AirPlayFps = 60; s.AirPlayH265 = false; },
            s => (s.AirPlayResolution, s.AirPlayFps, s.AirPlayH265)),
        new("Sharper", "1440p at 60 fps, for large monitors.",
            s => { s.AirPlayResolution = "2560x1440@60"; s.AirPlayFps = 60; s.AirPlayH265 = false; },
            s => (s.AirPlayResolution, s.AirPlayFps, s.AirPlayH265)),
        new("4K", "4K with H.265. Needs a recent iPhone/iPad and a fast Wi-Fi.",
            s => { s.AirPlayResolution = "3840x2160@60"; s.AirPlayFps = 60; s.AirPlayH265 = true; },
            s => (s.AirPlayResolution, s.AirPlayFps, s.AirPlayH265)),
    ];
}

/// <summary>A named set of values; <see cref="Matches"/> tells whether the current values equal it.</summary>
public sealed record QualityPreset<T>(string Name, string Description, Action<T> Apply, Func<T, object> Key)
{
    public bool Matches(T current, T scratch)
    {
        Apply(scratch);
        return Equals(Key(scratch), Key(current));
    }
}
