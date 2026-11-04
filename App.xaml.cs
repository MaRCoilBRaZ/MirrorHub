using System.Windows;

namespace MirrorHub;

public partial class App : Application
{
    // Single instance: a second launch signals the running one to show itself, then exits.
    Mutex? _instanceMutex;
    EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, @"Local\MirrorHub.SingleInstance", out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MirrorHub.Show");
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.ShowAtStartup();

        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(window.ShowFromTray);
        })
        { IsBackground = true, Name = "MirrorHub.ShowSignal" }.Start();
    }
}
