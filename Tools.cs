using System.IO;

namespace MirrorHub;

/// <summary>
/// Locates external executables. The portable release ships them in a "tools" folder next to
/// MirrorHub.exe; those copies win over anything installed on the system.
/// </summary>
static class Tools
{
    // Declared first: static initializers run in textual order.
    static readonly string BundleDir = Path.Combine(AppContext.BaseDirectory, "tools");

    public static string? Scrcpy { get; } = Bundled("scrcpy", "scrcpy.exe") ?? FindScrcpy();

    // A single adb for everything (also passed to scrcpy via the ADB env var),
    // so two different adb versions never fight over the server on port 5037.
    public static string Adb { get; } = FindAdb();

    // Bundled UxPlay (bin/ + lib/gstreamer-1.0/), or one built with MSYS2 UCRT64.
    public static string? UxPlay { get; } =
        Bundled("uxplay", "bin", "uxplay.exe") ??
        OnPath("uxplay.exe") ??
        (File.Exists(@"C:\msys64\ucrt64\bin\uxplay.exe") ? @"C:\msys64\ucrt64\bin\uxplay.exe" : null);

    /// <summary>True when UxPlay comes from the portable "tools" folder (with its own GStreamer).</summary>
    public static bool UxPlayIsBundled => UxPlay != null && UxPlay.StartsWith(BundleDir, StringComparison.OrdinalIgnoreCase);

    public static string SafeName(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));

    static string? Bundled(params string[] parts)
    {
        var p = Path.Combine([BundleDir, .. parts]);
        return File.Exists(p) ? p : null;
    }

    static IEnumerable<string> PathDirs() =>
        new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        }
        .Where(s => s != null)
        .SelectMany(s => s!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Distinct(StringComparer.OrdinalIgnoreCase);

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

    // winget installs a symlink alias; scrcpy needs its real folder (scrcpy-server lives there).
    static string Resolve(string p)
    {
        try { return new FileInfo(p).ResolveLinkTarget(true)?.FullName ?? p; }
        catch { return p; }
    }

    static string? FindScrcpy()
    {
        var p = OnPath("scrcpy.exe");
        if (p != null) return p;

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(root)) return null;
        foreach (var d in Directory.EnumerateDirectories(root, "Genymobile.scrcpy*"))
        {
            var f = Directory.EnumerateFiles(d, "scrcpy.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (f != null) return f;
        }
        return null;
    }

    static string FindAdb()
    {
        var p = OnPath("adb.exe");
        if (p != null) return p;
        if (Scrcpy != null)
        {
            var bundled = Path.Combine(Path.GetDirectoryName(Scrcpy)!, "adb.exe");
            if (File.Exists(bundled)) return bundled;
        }
        return "adb";
    }
}
