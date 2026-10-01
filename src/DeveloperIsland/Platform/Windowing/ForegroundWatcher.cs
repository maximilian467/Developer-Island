using System.Runtime.InteropServices;
using System.Text;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Island;
using static DeveloperIsland.Platform.Native.NativeMethods;

namespace DeveloperIsland.Platform.Windowing;

/// <summary>
/// Tracks the foreground window for Smart Auto-Hide, event-driven and cheap:
/// <list type="bullet">
/// <item>One global WinEvent hook for foreground changes.</item>
/// <item>While a watched process (a browser) is in front, a second hook scoped to that process only
/// reports its window being maximized or restored (location changes of that one window).</item>
/// <item>Our own windows never change the result, so opening the island from the notch keeps it a notch.</item>
/// <item>The window judged is the top-level owner of the window that is in front right now: a queued
/// event for a window that has already lost the foreground is never applied, and a browser's menus,
/// popups or dialogs count as the browser's main window (whose maximized state matters).</item>
/// <item>A restore from the taskbar (minimized, then maximized) is caught by a minimize-end hook.</item>
/// </list>
/// Callbacks arrive on the UI thread (the thread that installed the hooks).
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint GA_ROOTOWNER = 3;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private readonly IntPtr _island;
    private readonly Func<IReadOnlyCollection<string>> _watched;
    private readonly Action<ForegroundInfo?> _changed;
    private readonly WinEventProc _proc;
    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _restoreHook;
    private readonly Dictionary<uint, string> _names = [];
    private IntPtr _locationHook;
    private uint _locationPid;
    private IntPtr _foreground;
    private ForegroundInfo? _current;

    public ForegroundWatcher(IntPtr islandWindow, Func<IReadOnlyCollection<string>> watchedProcesses, Action<ForegroundInfo?> changed)
    {
        _island = islandWindow;
        _watched = watchedProcesses;
        _changed = changed;
        _proc = OnWinEvent;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_foregroundHook == IntPtr.Zero)
        {
            Log.Warn("window", "Foreground tracking unavailable; Smart Auto-Hide stays off");
        }

        _restoreHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        Refresh();
    }

    public ForegroundInfo? Current => _current;

    /// <summary>Re-evaluates now (settings or island monitor changed, or the reconciliation check).</summary>
    public void Refresh() => Evaluate(GetForegroundWindow(), foregroundChanged: true);

    /// <summary>
    /// What is in front right now, read directly from Windows without hooks or callbacks: the check
    /// a decision makes before it is applied. Our own windows (or none) keep the last result.
    /// </summary>
    public ForegroundInfo? Probe()
    {
        var hwnd = Root(GetForegroundWindow());
        if (hwnd == IntPtr.Zero)
        {
            return _current;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId ? _current : Describe(hwnd, pid);
    }

    public void Dispose()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
        }

        if (_restoreHook != IntPtr.Zero)
        {
            UnhookWinEvent(_restoreHook);
        }

        UnhookLocation();
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (evt is EVENT_SYSTEM_FOREGROUND or EVENT_SYSTEM_MINIMIZEEND)
            {
                // Events arrive queued: judge the window that is in front now, not the one the event
                // names, so a late event from a fast switch cannot win over the newest one.
                var current = GetForegroundWindow();
                Evaluate(current != IntPtr.Zero ? current : hwnd, foregroundChanged: true);
            }
            else if (evt == EVENT_OBJECT_LOCATIONCHANGE && hwnd == _foreground && idObject == 0 && idChild == 0)
            {
                // Maximize, restore or move of the watched window itself (not carets or child objects).
                Evaluate(hwnd, foregroundChanged: false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("window", "Foreground evaluation failed", new { error = ex.GetType().Name });
        }
    }

    private void Evaluate(IntPtr hwnd, bool foregroundChanged)
    {
        hwnd = Root(hwnd);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId)
        {
            // The island or Settings took focus: keep the previous decision.
            return;
        }

        var name = ProcessName(pid);
        var watched = _watched().Contains(name);
        if (foregroundChanged)
        {
            _foreground = hwnd;
            if (watched && pid != _locationPid)
            {
                UnhookLocation();
                _locationHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _proc, pid, 0, WINEVENT_OUTOFCONTEXT);
                _locationPid = pid;
            }
            else if (!watched)
            {
                UnhookLocation();
            }
        }

        var info = Describe(hwnd, pid, name);
        if (info != _current)
        {
            _current = info;
            _changed(info);
        }
    }

    private ForegroundInfo Describe(IntPtr hwnd, uint pid, string? name = null) => new(
        name ?? ProcessName(pid),
        IsZoomed(hwnd),
        MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == MonitorFromWindow(_island, MONITOR_DEFAULTTONEAREST));

    /// <summary>The top-level owner: a menu, popup or dialog stands for its main window.</summary>
    private static IntPtr Root(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return hwnd;
        }

        var root = GetAncestor(hwnd, GA_ROOTOWNER);
        return root != IntPtr.Zero ? root : hwnd;
    }

    private void UnhookLocation()
    {
        if (_locationHook != IntPtr.Zero)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }

        _locationPid = 0;
    }

    private string ProcessName(uint pid)
    {
        if (_names.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        var name = string.Empty;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process != IntPtr.Zero)
        {
            try
            {
                var buffer = new StringBuilder(1024);
                var size = buffer.Capacity;
                if (QueryFullProcessImageName(process, 0, buffer, ref size))
                {
                    name = SmartHidePolicy.NormalizeProcessName(buffer.ToString());
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        if (_names.Count > 64)
        {
            _names.Clear();
        }

        _names[pid] = name;
        return name;
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
