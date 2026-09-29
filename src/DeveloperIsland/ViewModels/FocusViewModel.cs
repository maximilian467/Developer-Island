using DeveloperIsland.Core.Focus;
using DeveloperIsland.Core.Formatting;

namespace DeveloperIsland.ViewModels;

/// <summary>Focus timer state and actions.</summary>
public sealed class FocusViewModel : ObservableObject
{
    private readonly FocusTimer _timer;
    private readonly FocusHistory _history;
    private readonly Action<int> _saveCustomMinutes;
    private FocusSnapshot _snapshot = FocusSnapshot.Idle;
    private int _customMinutes;
    private bool _isCustomOpen;
    private bool _isEnabled = true;
    private string _todayText = string.Empty;
    private string _weekText = string.Empty;

    public FocusViewModel(FocusTimer timer, FocusHistory history, int customMinutes, Action<int> saveCustomMinutes)
    {
        _timer = timer;
        _history = history;
        _customMinutes = customMinutes;
        _saveCustomMinutes = saveCustomMinutes;
        RefreshToday();
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnAllPropertiesChanged();
            }
        }
    }

    public FocusSnapshot Snapshot => _snapshot;

    public bool IsActive => _isEnabled && _snapshot.IsActive;

    public bool IsIdle => !_snapshot.IsActive;

    public bool IsRunning => _snapshot.State == FocusState.Running;

    public bool IsPaused => _snapshot.State == FocusState.Paused;

    public string RemainingText => DisplayFormat.Clock(_snapshot.Remaining);

    public double Progress => _snapshot.Progress;

    public string StatusText => _snapshot.State switch
    {
        FocusState.Running => $"{DisplayFormat.Duration(_snapshot.Planned)} session",
        FocusState.Paused => "Paused",
        _ => "Start a focus session",
    };

    public string PauseResumeLabel => IsPaused ? "Resume" : "Pause";

    public string TodayText => _todayText;

    /// <summary>Focus history of the last seven days.</summary>
    public string WeekText => _weekText;

    public int CustomMinutes => _customMinutes;

    public string CustomMinutesText => $"{_customMinutes}{DisplayFormat.Nbsp}min";

    public bool IsCustomOpen
    {
        get => _isCustomOpen;
        set
        {
            if (SetProperty(ref _isCustomOpen, value))
            {
                OnPropertyChanged(nameof(IsPresetRowVisible));
            }
        }
    }

    public bool IsPresetRowVisible => IsIdle && !_isCustomOpen;

    public bool IsCustomRowVisible => IsIdle && _isCustomOpen;

    public string AccessibleSummary => _snapshot.State switch
    {
        FocusState.Running => $"Focus, {RemainingText} remaining",
        FocusState.Paused => $"Focus paused, {RemainingText} remaining",
        _ => "Focus idle",
    };

    public void Update(FocusSnapshot snapshot)
    {
        var stateChanged = snapshot.State != _snapshot.State;
        _snapshot = snapshot;
        if (stateChanged)
        {
            _isCustomOpen = false;
            OnAllPropertiesChanged();
        }
        else
        {
            OnPropertyChanged(nameof(RemainingText));
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(AccessibleSummary));
        }
    }

    public void RefreshToday()
    {
        var today = _history.GetToday();
        _todayText = today.Sessions == 0
            ? "No focus sessions today"
            : $"{DisplayFormat.Duration(TimeSpan.FromSeconds(today.FocusedSeconds))} today, {DisplayFormat.Count(today.Sessions, "session", "sessions")}";
        OnPropertyChanged(nameof(TodayText));

        var week = _history.GetDays(7);
        var seconds = week.Sum(d => d.FocusedSeconds);
        var sessions = week.Sum(d => d.Sessions);
        _weekText = sessions == 0
            ? "No focus sessions in the last 7 days"
            : $"Last 7 days: {DisplayFormat.Duration(TimeSpan.FromSeconds(seconds))} in {DisplayFormat.Count(sessions, "session", "sessions")}";
        OnPropertyChanged(nameof(WeekText));
    }

    public void Start(int minutes) => _timer.Start(TimeSpan.FromMinutes(minutes));

    public void StartCustom()
    {
        _saveCustomMinutes(_customMinutes);
        Start(_customMinutes);
    }

    public void AdjustCustom(int deltaMinutes)
    {
        var next = Math.Clamp(_customMinutes + deltaMinutes, 5, 240);
        if (next != _customMinutes)
        {
            _customMinutes = next;
            OnPropertyChanged(nameof(CustomMinutes));
            OnPropertyChanged(nameof(CustomMinutesText));
        }
    }

    public void OpenCustom()
    {
        IsCustomOpen = true;
        OnPropertyChanged(nameof(IsCustomRowVisible));
    }

    public void CloseCustom()
    {
        IsCustomOpen = false;
        OnPropertyChanged(nameof(IsCustomRowVisible));
    }

    public void TogglePause() => _timer.TogglePause();

    public void Stop() => _timer.Stop();
}
