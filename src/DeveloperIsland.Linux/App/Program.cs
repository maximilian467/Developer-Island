using Avalonia;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland;

public static class Program
{
    /// <summary>
    /// GPU first (GLX, then EGL), software as the fallback. DI_RENDER=software|glx|egl forces one,
    /// for drivers where the GPU path stutters.
    /// </summary>
    private static X11RenderingMode[] RenderingModes() => Environment.GetEnvironmentVariable("DI_RENDER")?.ToLowerInvariant() switch
    {
        "software" => [X11RenderingMode.Software],
        "egl" => [X11RenderingMode.Egl, X11RenderingMode.Software],
        "glx" => [X11RenderingMode.Glx, X11RenderingMode.Software],
        _ => [X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software],
    };

    [STAThread]
    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("DI_LOG") is "debug")
        {
            Log.MinimumLevel = LogLevel.Debug;
        }

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

        // Updates without reinstalling: queue a build or patch for the running (or next) island.
        if (args.Length >= 2 && args[0] == "--update")
        {
            return Platform.Updates.Updater.QueueFromCommandLine(args[1]);
        }

        // Music troubleshooting: prints what every MPRIS player reports, then exits.
        if (args.Contains("--media-report", StringComparer.Ordinal))
        {
            try
            {
                Console.WriteLine(Platform.Media.MprisMediaSource.ReportAsync().GetAwaiter().GetResult());
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Session bus unavailable: {ex.Message}");
                return 1;
            }
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

        // Apply updates waiting in ~/.local/share/developer-island/updates and run the newest build
        // (replaces this process when a newer per-user build exists).
        if (!options.IsDemo && options.SnapshotDirectory is null && options.HomeCloudUploadPath is null)
        {
            Platform.Updates.Updater.EnsureNewest(args);
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
                RenderingMode = RenderingModes(),
            })
            .WithInterFont()
            .AfterSetup(_ => Avalonia.Logging.Logger.Sink = new AvaloniaLogSink());

    // For the Avalonia XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}

/// <summary>Avalonia's own warnings (renderer fallbacks, platform errors) in the app log.</summary>
internal sealed class AvaloniaLogSink : Avalonia.Logging.ILogSink
{
    public bool IsEnabled(Avalonia.Logging.LogEventLevel level, string area) =>
        level >= Avalonia.Logging.LogEventLevel.Warning || (level >= Avalonia.Logging.LogEventLevel.Information && area is "Platform" or "OpenGL");

    public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate) =>
        Write(level, area, messageTemplate);

    public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        var text = messageTemplate;
        try
        {
            var i = 0;
            text = System.Text.RegularExpressions.Regex.Replace(messageTemplate, @"\{[^}]+\}", _ => i < propertyValues.Length ? propertyValues[i++]?.ToString() ?? "null" : "?");
        }
        catch (Exception)
        {
        }

        Write(level, area, text);
    }

    private static void Write(Avalonia.Logging.LogEventLevel level, string area, string text)
    {
        if (level >= Avalonia.Logging.LogEventLevel.Warning)
        {
            Core.Diagnostics.Log.Warn("avalonia", text, new { area });
        }
        else
        {
            Core.Diagnostics.Log.Info("avalonia", text, new { area });
        }
    }
}
