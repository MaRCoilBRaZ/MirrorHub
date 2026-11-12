namespace MirrorHub;

public sealed record AppCommand(string Name, string? HotkeyId, string Label, Action<MainWindow> Execute);

/// <summary>Single source of truth for commands invoked via global hotkeys or the command pipe.</summary>
static class AppCommands
{
    static string HotkeyLabel(string id) => HotkeyActions.All.First(a => a.Id == id).Label;

    public static readonly AppCommand[] All =
    [
        new("show", null, "Show MirrorHub", w => w.ShowFromTray()),
        new("toggle-window", HotkeyActions.ToggleWindow, HotkeyLabel(HotkeyActions.ToggleWindow), w => w.ToggleWindow()),
        new("toggle-mirror", HotkeyActions.ToggleMirror, HotkeyLabel(HotkeyActions.ToggleMirror), w => w.ToggleMirror()),
        new("toggle-audio-only", HotkeyActions.ToggleAudioOnly, HotkeyLabel(HotkeyActions.ToggleAudioOnly), w => w.ToggleAudioOnly()),
        new("toggle-airplay", HotkeyActions.ToggleAirPlay, HotkeyLabel(HotkeyActions.ToggleAirPlay), w => w.ToggleAirPlay()),
        new("toggle-airplay-audio", HotkeyActions.ToggleAirAudio, HotkeyLabel(HotkeyActions.ToggleAirAudio), w => w.ToggleAirPlay(audioOnly: true)),
        new("toggle-camera", HotkeyActions.ToggleCamera, HotkeyLabel(HotkeyActions.ToggleCamera), w => w.ToggleCamera()),
        new("screenshot", HotkeyActions.Screenshot, HotkeyLabel(HotkeyActions.Screenshot), w => _ = w.TakeScreenshot()),
        new("exit", null, "Exit MirrorHub", w => w.ExitApp()),
    ];

    public static AppCommand? FindByName(string name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    public static AppCommand? FindByHotkeyId(string id) =>
        All.FirstOrDefault(c => c.HotkeyId == id);
}
