namespace MirrorHub;

/// <summary>Command-line flags shared across startup and single-instance restarts.</summary>
static class Startup
{
    public const string AutostartArg = "--autostart";
    // Passed by "Restart": wait for the old instance to exit, keep the window and the receiver as they were.
    public const string RestartArg = "--restart";
    public const string ShowArg = "--show";
    public const string AirPlayArg = "--airplay";
    public const string AirAudioArg = "--airplay-audio";
    public const string CommandArg = "--command";

    public static bool HasArg(string arg) => Environment.GetCommandLineArgs().Contains(arg);

    /// <summary>True when this process was launched by the OS at logon.</summary>
    public static bool LaunchedAtLogon => Environment.GetCommandLineArgs().Contains(AutostartArg);
}
