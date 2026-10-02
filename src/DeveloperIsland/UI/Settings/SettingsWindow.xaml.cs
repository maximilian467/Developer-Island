using System.Diagnostics;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Placement;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.Platform.Monitors;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace DeveloperIsland.UI.Settings;

/// <summary>
/// Settings: General, Position, Modules, Appearance and About. Every change is saved immediately
/// (no Apply button) and takes effect live through <see cref="SettingsStore.Changed"/>.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly bool _isDemo;
    private readonly Dictionary<string, (Button Nav, FrameworkElement Section)> _sections;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private bool _loading;
    private readonly Func<bool> _isShortcutInUse;
    private readonly Core.Calendar.ISecretStore _secrets;
    private string _selected = "General";

    internal SettingsWindow(SettingsStore store, bool isDemo, Func<bool> isShortcutInUse, Core.Calendar.ISecretStore secrets)
    {
        _secrets = secrets;
        _store = store;
        _isDemo = isDemo;
        _isShortcutInUse = isShortcutInUse;
        InitializeComponent();
        foreach (var preset in ShortcutGesture.Presets)
        {
            ShortcutPicker.Items.Add(preset.Text);
        }


        _sections = new()
        {
            ["General"] = (NavGeneral, GeneralSection),
            ["Position"] = (NavPosition, PositionSection),
            ["Modules"] = (NavModules, ModulesSection),
            ["Appearance"] = (NavAppearance, AppearanceSection),
            ["About"] = (NavAbout, AboutSection),
        };

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        AppWindow.SetIcon(AppPaths.Icon);
        AppWindow.Title = "Developer Island Settings";
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }

        VersionText.Text = $"Version {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)}{(isDemo ? ", demo mode" : string.Empty)}";
        DataPathText.Text = AppPaths.Root;

        _store.Changed += OnStoreChanged;
        Closed += (_, _) => _store.Changed -= OnStoreChanged;
        Root.ActualThemeChanged += (_, _) =>
        {
            ApplyCaptionColors();
            ApplyFlyoutTheme();
            Select(_selected);
        };

        LoadFromSettings(_store.Current);
        ApplyTheme(_store.Current.Theme);
        Select("General");
        SizeAndCenter();
    }

    /// <summary>The user confirmed Quit Developer Island: the host ends the whole app.</summary>
    public event Action? QuitRequested;

    public void ShowAndFocus()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
    }

    /// <summary>Opens a section by name (General, Position, Modules, Appearance, About).</summary>
    public void ShowSection(string name)
    {
        if (_sections.ContainsKey(name))
        {
            Select(name);
        }
    }

    /// <summary>
    /// Development support (<c>--demo --snapshot</c>): renders every section offscreen, then closes.
    /// Mica does not render into a bitmap, so a solid background stands in.
    /// </summary>
    public async Task SaveSnapshotsAsync(string directory)
    {
        AppWindow.Move(new PointInt32(-32000, -32000));
        AppWindow.Show(activateWindow: false);
        Root.Background = new SolidColorBrush(Root.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243));
        foreach (var name in _sections.Keys.ToList())
        {
            Select(name);
            await Task.Delay(500);
            ContentScroll.ChangeView(null, 0, null, disableAnimation: true);
            await Task.Delay(150);
            await UI.Components.SnapshotWriter.SaveAsync(Root, Path.Combine(directory, "30-settings-" + name.ToLowerInvariant() + ".png"));
            if (ContentScroll.ScrollableHeight > 1)
            {
                ContentScroll.ChangeView(null, ContentScroll.ScrollableHeight, null, disableAnimation: true);
                await Task.Delay(300);
                await UI.Components.SnapshotWriter.SaveAsync(Root, Path.Combine(directory, "30-settings-" + name.ToLowerInvariant() + "-end.png"));
            }
        }

        Close();
    }

    public void ApplyTheme(AppTheme theme)
    {
        Root.RequestedTheme = theme == AppTheme.Dark ? ElementTheme.Dark : ElementTheme.Default;
        ApplyCaptionColors();
        ApplyFlyoutTheme();
    }

    /// <summary>A flyout opens in the window's popup layer, outside Root: give it this window's theme.</summary>
    private void ApplyFlyoutTheme() => QuitFlyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
    {
        Setters = { new Setter(FrameworkElement.RequestedThemeProperty, Root.ActualTheme) },
    };

    private void OnConfirmQuit(object sender, RoutedEventArgs e)
    {
        QuitFlyout.Hide();
        QuitRequested?.Invoke();
    }

    private void SizeAndCenter()
    {
        var scale = Platform.Native.NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new SizeInt32((int)(880 * scale), (int)(620 * scale));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height));
    }

    private void ApplyCaptionColors()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Color.FromArgb(255, 245, 245, 247) : Color.FromArgb(255, 29, 29, 31);
        bar.ButtonInactiveForegroundColor = Color.FromArgb(255, 122, 122, 122);
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x1A, 255, 255, 255) : Color.FromArgb(0x14, 0, 0, 0);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
    }

    private void OnStoreChanged(AppSettings settings) => DispatcherQueue.TryEnqueue(() => LoadFromSettings(settings));

    private void LoadFromSettings(AppSettings s)
    {
        _loading = true;
        try
        {
            StartWithWindowsToggle.IsOn = s.StartWithWindows;
            StartWithWindowsToggle.IsEnabled = !_isDemo;
            AlwaysOnTopToggle.IsOn = s.AlwaysOnTop;
            LaunchHiddenToggle.IsOn = s.LaunchHidden;
            BuildModuleRows(s);
            UpdatePlanConnection();
            BuildRepositoryRows(s);
            BuildCalendarRows(s);
            ShortcutToggle.IsOn = s.GlobalShortcutEnabled;
            ShortcutPicker.SelectedItem = ShortcutGesture.FromText(s.GlobalShortcut).Text;
            ShortcutPicker.IsEnabled = s.GlobalShortcutEnabled;
            UpdateShortcutStatus(s);
            SmartHideToggle.IsOn = s.SmartHideEnabled;
            (s.SmartHideBehavior == SmartHideBehavior.Hide ? SmartHideHide : SmartHideRetract).IsChecked = true;
            AutoHideMaximizedToggle.IsOn = s.AutoHideOnlyMaximized;
            BuildAutoHideRows(s);
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

            _monitors = MonitorService.GetMonitors();
            MonitorPicker.Items.Clear();
            var selected = 0;
            for (var i = 0; i < _monitors.Count; i++)
            {
                MonitorPicker.Items.Add(MonitorService.DisplayName(_monitors[i]));
                var isSaved = s.MonitorDevice is null ? _monitors[i].IsPrimary : string.Equals(_monitors[i].DeviceName, s.MonitorDevice, StringComparison.OrdinalIgnoreCase);
                if (isSaved)
                {
                    selected = i;
                }
            }

            MonitorPicker.SelectedIndex = _monitors.Count > 0 ? selected : -1;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            Select(tag);
        }
    }

    private void Select(string name)
    {
        _selected = name;
        var selectedBrush = ThemeBrush("SettingsNavSelectedBrush");
        foreach (var (key, (nav, section)) in _sections)
        {
            var active = key == name;
            section.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            nav.Background = active ? selectedBrush : new SolidColorBrush(Colors.Transparent);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(nav, active ? "Selected" : string.Empty);
        }
    }

    private Brush ThemeBrush(string key)
    {
        var theme = Root.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        return Root.Resources.ThemeDictionaries.TryGetValue(theme, out var dict) && dict is ResourceDictionary rd && rd.TryGetValue(key, out var brush)
            ? (Brush)brush
            : new SolidColorBrush(Colors.Transparent);
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch { Tag: string tag } toggle)
        {
            return;
        }

        var on = toggle.IsOn;
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

    private void OnShortcutChanged(object sender, SelectionChangedEventArgs e)
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
        ShortcutText.Text = "Opens or closes the island from anywhere.";
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (s.GlobalShortcutEnabled && _isShortcutInUse())
            {
                ShortcutText.Text = "Another app already uses these keys. Choose another combination.";
            }
        });
    }

    private void OnSmartHideBehaviorChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<SmartHideBehavior>(tag, out var behavior))
        {
            return;
        }

        _store.Update(s => s.SmartHideBehavior = behavior);
        Log.Info("settings", "Setting changed", new { setting = "SmartHideBehavior", value = behavior.ToString() });
    }

    private void OnAnchorChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<IslandAnchor>(tag, out var anchor))
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

    private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MonitorPicker.SelectedIndex < 0 || MonitorPicker.SelectedIndex >= _monitors.Count)
        {
            return;
        }

        var monitor = _monitors[MonitorPicker.SelectedIndex];
        _store.Update(s => s.MonitorDevice = monitor.IsPrimary ? null : monitor.DeviceName);
    }

    private void OnResetPosition(object sender, RoutedEventArgs e) => _store.Update(s =>
    {
        s.OffsetX = 0;
        s.OffsetY = 0;
    });

    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<AppTheme>(tag, out var theme))
        {
            return;
        }

        _store.Update(s => s.Theme = theme);
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Data folder could not be opened", ex: ex);
        }
    }
}
