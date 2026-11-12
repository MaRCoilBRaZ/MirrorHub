using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MirrorHub;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            // Do not assign desktop.MainWindow when starting hidden in the tray, because
            // ClassicDesktopStyleApplicationLifetime auto-shows desktop.MainWindow after this method.
            if (window.ShowAtStartup())
                desktop.MainWindow = window;

            desktop.ShutdownRequested += (_, _) => window.OnAppShutdown();
            Program.NotifyWindowReady(window);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
