using DeveloperIsland.Core.Dictation;
using DeveloperIsland.Core.Privacy;

namespace DeveloperIsland.ViewModels;

/// <summary>
/// Camera and microphone in use: one shared instance, shown by every island state (compact, live
/// activity, expanded, notch). It never changes what the island shows or whether it opens. With the
/// WhisperFlow integration on, the dictation's phase joins the microphone mark.
/// </summary>
public sealed class PrivacyViewModel : ObservableObject
{
    private PrivacyState _state;
    private DictationPhase _dictation;

    public PrivacyState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                Notify();
            }
        }
    }

    public DictationPhase Dictation
    {
        get => _dictation;
        set
        {
            if (SetProperty(ref _dictation, value))
            {
                Notify();
            }
        }
    }

    /// <summary>The microphone mark: in use by any app, or recording for WhisperFlow (ahead of Windows' own record).</summary>
    public bool Microphone => _state.Microphone || _dictation == DictationPhase.Recording;

    public bool Camera => _state.Camera;

    public bool IsActive => _state.IsActive || _dictation != DictationPhase.Idle;

    public string DictationLabel => DictationText.Label(_dictation);

    public string Description => _dictation == DictationPhase.Idle
        ? _state.Description
        : string.Join(", ", new[] { DictationText.Description(_dictation), _state.Camera ? "camera in use" : "" }.Where(s => s.Length > 0));

    private void Notify()
    {
        OnPropertyChanged(nameof(Microphone));
        OnPropertyChanged(nameof(Camera));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(DictationLabel));
        OnPropertyChanged(nameof(Description));
    }
}
