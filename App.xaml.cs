using System.Windows;
using RobloxMultiLauncher.Services;

namespace RobloxMultiLauncher;

public partial class App : Application
{
    // Only one copy of this launcher may run: two launchers would fight over the singleton objects.
    private static Mutex? _appMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _appMutex = new Mutex(true, "RobloxMultiInstanceLauncher.SingleApp", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Roblox Multi-Instance Launcher is already running.", "Already running",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Logger.Error("Unhandled UI exception", ex.Exception);
            MessageBox.Show($"An unexpected error occurred and was logged:\n\n{ex.Exception.Message}\n\nLog file:\n{Logger.LogPath}",
                "Roblox Multi-Instance Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Logger.Error("Fatal exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Logger.Error("Unobserved task exception", ex.Exception);
            ex.SetObserved();
        };

        Logger.Info("Launcher started.");
        base.OnStartup(e);
    }
}
