using System.IO;

namespace MirrorHub;

/// <summary>
/// Copy of the log in %AppData%\MirrorHub\mirrorhub.log (the previous session is kept as
/// mirrorhub.old.log), so a problem can still be reported after the app was closed.
/// </summary>
static class LogFile
{
    static readonly string Dir = AppPaths.DataDir;
    static readonly string FilePath = Path.Combine(Dir, "mirrorhub.log");
    static bool _started, _failed;

    public static void Append(string line)
    {
        if (_failed) return;
        try
        {
            if (!_started)
            {
                _started = true;
                Directory.CreateDirectory(Dir);
                if (File.Exists(FilePath)) File.Move(FilePath, Path.Combine(Dir, "mirrorhub.old.log"), overwrite: true);
            }
            File.AppendAllText(FilePath, line);
        }
        catch { _failed = true; }
    }
}
