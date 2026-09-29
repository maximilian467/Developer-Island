using System.Runtime.InteropServices;
using DeveloperIsland.Core.Diagnostics;
using Microsoft.UI.Windowing;
using static DeveloperIsland.Platform.Native.NativeMethods;

namespace DeveloperIsland.Platform.Windowing;

/// <summary>
/// Turns a WinUI window into a chrome-less, per-pixel transparent, non-activating tool window,
/// and manages its click-through region.
/// </summary>
internal static class IslandWindowChrome
{
    private const long WS_EX_WINDOWEDGE = 0x00000100L;
    private const long WS_EX_CLIENTEDGE = 0x00000200L;
    private const long WS_EX_DLGMODALFRAME = 0x00000001L;
    private const long WS_OVERLAPPEDWINDOW_BITS = 0x00CF0000L; // caption, sysmenu, thickframe, min/max box

    public static void Apply(IntPtr hwnd, AppWindow appWindow, bool alwaysOnTop)
    {
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = alwaysOnTop;
        appWindow.SetPresenter(presenter);
        appWindow.IsShownInSwitchers = false;

        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style = (style & ~WS_OVERLAPPEDWINDOW_BITS) | WS_POPUP;
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);

        // Tool window: no taskbar button, no Alt+Tab. No-activate: hovering, clicking and dragging
        // the island never steal focus from the editor. Expanding activates explicitly.
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)ex);

        // No DWM rounding or border: the island draws its own geometry.
        var corner = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        SuppressBorder(hwnd);

        // Let DWM alpha-blend the composition content (transparent backdrop brush) with the desktop.
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        var blur = new DWM_BLURBEHIND
        {
            dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
            fEnable = 1,
            hRgnBlur = CreateRectRgn(-2, -2, -1, -1), // empty region: transparency without blur
        };
        DwmEnableBlurBehindWindow(hwnd, ref blur);
        DeleteObject(blur.hRgnBlur);

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Removes the Windows 11 window border. Must be repeated on activation: the active-window
    /// border colour is re-applied when the window becomes foreground.
    /// </summary>
    public static void SuppressBorder(IntPtr hwnd)
    {
        var border = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(uint));
    }

    public static void SetAlwaysOnTop(IntPtr hwnd, AppWindow appWindow, bool value)
    {
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = value;
        }

        SetWindowPos(hwnd, value ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Restricts hit-testing (and drawing) to the given rectangle in window pixels. Everything outside
    /// passes clicks through to the windows below, so the transparent parts never get in the way.
    /// </summary>
    public static void SetRegion(IntPtr hwnd, int left, int top, int right, int bottom)
    {
        var region = CreateRectRgn(left, top, right, bottom);
        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            // On failure the region is still ours to free.
            DeleteObject(region);
            Log.Warn("window", "Window region could not be set", new { error = Marshal.GetLastWin32Error() });
        }
    }

    public static void Move(IntPtr hwnd, int x, int y, int width, int height, bool topmost)
    {
        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : IntPtr.Zero, x, y, width, height, SWP_NOACTIVATE | (topmost ? 0 : SWP_NOZORDER));
    }
}
