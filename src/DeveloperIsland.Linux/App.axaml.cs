using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using DeveloperIsland.Core.Diagnostics;
using Microsoft.UI.Dispatching;

namespace DeveloperIsland;

public partial class App : Application
{
    private AppHost? _host;

    public App()
    {
    }

    internal AppOptions Options { get; set; } = new(false, false, null);

    internal SingleInstance? Instance { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Tabular figures for counters and clocks (WinUI: Typography.NumeralAlignment="Tabular").
        Resources["Tabular"] = new FontFeatureCollection { FontFeature.Parse("tnum") };
        Styles.Add(new Avalonia.Styling.Style(x => x.OfType<Avalonia.Controls.TextBlock>().Class("tnum"))
        {
            Setters = { new Avalonia.Styling.Setter(Avalonia.Controls.TextBlock.FontFeaturesProperty, Resources["Tabular"]) },
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The island lives in the tray; closing Settings must not end the process. Only Quit does.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => Quit(desktop, shutdown: false);

            _host = new AppHost(DispatcherQueue.GetForCurrentThread(), Options);
            _host.QuitRequested += () => Quit(desktop, shutdown: true);
            if (Instance is { } instance)
            {
                instance.MessageReceived += message => Avalonia.Threading.Dispatcher.UIThread.Post(() => _host?.OnInstanceMessage(message));
            }

            _host.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Quit (tray or Settings): stop every service, then end the app. Shutdown runs even if a service
    /// fails to stop, so Quit always ends the process.
    /// </summary>
    private void Quit(IClassicDesktopStyleApplicationLifetime desktop, bool shutdown)
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
            if (shutdown)
            {
                desktop.Shutdown(Program.ExitCode);
            }
        }
    }
}
