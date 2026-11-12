using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MirrorHub;

static class Program
{
    static readonly TaskCompletionSource<MainWindow> WindowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static string? PendingStartupCommand { get; private set; }

    public static void NotifyWindowReady(MainWindow window) => WindowReady.TrySetResult(window);

    [STAThread]
    public static int Main(string[] args)
    {
        var commandArg = ParseCommandArg(args);
        using var instanceLock = Os.TryAcquireInstanceLock(Startup.HasArg(Startup.RestartArg));

        if (instanceLock == null)
            return SendCommandToRunningInstance(commandArg ?? "show");

        if (string.Equals(commandArg, "exit", StringComparison.Ordinal))
            return 0;

        PendingStartupCommand = commandArg;

        using var pipeCts = new CancellationTokenSource();
        _ = RunPipeServerAsync(pipeCts.Token);

        var code = BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

        pipeCts.Cancel();
        WindowReady.TrySetCanceled();
        GC.KeepAlive(instanceLock);
        return code;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    static string? ParseCommandArg(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == Startup.CommandArg) return args[i + 1];
        }
        return null;
    }

    static string PipeName()
    {
        var user = Regex.Replace(Environment.UserName, @"[^A-Za-z0-9_-]", "_");
        return "MirrorHub.Commands." + (user.Length > 0 ? user : "user");
    }

    static int SendCommandToRunningInstance(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(5000);
            var utf8NoBom = new UTF8Encoding(false);
            using var writer = new StreamWriter(client, utf8NoBom, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, utf8NoBom, leaveOpen: true);
            writer.WriteLine(command);
            var response = reader.ReadLine()?.Trim();
            return response == "unknown" ? 2 : 0;
        }
        catch
        {
            return 1;
        }
    }

    static async Task RunPipeServerAsync(CancellationToken ct)
    {
        var pipeName = PipeName();
        var utf8NoBom = new UTF8Encoding(false);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server, utf8NoBom, leaveOpen: true);
                await using var writer = new StreamWriter(server, utf8NoBom, leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(ct);
                var cmd = AppCommands.FindByName((line ?? "").Trim());
                if (cmd != null)
                {
                    var win = await WindowReady.Task.WaitAsync(ct);
                    Dispatcher.UIThread.Post(() => cmd.Execute(win));
                    await writer.WriteLineAsync("ok");
                }
                else
                {
                    await writer.WriteLineAsync("unknown");
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
