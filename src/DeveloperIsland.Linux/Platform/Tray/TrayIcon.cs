using Avalonia.Controls;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Platform.Tray;

public enum TrayCommand
{
    Show = 1,
    Hide = 2,
    StartFocus = 3,
    Settings = 4,
    Quit = 5,
    NewTask = 6,
    Awake = 7,
}

/// <summary>
/// The status icon (StatusNotifierItem over D-Bus): shown in Ubuntu's top bar by the AppIndicator
/// extension, which Ubuntu enables by default, and natively on KDE, Xfce, Cinnamon and others. The
/// menu mirrors the Windows tray menu; entries enable and disable with the island's state.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Avalonia.Controls.TrayIcon _icon;
    private readonly NativeMenuItem _show = new("Show Developer Island");
    private readonly NativeMenuItem _hide = new("Hide Developer Island");
    private readonly NativeMenuItem _focus = new("Start Focus");
    private readonly NativeMenuItem _task = new("New Task…");
    private readonly NativeMenuItem _awake = new("Stay Awake…");
    private readonly NativeMenuItem _open = new("Open Developer Island");

    public TrayIcon(string iconPath)
    {
        var menu = new NativeMenu();
        menu.Items.Add(_open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_show);
        menu.Items.Add(_hide);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_focus);
        menu.Items.Add(_task);
        menu.Items.Add(_awake);
        menu.Items.Add(Item("Settings…", TrayCommand.Settings));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Quit Developer Island", TrayCommand.Quit));
        menu.NeedsUpdate += (_, _) => Refresh();

        _open.Click += (_, _) => Activated?.Invoke();
        _show.Click += (_, _) => CommandInvoked?.Invoke(TrayCommand.Show);
        _hide.Click += (_, _) => CommandInvoked?.Invoke(TrayCommand.Hide);
        _focus.Click += (_, _) => CommandInvoked?.Invoke(TrayCommand.StartFocus);
        _task.Click += (_, _) => CommandInvoked?.Invoke(TrayCommand.NewTask);
        _awake.Click += (_, _) => CommandInvoked?.Invoke(TrayCommand.Awake);

        _icon = new Avalonia.Controls.TrayIcon
        {
            ToolTipText = "Developer Island",
            Menu = menu,
            IsVisible = false,
        };
        try
        {
            _icon.Icon = new WindowIcon(iconPath);
        }
        catch (Exception ex)
        {
            Log.Warn("tray", "Tray icon image missing", new { error = ex.GetType().Name });
        }

        // Most Linux trays open the menu on any click; where a primary click is delivered, it opens the island.
        _icon.Clicked += (_, _) => Activated?.Invoke();
    }

    public event Action? Activated;

    public event Action<TrayCommand>? CommandInvoked;

    public Func<bool>? IsIslandVisible { get; set; }

    public Func<bool>? IsFocusAvailable { get; set; }

    public Func<bool> IsTasksAvailable { get; set; } = () => true;

    public Func<(bool Enabled, bool Active)> AwakeState { get; set; } = () => (false, false);

    public void Add()
    {
        Refresh();
        _icon.IsVisible = true;
        Log.Info("tray", "Status icon added");
    }

    /// <summary>Updates which entries apply (also before the menu opens, where the tray asks).</summary>
    public void Refresh()
    {
        var visible = IsIslandVisible?.Invoke() ?? true;
        _show.IsEnabled = !visible;
        _hide.IsEnabled = visible;
        _focus.IsEnabled = IsFocusAvailable?.Invoke() ?? false;
        _task.IsEnabled = IsTasksAvailable();
        var awake = AwakeState();
        _awake.IsEnabled = awake.Enabled;
        _awake.Header = awake.Active ? "End Stay Awake" : "Stay Awake…";
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }

    private NativeMenuItem Item(string header, TrayCommand command)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => CommandInvoked?.Invoke(command);
        return item;
    }
}
