using System.Runtime.InteropServices;
using DeveloperIsland.Core.Diagnostics;
using static DeveloperIsland.Platform.Native.ShellNative;

namespace DeveloperIsland.Platform.Tray;

/// <summary>
/// An invisible top-level Win32 window that receives tray callbacks and system broadcasts
/// (display changes, work-area changes, Explorer restarts). Top-level rather than message-only,
/// because broadcasts such as WM_DISPLAYCHANGE are not delivered to message-only windows.
/// Lives on the UI thread.
/// </summary>
internal sealed class HostWindow : IDisposable
{
    private const string ClassName = "DeveloperIsland.Host";
    private readonly WndProc _wndProc;
    private readonly uint _taskbarCreated;

    public HostWindow()
    {
        _wndProc = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);
        Handle = CreateWindowEx(0, ClassName, "Developer Island", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            Log.Error("window", "Host window could not be created", data: new { error = Marshal.GetLastWin32Error() });
        }
    }

    public IntPtr Handle { get; }

    /// <summary>Tray icon callback: message id, lParam, wParam.</summary>
    public event Action<uint, IntPtr, IntPtr>? Message;

    /// <summary>Monitor layout, DPI or work area changed.</summary>
    public event Action? DisplaySettingsChanged;

    /// <summary>Explorer restarted; tray icons must be re-added.</summary>
    public event Action? TaskbarCreated;

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyWindow(Handle);
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == _taskbarCreated)
            {
                TaskbarCreated?.Invoke();
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case WM_DISPLAYCHANGE:
                case WM_DPICHANGED:
                    DisplaySettingsChanged?.Invoke();
                    break;
                case WM_SETTINGCHANGE:
                    // SPI_SETWORKAREA = 0x2F (taskbar moved or resized).
                    if (wParam == 0x2F)
                    {
                        DisplaySettingsChanged?.Invoke();
                    }

                    break;
                case >= WM_APP and < 0xC000:
                    Message?.Invoke(msg, lParam, wParam);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Log.Error("window", "Host window message failed", ex);
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
