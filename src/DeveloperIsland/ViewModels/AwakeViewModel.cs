using DeveloperIsland.Core.Awake;
using DeveloperIsland.Core.Formatting;

namespace DeveloperIsland.ViewModels;

/// <summary>Awake session state and actions (keep the PC awake, also with the lid closed).</summary>
public sealed class AwakeViewModel : ObservableObject
{
    public const int CustomStepMinutes = 30;
    public const int CustomMinMinutes = 30;
    public const int CustomMaxMinutes = 24 * 60;

    private AwakeController? _controller;
    private Action<int> _saveCustomMinutes = _ => { };
    private AwakeSnapshot _snapshot = AwakeSnapshot.Idle;
    private int _customMinutes = 120;
    private int _batteryFloor = 10;
    private bool _isCustomOpen;
    private bool _isEnabled;

    /// <summary>Connects the session (the host does this once; without it the module stays off).</summary>
    public void Attach(AwakeController controller, int customMinutes, Action<int> saveCustomMinutes)
    {
        _controller = controller;
        _customMinutes = Math.Clamp(customMinutes, CustomMinMinutes, CustomMaxMinutes);
        _saveCustomMinutes = saveCustomMinutes;
        _snapshot = controller.Snapshot;
        OnAllPropertiesChanged();
    }

    public bool IsEnabled
    {
        get => _isEnabled && _controller is not null;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnAllPropertiesChanged();
            }
        }
    }

    public int BatteryFloor
    {
        get => _batteryFloor;
        set
        {
            if (SetProperty(ref _batteryFloor, value))
            {
                OnPropertyChanged(nameof(SafetyText));
            }
        }
    }

    public AwakeSnapshot Snapshot => _snapshot;

    public bool IsActive => IsEnabled && _snapshot.IsActive;

    public bool IsIdle => !_snapshot.IsActive;

    public bool IsTimed => _snapshot.Mode == AwakeMode.Timed;

    public bool IsIndefinite => _snapshot.IsIndefinite;

    /// <summary>The countdown, or the infinity sign for a session without an end (capsule, peek and panel).</summary>
    public string RemainingText => IsTimed ? DisplayFormat.Clock(_snapshot.Remaining) : "∞";

    public string StatusText => _snapshot.Mode switch
    {
        AwakeMode.Timed => $"{DisplayFormat.Duration(_snapshot.Planned)} session",
        AwakeMode.Indefinite => "Indefinitely, until you end it",
        _ => "Stay awake",
    };

    /// <summary>When it ends, or since when it runs.</summary>
    public string DetailText => _snapshot.StartedAt is not { } started
        ? string.Empty
        : IsTimed
            ? $"Until {DisplayFormat.TimeOfDay((started + _snapshot.Planned).ToLocalTime())}"
            : $"Since {DisplayFormat.TimeOfDay(started.ToLocalTime())}";

    public bool LidFailed => _snapshot.IsActive && _snapshot.Lid == AwakeLid.Failed;

    public bool LidProtected => _snapshot.IsActive && _snapshot.Lid == AwakeLid.Protected;

    public string LidText => LidFailed
        ? "Closing the lid will still sleep this PC: Windows refused the lid setting."
        : "Close the lid: agents keep working.";

    public string SafetyText => _batteryFloor > 0
        ? $"Ends on battery below {_batteryFloor}{DisplayFormat.Nbsp}%"
        : "No battery limit";

    public double Progress => _snapshot.Progress;

    public string CustomMinutesText => DisplayFormat.Duration(TimeSpan.FromMinutes(_customMinutes));

    public bool IsCustomOpen
    {
        get => _isCustomOpen;
        set
        {
            if (SetProperty(ref _isCustomOpen, value))
            {
                OnPropertyChanged(nameof(IsPresetRowVisible));
                OnPropertyChanged(nameof(IsCustomRowVisible));
            }
        }
    }

    public bool IsPresetRowVisible => IsIdle && !_isCustomOpen;

    public bool IsCustomRowVisible => IsIdle && _isCustomOpen;

    public string AccessibleSummary => _snapshot.Mode switch
    {
        AwakeMode.Timed => $"Awake, {RemainingText} remaining",
        AwakeMode.Indefinite => "Awake until you end it",
        _ => "Awake off",
    };

    public void Update(AwakeSnapshot snapshot)
    {
        var changed = snapshot.Mode != _snapshot.Mode || snapshot.StartedAt != _snapshot.StartedAt
            || snapshot.Planned != _snapshot.Planned || snapshot.Lid != _snapshot.Lid;
        _snapshot = snapshot;
        if (changed)
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

    public void Start(int minutes) => _controller?.StartTimed(TimeSpan.FromMinutes(minutes));

    public void StartIndefinite() => _controller?.StartIndefinite();

    public void StartCustom()
    {
        _saveCustomMinutes(_customMinutes);
        Start(_customMinutes);
    }

    public void AdjustCustom(int deltaMinutes)
    {
        var next = Math.Clamp(_customMinutes + deltaMinutes, CustomMinMinutes, CustomMaxMinutes);
        if (next != _customMinutes)
        {
            _customMinutes = next;
            OnPropertyChanged(nameof(CustomMinutesText));
        }
    }

    public void OpenCustom() => IsCustomOpen = true;

    public void CloseCustom() => IsCustomOpen = false;

    public void Extend(int minutes) => _controller?.Extend(TimeSpan.FromMinutes(minutes));

    public void Stop() => _controller?.Stop();
}
