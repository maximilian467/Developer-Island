using DeveloperIsland.Core.Diagnostics;
using static DeveloperIsland.Platform.Native.NativeMethods;

namespace DeveloperIsland.Platform.Windowing;

/// <summary>
/// Keeps the island's Win32 background transparent in light mode. WinUI paints a window's
/// background in the app theme's colour: black in dark mode, white in light mode. With the frame
/// extended into the client area (see <see cref="IslandWindowChrome"/>), DWM shows black GDI pixels
/// as transparent, but white ones as a white rectangle around the capsule. The hook paints black
/// instead, whatever the theme, and does so after WinUI's own paint as well.
/// </summary>
internal sealed class ClearBackgroundHook : IDisposable
{
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_PAINT = 0x000F;
    private const int BLACK_BRUSH = 4;
    private static readonly UIntPtr SubclassId = 0x0D2;

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc;
    private bool _installed;

    public ClearBackgroundHook(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = WindowProc; // kept alive for as long as the subclass is installed
        _installed = SetWindowSubclass(hwnd, _proc, SubclassId, UIntPtr.Zero);
        if (!_installed)
        {
            Log.Warn("window", "Background hook could not be installed");
        }
    }

    public void Dispose()
    {
        if (_installed)
        {
            RemoveWindowSubclass(_hwnd, _proc, SubclassId);
            _installed = false;
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                FillBlack(hWnd, wParam);
                return 1;
            case WM_PAINT:
                var result = DefSubclassProc(hWnd, msg, wParam, lParam);
                var hdc = GetDC(hWnd);
                if (hdc != IntPtr.Zero)
                {
                    FillBlack(hWnd, hdc);
                    _ = ReleaseDC(hWnd, hdc);
                }

                return result;
            default:
                return DefSubclassProc(hWnd, msg, wParam, lParam);
        }
    }

    private static void FillBlack(IntPtr hWnd, IntPtr hdc)
    {
        if (GetClientRect(hWnd, out var rect))
        {
            _ = FillRect(hdc, ref rect, GetStockObject(BLACK_BRUSH));
        }
    }
}
