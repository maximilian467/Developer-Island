using DeveloperIsland.Core.Dictation;
using DeveloperIsland.Core.Settings;

namespace DeveloperIsland.UI.Settings;

/// <summary>Settings, Modules: the optional WhisperFlow dictation beside the microphone mark.</summary>
public sealed partial class SettingsWindow
{
    private const string WhisperFlowDescription = "For the local WhisperFlow dictation tool. Beside the microphone mark, the island shows when WhisperFlow records, a spinner while it transcribes, and whether the text was inserted, no speech was heard or it failed. Only WhisperFlow's status file is read (its state and the time), never audio or text.";

    private void LoadWhisperFlow(AppSettings s)
    {
        WhisperFlowToggle.IsOn = s.WhisperFlowEnabled;
        var waiting = s.WhisperFlowEnabled && !_isDemo && !File.Exists(WhisperFlowStatusFile.DefaultPath());
        WhisperFlowStatusText.Text = waiting
            ? WhisperFlowDescription + " No status from WhisperFlow yet: start or restart it once."
            : WhisperFlowDescription;
    }
}
