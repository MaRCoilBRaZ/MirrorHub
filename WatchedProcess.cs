using System.Diagnostics;
using System.IO;

namespace MirrorHub;

/// <summary>
/// An external process (scrcpy, uxplay) with a watchdog that relaunches it with
/// exponential backoff when it exits unexpectedly. Arguments are rebuilt on every
/// launch, so setting changes apply to relaunches.
/// </summary>
public sealed class WatchedProcess(
    string exe,
    Func<IEnumerable<string>> args,
    Func<bool> restartOnCrash,
    IReadOnlyDictionary<string, string>? env = null,
    Func<Task>? beforeRelaunch = null)
{
    const int MaxRetries = 5;

    Process? _process;
    bool _stopRequested;
    int _retries;
    DateTime _startedAt;

    public event Action<string>? Log;
    /// <summary>Raised before each relaunch attempt (1-based).</summary>
    public event Action<int>? Restarting;
    /// <summary>Raised once when the session is over; true = it ended unexpectedly (crash, retries exhausted).</summary>
    public event Action<bool>? Ended;

    public void Start()
    {
        _stopRequested = false;
        _retries = 0;
        Launch();
    }

    public void Stop()
    {
        _stopRequested = true;
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
    }

    void Launch()
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        foreach (var a in args()) psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(e.Data); };
        p.Exited += (_, _) => OnExited(p);
        try { p.Start(); }
        catch (Exception ex)
        {
            Log?.Invoke($"Failed to start {Path.GetFileName(exe)}: {ex.Message}");
            Ended?.Invoke(true);
            return;
        }
        ChildProcesses.Track(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _process = p;
        _startedAt = DateTime.UtcNow;
    }

    async void OnExited(Process p)
    {
        var code = p.ExitCode;
        Log?.Invoke($"{Path.GetFileNameWithoutExtension(exe)} exited (code {code})");

        // A run that was stable earns a fresh retry budget.
        if (DateTime.UtcNow - _startedAt > TimeSpan.FromSeconds(20)) _retries = 0;

        // Exit code 0 = closed normally (e.g. user closed the mirror window): do not relaunch.
        var unexpected = !_stopRequested && code != 0;
        if (!unexpected || !restartOnCrash() || _retries >= MaxRetries)
        {
            Ended?.Invoke(unexpected);
            return;
        }

        _retries++;
        var delay = TimeSpan.FromSeconds(Math.Pow(2, _retries - 1));
        Log?.Invoke($"Restart {_retries}/{MaxRetries} in {delay.TotalSeconds:0}s…");
        Restarting?.Invoke(_retries);
        await Task.Delay(delay);
        if (_stopRequested) { Ended?.Invoke(false); return; }
        if (beforeRelaunch != null) await beforeRelaunch();
        Launch();
    }
}
