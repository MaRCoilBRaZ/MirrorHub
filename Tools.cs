using System.IO;

namespace MirrorHub;

/// <summary>
/// Locates external executables. The portable release ships them in a "tools" folder next to
/// the MirrorHub executable; those copies win over anything installed on the system.
/// </summary>
static class Tools
{
    static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Declared first: static initializers run in textual order.
    static readonly string BundleDir = Path.Combine(AppContext.BaseDirectory, "tools");

    public static string? Scrcpy { get; } = Bundled("scrcpy", Os.Exe("scrcpy")) ?? FindScrcpy();

    // A single adb for everything (also passed to scrcpy via the ADB env var),
    // so two different adb versions never fight over the server on port 5037.
    public static string Adb { get; } = FindAdb();

    // Bundled UxPlay (bin/ + lib/gstreamer-1.0/), or on system PATH, or MSYS2 UCRT64 on Windows.
    public static string? UxPlay { get; } =
        Bundled("uxplay", "bin", Os.Exe("uxplay")) ??
        Bundled("uxplay", Os.Exe("uxplay")) ??
        OnPath(Os.Exe("uxplay")) ??
        (OperatingSystem.IsWindows() && File.Exists(@"C:\msys64\ucrt64\bin\uxplay.exe")
            ? @"C:\msys64\ucrt64\bin\uxplay.exe"
            : null);

    /// <summary>True when UxPlay comes from the portable "tools" folder (with its own GStreamer).</summary>
    public static bool UxPlayIsBundled => UxPlay != null && UxPlay.StartsWith(BundleDir, PathComparison);

    /// <summary>Why scrcpy/uxplay are missing, in words a non-technical user can act on.</summary>
    public static string MissingToolsHint =>
        OperatingSystem.IsWindows()
            ? AppContext.BaseDirectory.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
                ? "MirrorHub was opened directly from inside the zip file, so it cannot find its tools.\n\n" +
                  "Close it, right-click the zip → \"Extract All…\", then open MirrorHub.exe from the extracted folder."
                : "The \"tools\" folder was not found next to MirrorHub.exe.\n\n" +
                  "Keep the whole extracted folder together (MirrorHub.exe and the \"tools\" folder): " +
                  "extract the zip again and open MirrorHub.exe from there."
            : "The \"tools\" folder was not found next to MirrorHub. Extract the archive again (tar -xzf ...) and keep the whole folder together, or install scrcpy with your package manager.";

    public static string MissingUxPlayHint =>
        OperatingSystem.IsWindows()
            ? MissingToolsHint
            : "UxPlay is not installed. Debian/Ubuntu: sudo apt install uxplay gstreamer1.0-plugins-bad gstreamer1.0-libav gstreamer1.0-gl avahi-daemon";

    public static string SafeName(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));

    static string? Bundled(params string[] parts)
    {
        var p = Path.Combine([BundleDir, .. parts]);
        return File.Exists(p) ? p : null;
    }

    static IEnumerable<string> PathDirs()
    {
        var vars = OperatingSystem.IsWindows()
            ? new[]
            {
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            }
            : [Environment.GetEnvironmentVariable("PATH")];

        return vars
            .Where(s => s != null)
            .SelectMany(s => s!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(PathComparer);
    }

    static string? OnPath(string exe)
    {
        foreach (var dir in PathDirs())
        {
            string p;
            // GetFullPath also normalizes odd PATH entries such as "C:\dir\bin." (trailing dot).
            try { p = Path.GetFullPath(Path.Combine(dir, exe)); } catch { continue; }
            if (File.Exists(p)) return Resolve(p);
        }
        return null;
    }

    // winget / package managers may install a symlink alias; scrcpy needs its real folder (scrcpy-server lives there).
    static string Resolve(string p)
    {
        try { return new FileInfo(p).ResolveLinkTarget(true)?.FullName ?? p; }
        catch { return p; }
    }

    static string? FindScrcpy() =>
        OnPath(Os.Exe("scrcpy")) ?? Os.FindWinGetScrcpy();

    static string FindAdb()
    {
        var bundled = Bundled("scrcpy", Os.Exe("adb"));
        if (bundled != null) return bundled;
        var p = OnPath(Os.Exe("adb"));
        if (p != null) return p;
        if (Scrcpy != null)
        {
            var nextToScrcpy = Path.Combine(Path.GetDirectoryName(Scrcpy)!, Os.Exe("adb"));
            if (File.Exists(nextToScrcpy)) return nextToScrcpy;
        }
        return "adb";
    }
}
