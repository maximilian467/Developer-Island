using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.Core.Updates;
using Microsoft.UI.Xaml;

namespace DeveloperIsland.UI.Settings;

/// <summary>Settings, General: "Check for updates", the last check and "Check now".</summary>
public sealed partial class SettingsWindow
{
    private UpdateService? _updates;
    private Action? _openUpdate;

    /// <summary>The update service (none in demo mode) and how to open the update dialog.</summary>
    internal void AttachUpdates(UpdateService? updates, Action openUpdate)
    {
        _updates = updates;
        _openUpdate = openUpdate;
        RefreshUpdates();
    }

    /// <summary>The check started or finished, or settings changed.</summary>
    public void RefreshUpdates()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            LoadUpdates(_store.Current);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void LoadUpdates(AppSettings s)
    {
        UpdatesToggle.IsOn = s.CheckForUpdates;
        UpdatesToggle.IsEnabled = !_isDemo;
        UpdateInstallButton.Visibility = Visibility.Collapsed;
        if (_updates is null)
        {
            UpdateStatusTitle.Text = $"Version {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)}";
            UpdateStatusText.Text = _isDemo ? "Demo mode never checks for updates." : "Not checked yet.";
            UpdateCheckButton.IsEnabled = false;
            return;
        }

        var status = _updates.Status;
        UpdateCheckButton.IsEnabled = !status.IsChecking;
        UpdateStatusText.Text = status.LastCheckedAt is { } at
            ? $"Last checked {DisplayFormat.Ago(DateTimeOffset.UtcNow - at)}"
            : "Not checked yet.";
        if (status.IsChecking)
        {
            UpdateStatusTitle.Text = "Checking for updates…";
        }
        else if (status.Available is { } release)
        {
            var skipped = release.Version.ToString() == s.SkippedUpdateVersion;
            UpdateStatusTitle.Text = $"Developer Island {release.Version} is available{(skipped ? " (skipped)" : string.Empty)}";
            UpdateInstallButton.Visibility = Visibility.Visible;
        }
        else if (status.LastOutcome == UpdateCheckOutcome.Failed)
        {
            UpdateStatusTitle.Text = "GitHub could not be reached. Try again later.";
        }
        else
        {
            UpdateStatusTitle.Text = status.LastCheckedAt is null
                ? $"Version {status.Current}"
                : $"Developer Island {status.Current} is up to date";
        }
    }

    private void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        if (_updates is not null)
        {
            UpdateCheckButton.IsEnabled = false;
            _ = _updates.CheckNowAsync();
        }
    }

    private void OnOpenUpdate(object sender, RoutedEventArgs e) => _openUpdate?.Invoke();
}
