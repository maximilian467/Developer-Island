using System.Runtime.InteropServices;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.HomeCloud;
using DeveloperIsland.Core.Placement;
using static DeveloperIsland.Platform.Native.NativeMethods;

namespace DeveloperIsland.Platform.Windowing;

/// <summary>
/// Home Cloud: notices a drag heading for the top edge while the island is a notch or hidden, where
/// no drop target is on screen. A low-level mouse hook runs on its own thread with its own message
/// loop, so mouse input never waits for the UI thread. The callback only feeds
/// <see cref="DragArmDetector"/> and returns at once; <c>armedChanged</c> is called on the hook
/// thread once per change. Created only while arming applies, disposed (unhooked, thread ended) as
/// soon as it does not.
/// </summary>
internal sealed class DragArming : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_QUIT = 0x0012;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const uint PM_NOREMOVE = 0;
    private const int CURSOR_SHOWING = 0x1;

    private static readonly Func<bool> CursorShowing = IsCursorShowing;

    private readonly Action<bool> _armedChanged;
    private readonly LowLevelMouseProc _proc; // referenced for the hook's lifetime: never collected
    private readonly DragArmDetector _detector;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;

    public DragArming(PixelRect zone, Action<bool> armedChanged)
    {
        _armedChanged = armedChanged;
        _proc = HookProc;
        _detector = new DragArmDetector(GetSystemMetrics(SM_CXDRAG), GetSystemMetrics(SM_CYDRAG)) { Zone = zone };
        _thread = new Thread(Run) { IsBackground = true, Name = "Home Cloud drag arming" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>The arm zone moved (placement, DPI): screen pixels.</summary>
    public void SetZone(PixelRect zone)
    {
        lock (_gate)
        {
            _detector.Zone = zone;
        }
    }

    public void Dispose()
    {
        var thread = _thread;
        if (thread is null)
        {
            return;
        }

        _thread = null;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (!thread.Join(TimeSpan.FromSeconds(2)))
        {
            Log.Warn("homecloud", "Drag arming thread did not end in time");
        }

        _ready.Dispose();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();

        // Make sure the thread has a message queue before anyone posts WM_QUIT to it.
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Warn("homecloud", "Drag arming hook unavailable", new { error = Marshal.GetLastWin32Error() });
        }
        else if (IsPrimaryButtonDown() && GetCursorPos(out var cursor))
        {
            // Installed in the middle of a drag (the zone closed under the cursor): it counts from here.
            lock (_gate)
            {
                _detector.ButtonDown(cursor.X, cursor.Y);
            }
        }

        _ready.Set();
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
        {
        }

        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam is WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_LBUTTONUP)
        {
            try
            {
                // MSLLHOOKSTRUCT starts with the point in screen pixels.
                var x = Marshal.ReadInt32(lParam);
                var y = Marshal.ReadInt32(lParam, 4);
                bool changed;
                bool armed;
                lock (_gate)
                {
                    changed = (int)wParam switch
                    {
                        WM_LBUTTONDOWN => _detector.ButtonDown(x, y),
                        WM_LBUTTONUP => _detector.ButtonUp(),
                        _ => _detector.Moved(x, y, CursorShowing),
                    };
                    armed = _detector.IsArmed;
                }

                if (changed)
                {
                    _armedChanged(armed);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("homecloud", "Drag arming callback failed", new { error = ex.GetType().Name });
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>A game that hides the cursor never gets the island in its way.</summary>
    private static bool IsCursorShowing()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref info) && (info.flags & CURSOR_SHOWING) != 0;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
