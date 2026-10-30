using System.Runtime.InteropServices;

namespace DeveloperIsland.Platform.X11;

/// <summary>
/// The few Xlib, XShape and XRandR calls the island needs beyond what Avalonia exposes. Every call
/// goes through its own <see cref="XDisplay"/> connection (Avalonia keeps its own); X11 lets several
/// clients act on the same window.
/// </summary>
internal static unsafe partial class Xlib
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXext = "libXext.so.6";

    public const int Success = 0;
    public const int PropModeReplace = 0;
    public const int ClientMessage = 33;
    public const int PropertyNotify = 28;
    public const int KeyPress = 2;
    public const long PropertyChangeMask = 1L << 22;
    public const long SubstructureRedirectMask = 1L << 20;
    public const long SubstructureNotifyMask = 1L << 19;
    public const int GrabModeAsync = 1;
    public const int ShapeInput = 2;
    public const int ShapeBounding = 0;
    public const int ShapeSet = 0;
    public const int Unsorted = 0;
    public const uint ShiftMask = 1 << 0;
    public const uint LockMask = 1 << 1;
    public const uint ControlMask = 1 << 2;
    public const uint Mod1Mask = 1 << 3; // Alt
    public const uint Mod2Mask = 1 << 4; // Num Lock
    public const uint Mod4Mask = 1 << 6; // Super
    public const int Button1Mask = 1 << 8;
    public static readonly IntPtr AnyPropertyType = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    public struct XRectangle
    {
        public short X;
        public short Y;
        public ushort Width;
        public ushort Height;
    }

    /// <summary>XEvent is a 24-long union; only the fields read here are mapped.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24 * 8)]
    public struct XEvent
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(0)] public XAnyEvent Any;
        [FieldOffset(0)] public XPropertyEvent Property;
        [FieldOffset(0)] public XKeyEvent Key;
        [FieldOffset(0)] public XClientMessageEvent Client;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XAnyEvent
    {
        public int Type;
        public IntPtr Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Window;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XPropertyEvent
    {
        public int Type;
        public IntPtr Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Window;
        public IntPtr Atom;
        public IntPtr Time;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XKeyEvent
    {
        public int Type;
        public IntPtr Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Window;
        public IntPtr Root;
        public IntPtr Subwindow;
        public IntPtr Time;
        public int X;
        public int Y;
        public int XRoot;
        public int YRoot;
        public uint State;
        public uint Keycode;
        public int SameScreen;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XClientMessageEvent
    {
        public int Type;
        public IntPtr Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Window;
        public IntPtr MessageType;
        public int Format;
        public IntPtr L0;
        public IntPtr L1;
        public IntPtr L2;
        public IntPtr L3;
        public IntPtr L4;
    }

    public delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    [LibraryImport(LibX11)] public static partial int XInitThreads();
    [LibraryImport(LibX11)] public static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport(LibX11)] public static partial int XCloseDisplay(IntPtr display);
    [LibraryImport(LibX11)] public static partial IntPtr XDefaultRootWindow(IntPtr display);
    [LibraryImport(LibX11)] public static partial int XConnectionNumber(IntPtr display);
    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [LibraryImport(LibX11)] public static partial int XFlush(IntPtr display);
    [LibraryImport(LibX11)] public static partial int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [LibraryImport(LibX11)] public static partial int XPending(IntPtr display);
    [LibraryImport(LibX11)] public static partial int XNextEvent(IntPtr display, out XEvent ev);
    [LibraryImport(LibX11)] public static partial int XSelectInput(IntPtr display, IntPtr window, long mask);
    [LibraryImport(LibX11)] public static partial int XFree(IntPtr data);
    [LibraryImport(LibX11)] public static partial IntPtr XSetErrorHandler(XErrorHandler? handler);
    [LibraryImport(LibX11)] public static partial int XRaiseWindow(IntPtr display, IntPtr window);
    [LibraryImport(LibX11)] public static partial int XMoveWindow(IntPtr display, IntPtr window, int x, int y);
    [LibraryImport(LibX11)] public static partial int XMoveResizeWindow(IntPtr display, IntPtr window, int x, int y, uint width, uint height);
    [LibraryImport(LibX11)] public static partial int XSetInputFocus(IntPtr display, IntPtr window, int revertTo, IntPtr time);

    [LibraryImport(LibX11)]
    public static partial int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, void* data, int elements);

    [LibraryImport(LibX11)]
    public static partial int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr offset, IntPtr length,
        [MarshalAs(UnmanagedType.Bool)] bool delete, IntPtr reqType, out IntPtr actualType, out int actualFormat, out IntPtr items, out IntPtr bytesAfter, out IntPtr prop);

    [LibraryImport(LibX11)]
    public static partial int XSendEvent(IntPtr display, IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool propagate, long mask, ref XEvent ev);

    [LibraryImport(LibX11)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child, out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [LibraryImport(LibX11)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool XTranslateCoordinates(IntPtr display, IntPtr src, IntPtr dest, int srcX, int srcY, out int destX, out int destY, out IntPtr child);

    [LibraryImport(LibX11)]
    public static partial int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);

    [LibraryImport(LibX11)] public static partial int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, [MarshalAs(UnmanagedType.Bool)] bool ownerEvents, int pointerMode, int keyboardMode);
    [LibraryImport(LibX11)] public static partial int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);
    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr XStringToKeysym(string name);
    [LibraryImport(LibX11)] public static partial byte XKeysymToKeycode(IntPtr display, IntPtr keysym);

    [LibraryImport(LibXext)]
    public static partial void XShapeCombineRectangles(IntPtr display, IntPtr window, int destKind, int xOff, int yOff, XRectangle* rectangles, int count, int op, int ordering);

    [LibraryImport(LibXext)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool XShapeQueryExtension(IntPtr display, out int eventBase, out int errorBase);
}
