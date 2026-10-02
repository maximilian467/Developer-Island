using DeveloperIsland.Core.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeveloperIsland;

public partial class App : Application
{
    private readonly AppOptions _options;
    private AppHost? _host;

    internal App(AppOptions options)
    {
        _options = options;
        InitializeComponent();

        // The island lives in the tray; closing Settings must not end the process. Only Quit does.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) =>
        {
            Log.Error("app", "Unhandled UI exception", e.Exception);
            Log.Flush();

            // Keep running: provider and UI errors are isolated and logged rather than fatal.
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("app", "Unhandled exception", e.ExceptionObject as Exception);
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("app", "Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    internal static App? CurrentApp => Current as App;

    internal void OnSecondInstance() => _host?.OnSecondInstance();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _host = new AppHost(DispatcherQueue.GetForCurrentThread(), _options);
        _host.QuitRequested += Quit;
        _host.Start();
    }

    /// <summary>
    /// Quit (tray or Settings): stop every service, then end the app. Exit runs even if a service
    /// fails to stop, so Quit always ends the process; helper tools end with it (HelperProcessJob).
    /// </summary>
    private void Quit()
    {
        try
        {
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("app", "Shutdown did not finish cleanly", ex);
            Log.Flush();
        }
        finally
        {
            _host = null;
            Exit();
        }
    }
}
