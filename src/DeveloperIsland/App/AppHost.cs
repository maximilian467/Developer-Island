using System.Globalization;
using DeveloperIsland.Core.Demo;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Focus;
using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Placement;
using DeveloperIsland.Core.Pricing;
using DeveloperIsland.Core.Providers;
using DeveloperIsland.Core.Providers.Claude;
using DeveloperIsland.Core.Providers.Codex;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;
using DeveloperIsland.Platform.Media;
using DeveloperIsland.Platform.Monitors;
using DeveloperIsland.Platform.Startup;
using DeveloperIsland.Platform.Tray;
using DeveloperIsland.Platform.Windowing;
using DeveloperIsland.UI.Island;
using DeveloperIsland.UI.Settings;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Dispatching;

namespace DeveloperIsland;

/// <summary>
/// Composition root. Wires providers, storage, view models, the island window and the tray.
/// Provider events arrive on background threads and are marshalled to the UI thread here;
/// view models and windows are only touched on the UI thread.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly AppOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ProviderHost _providers = new();
    private SettingsStore _settings = null!;
    private UsageDatabase _database = null!;
    private UsageHistoryService _history = null!;
    private FocusTimer _focusTimer = null!;
    private FocusHistory _focusHistory = null!;
    private IMediaSource _media = null!;
    private IslandViewModel _viewModel = null!;
    private IslandStateMachine _state = null!;
    private IslandWindow _window = null!;
    private HostWindow _host = null!;
    private TrayIcon _tray = null!;
    private FullscreenWatcher? _fullscreen;
    private ForegroundWatcher? _foreground;
    private GlobalHotKey? _hotkey;
    private SettingsWindow? _settingsWindow;
    private ModuleHost _modules = null!;
    private UiStateStore _uiState = null!;
    private DispatcherQueueTimer? _rotation;
    private DispatcherQueueTimer? _historyDebounce;
    private DispatcherQueueTimer? _midnight;
    private DispatcherQueueTimer? _smartHideSettle;
    private FocusState _lastFocusState = FocusState.Idle;
    private DateTimeOffset? _lastFocusStartedAt;
    private FocusSessionStore? _focusStore;
    private bool _mediaPrimed;
    private bool _hiddenByUser;
    private bool _hiddenForFullscreen;

    public AppHost(DispatcherQueue dispatcher, AppOptions options)
    {
        _dispatcher = dispatcher;
        _options = options;
    }

    public event Action? QuitRequested;

    public void Start()
    {
        // Demo and production may run concurrently; each writer needs its own file.
        Log.Initialize(_options.IsDemo ? Path.Combine(AppPaths.Logs, "demo") : AppPaths.Logs);
        if (Environment.GetEnvironmentVariable("DI_LOG_DEBUG") == "1")
        {
            Log.MinimumLevel = LogLevel.Debug;
        }
        Log.Info("startup", "Developer Island starting", new
        {
            version = typeof(AppHost).Assembly.GetName().Version?.ToString(),
            demo = _options.IsDemo,
            autostart = _options.IsAutostart,
            os = Environment.OSVersion.VersionString,
        });

        _settings = new SettingsStore(AppPaths.Settings(_options.IsDemo));
        var pricing = ModelPricingService.Load(AppPaths.PricingOverrides);
        _database = OpenDatabase();
        _history = new UsageHistoryService(_database, pricing);
        _focusHistory = new FocusHistory(_database);
        _focusTimer = new FocusTimer();
        _media = _options.IsDemo ? new DemoMediaSource(AppPaths.Assets) : new MediaSessionService();

        var settings = _settings.Current;
        var usage = new UsageViewModel();
        var music = new MusicViewModel(_media);
        var focus = new FocusViewModel(_focusTimer, _focusHistory, settings.CustomFocusMinutes,
            minutes => _settings.Update(s => s.CustomFocusMinutes = minutes));
        var modules = ModuleViewModels.Create();
        _viewModel = new IslandViewModel(usage, music, focus, modules, _options.IsDemo);
        _modules = new ModuleHost(_dispatcher, modules, _viewModel, _settings, _options.IsDemo) { OpenSettings = OpenSettings };
        ApplyModuleFlags(settings);
        _modules.Apply(settings);
        _uiState = new UiStateStore(_options.IsDemo ? null : AppPaths.UiState);
        _viewModel.SetLastActive(_uiState.LastActiveModule);
        ApplyFavorites(settings);

        _state = new IslandStateMachine(TimeProvider.System, action => _dispatcher.TryEnqueue(() => action()));
        _window = new IslandWindow(_viewModel, _state, settings.AlwaysOnTop) { SnapshotMode = IsSnapshot };
        _window.SettingsRequested += OpenSettings;
        _window.VisiblePanelChanged += tab => _modules.OnPanelVisible(tab);
        _window.DragCompleted += OnDragCompleted;
        _window.PlacementInvalidated += () => _dispatcher.TryEnqueue(PlaceIsland);
        PlaceIsland();

        _host = new HostWindow();
        _host.DisplaySettingsChanged += () => _dispatcher.TryEnqueue(PlaceIsland);
        _tray = new TrayIcon(_host, AppPaths.Icon)
        {
            IsIslandVisible = () => _window.IsIslandVisible,
            IsFocusAvailable = () => _settings.Current.FocusEnabled && !_focusTimer.Snapshot.IsActive,
            IsTasksAvailable = () => _settings.Current.TasksEnabled,
        };
        _tray.Activated += () => ShowIsland(expand: true);
        _tray.CommandInvoked += OnTrayCommand;
        if (!IsSnapshot)
        {
            _tray.Add();
            _fullscreen = new FullscreenWatcher(_host, _window.Handle, OnFullscreenChanged);
            _foreground = new ForegroundWatcher(_window.Handle, () => _settings.Current.SmartHideProcesses, _ => ScheduleSmartHide());
            _hotkey = new GlobalHotKey(_host);
            _hotkey.Pressed += OnShortcut;
            _hotkey.Apply(settings.GlobalShortcutEnabled, ShortcutGesture.FromText(settings.GlobalShortcut));
        }

        WireEvents();
        RestoreFocusSession(settings);

        // The installer may have registered autostart; a fresh install adopts that choice.
        if (_settings.IsFirstRun && !_options.IsDemo && AutostartService.IsEnabled())
        {
            _settings.Update(s => s.StartWithWindows = true);
            settings = _settings.Current;
        }

        SyncAutostart(settings);

        var startHidden = _options.IsAutostart && settings.LaunchHidden;
        if (startHidden)
        {
            _hiddenByUser = true;
            _state.Hide();
        }
        else
        {
            _window.ShowIsland();
        }

        ScheduleMidnight();
        _ = StartBackgroundAsync();
        if (IsSnapshot)
        {
            _ = RunSnapshotsAsync(_options.SnapshotDirectory!);
            return;
        }

        if (_options.SettingsSection is { } section)
        {
            OpenSettings(section);
        }
    }

    private bool IsSnapshot => _options.SnapshotDirectory is not null;

    /// <summary>
    /// Development: renders each island state to PNG files and quits. Nothing appears on screen
    /// and no input is simulated; states are driven through the state machine and view model.
    /// </summary>
    private async Task RunSnapshotsAsync(string directory)
    {
        async Task Shot(string name)
        {
            await Task.Delay(1100);
            await _window.SaveSnapshotAsync(Path.Combine(directory, name + ".png"));
        }

        try
        {
            await Task.Delay(4500);
            _state.Dismiss();
            await Shot("01-compact");

            _viewModel.PreparePeek();
            _state.ShowEvent(TimeSpan.FromMinutes(5));
            await Shot("02-peek");

            _viewModel.OnTrackChanged();
            await Shot("03-activity-music");

            _viewModel.OnAiActivity(new AiActivity(AiProviderKind.Claude, AiActivityKind.SessionStarted, "Claude Code session started", "developer-island"));
            await Shot("04-activity-claude");

            _state.Dismiss();
            await Task.Delay(600);
            _state.Activate();
            foreach (var tab in _viewModel.AvailableTabs)
            {
                _viewModel.SelectedTab = tab;
                await Shot("10-expanded-" + tab.ToString().ToLowerInvariant());
            }

            _state.Dismiss();
            await ExtraSnapshotsAsync(Shot);
        }
        catch (Exception ex)
        {
            Log.Error("snapshot", "Snapshot run failed", ex);
        }
        finally
        {
            QuitRequested?.Invoke();
        }
    }

    /// <summary>Browser notch at rest, its peek, and module states.</summary>
    private async Task ExtraSnapshotsAsync(Func<string, Task> shot)
    {
        _state.SetRest(Core.Island.RestMode.Retracted);
        await shot("20-notch");
        _viewModel.PreparePeek();
        _state.Activate();
        await Task.Delay(1500); // the morph from a 48 px notch travels furthest
        await shot("21-notch-opened");
        _state.Dismiss();
        _state.SetRest(Core.Island.RestMode.Compact);
        await Task.Delay(600);

        // Non-ready module states, as a user without gh, calendars or a readable repository sees them.
        var now = DateTimeOffset.UtcNow;
        _viewModel.GitHub.Update(new Core.Modules.ModuleStatus(Core.Modules.ModuleState.Unavailable, "Install the GitHub CLI and run “gh auth login” to see pull requests, CI and notifications."), null, now);
        _viewModel.Calendar.Update(new Core.Modules.ModuleStatus(Core.Modules.ModuleState.Empty, "Add a calendar link (iCal format) or an .ics file in Settings."), [], [], now);
        _viewModel.Git.Update(new Core.Modules.ModuleStatus(Core.Modules.ModuleState.Error, "Git could not read these repositories."), [], now);
        _state.Activate();
        foreach (var (tab, name) in new[] { (IslandTab.GitHub, "github-unavailable"), (IslandTab.Calendar, "calendar-empty"), (IslandTab.Git, "git-error") })
        {
            _viewModel.SelectedTab = tab;
            await shot("40-state-" + name);
        }

        _state.Dismiss();
        await Task.Delay(600);

        _modules.Apply(_settings.Current); // back to the demo content after the empty and error states

        // The compact island featuring each module (as a single favorite).
        foreach (var module in new[] { ModuleId.Claude, ModuleId.Codex, ModuleId.Music, ModuleId.Focus, ModuleId.System, ModuleId.Calendar, ModuleId.Git, ModuleId.GitHub, ModuleId.Tasks })
        {
            _viewModel.SetFavorites([module]);
            await shot("50-compact-" + module.ToString().ToLowerInvariant());
        }

        _viewModel.SetFavorites([ModuleId.Claude, ModuleId.Music, ModuleId.Calendar]);
        await shot("51-compact-favorites-turn-0");
        _viewModel.AdvanceCompactRotation();
        await shot("51-compact-favorites-turn-1");
        _viewModel.SetFavorites([]);
        _viewModel.SetLastActive(null);
        await Task.Delay(400);

        await new SettingsWindow(_settings, _options.IsDemo, () => false).SaveSnapshotsAsync(_options.SnapshotDirectory!);
    }

    /// <summary>Second launch of the app: show the island.</summary>
    public void OnSecondInstance() => ShowIsland(expand: false);

    public void Dispose()
    {
        _shutdown.Cancel();
        _providers.Dispose();
        _focusTimer?.Dispose();
        _modules?.Dispose();
        _media?.Dispose();
        _state?.Dispose();
        _fullscreen?.Dispose();
        _foreground?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _host?.Dispose();
        _database?.Dispose();
        Log.Info("startup", "Developer Island stopped");
        Log.Flush();
    }

    private UsageDatabase OpenDatabase()
    {
        if (_options.IsDemo)
        {
            return UsageDatabase.OpenInMemory();
        }

        try
        {
            var db = UsageDatabase.Open(AppPaths.Database);
            db.PruneEvents(DateOnly.FromDateTime(DateTime.Today).AddDays(-400));
            return db;
        }
        catch (Exception ex)
        {
            // The island still works; history just is not kept this session.
            Log.Error("database", "Usage database unavailable; using a temporary in-memory store", ex);
            return UsageDatabase.OpenInMemory();
        }
    }

    /// <summary>A session running or paused when the app closed continues (or completes) now.</summary>
    private void RestoreFocusSession(AppSettings settings)
    {
        if (_options.IsDemo)
        {
            return;
        }

        _focusStore = new FocusSessionStore(AppPaths.FocusSession);
        var saved = _focusStore.Load();
        if (saved is null || !settings.FocusEnabled)
        {
            _focusStore.Save(null);
            return;
        }

        // Restoring is not a new start: no "focus started" peek.
        _lastFocusState = saved.State;
        _lastFocusStartedAt = saved.StartedAt;
        _focusTimer.RestoreSession(saved);
        Log.Info("focus", "Session restored", new { state = _focusTimer.Snapshot.State.ToString() });
    }

    private async Task StartBackgroundAsync()
    {
        try
        {
            if (_options.IsDemo)
            {
                await Task.Run(() => DemoData.SeedHistory(_history, _database, TimeProvider.System));
                await _providers.AddAsync(new DemoAiProvider(DemoData.ClaudeSnapshot(_history)), _shutdown.Token);
                await _providers.AddAsync(new DemoAiProvider(DemoData.CodexSnapshot(_history)), _shutdown.Token);
                _dispatcher.TryEnqueue(() =>
                {
                    _focusTimer.Restore(TimeSpan.FromMinutes(50), TimeSpan.FromSeconds(42 * 60 + 13));
                    _viewModel.Focus.RefreshToday();
                });
            }
            else
            {
                await SyncProvidersAsync(_settings.Current);
            }

            await _media.StartAsync();
            RequestHistoryReload();
            TrimMemory();
        }
        catch (Exception ex)
        {
            Log.Error("startup", "Background start failed", ex);
        }
    }

    private void WireEvents()
    {
        _providers.SnapshotChanged += snapshot => _dispatcher.TryEnqueue(() =>
        {
            var vm = snapshot.Provider == AiProviderKind.Claude ? _viewModel.Usage.Claude : _viewModel.Usage.Codex;
            vm.Update(snapshot);
            _modules.OnAiSnapshot(snapshot);
        });
        _providers.Activity += activity => _dispatcher.TryEnqueue(() => _viewModel.OnAiActivity(activity));
        _history.Changed += _ => _dispatcher.TryEnqueue(RequestHistoryReload);

        _media.Changed += snapshot => _dispatcher.TryEnqueue(() =>
        {
            // The first snapshot after launch is the current state, not a song change.
            if (!_mediaPrimed)
            {
                _mediaPrimed = true;
                _viewModel.Music.TrackChanged -= _viewModel.OnTrackChanged;
                _viewModel.Music.Update(snapshot);
                _viewModel.Music.TrackChanged += _viewModel.OnTrackChanged;
                return;
            }

            _viewModel.Music.Update(snapshot);
        });

        _focusTimer.Changed += snapshot => _dispatcher.TryEnqueue(() =>
        {
            _viewModel.Focus.Update(snapshot);
            _viewModel.RefreshActivityTrailing();
            if (snapshot.State == FocusState.Running && _lastFocusState == FocusState.Idle)
            {
                _viewModel.OnFocusStarted(snapshot);
            }

            // Persist on start, pause, resume and stop; never per tick.
            if (snapshot.State != _lastFocusState || snapshot.StartedAt != _lastFocusStartedAt)
            {
                _focusStore?.Save(_focusTimer.Capture());
            }

            _lastFocusState = snapshot.State;
            _lastFocusStartedAt = snapshot.StartedAt;
        });
        _focusTimer.SessionEnded += record =>
        {
            _focusHistory.Record(record);

            _dispatcher.TryEnqueue(() =>
            {
                _viewModel.Focus.RefreshToday();
                _viewModel.OnFocusEnded(record.Completed, TimeSpan.FromSeconds(record.FocusedSeconds));
            });
        };

        _viewModel.ActivityRequested += duration =>
        {
            var shown = _state.ShowEvent(duration);
            Log.Debug("island", "Activity requested", new { kind = _viewModel.Activity.Kind.ToString(), shown });
        };
        _settings.Changed += settings => _dispatcher.TryEnqueue(() => ApplySettings(settings));

        // Favorites (stars) live in settings; the module opened last in the small UI state file.
        _viewModel.FavoriteChanged += (module, favorite) => _settings.Update(s => s.SetFavorite(module, favorite));
        _viewModel.ModuleOpened += module =>
        {
            _uiState.SetLastActive(module);
            _viewModel.SetLastActive(module);
        };
        _state.ModeChanged += (_, _) => UpdateRotation();
    }

    // Settings --------------------------------------------------------------------------------------

    private void ApplySettings(AppSettings settings)
    {
        ApplyModuleFlags(settings);
        _modules.Apply(settings);
        ApplyFavorites(settings);
        _viewModel.EnsureValidTab();
        _window.SetAlwaysOnTop(settings.AlwaysOnTop);
        PlaceIsland();
        SyncAutostart(settings);
        _settingsWindow?.ApplyTheme(settings.Theme);
        _hotkey?.Apply(settings.GlobalShortcutEnabled, ShortcutGesture.FromText(settings.GlobalShortcut));
        _foreground?.Refresh();
        ApplySmartHide();
        if (!settings.FocusEnabled && _focusTimer.Snapshot.IsActive)
        {
            _focusTimer.Stop();
        }

        if (!_options.IsDemo)
        {
            _ = SyncProvidersAsync(settings);
        }
    }

    /// <summary>Enabled favorites, in tab order, feature in the compact island.</summary>
    private void ApplyFavorites(AppSettings settings)
    {
        _viewModel.SetFavorites(CompactSelector.EffectiveFavorites(settings.FavoriteModules, settings.Order, settings.IsEnabled));
        UpdateRotation();
    }

    /// <summary>Favorites take turns only while the compact capsule is on screen; otherwise no timer runs.</summary>
    private void UpdateRotation()
    {
        if (_state is null)
        {
            return;
        }

        var needed = _viewModel.CompactRotates && _state.Mode == Core.Island.IslandMode.Compact;
        if (needed && _rotation is null)
        {
            _rotation = _dispatcher.CreateTimer();
            _rotation.Interval = CompactSelector.RotationInterval;
            _rotation.Tick += (_, _) => _viewModel.AdvanceCompactRotation();
        }

        if (needed && _rotation is { IsRunning: false })
        {
            _rotation.Start();
        }
        else if (!needed && _rotation is { IsRunning: true })
        {
            _rotation.Stop();
        }
    }

    private void ApplyModuleFlags(AppSettings settings)
    {
        _viewModel.Usage.Claude.IsEnabled = settings.ClaudeEnabled;
        _viewModel.Usage.Codex.IsEnabled = settings.CodexEnabled;
        _viewModel.Music.IsEnabled = settings.MusicEnabled;
        _viewModel.Focus.IsEnabled = settings.FocusEnabled;
    }

    private async Task SyncProvidersAsync(AppSettings settings)
    {
        var running = _providers.Providers.Select(p => p.Kind).ToHashSet();
        foreach (var (kind, enabled) in new[] { (AiProviderKind.Claude, settings.ClaudeEnabled), (AiProviderKind.Codex, settings.CodexEnabled) })
        {
            if (enabled && !running.Contains(kind))
            {
                IAiUsageProvider provider = kind == AiProviderKind.Claude
                    ? new ClaudeUsageProvider(ClaudeUsageProvider.DefaultRoot(), _history, _database)
                    : new CodexUsageProvider(CodexUsageProvider.DefaultRoot(), _history, _database);
                await _providers.AddAsync(provider, _shutdown.Token);
            }
            else if (!enabled && running.Contains(kind))
            {
                _providers.Remove(kind);
            }
        }
    }

    private void SyncAutostart(AppSettings settings)
    {
        if (_options.IsDemo)
        {
            return;
        }

        if (AutostartService.IsEnabled() != settings.StartWithWindows)
        {
            AutostartService.SetEnabled(settings.StartWithWindows);
        }
    }

    // Placement --------------------------------------------------------------------------------------

    private void PlaceIsland()
    {
        try
        {
            var s = _settings.Current;
            var placement = IslandPlacement.Resolve(MonitorService.GetMonitors(), s.MonitorDevice, s.Anchor, s.OffsetX, s.OffsetY);
            if (placement.IsFallback)
            {
                Log.Info("window", "Saved monitor not found; using the primary monitor", new { device = s.MonitorDevice });
            }

            _window.Place(placement.Monitor, placement.Anchor, placement.OffsetX, placement.OffsetY);
            _foreground?.Refresh();
            ApplySmartHide();
        }
        catch (Exception ex)
        {
            Log.Error("window", "Island placement failed", ex);
        }
    }

    private void OnDragCompleted(PixelRect capsule)
    {
        var drop = IslandPlacement.FromDrop(MonitorService.GetMonitors(), capsule);
        Log.Info("window", "Island moved", new { anchor = drop.Anchor.ToString(), drop.OffsetX, drop.OffsetY });
        _settings.Update(s =>
        {
            s.MonitorDevice = drop.Monitor.IsPrimary ? null : drop.Monitor.DeviceName;
            s.Anchor = drop.Anchor;
            s.OffsetX = drop.OffsetX;
            s.OffsetY = drop.OffsetY;
        });
    }

    // Visibility ---------------------------------------------------------------------------------------

    private void ShowIsland(bool expand)
    {
        _hiddenByUser = false;
        _hiddenForFullscreen = false;
        _state.Show();
        _window.ShowIsland();
        if (expand)
        {
            _window.ExpandFromShortcut();
        }
    }

    private void HideIsland()
    {
        _hiddenByUser = true;
        _state.Hide();
    }

    /// <summary>
    /// Foreground changes arrive in bursts (Alt+Tab, a click on the taskbar); let them settle for
    /// 150 ms so the island does not retract and return within a frame.
    /// </summary>
    private void ScheduleSmartHide()
    {
        if (_smartHideSettle is null)
        {
            _smartHideSettle = _dispatcher.CreateTimer();
            _smartHideSettle.IsRepeating = false;
            _smartHideSettle.Interval = TimeSpan.FromMilliseconds(150);
            _smartHideSettle.Tick += (_, _) => ApplySmartHide();
        }

        _smartHideSettle.Stop();
        _smartHideSettle.Start();
    }

    /// <summary>
    /// Smart Auto-Hide: rest as a notch (or hidden) while a maximized browser is in front of a
    /// top-center island; rest as the compact capsule otherwise.
    /// </summary>
    private void ApplySmartHide()
    {
        var s = _settings.Current;
        var placement = IslandPlacement.Resolve(MonitorService.GetMonitors(), s.MonitorDevice, s.Anchor, s.OffsetX, s.OffsetY);
        var rest = Core.Island.SmartHidePolicy.Decide(s, placement.Anchor, placement.OffsetY, _foreground?.Current);
        if (rest != _state.Rest)
        {
            Log.Info("window", "Smart Auto-Hide", new { rest = rest.ToString(), process = _foreground?.Current?.ProcessName });
            _state.SetRest(rest);
        }
    }

    /// <summary>Ctrl+Alt+Space: open from anywhere (including hidden), close when expanded.</summary>
    private void OnShortcut()
    {
        _hiddenByUser = false;
        _hiddenForFullscreen = false;
        _window.ShowIsland();
        _window.ToggleFromShortcut();
    }

    /// <summary>Steps aside while a fullscreen app (video, game, presentation) owns the island's monitor.</summary>
    private void OnFullscreenChanged(bool fullscreen)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_hiddenByUser)
            {
                return;
            }

            Log.Info("window", fullscreen ? "Fullscreen app on the island monitor; stepping aside" : "Fullscreen app closed", new { hiddenByUser = _hiddenByUser });
            if (fullscreen && !_hiddenForFullscreen)
            {
                _hiddenForFullscreen = true;
                _state.Hide();
            }
            else if (!fullscreen && _hiddenForFullscreen)
            {
                _hiddenForFullscreen = false;
                _state.Show();
            }
        });
    }

    private void OnTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Show:
                ShowIsland(expand: false);
                break;
            case TrayCommand.Hide:
                HideIsland();
                break;
            case TrayCommand.StartFocus:
                if (!_focusTimer.Snapshot.IsActive)
                {
                    ShowIsland(expand: false);
                    _focusTimer.Start(TimeSpan.FromMinutes(25));
                }

                break;
            case TrayCommand.NewTask:
                if (_settings.Current.TasksEnabled)
                {
                    ShowIsland(expand: false);
                    _viewModel.PrepareExpandTo(IslandTab.Tasks);
                    _window.ExpandToTaskCapture();
                }

                break;
            case TrayCommand.Settings:
                OpenSettings();
                break;
            case TrayCommand.Quit:
                QuitRequested?.Invoke();
                break;
        }
    }

    private void OpenSettings() => OpenSettings(null);

    private void OpenSettings(string? section)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, _options.IsDemo, () => _hotkey?.IsInUse ?? false);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        if (section is not null)
        {
            _settingsWindow.ShowSection(section);
        }

        _settingsWindow.ShowAndFocus();
    }

    /// <summary>
    /// The first scan parses thousands of log lines; afterwards the steady state is small. Compacting
    /// once and returning unused pages keeps the long-running footprint low.
    /// </summary>
    private static void TrimMemory()
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Platform.Native.NativeMethods.EmptyWorkingSet(process.Handle);
        }
        catch (Exception ex)
        {
            Log.Debug("startup", "Memory trim skipped", new { error = ex.GetType().Name });
        }
    }

    // History ------------------------------------------------------------------------------------------

    private void RequestHistoryReload()
    {
        if (_historyDebounce is null)
        {
            _historyDebounce = _dispatcher.CreateTimer();
            _historyDebounce.IsRepeating = false;
            _historyDebounce.Interval = TimeSpan.FromMilliseconds(1500);
            _historyDebounce.Tick += (_, _) => _ = ReloadHistoryAsync();
        }

        _historyDebounce.Stop();
        _historyDebounce.Start();
    }

    private async Task ReloadHistoryAsync()
    {
        try
        {
            var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            var todayRow = ((int)DateTime.Today.DayOfWeek - (int)firstDay + 7) % 7;
            var days = UsageViewModel.HistoryWeeks * 7 + todayRow + 1;
            var history = await Task.Run(() => _history.GetHistory(days));
            _viewModel.Usage.SetHistory(history);
        }
        catch (Exception ex)
        {
            Log.Error("database", "History could not be loaded", ex);
        }
    }

    private void ScheduleMidnight()
    {
        _midnight ??= _dispatcher.CreateTimer();
        _midnight.IsRepeating = false;
        _midnight.Interval = DateTime.Today.AddDays(1) - DateTime.Now + TimeSpan.FromSeconds(3);
        _midnight.Tick += OnMidnight;
        _midnight.Start();
    }

    private void OnMidnight(DispatcherQueueTimer sender, object args)
    {
        sender.Tick -= OnMidnight;
        _viewModel.Focus.RefreshToday();
        RequestHistoryReload();
        ScheduleMidnight();
    }
}
