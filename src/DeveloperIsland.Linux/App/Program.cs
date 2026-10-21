using Avalonia;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Claude Code status line bridge: no UI, no single-instance check, done in milliseconds.
        if (StatusLineBridge.IsRequested(args))
        {
            return StatusLineBridge.Run();
        }

        // Hardware support check: prints what the System module can read, then exits.
        if (SystemReport.IsRequested(args))
        {
            return SystemReport.Run(args);
        }

        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine($"Developer Island {typeof(Program).Assembly.GetName().Version?.ToString(3)} (Linux)");
            return 0;
        }

        var options = AppOptions.Parse(args);
        if (options.SnapshotCulture is { Length: > 0 } cultureName)
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(cultureName);
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
            System.Globalization.CultureInfo.CurrentCulture = culture;
            System.Globalization.CultureInfo.CurrentUICulture = culture;
        }

        // One island per user session. A second launch shows the running island (or toggles it, for the
        // global shortcut) instead. Demo mode and the Home Cloud debug run use their own instance names.
        var name = options.HomeCloudUploadPath is not null ? (options.IsDemo ? "developer-island-homecloud-demo" : "developer-island-homecloud")
            : options.SnapshotDirectory is not null ? "developer-island-snapshot"
            : options.IsDemo ? "developer-island-demo" : "developer-island";
        var instance = SingleInstance.TryBecome(name);
        if (instance is null)
        {
            if (options.HomeCloudUploadPath is not null)
            {
                return 3; // another upload debug run is still going
            }

            var message = options.Toggle ? "toggle" : options.SettingsSection is { } section ? "settings:" + section : "show";
            return SingleInstance.TrySend(name, message) ? 0 : 1;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Console.Error.WriteLine("Developer Island needs an X server (X11, or XWayland on a Wayland session): DISPLAY is not set.");
            instance.Dispose();
            return 2;
        }

        using (instance)
        {
            try
            {
                return BuildAvaloniaApp(options, instance).StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception ex)
            {
                Log.Error("app", "Fatal error", ex);
                Log.Flush();
                Console.Error.WriteLine(ex);
                return 1;
            }
        }
    }

    /// <summary>The process exit code; only the Home Cloud upload debug run sets it.</summary>
    internal static int ExitCode { get; set; }

    /// <summary>
    /// Avalonia on X11. On a Wayland session this runs through XWayland, which is what lets the island
    /// place itself and stay above other windows (Wayland gives apps neither).
    /// </summary>
    private static AppBuilder BuildAvaloniaApp(AppOptions options, SingleInstance instance) =>
        AppBuilder.Configure(() => new App { Options = options, Instance = instance })
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                WmClass = "developer-island",
                EnableSessionManagement = false,
                EnableIme = true,
            })
            .WithInterFont()
            .LogToTrace();

    // For the Avalonia XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}
