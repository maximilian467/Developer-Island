using System.Runtime.InteropServices;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Platform.Windowing;

/// <summary>
/// Reports a mouse press anywhere outside the expanded island, so it closes like a popover even when
/// Windows refused to give it focus (then no deactivation ever arrives). A low-level mouse hook is
/// installed only while the island is expanded and removed as soon as it collapses; the callback only
/// compares the press position with the capsule rectangle and returns at once.
/// </summary>
internal sealed class OutsideClickWatcher : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    private readonly Action _clickedOutside;
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hook;
    private RECT _capsule;

    /// <param name="clickedOutside">Called on the hook's thread (the UI thread that installed it).</param>
    public OutsideClickWatcher(Action clickedOutside)
    {
        _clickedOutside = clickedOutside;
        _proc = HookProc;
    }

    public bool IsActive => _hook != IntPtr.Zero;

    /// <summary>Starts watching, or updates the capsule while watching (screen pixels).</summary>
    public void Watch(int left, int top, int right, int bottom)
    {
        _capsule = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Debug("window", "Outside-click hook unavailable", new { error = Marshal.GetLastWin32Error() });
        }
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var c = _capsule;
            var inside = info.pt.X >= c.Left && info.pt.X < c.Right && info.pt.Y >= c.Top && info.pt.Y < c.Bottom;

            // Our own popups (context menus, tooltips) and windows are never "outside".
            if (!inside && GetWindowThreadProcessId(WindowFromPoint(info.pt), out var pid) != 0 && pid == (uint)Environment.ProcessId)
            {
                inside = true;
            }

            if (!inside)
            {
                try
                {
                    _clickedOutside();
                }
                catch (Exception ex)
                {
                    Log.Debug("window", "Outside-click handler failed", new { error = ex.GetType().Name });
                }
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
