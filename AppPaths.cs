using System.IO;

namespace MirrorHub;

/// <summary>Centralized absolute paths for application data and default user output directories.</summary>
static class AppPaths
{
    public static string DataDir { get; } = ComputeDataDir();

    public static string DefaultOutputDir { get; } = ComputeDefaultOutputDir();

    public static string HomeDir()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home) && Path.IsPathRooted(home)) return home;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        return !string.IsNullOrEmpty(profile) ? profile : AppContext.BaseDirectory;
    }

    static string ComputeDataDir()
    {
        var dir = Path.GetFullPath(Path.Combine(Os.ConfigDir, "MirrorHub"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string ComputeDefaultOutputDir()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos) || !Path.IsPathRooted(videos))
            videos = Path.Combine(HomeDir(), "Videos");
        return Path.GetFullPath(Path.Combine(videos, "MirrorHub"));
    }
}
