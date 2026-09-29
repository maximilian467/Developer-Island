using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeveloperIsland.ViewModels;

/// <summary>Current track and transport state for the compact, activity and expanded music views.</summary>
public sealed class MusicViewModel : ObservableObject
{
    private readonly IMediaController _controller;
    private MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private byte[]? _artBytes;
    private ImageSource? _art;
    private bool _isEnabled = true;

    public MusicViewModel(IMediaController controller)
    {
        _controller = controller;
    }

    /// <summary>Raised when a different track starts (not on pause/resume or seek).</summary>
    public event Action? TrackChanged;

    /// <summary>Raised when artwork finished decoding (it arrives after the track change).</summary>
    public event Action? ArtChanged;

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

    public MediaSnapshot Snapshot => _snapshot;

    public bool HasTrack => _isEnabled && _snapshot.HasTrack;

    public bool HasNoTrack => !HasTrack;

    public bool IsPlaying => HasTrack && _snapshot.IsPlaying;

    public string Title => _snapshot.Title ?? string.Empty;

    public string Artist => string.IsNullOrWhiteSpace(_snapshot.Artist) ? "Unknown artist" : _snapshot.Artist!;

    public string SourceText => _snapshot.SourceApp ?? string.Empty;

    public bool HasSource => !string.IsNullOrEmpty(_snapshot.SourceApp);

    public ImageSource? Art => _art;

    public bool HasArt => _art is not null;

    public bool HasNoArt => _art is null;

    public string PlayPauseGlyph => IsPlaying ? "" : "";

    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    public bool CanPlayPause => HasTrack && _snapshot.CanPlayPause;

    public bool CanNext => HasTrack && _snapshot.CanNext;

    public bool CanPrevious => HasTrack && _snapshot.CanPrevious;

    public bool CanSeek => HasTrack && _snapshot.CanSeek;

    public bool HasTimeline => HasTrack && _snapshot.Duration > TimeSpan.Zero;

    public TimeSpan Position => _snapshot.EstimatePosition(DateTimeOffset.UtcNow);

    public string PositionText => DisplayFormat.MediaTime(Position);

    public string DurationText => DisplayFormat.MediaTime(_snapshot.Duration);

    public double Progress => _snapshot.Duration > TimeSpan.Zero ? Math.Clamp(Position / _snapshot.Duration, 0, 1) : 0;

    public string AccessibleSummary => HasTrack ? $"{Title} by {Artist}, {(IsPlaying ? "playing" : "paused")}" : "Nothing playing";

    public void Update(MediaSnapshot snapshot)
    {
        var trackChanged = snapshot.HasTrack && snapshot.TrackKey != _snapshot.TrackKey;
        _snapshot = snapshot;
        if (!ReferenceEquals(snapshot.Thumbnail, _artBytes))
        {
            _artBytes = snapshot.Thumbnail;
            _ = LoadArtAsync(snapshot.Thumbnail);
        }

        OnAllPropertiesChanged();
        if (trackChanged && _isEnabled)
        {
            TrackChanged?.Invoke();
        }
    }

    /// <summary>Refreshes the extrapolated position; called once per second while the timeline is visible.</summary>
    public void Tick()
    {
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(Progress));
    }

    public Task TogglePlayPauseAsync() => _controller.TogglePlayPauseAsync();

    public Task NextAsync() => _controller.NextAsync();

    public Task PreviousAsync() => _controller.PreviousAsync();

    public Task SeekToFractionAsync(double fraction) =>
        CanSeek ? _controller.SeekAsync(_snapshot.Duration * Math.Clamp(fraction, 0, 1)) : Task.CompletedTask;

    private async Task LoadArtAsync(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            _art = null;
            OnPropertyChanged(nameof(Art));
            OnPropertyChanged(nameof(HasArt));
            OnPropertyChanged(nameof(HasNoArt));
            return;
        }

        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 128, DecodePixelType = DecodePixelType.Logical };
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            if (ReferenceEquals(bytes, _artBytes))
            {
                _art = bitmap;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("media", "Artwork could not be decoded", new { error = ex.GetType().Name });
            _art = null;
        }

        OnPropertyChanged(nameof(Art));
        OnPropertyChanged(nameof(HasArt));
        OnPropertyChanged(nameof(HasNoArt));
        ArtChanged?.Invoke();
    }
}
