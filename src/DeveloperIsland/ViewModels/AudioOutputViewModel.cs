using System.Collections.ObjectModel;
using DeveloperIsland.Core.Audio;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.ViewModels;

public sealed class AudioOutputViewModel : ObservableObject
{
    private readonly AudioOutputService? _service;
    private string? _selectedDeviceId;
    private string _statusText = "Audio outputs have not been loaded.";
    private bool _isBusy;

    public AudioOutputViewModel(AudioOutputService? service)
    {
        _service = service;
        _statusText = service is null ? "Audio output control is disabled for snapshots." : _statusText;
    }

    public ObservableCollection<AudioOutputDevice> Devices { get; } = [];

    public string? SelectedDeviceId
    {
        get => _selectedDeviceId;
        set
        {
            if (SetProperty(ref _selectedDeviceId, value))
            {
                OnPropertyChanged(nameof(CanSwitch));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSwitch));
                OnPropertyChanged(nameof(CanRefresh));
            }
        }
    }

    public bool CanSwitch => _service is not null && !IsBusy && SelectedDeviceId is { } selected &&
        Devices.Any(device => device.Id == selected && !device.IsDefault);

    public bool CanRefresh => _service is not null && !IsBusy;

    public async Task RefreshAsync()
    {
        if (_service is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Finding playback devices…";
        try
        {
            var outputs = await Task.Run(_service.GetActiveOutputs);
            Devices.Clear();
            foreach (var output in outputs)
            {
                Devices.Add(output);
            }

            var currentOutput = outputs.FirstOrDefault(device => device.IsDefault);
            SelectedDeviceId = currentOutput?.Id ?? outputs.FirstOrDefault()?.Id;
            StatusText = currentOutput is not null
                ? $"Current output: {currentOutput.Name}"
                : outputs.Count == 0 ? "No active playback devices found." : "Windows has no default output. Choose a device.";
        }
        catch (Exception ex)
        {
            Log.Warn("audio", "Playback devices could not be listed", ex: ex);
            Devices.Clear();
            SelectedDeviceId = null;
            StatusText = "Could not read playback devices.";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanSwitch));
        }
    }

    public async Task SwitchAsync()
    {
        if (!CanSwitch || SelectedDeviceId is not { } selected)
        {
            return;
        }

        var selectedName = Devices.First(device => device.Id == selected).Name;
        IsBusy = true;
        StatusText = $"Switching to {selectedName}…";
        try
        {
            try
            {
                await Task.Run(() => _service!.SwitchTo(selected));
            }
            catch (Exception ex)
            {
                Log.Warn("audio", "Playback output could not be changed", ex: ex);
                var errorText = ex is AudioOutputUnavailableException
                    ? "That device disconnected. Refresh and choose an active output."
                    : "Could not switch the playback output.";
                await RefreshAfterSwitchAsync();
                StatusText = errorText;
                return;
            }

            StatusText = await RefreshAfterSwitchAsync()
                ? $"Current output: {selectedName}"
                : $"Switched to {selectedName}, but the device list could not be refreshed.";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanSwitch));
            OnPropertyChanged(nameof(CanRefresh));
        }
    }

    private async Task<bool> RefreshAfterSwitchAsync()
    {
        try
        {
            var outputs = await Task.Run(_service!.GetActiveOutputs);
            Devices.Clear();
            foreach (var output in outputs)
            {
                Devices.Add(output);
            }

            var currentOutput = outputs.FirstOrDefault(device => device.IsDefault);
            SelectedDeviceId = currentOutput?.Id ?? outputs.FirstOrDefault()?.Id;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("audio", "Playback devices could not be refreshed after switching", ex: ex);
            Devices.Clear();
            SelectedDeviceId = null;
            return false;
        }
    }
}
