using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Placement;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.Platform.Monitors;

namespace DeveloperIsland.UI.Settings;

/// <summary>
/// Settings: General, Position, Modules, Home Cloud, Appearance and About. Every change is saved
/// immediately (no Apply button) and takes effect live through <see cref="SettingsStore.Changed"/>.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>The battery floors offered; 0 turns the rule off.</summary>
    private static readonly int[] AwakeFloors = [0, 5, 10, 15, 20, 30];

    private readonly SettingsStore _store = null!;
    private readonly bool _isDemo;
    private readonly Dictionary<string, (Button Nav, Control Section)> _sections = [];
    private readonly Func<bool> _isShortcutInUse = () => false;
    private readonly Core.Calendar.ISecretStore _secrets = null!;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private bool _loading;
    private string _selected = "General";

    public SettingsWindow()
    {
        // For the XAML previewer only.
        InitializeComponent();
    }

    internal SettingsWindow(SettingsStore store, bool isDemo, Func<bool> isShortcutInUse, Core.Calendar.ISecretStore secrets)
    {
        _secrets = secrets;
        _store = store;
        _isDemo = isDemo;
        _isShortcutInUse = isShortcutInUse;
        InitializeComponent();
        ShortcutPicker.ItemsSource = ShortcutGesture.Presets.Select(p => p.Text).ToList();
        AwakeBatteryPicker.ItemsSource = AwakeFloors.Select(f => f == 0 ? "Off" : $"{f}{DisplayFormat.Nbsp}%").ToList();

        _sections = new()
        {
            ["General"] = (NavGeneral, GeneralSection),
            ["Position"] = (NavPosition, PositionSection),
            ["Modules"] = (NavModules, ModulesSection),
            ["HomeCloud"] = (NavHomeCloud, HomeCloudSection),
            ["Appearance"] = (NavAppearance, AppearanceSection),
            ["About"] = (NavAbout, AboutSection),
        };

        try
        {
            var icon = new Bitmap(AppPaths.Icon);
            Icon = new WindowIcon(icon);
            TitleIcon.Source = icon;
            AboutIcon.Source = icon;
        }
        catch (Exception ex)
        {
            Log.Debug("settings", "Icon missing", new { error = ex.GetType().Name });
        }

        VersionText.Text = $"Version {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)} for Linux{(isDemo ? ", demo mode" : string.Empty)}";
        DataPathText.Text = AppPaths.Root;

        _store.Changed += OnStoreChanged;
        Closed += (_, _) => _store.Changed -= OnStoreChanged;

        LoadFromSettings(_store.Current);
        ApplyTheme(_store.Current.Theme);
        Select("General");
    }

    /// <summary>The user confirmed Quit Developer Island: the host ends the whole app.</summary>
    public event Action? QuitRequested;

    public void ShowAndFocus()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>Opens a section by name (General, Position, Modules, HomeCloud, Appearance, About).</summary>
    public void ShowSection(string name)
    {
        if (_sections.ContainsKey(name))
        {
            Select(name);
        }
    }

    /// <summary>Development support (<c>--demo --snapshot</c>): renders every section offscreen, then closes.</summary>
    public async Task SaveSnapshotsAsync(string directory)
    {
        Position = new PixelPoint(-32000, -32000);
        ShowActivated = false;
        Show();
        foreach (var name in _sections.Keys.ToList())
        {
            Select(name);
            await Task.Delay(400);
            ContentScroll.Offset = default;
            await Task.Delay(150);
            SaveSnapshot(Path.Combine(directory, "30-settings-" + name.ToLowerInvariant() + ".png"));
        }

        Close();
    }

    private void SaveSnapshot(string path)
    {
        var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(this);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path);
    }

    public void ApplyTheme(AppTheme theme) =>
        RequestedThemeVariant = theme == AppTheme.Dark ? ThemeVariant.Dark : ThemeVariant.Default;

    private void OnConfirmQuit(object? sender, RoutedEventArgs e)
    {
        QuitButton.Flyout?.Hide();
        QuitRequested?.Invoke();
    }

    private void OnStoreChanged(AppSettings settings) => Dispatcher.UIThread.Post(() => LoadFromSettings(settings));

    private void LoadFromSettings(AppSettings s)
    {
        _loading = true;
        try
        {
            StartWithWindowsToggle.IsChecked = s.StartWithWindows;
            StartWithWindowsToggle.IsEnabled = !_isDemo;
            AlwaysOnTopToggle.IsChecked = s.AlwaysOnTop;
            LaunchHiddenToggle.IsChecked = s.LaunchHidden;
            BuildModuleRows(s);
            UpdatePlanConnection();
            BuildRepositoryRows(s);
            BuildCalendarRows(s);
            ShortcutToggle.IsChecked = s.GlobalShortcutEnabled;
            ShortcutPicker.SelectedItem = ShortcutGesture.FromText(s.GlobalShortcut).Text;
            ShortcutPicker.IsEnabled = s.GlobalShortcutEnabled;
            UpdateShortcutStatus(s);
            SmartHideToggle.IsChecked = s.SmartHideEnabled;
            (s.SmartHideBehavior == SmartHideBehavior.Hide ? SmartHideHide : SmartHideRetract).IsChecked = true;
            AutoHideMaximizedToggle.IsChecked = s.AutoHideOnlyMaximized;
            BuildAutoHideRows(s);
            UpdateShellHelper();
            LoadHomeCloud(s);
            LoadAwake(s);
            LoadTokenMetric(s);
            foreach (var dependent in new Control[] { SmartHideRetract, SmartHideHide, AutoHideMaximizedToggle })
            {
                dependent.IsEnabled = s.SmartHideEnabled;
            }

            SmartHideText.Text = s.Anchor == IslandAnchor.TopCenter
                ? "While one of the apps below is in front, the island rests as a small notch. Hover or click it to open."
                : "Only applies at Top Center, where the island covers tabs and title bars.";
            (s.Theme == AppTheme.Dark ? ThemeDark : ThemeSystem).IsChecked = true;

            var anchorButton = s.Anchor switch
            {
                IslandAnchor.TopLeft => AnchorTopLeft,
                IslandAnchor.TopRight => AnchorTopRight,
                IslandAnchor.BottomLeft => AnchorBottomLeft,
                IslandAnchor.BottomCenter => AnchorBottomCenter,
                IslandAnchor.BottomRight => AnchorBottomRight,
                _ => AnchorTopCenter,
            };
            anchorButton.IsChecked = true;
            AnchorName.Text = s.HasCustomPosition ? $"{s.Anchor.DisplayName()}, moved" : s.Anchor.DisplayName();

            CustomPositionText.Text = s.HasCustomPosition
                ? $"Moved {Math.Round(s.OffsetX)}, {Math.Round(s.OffsetY)} from {s.Anchor.DisplayName()}. Drag the island to move it again."
                : "Drag the island anywhere. Near an anchor, it snaps into place.";
            ResetPositionButton.IsEnabled = s.HasCustomPosition;

            LoadMonitors(s);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Displays were connected, disconnected or changed: list the ones there are now.</summary>
    public void RefreshMonitors()
    {
        _loading = true;
        try
        {
            LoadMonitors(_store.Current);
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadMonitors(AppSettings s)
    {
        _monitors = MonitorService.GetMonitors();
        MonitorPicker.ItemsSource = _monitors.Select(MonitorService.DisplayName).ToList();
        var shown = IslandPlacement.Resolve(_monitors, s.MonitorId, s.MonitorDevice, s.Anchor, s.OffsetX, s.OffsetY).Monitor;
        MonitorPicker.SelectedIndex = _monitors.Count > 0 ? Math.Max(0, _monitors.ToList().IndexOf(shown)) : -1;
    }

    private void OnNav(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            Select(tag);
        }
    }

    private void Select(string name)
    {
        _selected = name;
        foreach (var (key, (nav, section)) in _sections)
        {
            var active = key == name;
            section.IsVisible = active;
            nav.Classes.Set("selected", active);
        }

        ContentScroll.Offset = default;
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch { Tag: string tag } toggle)
        {
            return;
        }

        var on = toggle.IsChecked == true;
        _store.Update(s =>
        {
            switch (tag)
            {
                case "StartWithWindows": s.StartWithWindows = on; break;
                case "AlwaysOnTop": s.AlwaysOnTop = on; break;
                case "LaunchHidden": s.LaunchHidden = on; break;
                case var module when module.StartsWith("Module:", StringComparison.Ordinal)
                    && Enum.TryParse<ModuleId>(module["Module:".Length..], out var id):
                    s.SetEnabled(id, on);
                    break;
                case "Shortcut": s.GlobalShortcutEnabled = on; break;
                case "SmartHide": s.SmartHideEnabled = on; break;
                case "AutoHideMaximized": s.AutoHideOnlyMaximized = on; break;
                case var app when app.StartsWith("App:", StringComparison.Ordinal):
                    SetAppEnabled(s, app["App:".Length..], on);
                    break;
            }
        });
        Log.Info("settings", "Setting changed", new { setting = tag, value = on });
    }

    private void OnShortcutChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || ShortcutPicker.SelectedItem is not string text)
        {
            return;
        }

        _store.Update(s => s.GlobalShortcut = text);
        Log.Info("settings", "Setting changed", new { setting = "GlobalShortcut", value = text });
    }

    /// <summary>Registration happens when settings apply; read the outcome right after.</summary>
    private void UpdateShortcutStatus(AppSettings s)
    {
        ShortcutText.Text = IsGnome()
            ? "Opens or closes the island from anywhere. Added to GNOME's keyboard shortcuts."
            : "Opens or closes the island from anywhere.";
        Dispatcher.UIThread.Post(() =>
        {
            if (s.GlobalShortcutEnabled && _isShortcutInUse())
            {
                ShortcutText.Text = "Another app already uses these keys. Choose another combination.";
            }
        }, DispatcherPriority.Background);
    }

    private static bool IsGnome() =>
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty).Contains("GNOME", StringComparison.OrdinalIgnoreCase);

    private void OnSmartHideBehaviorChecked(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag, IsChecked: true } || !Enum.TryParse<SmartHideBehavior>(tag, out var behavior))
        {
            return;
        }

        _store.Update(s => s.SmartHideBehavior = behavior);
        Log.Info("settings", "Setting changed", new { setting = "SmartHideBehavior", value = behavior.ToString() });
    }

    private void OnAnchorChecked(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag, IsChecked: true } || !Enum.TryParse<IslandAnchor>(tag, out var anchor))
        {
            return;
        }

        // Picking an anchor places the island exactly there.
        _store.Update(s =>
        {
            s.Anchor = anchor;
            s.OffsetX = 0;
            s.OffsetY = 0;
        });
    }

    private void OnMonitorChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || MonitorPicker.SelectedIndex < 0 || MonitorPicker.SelectedIndex >= _monitors.Count)
        {
            return;
        }

        var monitor = _monitors[MonitorPicker.SelectedIndex];
        _store.Update(s =>
        {
            s.MonitorDevice = monitor.IsPrimary ? null : monitor.DeviceName;
            s.MonitorId = monitor.IsPrimary ? null : monitor.Id;
        });
    }

    private void OnResetPosition(object? sender, RoutedEventArgs e) => _store.Update(s =>
    {
        s.OffsetX = 0;
        s.OffsetY = 0;
    });

    private void OnThemeChecked(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag, IsChecked: true } || !Enum.TryParse<AppTheme>(tag, out var theme))
        {
            return;
        }

        _store.Update(s => s.Theme = theme);
    }

    private void OnOpenDataFolder(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Root);
        OpenInFileManager(AppPaths.Root);
    }

    /// <summary>Opens a folder in the desktop's file manager (Files on Ubuntu).</summary>
    internal static bool OpenInFileManager(string path)
    {
        try
        {
            var info = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            info.ArgumentList.Add(path);
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Folder could not be opened", new { error = ex.GetType().Name });
            return false;
        }
    }

    // Token metric and Awake -------------------------------------------------------------------------

    private void LoadTokenMetric(AppSettings s)
    {
        (s.TokenMetric == TokenMetric.AllProcessed ? TokenMetricAll : TokenMetricFresh).IsChecked = true;
        TokenMetricFresh.IsEnabled = TokenMetricAll.IsEnabled = s.ClaudeEnabled || s.CodexEnabled;
    }

    private void OnTokenMetricChecked(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag, IsChecked: true } || !Enum.TryParse<TokenMetric>(tag, out var metric))
        {
            return;
        }

        _store.Update(s => s.TokenMetric = metric);
        Log.Info("settings", "Setting changed", new { setting = "TokenMetric", value = metric.ToString() });
    }

    private void LoadAwake(AppSettings s)
    {
        // A hand-edited value that is not offered selects the nearest one shown.
        var nearest = AwakeFloors.MinBy(f => Math.Abs(f - s.AwakeBatteryFloor));
        AwakeBatteryPicker.SelectedIndex = Array.IndexOf(AwakeFloors, nearest);
        AwakeBatteryPicker.IsEnabled = s.AwakeEnabled;
    }

    private void OnAwakeBatteryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || AwakeBatteryPicker.SelectedIndex < 0)
        {
            return;
        }

        var floor = AwakeFloors[AwakeBatteryPicker.SelectedIndex];
        _store.Update(s => s.AwakeBatteryFloor = floor);
        Log.Info("settings", "Setting changed", new { setting = "AwakeBatteryFloor", value = floor });
    }
}
