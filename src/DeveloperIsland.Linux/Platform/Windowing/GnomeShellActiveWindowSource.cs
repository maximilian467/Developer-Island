using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Placement;
using Tmds.DBus.Protocol;
using Tmds.DBus.SourceGenerator;

namespace DeveloperIsland.Platform.Windowing;

/// <summary>
/// The focused window as GNOME Shell sees it, through the Developer Island GNOME Shell extension
/// (shipped with the app, see gnome-extension/). On a Wayland session this is the only complete view:
/// X11 there sees XWayland apps only. The extension sends a signal on focus and window state changes;
/// nothing polls.
/// </summary>
internal sealed class GnomeShellActiveWindowSource : IActiveWindowSource
{
    public const string BusName = "io.github.DeveloperIsland.Shell";
    private const string ObjectPath = "/io/github/DeveloperIsland/Shell";

    private readonly Connection _connection;
    private readonly IoGithubDeveloperIslandShell1Proxy _proxy;
    private IDisposable? _watch;

    private GnomeShellActiveWindowSource(Connection connection)
    {
        _connection = connection;
        _proxy = new IoGithubDeveloperIslandShell1Proxy(connection, BusName, ObjectPath);
    }

    public event Action? Changed;

    public bool IsComplete => true;

    /// <summary>The extension's service, or null when the extension is not installed or not enabled.</summary>
    public static async Task<GnomeShellActiveWindowSource?> TryCreateAsync()
    {
        Connection? connection = null;
        try
        {
            connection = new Connection(Address.Session!);
            await connection.ConnectAsync();
            var bus = new OrgFreedesktopDBusProxy(connection, "org.freedesktop.DBus", "/org/freedesktop/DBus");
            if (!await bus.NameHasOwnerAsync(BusName))
            {
                connection.Dispose();
                return null;
            }

            var source = new GnomeShellActiveWindowSource(connection);
            source._watch = await source._proxy.WatchFocusChangedAsync(ex =>
            {
                if (ex is null)
                {
                    source.Changed?.Invoke();
                }
            }, emitOnCapturedContext: false);
            Log.Info("window", "Using GNOME Shell for the focused window");
            return source;
        }
        catch (Exception ex)
        {
            Log.Debug("window", "GNOME Shell extension unavailable", new { error = ex.GetType().Name });
            connection?.Dispose();
            return null;
        }
    }

    public ActiveWindow? Read()
    {
        try
        {
            var task = _proxy.GetFocusAsync();
            if (!task.Wait(TimeSpan.FromMilliseconds(500)))
            {
                return null;
            }

            var (app, pid, maximized, fullscreen, x, y, w, h) = task.Result;
            if (string.IsNullOrEmpty(app) && pid <= 0)
            {
                return null;
            }

            var name = X11ActiveWindowSource.AppName(pid > 0 ? pid : null, (app, app));
            return new ActiveWindow(name, maximized, fullscreen, new PixelRect(x, y, w, h), pid > 0 ? pid : null);
        }
        catch (Exception ex)
        {
            Log.Debug("window", "GNOME Shell focus unreadable", new { error = ex.GetType().Name });
            return null;
        }
    }

    public void Dispose()
    {
        _watch?.Dispose();
        _connection.Dispose();
    }
}
