using DeveloperIsland.Core.Calendar;
using DeveloperIsland.Core.Demo;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Git;
using DeveloperIsland.Core.GitHub;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.Core.SystemInfo;
using DeveloperIsland.Core.Tasks;
using DeveloperIsland.Platform.SystemInfo;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Dispatching;

namespace DeveloperIsland;

/// <summary>
/// Owns the modules added after V1 (Git, GitHub, Calendar, Tasks, System): creates their services,
/// applies settings, and moves their results to the view models on the UI thread. A disabled module's
/// service is configured off and does no work: no watchers, timers or processes.
/// </summary>
internal sealed class ModuleHost : IDisposable
{
    private static readonly TimeSpan CalendarTick = TimeSpan.FromSeconds(30);

    private readonly DispatcherQueue _dispatcher;
    private readonly ModuleViewModels _vms;
    private readonly IslandViewModel _island;
    private readonly SettingsStore _settings;
    private readonly bool _demo;
    private readonly HttpClient _http;
    private readonly GitService _git = new();
    private readonly GitHubService _github = new();
    private readonly CalendarService _calendar;
    private readonly SystemMonitor _system;
    private readonly TaskStore _tasks;
    private DispatcherQueueTimer? _calendarTimer;
    private string? _announcedEvent;
    private volatile bool _systemVisible;
    private (ModuleState State, SystemAlert Alert)? _systemShown;
    private (ModuleState State, SystemAlert Alert)? _systemPosted;
    private IReadOnlyList<CalendarEvent> _demoEvents = [];

    public ModuleHost(DispatcherQueue dispatcher, ModuleViewModels vms, IslandViewModel island, SettingsStore settings, bool demo)
    {
        _dispatcher = dispatcher;
        _vms = vms;
        _island = island;
        _settings = settings;
        _demo = demo;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DeveloperIsland/1.0 (+calendar; read-only)");
        _calendar = new CalendarService(location => new IcsCalendarSource(location, _http));
        var reader = new SystemMetricsReader();
        _system = new SystemMonitor(reader.Read);
        _tasks = new TaskStore(demo ? null : AppPaths.Tasks);
        if (demo)
        {
            DemoModules.SeedTasks(_tasks);
        }

        _git.Changed += () => Post(UpdateGit);
        _git.RecentChanged += recent => Post(() => _settings.Update(s => s.GitRecentRepositories = recent.ToList()));
        _github.Changed += () => Post(UpdateGitHub);
        _calendar.Changed += () => Post(UpdateCalendar);
        _system.Changed += OnSystemSampled;
        _vms.Git.RepositorySelected += root => _git.SetActive(root);
        foreach (var vm in _vms.All)
        {
            vm.ActionRequested += OnAction;
        }
    }

    /// <summary>Opens Settings on a section (wired by the host).</summary>
    public Action<string>? OpenSettings { get; set; }

    /// <summary>Applies module switches and configuration. Call on the UI thread.</summary>
    public void Apply(AppSettings settings)
    {
        _island.SetModuleOrder(settings.Order);
        if (_demo)
        {
            ApplyDemo(settings);
            return;
        }

        _git.Configure(settings.GitEnabled, settings.GitRepositories, settings.GitRecentRepositories);
        _github.Configure(settings.GitHubEnabled);
        _calendar.Configure(settings.CalendarEnabled, settings.CalendarSources);
        _system.Configure(settings.SystemEnabled);
        _vms.Tasks.Attach(_tasks, settings.TasksEnabled);
        UpdateGit();
        UpdateGitHub();
        UpdateCalendar();
        UpdateSystem();
        SetCalendarTimer(settings.CalendarEnabled && settings.CalendarSources.Count > 0);
    }

    /// <summary>An AI session reported activity: its folder may be a repository to follow.</summary>
    public void OnAiSnapshot(AiUsageSnapshot snapshot)
    {
        if (!_demo && snapshot.CurrentProjectPath is { } path && snapshot.IsActive)
        {
            _git.NoteProjectPath(path);
        }
    }

    /// <summary>The expanded island shows <paramref name="tab"/> (null: collapsed).</summary>
    public void OnPanelVisible(IslandTab? tab)
    {
        _git.SetVisible(tab == IslandTab.Git);
        _systemVisible = tab == IslandTab.System;
        _system.SetVisible(_systemVisible);
        if (_systemVisible)
        {
            UpdateSystem();
        }
        if (_demo)
        {
            return;
        }

        switch (tab)
        {
            case IslandTab.Git:
                _ = _git.RefreshAsync();
                break;
            case IslandTab.GitHub:
                _ = _github.RefreshIfStaleAsync();
                break;
            case IslandTab.Calendar:
                _vms.Calendar.Tick(DateTimeOffset.UtcNow);
                break;
        }
    }

    public void Dispose()
    {
        _calendarTimer?.Stop();
        _git.Dispose();
        _github.Dispose();
        _calendar.Dispose();
        _system.Dispose();
        _http.Dispose();
    }

    private void Post(Action action) => _dispatcher.TryEnqueue(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("modules", "Module update failed", ex);
        }
    });

    private void UpdateGit()
    {
        var repositories = _git.Repositories;
        _vms.Git.Update(_git.Status, repositories, DateTimeOffset.UtcNow);
        var active = repositories.FirstOrDefault();
        _github.SetRepository(active?.GitHubRepository, active?.Branch);
        _island.EnsureValidTab();
    }

    private void UpdateGitHub()
    {
        _vms.GitHub.Update(_github.Status, _github.Snapshot, DateTimeOffset.UtcNow);
        _island.EnsureValidTab();
    }

    private void UpdateCalendar()
    {
        _vms.Calendar.Update(_calendar.Status, _calendar.Events, _calendar.FailedSources, DateTimeOffset.UtcNow);
        AnnounceEventIfSoon();
        _island.EnsureValidTab();
    }

    /// <summary>
    /// Runs on the sampling thread. Waking the UI thread is the expensive part of a sample, so it
    /// happens only while the panel is visible or when the state or alert changed.
    /// </summary>
    private void OnSystemSampled()
    {
        var key = (_system.Status.State, _system.Alert);
        lock (_system)
        {
            if (!_systemVisible && _systemPosted == key)
            {
                return;
            }

            _systemPosted = key;
        }

        Post(UpdateSystem);
    }

    /// <summary>
    /// Samples arrive every few seconds; the UI hears about them only while the System panel is on
    /// screen, or when the state or alert changes (so an idle island does no binding work).
    /// </summary>
    private void UpdateSystem()
    {
        var status = _system.Status;
        var alert = _system.Alert;
        if (!_systemVisible && _systemShown == (status.State, alert))
        {
            return;
        }

        _systemShown = (status.State, alert);
        _vms.System.Update(status, _system.Latest, alert);
    }

    private void SetCalendarTimer(bool on)
    {
        if (on)
        {
            if (_calendarTimer is null)
            {
                _calendarTimer = _dispatcher.CreateTimer();
                _calendarTimer.Interval = CalendarTick;
                _calendarTimer.Tick += (_, _) =>
                {
                    _vms.Calendar.Tick(DateTimeOffset.UtcNow);
                    AnnounceEventIfSoon();
                };
            }

            _calendarTimer.Start();
        }
        else
        {
            _calendarTimer?.Stop();
        }
    }

    /// <summary>Shows the medium state once when an event enters the compact window (an hour ahead).</summary>
    private void AnnounceEventIfSoon()
    {
        if (!_vms.Calendar.HasCompact)
        {
            return;
        }

        var key = _vms.Calendar.CompactText;
        if (key != _announcedEvent)
        {
            _announcedEvent = key;
            _island.OnEventSoon();
        }
    }

    private void OnAction(ModuleViewModel vm)
    {
        switch (vm.Status.State)
        {
            case ModuleState.Empty:
            case ModuleState.Disabled:
                OpenSettings?.Invoke("Modules");
                break;
            case ModuleState.Unavailable when vm.Module == ModuleId.Git:
                OpenLink("https://git-scm.com/download/win");
                break;
            case ModuleState.Unavailable when vm.Module == ModuleId.GitHub:
                OpenLink("https://cli.github.com/");
                break;
            case ModuleState.Error:
                if (vm.Module == ModuleId.GitHub) _ = _github.RefreshAsync();
                else if (vm.Module == ModuleId.Calendar) _ = _calendar.RefreshAsync();
                else if (vm.Module == ModuleId.Git) _ = _git.RefreshAsync();
                break;
        }
    }

    private static void OpenLink(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("modules", "Link could not be opened", new { error = ex.GetType().Name });
        }
    }

    // Demo -------------------------------------------------------------------------------------------

    private void ApplyDemo(AppSettings settings)
    {
        var now = DateTimeOffset.UtcNow;
        if (_demoEvents.Count == 0)
        {
            _demoEvents = DemoModules.Events(now, TimeZoneInfo.Local);
        }

        _vms.Git.Update(settings.GitEnabled ? ModuleStatus.Ready : ModuleStatus.Disabled, settings.GitEnabled ? DemoModules.Repositories(now) : [], now);
        _vms.GitHub.Update(settings.GitHubEnabled ? ModuleStatus.Ready : ModuleStatus.Disabled, settings.GitHubEnabled ? DemoModules.GitHub(now) : null, now);
        _vms.Calendar.Update(settings.CalendarEnabled ? ModuleStatus.Ready : ModuleStatus.Disabled, _demoEvents, [], now);
        _vms.Tasks.Attach(_tasks, settings.TasksEnabled);
        _vms.System.Update(settings.SystemEnabled ? ModuleStatus.Ready : ModuleStatus.Disabled, DemoModules.System, SystemAlert.None);
        SetCalendarTimer(settings.CalendarEnabled);
        _island.EnsureValidTab();
    }
}
