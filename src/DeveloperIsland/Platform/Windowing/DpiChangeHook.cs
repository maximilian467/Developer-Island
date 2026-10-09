using System.Runtime.InteropServices;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Placement;
using static DeveloperIsland.Platform.Native.NativeMethods;

namespace DeveloperIsland.Platform.Windowing;

/// <summary>
/// Answers WM_DPICHANGED with the island's own rectangle. Windows suggests the old rectangle scaled
/// by the DPI ratio, which WinUI applies as is: for the island that is the wrong size at the wrong
/// place until the next placement, and a window that lands mostly on the neighbouring monitor
/// changes DPI again. The hook replaces the suggestion before WinUI sees it.
/// </summary>
internal sealed class DpiChangeHook : IDisposable
{
    private const uint WM_DPICHANGED = 0x02E0;
    private static readonly UIntPtr SubclassId = 0x0D1;

    private readonly IntPtr _hwnd;
    private readonly Func<double, PixelRect?> _rectFor;
    private readonly SubclassProc _proc;
    private bool _installed;

    /// <param name="rectFor">The window rectangle for a scale, or null to keep Windows' suggestion.</param>
    public DpiChangeHook(IntPtr hwnd, Func<double, PixelRect?> rectFor)
    {
        _hwnd = hwnd;
        _rectFor = rectFor;
        _proc = WindowProc; // kept alive for as long as the subclass is installed
        _installed = SetWindowSubclass(hwnd, _proc, SubclassId, UIntPtr.Zero);
        if (!_installed)
        {
            Log.Warn("window", "DPI change hook could not be installed");
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
        if (msg != WM_DPICHANGED || lParam == IntPtr.Zero)
        {
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        PixelRect? rect = null;
        try
        {
            var dpi = (int)(wParam.ToInt64() & 0xFFFF);
            rect = _rectFor(dpi / 96.0);
            if (rect is { } r)
            {
                Log.Debug("window", "DPI changed", new { dpi, r.X, r.Y, r.Width, r.Height });
                Marshal.StructureToPtr(new RECT { Left = r.X, Top = r.Y, Right = r.Right, Bottom = r.Bottom }, lParam, false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("window", "DPI change handling failed", ex);
        }

        var result = DefSubclassProc(hWnd, msg, wParam, lParam);

        // Applied again in case the default handling sized the window some other way.
        if (rect is { } final)
        {
            SetWindowPos(hWnd, IntPtr.Zero, final.X, final.Y, final.Width, final.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        return result;
    }
}
