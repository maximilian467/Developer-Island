using DeveloperIsland.Core.Privacy;

namespace DeveloperIsland.ViewModels;

/// <summary>
/// Camera and microphone in use: one shared instance, shown by every island state (compact, live
/// activity, expanded, notch). It never changes what the island shows or whether it opens.
/// </summary>
public sealed class PrivacyViewModel : ObservableObject
{
    private PrivacyState _state;

    public PrivacyState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(Microphone));
                OnPropertyChanged(nameof(Camera));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(Description));
            }
        }
    }

    public bool Microphone => _state.Microphone;

    public bool Camera => _state.Camera;

    public bool IsActive => _state.IsActive;

    public string Description => _state.Description;
}
