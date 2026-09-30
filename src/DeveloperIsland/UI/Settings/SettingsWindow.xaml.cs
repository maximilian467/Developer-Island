using System.Diagnostics;
using DeveloperIsland.Core.Diagnostics;
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
    private string _selected = "General";

    internal SettingsWindow(SettingsStore store, bool isDemo)
    {
        _store = store;
        _isDemo = isDemo;
        InitializeComponent();

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
            Select(_selected);
        };

        LoadFromSettings(_store.Current);
        ApplyTheme(_store.Current.Theme);
        Select("General");
        SizeAndCenter();
    }

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

    public void ApplyTheme(AppTheme theme)
    {
        Root.RequestedTheme = theme == AppTheme.Dark ? ElementTheme.Dark : ElementTheme.Default;
        ApplyCaptionColors();
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
            ClaudeToggle.IsOn = s.ClaudeEnabled;
            CodexToggle.IsOn = s.CodexEnabled;
            MusicToggle.IsOn = s.MusicEnabled;
            FocusToggle.IsOn = s.FocusEnabled;
            ShortcutToggle.IsOn = s.GlobalShortcutEnabled;
            SmartHideToggle.IsOn = s.SmartHideEnabled;
            (s.SmartHideBehavior == SmartHideBehavior.Hide ? SmartHideHide : SmartHideRetract).IsChecked = true;
            BrowserChromeToggle.IsOn = s.SmartHideProcesses.Contains("chrome");
            BrowserEdgeToggle.IsOn = s.SmartHideProcesses.Contains("msedge");
            BrowserFirefoxToggle.IsOn = s.SmartHideProcesses.Contains("firefox");
            foreach (var dependent in new Control[] { SmartHideRetract, SmartHideHide, BrowserChromeToggle, BrowserEdgeToggle, BrowserFirefoxToggle })
            {
                dependent.IsEnabled = s.SmartHideEnabled;
            }

            SmartHideText.Text = s.Anchor == IslandAnchor.TopCenter
                ? "While a maximized browser is in front, the island rests as a small notch. Hover or click it to open."
                : "Only applies at Top Center, where the island covers browser tabs.";
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
                case "Claude": s.ClaudeEnabled = on; break;
                case "Codex": s.CodexEnabled = on; break;
                case "Music": s.MusicEnabled = on; break;
                case "Focus": s.FocusEnabled = on; break;
                case "Shortcut": s.GlobalShortcutEnabled = on; break;
                case "SmartHide": s.SmartHideEnabled = on; break;
                case var browser when browser.StartsWith("Browser:", StringComparison.Ordinal):
                    var process = browser["Browser:".Length..];
                    s.SmartHideProcesses.Remove(process);
                    if (on)
                    {
                        s.SmartHideProcesses.Add(process);
                    }

                    break;
            }
        });
        Log.Info("settings", "Setting changed", new { setting = tag, value = on });
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
