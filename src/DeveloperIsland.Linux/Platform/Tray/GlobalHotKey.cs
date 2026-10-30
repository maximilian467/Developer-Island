using System.Diagnostics;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.Platform.X11;
using static DeveloperIsland.Platform.X11.Xlib;

namespace DeveloperIsland.Platform.Tray;

/// <summary>
/// The global shortcut (Ctrl+Alt+Space by default) that opens or closes the island from anywhere.
/// <list type="bullet">
/// <item>GNOME (Ubuntu's desktop, X11 or Wayland): a GNOME custom keyboard shortcut that runs
/// <c>developer-island --toggle</c>; the running island receives it over its single-instance socket.
/// It shows up in Settings › Keyboard › Custom Shortcuts like any other, and is removed when the
/// shortcut is turned off here. Wayland lets no app grab keys itself, so this is the way that works there.</item>
/// <item>Other X11 desktops: a key grab on the root window.</item>
/// </list>
/// </summary>
internal sealed class GlobalHotKey : IDisposable
{
    private const string Schema = "org.gnome.settings-daemon.plugins.media-keys";
    private const string BindingPath = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/developer-island/";

    private readonly bool _gnome;
    private ShortcutGesture? _applied;
    private XGrab? _grab;

    public GlobalHotKey()
    {
        _gnome = IsGnome() && ProcessRunner.FindExecutable("gsettings", "/usr/bin/gsettings") is not null;
    }

    public event Action? Pressed;

    /// <summary>Enabled, but another app owns the combination (known only for X11 grabs).</summary>
    public bool IsInUse { get; private set; }

    /// <summary>The single-instance socket received <c>--toggle</c> (the GNOME binding).</summary>
    public void RaisePressed() => Pressed?.Invoke();

    public void Apply(bool enabled, ShortcutGesture gesture)
    {
        if (enabled && gesture == _applied)
        {
            return;
        }

        IsInUse = false;
        if (_gnome)
        {
            _ = Task.Run(() => ApplyGnome(enabled, gesture));
        }
        else
        {
            _grab?.Dispose();
            _grab = null;
            if (enabled)
            {
                _grab = XGrab.TryCreate(gesture, () => Avalonia.Threading.Dispatcher.UIThread.Post(() => Pressed?.Invoke()));
                IsInUse = _grab is null && XDisplay.IsAvailable;
            }
        }

        _applied = enabled ? gesture : null;
    }

    public void Dispose()
    {
        // The GNOME binding stays when the app quits: pressing it starts the island again.
        _grab?.Dispose();
        _grab = null;
    }

    /// <summary>GNOME's accelerator syntax, e.g. "&lt;Primary&gt;&lt;Alt&gt;space".</summary>
    internal static string Accelerator(ShortcutGesture g) =>
        (g.Ctrl ? "<Primary>" : string.Empty) + (g.Alt ? "<Alt>" : string.Empty) + (g.Shift ? "<Shift>" : string.Empty)
        + (g.VirtualKey == 0x20 ? "space" : char.ToLowerInvariant((char)g.VirtualKey).ToString());

    private static bool IsGnome() =>
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty)
            .Split(':').Any(d => d.Equals("GNOME", StringComparison.OrdinalIgnoreCase) || d.Equals("ubuntu", StringComparison.OrdinalIgnoreCase)
                || d.Equals("Unity", StringComparison.OrdinalIgnoreCase) || d.Equals("Budgie", StringComparison.OrdinalIgnoreCase));

    private static void ApplyGnome(bool enabled, ShortcutGesture gesture)
    {
        try
        {
            var list = ParseList(Gsettings("get", Schema, "custom-keybindings"));
            var relocatable = $"{Schema}.custom-keybinding:{BindingPath}";
            if (enabled)
            {
                Gsettings("set", relocatable, "name", "Developer Island");
                Gsettings("set", relocatable, "command", $"{Startup.AutostartService.LauncherPath} --toggle");
                Gsettings("set", relocatable, "binding", Accelerator(gesture));
                if (!list.Contains(BindingPath))
                {
                    list.Add(BindingPath);
                    Gsettings("set", Schema, "custom-keybindings", FormatList(list));
                }

                Log.Info("hotkey", "GNOME shortcut set", new { shortcut = gesture.Text });
            }
            else if (list.Remove(BindingPath))
            {
                Gsettings("set", Schema, "custom-keybindings", FormatList(list));
                Gsettings("reset", relocatable, "binding");
                Gsettings("reset", relocatable, "command");
                Gsettings("reset", relocatable, "name");
                Log.Info("hotkey", "GNOME shortcut removed");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("hotkey", "GNOME shortcut could not be changed", new { error = ex.GetType().Name });
        }
    }

    private static List<string> ParseList(string? value)
    {
        // "@as []" or "['/org/.../custom0/', '/org/.../custom1/']"
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('['))
        {
            return [];
        }

        var inner = value[(value.IndexOf('[') + 1)..value.LastIndexOf(']')];
        return inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim('\'', '"')).Where(s => s.Length > 0).ToList();
    }

    private static string FormatList(List<string> paths) =>
        paths.Count == 0 ? "@as []" : "[" + string.Join(", ", paths.Select(p => $"'{p}'")) + "]";

    private static string? Gsettings(params string[] args)
    {
        var info = new ProcessStartInfo("gsettings")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return process.ExitCode == 0 ? output.Trim() : null;
    }

    /// <summary>An X11 key grab on the root window, with its own connection and event thread.</summary>
    private sealed class XGrab : IDisposable
    {
        private static readonly uint[] LockVariants = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];
        private readonly XDisplay _display;
        private readonly int _keycode;
        private readonly uint _modifiers;
        private volatile bool _stopped;

        private XGrab(XDisplay display, int keycode, uint modifiers, Action pressed)
        {
            _display = display;
            _keycode = keycode;
            _modifiers = modifiers;
            new Thread(() => Loop(pressed)) { IsBackground = true, Name = "Global shortcut" }.Start();
        }

        public static XGrab? TryCreate(ShortcutGesture gesture, Action pressed)
        {
            var display = XDisplay.Open();
            if (display is null)
            {
                return null;
            }

            var keysym = XStringToKeysym(gesture.VirtualKey == 0x20 ? "space" : ((char)gesture.VirtualKey).ToString().ToLowerInvariant());
            var keycode = XKeysymToKeycode(display.Handle, keysym);
            if (keycode == 0)
            {
                display.Dispose();
                return null;
            }

            var modifiers = (gesture.Ctrl ? ControlMask : 0) | (gesture.Alt ? Mod1Mask : 0) | (gesture.Shift ? ShiftMask : 0);
            foreach (var variant in LockVariants)
            {
                XGrabKey(display.Handle, keycode, modifiers | variant, display.Root, false, GrabModeAsync, GrabModeAsync);
            }

            XSync(display.Handle, false);
            Log.Info("hotkey", "X11 shortcut grabbed", new { shortcut = gesture.Text });
            return new XGrab(display, keycode, modifiers, pressed);
        }

        private void Loop(Action pressed)
        {
            var fd = XConnectionNumber(_display.Handle);
            while (!_stopped)
            {
                while (XPending(_display.Handle) > 0)
                {
                    XNextEvent(_display.Handle, out var ev);
                    if (ev.Type == KeyPress)
                    {
                        pressed();
                    }
                }

                PollReadable(fd, 250);
            }
        }

        public void Dispose()
        {
            _stopped = true;
            foreach (var variant in LockVariants)
            {
                XUngrabKey(_display.Handle, _keycode, _modifiers | variant, _display.Root);
            }

            XFlush(_display.Handle);
        }
    }

    internal static unsafe void PollReadable(int fd, int timeoutMs)
    {
        var pfd = new PollFd { Fd = fd, Events = 1 };
        poll(&pfd, 1, timeoutMs);
    }

    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern unsafe int poll(PollFd* fds, nuint count, int timeout);
}
