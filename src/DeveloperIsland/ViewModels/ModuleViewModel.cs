using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.ViewModels;

/// <summary>
/// Shared state handling for a module panel: Ready shows the module's content; every other state
/// shows one calm message with at most one action (Provider, then State, then ViewModel, then View).
/// </summary>
public abstract class ModuleViewModel : ObservableObject
{
    private ModuleStatus _status = ModuleStatus.Disabled;

    protected ModuleViewModel(ModuleId module)
    {
        Module = module;
    }

    /// <summary>The panel asks for its one action (open Settings, retry, get a tool).</summary>
    public event Action<ModuleViewModel>? ActionRequested;

    public ModuleId Module { get; }

    public string Name => ModuleCatalog.DisplayName(Module);

    public ModuleStatus Status => _status;

    public bool IsEnabled => _status.State != ModuleState.Disabled;

    public bool IsReady => _status.State == ModuleState.Ready;

    public bool ShowState => !IsReady;

    public bool IsLoading => _status.State == ModuleState.Loading;

    public string StateTitle => _status.State switch
    {
        ModuleState.Loading => "Loading…",
        ModuleState.Empty => EmptyTitle,
        ModuleState.Unavailable => UnavailableTitle,
        ModuleState.Error => "Something went wrong",
        ModuleState.Disabled => $"{Name} is off",
        _ => string.Empty,
    };

    public string StateMessage => _status.State switch
    {
        ModuleState.Disabled => "Turn it on in Settings, Modules.",
        _ => _status.Message ?? string.Empty,
    };

    public string ActionLabel => _status.State switch
    {
        ModuleState.Empty => EmptyActionLabel,
        ModuleState.Unavailable => UnavailableActionLabel,
        ModuleState.Error => "Try again",
        _ => string.Empty,
    };

    public bool HasAction => ActionLabel.Length > 0;

    protected virtual string EmptyTitle => "Nothing here yet";

    protected virtual string EmptyActionLabel => "Open Settings";

    protected virtual string UnavailableTitle => "Not connected";

    protected virtual string UnavailableActionLabel => string.Empty;

    public void RequestAction() => ActionRequested?.Invoke(this);

    /// <summary>Applies a new status; subclasses call this from their Update methods.</summary>
    protected void SetStatus(ModuleStatus status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(ShowState));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(StateMessage));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(HasAction));
    }
}
