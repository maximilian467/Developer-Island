using System.Runtime.InteropServices;
using System.Text;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Platform.Tray;
using static DeveloperIsland.Platform.Native.NativeMethods;

namespace DeveloperIsland.Platform.Windowing;

/// <summary>
/// Reports whether a fullscreen app (video, game, slideshow) covers the island's monitor, so the
/// island can step aside. Event-driven: a foreground-change WinEvent hook plus the shell's
/// ABN_FULLSCREENAPP appbar notification. Nothing polls.
/// </summary>
internal sealed partial class FullscreenWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint ABM_NEW = 0x0;
    private const uint ABM_REMOVE = 0x1;
    private const int ABN_FULLSCREENAPP = 0x2;
    private const uint CallbackMessage = 0x8000 + 2; // WM_APP + 2

    private readonly HostWindow _host;
    private readonly IntPtr _island;
    private readonly Action<bool> _changed;
    private readonly WinEventProc _proc;
    private readonly IntPtr _hook;
    private bool _registeredAppBar;
    private bool? _last;

    public FullscreenWatcher(HostWindow host, IntPtr islandWindow, Action<bool> changed)
    {
        _host = host;
        _island = islandWindow;
        _changed = changed;
        _proc = OnWinEvent;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);

        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = host.Handle, uCallbackMessage = CallbackMessage };
        _registeredAppBar = SHAppBarMessage(ABM_NEW, ref data) != 0;
        host.Message += OnHostMessage;
        if (_hook == IntPtr.Zero && !_registeredAppBar)
        {
            Log.Warn("window", "Fullscreen detection unavailable");
        }
    }

    public void Dispose()
    {
        _host.Message -= OnHostMessage;
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
        }

        if (_registeredAppBar)
        {
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _host.Handle };
            SHAppBarMessage(ABM_REMOVE, ref data);
            _registeredAppBar = false;
        }
    }

    private void OnHostMessage(uint msg, IntPtr lParam, IntPtr wParam)
    {
        if (msg == CallbackMessage && wParam.ToInt64() == ABN_FULLSCREENAPP)
        {
            Evaluate();
        }
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) => Evaluate();

    private void Evaluate()
    {
        try
        {
            var fullscreen = IsFullscreenOnIslandMonitor();
            if (_last is null && !fullscreen)
            {
                // First look: nothing to step aside for, and nothing "closed".
                _last = false;
            }
            else if (fullscreen != _last)
            {
                _last = fullscreen;
                _changed(fullscreen);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("window", "Fullscreen check failed", new { error = ex.GetType().Name });
        }
    }

    private bool IsFullscreenOnIslandMonitor()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _island || foreground == _host.Handle)
        {
            return _last ?? false;
        }

        // Our own windows (Settings) never count.
        GetWindowThreadProcessId(foreground, out var pid);
        if (pid == (uint)Environment.ProcessId)
        {
            return _last ?? false;
        }

        // A maximized window or one with a caption is a normal window, even if the taskbar auto-hides.
        const long WS_CAPTION = 0x00C00000L;
        if (IsZoomed(foreground) || (GetWindowLongPtr(foreground, GWL_STYLE).ToInt64() & WS_CAPTION) == WS_CAPTION)
        {
            return false;
        }

        var className = new StringBuilder(64);
        GetClassName(foreground, className, className.Capacity);
        var cls = className.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false;
        }

        var monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        if (monitor != MonitorFromWindow(_island, MONITOR_DEFAULTTONEAREST))
        {
            return false;
        }

        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(foreground, out var rect))
        {
            return false;
        }

        var m = info.rcMonitor;
        var covers = rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
        if (covers)
        {
            Log.Debug("window", "Fullscreen window detected", new { cls });
        }

        return covers;
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc proc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
