using Microsoft.Win32;

namespace MirrorHub;

/// <summary>Start with Windows through the per-user Run key (no admin rights needed).</summary>
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "MirrorHub";
    public const string AutostartArg = "--autostart";
    // Passed by "Restart": wait for the old instance to exit, keep the window and the receiver as they were.
    public const string RestartArg = "--restart";
    public const string ShowArg = "--show";
    public const string AirPlayArg = "--airplay";
    public const string AirAudioArg = "--airplay-audio";
    public static bool HasArg(string arg) => Environment.GetCommandLineArgs().Contains(arg);

    /// <summary>True when this process was launched by Windows at logon.</summary>
    public static bool LaunchedAtLogon => Environment.GetCommandLineArgs().Contains(AutostartArg);

    /// <summary>Writes or removes the Run entry. Rewriting it on every launch keeps the path valid if the app is moved.</summary>
    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {AutostartArg}");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
