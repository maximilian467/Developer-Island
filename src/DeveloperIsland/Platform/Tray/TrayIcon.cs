using System.Runtime.InteropServices;
using DeveloperIsland.Core.Diagnostics;
using static DeveloperIsland.Platform.Native.ShellNative;

namespace DeveloperIsland.Platform.Tray;

public enum TrayCommand
{
    Show = 1,
    Hide = 2,
    StartFocus = 3,
    Settings = 4,
    Quit = 5,
    NewTask = 6,
}

/// <summary>
/// Notification-area icon with the native Windows context menu (which follows the system theme).
/// Uses Shell_NotifyIcon directly, so no WinForms and no extra dependency.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private const int CallbackMessage = WM_APP + 1;
    private readonly HostWindow _host;
    private readonly string _iconPath;
    private IntPtr _icon;
    private bool _added;

    public TrayIcon(HostWindow host, string iconPath)
    {
        _host = host;
        _iconPath = iconPath;
        _host.Message += OnMessage;
        _host.TaskbarCreated += () =>
        {
            _added = false;
            Add();
        };
        TryEnableDarkMenus();
    }

    /// <summary>Left click on the icon.</summary>
    public event Action? Activated;

    public event Action<TrayCommand>? CommandInvoked;

    /// <summary>Returns whether the island is currently visible, to enable Show or Hide.</summary>
    public Func<bool>? IsIslandVisible { get; set; }

    public Func<bool>? IsFocusAvailable { get; set; }

    public Func<bool> IsTasksAvailable { get; set; } = () => true;

    public void Add()
    {
        if (_added || _host.Handle == IntPtr.Zero)
        {
            return;
        }

        var size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        if (_icon == IntPtr.Zero)
        {
            _icon = LoadImage(IntPtr.Zero, _iconPath, IMAGE_ICON, size, size, LR_LOADFROMFILE);
        }

        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = "Developer Island";
        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            Log.Warn("tray", "Tray icon could not be added");
            return;
        }

        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        _added = true;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _host.Handle,
        uID = IconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void OnMessage(uint msg, IntPtr lParam, IntPtr wParam)
    {
        if (msg != CallbackMessage)
        {
            return;
        }

        // NOTIFYICON_VERSION_4: LOWORD(lParam) is the event, wParam carries the anchor point.
        var evt = (int)(lParam.ToInt64() & 0xFFFF);
        switch (evt)
        {
            case NIN_SELECT:
            case NIN_KEYSELECT:
            case WM_LBUTTONUP:
                Activated?.Invoke();
                break;
            case WM_CONTEXTMENU:
            case WM_RBUTTONUP:
                var x = (short)(wParam.ToInt64() & 0xFFFF);
                var y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                ShowMenu(x, y);
                break;
        }
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        try
        {
            var visible = IsIslandVisible?.Invoke() ?? true;
            var focus = IsFocusAvailable?.Invoke() ?? true;
            AppendMenu(menu, MF_STRING | (visible ? MF_GRAYED : 0), (UIntPtr)(uint)TrayCommand.Show, "Show Developer Island");
            AppendMenu(menu, MF_STRING | (visible ? 0 : MF_GRAYED), (UIntPtr)(uint)TrayCommand.Hide, "Hide Developer Island");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING | (focus ? 0 : MF_GRAYED), (UIntPtr)(uint)TrayCommand.StartFocus, "Start Focus");
            AppendMenu(menu, MF_STRING | (IsTasksAvailable() ? 0 : MF_GRAYED), (UIntPtr)(uint)TrayCommand.NewTask, "New Task…");
            AppendMenu(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.Settings, "Settings…");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.Quit, "Quit Developer Island");

            // Required so the menu closes when the user clicks elsewhere.
            Platform.Native.NativeMethods.SetForegroundWindow(_host.Handle);
            var command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_LEFTALIGN, x, y, _host.Handle, IntPtr.Zero);
            PostMessage(_host.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (command != 0)
            {
                CommandInvoked?.Invoke((TrayCommand)command);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void TryEnableDarkMenus()
    {
        try
        {
            SetPreferredAppMode(1); // AllowDark: follow the system app theme.
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Older Windows: light menus.
        }
    }
}
