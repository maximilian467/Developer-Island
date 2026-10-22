using System.Runtime.InteropServices;
using System.Text;
using static DeveloperIsland.Platform.X11.Xlib;

namespace DeveloperIsland.Platform.X11;

/// <summary>
/// One Xlib connection with the window queries and changes the island uses. Not thread-safe by
/// itself: each user keeps its own instance on one thread, or locks around it.
/// </summary>
internal sealed unsafe partial class XDisplay : IDisposable
{
    private readonly Dictionary<string, IntPtr> _atoms = new(StringComparer.Ordinal);

    private XDisplay(IntPtr handle)
    {
        Handle = handle;
        Root = XDefaultRootWindow(handle);
    }

    public IntPtr Handle { get; private set; }

    public IntPtr Root { get; }

    /// <summary>Whether this session has an X server to talk to (X11, or XWayland on Wayland).</summary>
    public static bool IsAvailable => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    /// <summary>A new connection, or null without an X server.</summary>
    public static XDisplay? Open()
    {
        if (!IsAvailable)
        {
            return null;
        }

        try
        {
            var handle = XOpenDisplay(IntPtr.Zero);
            return handle == IntPtr.Zero ? null : new XDisplay(handle);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    public IntPtr Atom(string name)
    {
        if (!_atoms.TryGetValue(name, out var atom))
        {
            atom = XInternAtom(Handle, name, false);
            _atoms[name] = atom;
        }

        return atom;
    }

    public void Flush() => XFlush(Handle);

    // Window properties ------------------------------------------------------------------------------

    /// <summary>The 32-bit values of a property (atoms, cardinals, window ids), or empty.</summary>
    public IntPtr[] ReadLongs(IntPtr window, string property, IntPtr type)
    {
        if (window == IntPtr.Zero)
        {
            return [];
        }

        var status = XGetWindowProperty(Handle, window, Atom(property), IntPtr.Zero, new IntPtr(1024), false, type,
            out _, out var format, out var count, out _, out var data);
        try
        {
            if (status != Success || data == IntPtr.Zero || format != 32)
            {
                return [];
            }

            var result = new IntPtr[(int)count];
            for (var i = 0; i < result.Length; i++)
            {
                // Format 32 is delivered as an array of C longs (8 bytes on 64-bit).
                result[i] = Marshal.ReadIntPtr(data, i * IntPtr.Size);
            }

            return result;
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                XFree(data);
            }
        }
    }

    public string? ReadString(IntPtr window, string property)
    {
        if (window == IntPtr.Zero)
        {
            return null;
        }

        var status = XGetWindowProperty(Handle, window, Atom(property), IntPtr.Zero, new IntPtr(1024), false, AnyPropertyType,
            out _, out var format, out var count, out _, out var data);
        try
        {
            if (status != Success || data == IntPtr.Zero || format != 8)
            {
                return null;
            }

            return Encoding.UTF8.GetString((byte*)data, (int)count);
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                XFree(data);
            }
        }
    }

    public void SetAtoms(IntPtr window, string property, params string[] values)
    {
        var atoms = values.Select(Atom).ToArray();
        fixed (IntPtr* p = atoms)
        {
            XChangeProperty(Handle, window, Atom(property), Atom("ATOM"), 32, PropModeReplace, p, atoms.Length);
        }
    }

    public void SetCardinal(IntPtr window, string property, uint value)
    {
        var data = new IntPtr((long)value);
        XChangeProperty(Handle, window, Atom(property), Atom("CARDINAL"), 32, PropModeReplace, &data, 1);
    }

    /// <summary>Adds or removes _NET_WM_STATE atoms of a mapped window through the window manager.</summary>
    public void ChangeState(IntPtr window, bool add, string state1, string? state2 = null)
    {
        var ev = new XEvent();
        ev.Client.Type = ClientMessage;
        ev.Client.Window = window;
        ev.Client.MessageType = Atom("_NET_WM_STATE");
        ev.Client.Format = 32;
        ev.Client.L0 = new IntPtr(add ? 1 : 0);
        ev.Client.L1 = Atom(state1);
        ev.Client.L2 = state2 is null ? IntPtr.Zero : Atom(state2);
        ev.Client.L3 = new IntPtr(1); // source: application
        XSendEvent(Handle, Root, false, SubstructureRedirectMask | SubstructureNotifyMask, ref ev);
    }

    /// <summary>Puts a mapped window on every workspace (_NET_WM_DESKTOP = all).</summary>
    public void SetAllDesktops(IntPtr window)
    {
        var ev = new XEvent();
        ev.Client.Type = ClientMessage;
        ev.Client.Window = window;
        ev.Client.MessageType = Atom("_NET_WM_DESKTOP");
        ev.Client.Format = 32;
        ev.Client.L0 = new IntPtr(0xFFFFFFFFL);
        ev.Client.L1 = new IntPtr(1);
        XSendEvent(Handle, Root, false, SubstructureRedirectMask | SubstructureNotifyMask, ref ev);
        SetCardinal(window, "_NET_WM_DESKTOP", 0xFFFFFFFF);
    }

    // Geometry and input -----------------------------------------------------------------------------

    /// <summary>The pointer on screen and whether the primary button is down.</summary>
    public (int X, int Y, bool Button1)? Pointer()
    {
        return XQueryPointer(Handle, Root, out _, out _, out var x, out var y, out _, out _, out var mask)
            ? (x, y, (mask & Button1Mask) != 0)
            : null;
    }

    /// <summary>A window's top-left corner on screen.</summary>
    public (int X, int Y)? Origin(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return null;
        }

        return XTranslateCoordinates(Handle, window, Root, 0, 0, out var x, out var y, out _) ? (x, y) : null;
    }

    /// <summary>A window's rectangle on screen.</summary>
    public (int X, int Y, int Width, int Height)? Rect(IntPtr window)
    {
        if (window == IntPtr.Zero || XGetGeometry(Handle, window, out _, out _, out _, out var w, out var h, out _, out _) == 0)
        {
            return null;
        }

        return Origin(window) is { } o ? (o.X, o.Y, (int)w, (int)h) : null;
    }

    public void Move(IntPtr window, int x, int y) => XMoveWindow(Handle, window, x, y);

    public void MoveResize(IntPtr window, int x, int y, int width, int height) =>
        XMoveResizeWindow(Handle, window, x, y, (uint)Math.Max(1, width), (uint)Math.Max(1, height));

    public void Raise(IntPtr window) => XRaiseWindow(Handle, window);

    /// <summary>Asks the window manager to focus a window (as a pager would: honored across apps).</summary>
    public void Activate(IntPtr window)
    {
        var ev = new XEvent();
        ev.Client.Type = ClientMessage;
        ev.Client.Window = window;
        ev.Client.MessageType = Atom("_NET_ACTIVE_WINDOW");
        ev.Client.Format = 32;
        ev.Client.L0 = new IntPtr(2); // source: pager
        XSendEvent(Handle, Root, false, SubstructureRedirectMask | SubstructureNotifyMask, ref ev);
        XFlush(Handle);
    }

    /// <summary>
    /// Sets where a window takes pointer input (window pixels); outside it, input goes to whatever is
    /// below. Applied to the window and its children: a GPU-rendered Avalonia window draws into a
    /// child window that would otherwise keep catching the pointer.
    /// </summary>
    public void SetInputRegion(IntPtr window, int left, int top, int right, int bottom)
    {
        var rect = new XRectangle
        {
            X = (short)Math.Clamp(left, short.MinValue, short.MaxValue),
            Y = (short)Math.Clamp(top, short.MinValue, short.MaxValue),
            Width = (ushort)Math.Clamp(right - left, 0, ushort.MaxValue),
            Height = (ushort)Math.Clamp(bottom - top, 0, ushort.MaxValue),
        };
        var count = rect.Width == 0 || rect.Height == 0 ? 0 : 1;
        foreach (var target in WithChildren(window))
        {
            XShapeCombineRectangles(Handle, target, ShapeInput, 0, 0, &rect, count, ShapeSet, Unsorted);
        }

        XFlush(Handle);
    }

    private List<IntPtr> WithChildren(IntPtr window)
    {
        var result = new List<IntPtr> { window };
        if (XQueryTree(Handle, window, out _, out _, out var children, out var count) != 0 && children != IntPtr.Zero)
        {
            for (var i = 0; i < count; i++)
            {
                result.Add(Marshal.ReadIntPtr(children, i * IntPtr.Size));
            }

            XFree(children);
        }

        return result;
    }

    // The active window ------------------------------------------------------------------------------

    /// <summary>The window manager's active (focused) top-level window, or zero.</summary>
    public IntPtr ActiveWindow() => ReadLongs(Root, "_NET_ACTIVE_WINDOW", Atom("WINDOW")) is [var w, ..] ? w : IntPtr.Zero;

    public int? Pid(IntPtr window) => ReadLongs(window, "_NET_WM_PID", Atom("CARDINAL")) is [var pid, ..] ? (int)pid : null;

    /// <summary>WM_CLASS: instance and class name (for example "google-chrome", "Google-chrome").</summary>
    public (string Instance, string Class)? WindowClass(IntPtr window)
    {
        var value = ReadString(window, "WM_CLASS");
        if (value is null)
        {
            return null;
        }

        var parts = value.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => null,
            1 => (parts[0], parts[0]),
            _ => (parts[0], parts[1]),
        };
    }

    public bool HasState(IntPtr window, string state)
    {
        var atom = Atom(state);
        return ReadLongs(window, "_NET_WM_STATE", Atom("ATOM")).Contains(atom);
    }

    public bool IsMaximized(IntPtr window)
    {
        var states = ReadLongs(window, "_NET_WM_STATE", Atom("ATOM"));
        return states.Contains(Atom("_NET_WM_STATE_MAXIMIZED_VERT")) && states.Contains(Atom("_NET_WM_STATE_MAXIMIZED_HORZ"));
    }

    public bool IsFullscreen(IntPtr window) => HasState(window, "_NET_WM_STATE_FULLSCREEN");

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            XCloseDisplay(Handle);
            Handle = IntPtr.Zero;
        }
    }

    [LibraryImport("libX11.so.6")]
    private static partial int XQueryTree(IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent, out IntPtr children, out uint count);
}
