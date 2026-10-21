using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Placement;
using DeveloperIsland.Platform.X11;
using static DeveloperIsland.Platform.X11.Xlib;

namespace DeveloperIsland.Platform.Windowing;

/// <summary>The focused top-level window, as far as auto-hide and fullscreen detection care.</summary>
/// <param name="App">Executable name, lower case ("chrome", "firefox", "code").</param>
/// <param name="Pid">The owning process when known (to recognise the island's own windows).</param>
internal sealed record ActiveWindow(string App, bool IsMaximized, bool IsFullscreen, PixelRect Bounds, int? Pid);

/// <summary>Where the focused window comes from: X11 (including XWayland apps) or GNOME Shell on Wayland.</summary>
internal interface IActiveWindowSource : IDisposable
{
    /// <summary>Raised on a background thread when focus moves or the focused window changes state.</summary>
    event Action? Changed;

    /// <summary>Reads the focused window now; null when there is none or it cannot be read.</summary>
    ActiveWindow? Read();

    /// <summary>False when this source cannot see every app (X11 on a Wayland session without the extension).</summary>
    bool IsComplete { get; }
}

/// <summary>
/// The focused window through X11's EWMH properties: the window manager announces focus in
/// <c>_NET_ACTIVE_WINDOW</c> on the root window, and window states (maximized, fullscreen) in
/// <c>_NET_WM_STATE</c>. Both are watched as property events on a dedicated connection and thread;
/// nothing polls. On a Wayland session this sees XWayland apps only (Chrome and Electron apps by
/// default; Firefox and GNOME apps are native Wayland), so GNOME Shell's view is preferred there.
/// </summary>
internal sealed class X11ActiveWindowSource : IActiveWindowSource
{
    private readonly XDisplay _events;
    private readonly XDisplay _queries;
    private readonly object _queryGate = new();
    private readonly Thread _thread;
    private volatile bool _stopped;
    private IntPtr _watchedWindow;

    private X11ActiveWindowSource(XDisplay events, XDisplay queries)
    {
        _events = events;
        _queries = queries;
        XSelectInput(events.Handle, events.Root, PropertyChangeMask);
        _thread = new Thread(Loop) { IsBackground = true, Name = "Active window" };
        _thread.Start();
    }

    public event Action? Changed;

    public bool IsComplete => !IsWaylandSession;

    public static bool IsWaylandSession =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public static X11ActiveWindowSource? TryCreate()
    {
        var events = XDisplay.Open();
        var queries = XDisplay.Open();
        if (events is null || queries is null)
        {
            events?.Dispose();
            queries?.Dispose();
            return null;
        }

        return new X11ActiveWindowSource(events, queries);
    }

    public ActiveWindow? Read()
    {
        lock (_queryGate)
        {
            var x = _queries;
            var window = x.ActiveWindow();
            if (window == IntPtr.Zero)
            {
                return null;
            }

            var pid = x.Pid(window);
            var app = AppName(pid, x.WindowClass(window));
            var rect = x.Rect(window);
            return new ActiveWindow(
                app,
                x.IsMaximized(window),
                x.IsFullscreen(window),
                rect is { } r ? new PixelRect(r.X, r.Y, r.Width, r.Height) : default,
                pid);
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _thread.Join(TimeSpan.FromSeconds(1));
        _events.Dispose();
        lock (_queryGate)
        {
            _queries.Dispose();
        }
    }

    private void Loop()
    {
        var activeAtom = _events.Atom("_NET_ACTIVE_WINDOW");
        var stateAtom = _events.Atom("_NET_WM_STATE");
        var fd = XConnectionNumber(_events.Handle);
        WatchActive();
        while (!_stopped)
        {
            var changed = false;
            while (XPending(_events.Handle) > 0)
            {
                XNextEvent(_events.Handle, out var ev);
                if (ev.Type != PropertyNotify)
                {
                    continue;
                }

                if (ev.Property.Window == _events.Root && ev.Property.Atom == activeAtom)
                {
                    WatchActive();
                    changed = true;
                }
                else if (ev.Property.Window == _watchedWindow && ev.Property.Atom == stateAtom)
                {
                    changed = true;
                }
            }

            if (changed)
            {
                try
                {
                    Changed?.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Debug("window", "Active window handler failed", new { error = ex.GetType().Name });
                }
            }

            Platform.Tray.GlobalHotKey.PollReadable(fd, 500);
        }
    }

    /// <summary>Follows state changes (maximize, fullscreen) of the window that is focused now.</summary>
    private void WatchActive()
    {
        var active = _events.ActiveWindow();
        if (active == _watchedWindow)
        {
            return;
        }

        if (_watchedWindow != IntPtr.Zero)
        {
            XSelectInput(_events.Handle, _watchedWindow, 0);
        }

        _watchedWindow = active;
        if (active != IntPtr.Zero)
        {
            XSelectInput(_events.Handle, active, PropertyChangeMask);
        }

        XFlush(_events.Handle);
    }

    /// <summary>
    /// The executable behind a window: from its process when the PID is ours to read (not inside a
    /// Flatpak or Snap PID namespace), else from WM_CLASS.
    /// </summary>
    internal static string AppName(int? pid, (string Instance, string Class)? windowClass)
    {
        if (pid is { } p && p > 0)
        {
            try
            {
                var exe = new FileInfo($"/proc/{p}/exe").LinkTarget;
                if (!string.IsNullOrEmpty(exe))
                {
                    return Path.GetFileName(exe).ToLowerInvariant();
                }
            }
            catch (Exception)
            {
                // Another user's process, or a sandbox: fall back below.
            }

            if (SystemInfo.SystemMetricsReader.ReadText($"/proc/{p}/comm") is { Length: > 0 } comm)
            {
                return comm.ToLowerInvariant();
            }
        }

        if (windowClass is { } wc)
        {
            return FromClass(wc.Class.Length > 0 ? wc.Class : wc.Instance);
        }

        return string.Empty;
    }

    /// <summary>Well-known window classes to the executable names the auto-hide list uses.</summary>
    internal static string FromClass(string windowClass) => windowClass.ToLowerInvariant() switch
    {
        "google-chrome" or "google-chrome-beta" or "google-chrome-unstable" => "chrome",
        "microsoft-edge" or "microsoft-edge-beta" or "microsoft-edge-dev" => "msedge",
        "firefox" or "firefox_firefox" or "navigator" or "firefox-esr" => "firefox",
        "chromium" or "chromium-browser" => "chromium",
        "brave-browser" => "brave",
        "code" or "code-oss" => "code",
        var other => other,
    };
}
