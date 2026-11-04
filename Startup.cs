using Microsoft.Win32;

namespace MirrorHub;

/// <summary>Start with Windows through the per-user Run key (no admin rights needed).</summary>
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "MirrorHub";
    public const string AutostartArg = "--autostart";

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
