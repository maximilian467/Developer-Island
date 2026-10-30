using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Models;
using Tmds.DBus.Protocol;
using Tmds.DBus.SourceGenerator;

namespace DeveloperIsland.Platform.Media;

/// <summary>A source of media state and transport controls (the system's players or demo data).</summary>
internal interface IMediaSource : IMediaController, IDisposable
{
    MediaSnapshot Snapshot { get; }

    /// <summary>Raised on a background thread.</summary>
    event Action<MediaSnapshot>? Changed;

    Task StartAsync();
}

/// <summary>
/// Reads media players through MPRIS (the freedesktop media player interface on the session bus),
/// so it works with Spotify, browsers (Firefox, Chrome, Chromium), Rhythmbox, VLC, mpv and any other
/// MPRIS player. No app-specific APIs. Event-driven: players announce changes as D-Bus signals and
/// nothing polls. Which player to show follows the same rules as on Windows
/// (<see cref="MediaSessionChooser"/>): the audible one first, then the one that last changed.
/// </summary>
internal sealed class MprisMediaSource : IMediaSource
{
    private const string Prefix = "org.mpris.MediaPlayer2.";
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const int MaxArtBytes = 4 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, Player> _players = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private Connection? _connection;
    private IDisposable? _ownerWatch;
    private MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private Player? _shown;
    private string? _lastActive;
    private string? _artUrl;
    private byte[]? _art;
    private CancellationTokenSource? _debounce;
    private bool _disposed;

    public event Action<MediaSnapshot>? Changed;

    public MediaSnapshot Snapshot => _snapshot;

    public async Task StartAsync()
    {
        try
        {
            _connection = new Connection(Address.Session!);
            await _connection.ConnectAsync();
            var bus = new OrgFreedesktopDBusProxy(_connection, "org.freedesktop.DBus", "/org/freedesktop/DBus");
            _ownerWatch = await bus.WatchNameOwnerChangedAsync((ex, change) =>
            {
                if (ex is null && change.Item1.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    _ = string.IsNullOrEmpty(change.Item3) ? RemoveAsync(change.Item1) : AddAsync(change.Item1, change.Item3);
                }
            }, emitOnCapturedContext: false);

            foreach (var name in await bus.ListNamesAsync())
            {
                if (name.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    var owner = await bus.GetNameOwnerAsync(name);
                    await AddAsync(name, owner);
                }
            }

            Log.Info("media", "MPRIS media players available", new { players = _players.Count, apps = string.Join(", ", PlayerNames()) });
            ScheduleRefresh();
            StartPolling();
        }
        catch (Exception ex)
        {
            // No session bus (a bare X session, a container): music stays idle, the app keeps working.
            Log.Warn("media", "Media players unavailable (no session bus)", ex: ex);
        }
    }

    public Task TogglePlayPauseAsync() => Try(p => p.Proxy.PlayPauseAsync());

    public Task NextAsync() => Try(p => p.Proxy.NextAsync());

    public Task PreviousAsync() => Try(p => p.Proxy.PreviousAsync());

    public Task SeekAsync(TimeSpan position) => Try(p =>
        p.TrackId is { } track ? p.Proxy.SetPositionAsync(new ObjectPath(track), (long)(position.TotalMilliseconds * 1000)) : Task.CompletedTask);

    public void Dispose()
    {
        _disposed = true;
        _debounce?.Cancel();
        _ownerWatch?.Dispose();
        lock (_gate)
        {
            foreach (var player in _players.Values)
            {
                player.Dispose();
            }

            _players.Clear();
        }

        _connection?.Dispose();
        _http.Dispose();
    }

    private async Task Try(Func<Player, Task> action)
    {
        var player = _shown;
        if (player is null)
        {
            return;
        }

        try
        {
            await action(player);
        }
        catch (Exception ex)
        {
            Log.Warn("media", "Media command failed", new { error = ex.GetType().Name });
        }
    }

    private async Task AddAsync(string name, string owner)
    {
        // playerctld re-publishes another player's state under its own name: showing it would duplicate one.
        if (_connection is null || name.EndsWith(".playerctld", StringComparison.Ordinal))
        {
            return;
        }

        await RemoveAsync(name, refresh: false);
        var player = new Player(name, owner, new OrgMprisMediaPlayer2PlayerProxy(_connection, name, ObjectPath));
        try
        {
            var app = new OrgMprisMediaPlayer2Proxy(_connection, name, ObjectPath);
            player.Identity = await app.GetIdentityPropertyAsync();
        }
        catch
        {
            // Identity is optional; the bus name gives a fallback.
        }

        try
        {
            player.Watches.Add(await player.Proxy.WatchPropertiesChangedAsync((ex, changes) =>
            {
                if (ex is not null)
                {
                    // The typed signal reader rejected an unusual value; the lenient read still works.
                    ScheduleRefresh();
                    return;
                }

                if (changes.HasChanged("PlaybackStatus"))
                {
                    _lastActive = name;
                }

                ScheduleRefresh();
            }, emitOnCapturedContext: false));
            player.Watches.Add(await player.Proxy.WatchSeekedAsync((ex, _) =>
            {
                if (ex is null)
                {
                    ScheduleRefresh();
                }
            }, emitOnCapturedContext: false));
        }
        catch (Exception ex)
        {
            Log.Debug("media", "Player signals unavailable", new { error = ex.GetType().Name });
        }

        lock (_gate)
        {
            _players[name] = player;
        }

        Log.Debug("media", "Player appeared", new { app = player.AppName });
        ScheduleRefresh();
    }

    private Task RemoveAsync(string name, bool refresh = true)
    {
        Player? removed;
        lock (_gate)
        {
            if (_players.Remove(name, out removed))
            {
                removed.Dispose();
            }
        }

        if (removed is not null && refresh)
        {
            ScheduleRefresh();
        }

        return Task.CompletedTask;
    }

    /// <summary>Players send bursts of property changes on a track change; coalesce them.</summary>
    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        _debounce?.Cancel();
        var cts = new CancellationTokenSource();
        _debounce = cts;
        _ = Task.Delay(120, cts.Token).ContinueWith(
            async _ => await RefreshAsync(),
            cts.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _refreshLock.WaitAsync();
        try
        {
            List<Player> players;
            lock (_gate)
            {
                players = [.. _players.Values];
            }

            var states = new List<(Player Player, PlayerProperties Props)>();
            foreach (var player in players)
            {
                try
                {
                    states.Add((player, await ReadPropertiesAsync(player)));
                    player.ReadFailureLogged = false;
                }
                catch (Exception ex)
                {
                    // A player that quit between the event and the read is not a candidate. Anything
                    // else is worth one line in the log: it is why a player would never show up.
                    if (!player.ReadFailureLogged)
                    {
                        player.ReadFailureLogged = true;
                        Log.Warn("media", "Player state could not be read", new { app = player.AppName, error = ex.GetType().Name, ex.Message });
                    }
                }
            }

            var candidates = states.Select(s => new MediaSessionCandidate(s.Player.Name, MapState(s.Props.PlaybackStatus), !string.IsNullOrWhiteSpace(Text(s.Props.Metadata, "xesam:title")))).ToList();
            var index = MediaSessionChooser.Choose(candidates, _lastActive ?? _shown?.Name);
            if (index < 0)
            {
                _shown = null;
                Publish(MediaSnapshot.Empty);
                return;
            }

            var (chosen, props) = states[index];
            _shown = chosen;
            Publish(await ReadAsync(chosen, props));
        }
        catch (Exception ex)
        {
            Log.Debug("media", "Media state could not be read", new { error = ex.GetType().Name });
            _shown = null;
            Publish(MediaSnapshot.Empty);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Properties.GetAll, read leniently: every value as a variant, converted by what it actually is.
    /// Players do not all follow the MPRIS types to the letter (Spotify, Electron and browser players
    /// differ in small ways), and a strict reader would drop the whole player over one odd property.
    /// </summary>
    private async Task<PlayerProperties> ReadPropertiesAsync(Player player)
    {
        var connection = _connection ?? throw new InvalidOperationException("No session bus");
        var values = await connection.CallMethodAsync(CreateGetAll(connection, player.Name), static (Message message, object? _) =>
        {
            var reader = message.GetBodyReader();
            return reader.ReadDictionary_aesv();
        });
        return PlayerProperties.From(values);
    }

    private static MessageBuffer CreateGetAll(Connection connection, string destination)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination, ObjectPath, "org.freedesktop.DBus.Properties", "GetAll", "s");
        writer.WriteString("org.mpris.MediaPlayer2.Player");
        return writer.CreateMessage();
    }

    /// <summary>Raw MPRIS player properties of every player on the bus, for <c>--media-report</c>.</summary>
    public static async Task<string> ReportAsync()
    {
        var text = new System.Text.StringBuilder();
        using var connection = new Connection(Address.Session!);
        await connection.ConnectAsync();
        var bus = new OrgFreedesktopDBusProxy(connection, "org.freedesktop.DBus", "/org/freedesktop/DBus");
        foreach (var name in (await bus.ListNamesAsync()).Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            text.AppendLine(name);
            try
            {
                var values = await connection.CallMethodAsync(CreateGetAll(connection, name), static (Message message, object? _) =>
                {
                    var reader = message.GetBodyReader();
                    return reader.ReadDictionary_aesv();
                });
                foreach (var (key, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
                {
                    text.AppendLine($"  {key} = {value}");
                }

                var props = PlayerProperties.From(values);
                text.AppendLine($"  -> status {props.PlaybackStatus ?? "(none)"}, title {Text(props.Metadata, "xesam:title") ?? "(none)"}");
            }
            catch (Exception ex)
            {
                text.AppendLine($"  error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return text.Length == 0 ? "No MPRIS players on the session bus." : text.ToString();
    }

    private async Task<MediaSnapshot> ReadAsync(Player player, PlayerProperties props)
    {
        var metadata = props.Metadata ?? [];
        player.TrackId = metadata.TryGetValue("mpris:trackid", out var track) ? ObjectPathOf(track) : null;
        var duration = metadata.TryGetValue("mpris:length", out var length) ? Microseconds(length) : TimeSpan.Zero;

        // Position is not announced in PropertiesChanged (per spec); reading it with every change
        // and extrapolating from there is what the island's timeline expects.
        var position = TimeSpan.FromTicks(Math.Max(0, props.Position) * 10);
        var artUrl = Text(metadata, "mpris:artUrl");
        if (artUrl != _artUrl)
        {
            _artUrl = artUrl;
            _art = await ReadArtAsync(artUrl);
        }

        var canControl = props.CanControl;
        return new MediaSnapshot
        {
            Title = Text(metadata, "xesam:title"),
            Artist = Texts(metadata, "xesam:artist") is { Length: > 0 } artists ? string.Join(", ", artists) : Texts(metadata, "xesam:albumArtist")?.FirstOrDefault(),
            Album = Text(metadata, "xesam:album"),
            Thumbnail = _art,
            State = MapState(props.PlaybackStatus),
            Position = position,
            Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
            PositionUpdatedAt = DateTimeOffset.UtcNow,
            CanPlayPause = canControl && (props.CanPlay || props.CanPause),
            CanNext = canControl && props.CanGoNext,
            CanPrevious = canControl && props.CanGoPrevious,
            CanSeek = canControl && props.CanSeek && duration > TimeSpan.Zero && player.TrackId is not null,
            SourceApp = player.AppName,
        };
    }

    private void Publish(MediaSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }

    private async Task<byte[]?> ReadArtAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        try
        {
            if (uri.IsFile)
            {
                var info = new FileInfo(uri.LocalPath);
                return info.Exists && info.Length is > 0 and <= MaxArtBytes ? await File.ReadAllBytesAsync(info.FullName) : null;
            }

            // The player already fetched this image (Spotify, browsers hand out its https address);
            // the island fetches the same picture once per track.
            if (uri.Scheme is "https" or "http")
            {
                using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxArtBytes)
                {
                    return null;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync();
                return bytes.Length is > 0 and <= MaxArtBytes ? bytes : null;
            }

            if (uri.Scheme == "data" && url.IndexOf(";base64,", StringComparison.Ordinal) is var comma and > 0)
            {
                return Convert.FromBase64String(url[(comma + 8)..]);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("media", "Artwork unavailable", new { error = ex.GetType().Name });
        }

        return null;
    }

    private static MediaPlaybackState MapState(string? status) => status switch
    {
        "Playing" => MediaPlaybackState.Playing,
        "Paused" => MediaPlaybackState.Paused,
        "Stopped" => MediaPlaybackState.Stopped,
        _ => MediaPlaybackState.None,
    };

    private static string? Text(Dictionary<string, VariantValue>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value))
        {
            return null;
        }

        try
        {
            return value.Type switch
            {
                VariantValueType.String => value.GetString(),
                VariantValueType.ObjectPath => value.GetObjectPathAsString(),
                VariantValueType.Array => value.GetArray<string>().FirstOrDefault(),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string[]? Texts(Dictionary<string, VariantValue> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value))
        {
            return null;
        }

        try
        {
            return value.Type == VariantValueType.Array ? value.GetArray<string>() : value.Type == VariantValueType.String ? [value.GetString()] : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ObjectPathOf(VariantValue value)
    {
        try
        {
            return value.Type switch
            {
                VariantValueType.ObjectPath => value.GetObjectPathAsString(),
                VariantValueType.String => value.GetString(), // some players send a string; SetPosition needs a valid path
                _ => null,
            } is { } path && path.StartsWith('/') ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>mpris:length is int64 microseconds, though some players send other integer types.</summary>
    private static TimeSpan Microseconds(VariantValue value)
    {
        try
        {
            long micros = value.Type switch
            {
                VariantValueType.Int64 => value.GetInt64(),
                VariantValueType.UInt64 => (long)value.GetUInt64(),
                VariantValueType.Int32 => value.GetInt32(),
                VariantValueType.UInt32 => value.GetUInt32(),
                VariantValueType.Double => (long)value.GetDouble(),
                _ => 0,
            };
            return micros > 0 ? TimeSpan.FromTicks(micros * 10) : TimeSpan.Zero;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private string[] PlayerNames()
    {
        lock (_gate)
        {
            return [.. _players.Values.Select(p => p.AppName)];
        }
    }

    /// <summary>
    /// A slow safety net next to the signals: some players skip PropertiesChanged for metadata or
    /// state (or send it in a form the signal reader rejects). Cheap: one GetAll per player.
    /// </summary>
    private void StartPolling()
    {
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (!_disposed && await timer.WaitForNextTickAsync())
            {
                bool any;
                lock (_gate)
                {
                    any = _players.Count > 0;
                }

                if (any || _snapshot.State != MediaPlaybackState.None)
                {
                    await RefreshAsync();
                }
            }
        });
    }

    /// <summary>MPRIS player properties, converted from whatever variant types the player sent.</summary>
    private sealed class PlayerProperties
    {
        public string? PlaybackStatus { get; private init; }

        public Dictionary<string, VariantValue> Metadata { get; private init; } = [];

        public long Position { get; private init; }

        public bool CanGoNext { get; private init; }

        public bool CanGoPrevious { get; private init; }

        public bool CanPlay { get; private init; }

        public bool CanPause { get; private init; }

        public bool CanSeek { get; private init; }

        public bool CanControl { get; private init; } = true;

        public static PlayerProperties From(Dictionary<string, VariantValue> values) => new()
        {
            PlaybackStatus = values.TryGetValue("PlaybackStatus", out var status) && status.Type == VariantValueType.String ? status.GetString() : null,
            Metadata = values.TryGetValue("Metadata", out var metadata) ? MetadataOf(metadata) : [],
            Position = values.TryGetValue("Position", out var position) ? (long)(Microseconds(position).Ticks / 10) : 0,
            CanGoNext = Bool(values, "CanGoNext", false),
            CanGoPrevious = Bool(values, "CanGoPrevious", false),
            CanPlay = Bool(values, "CanPlay", true),
            CanPause = Bool(values, "CanPause", true),
            CanSeek = Bool(values, "CanSeek", false),
            CanControl = Bool(values, "CanControl", true),
        };

        private static bool Bool(Dictionary<string, VariantValue> values, string key, bool fallback)
        {
            try
            {
                return values.TryGetValue(key, out var value) && value.Type == VariantValueType.Bool ? value.GetBool() : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static Dictionary<string, VariantValue> MetadataOf(VariantValue value)
        {
            try
            {
                return value.Type == VariantValueType.Dictionary ? value.GetDictionary<string, VariantValue>() : [];
            }
            catch
            {
                return [];
            }
        }
    }

    private sealed class Player(string name, string owner, OrgMprisMediaPlayer2PlayerProxy proxy) : IDisposable
    {
        public string Name { get; } = name;

        public string Owner { get; } = owner;

        public OrgMprisMediaPlayer2PlayerProxy Proxy { get; } = proxy;

        public List<IDisposable> Watches { get; } = [];

        public string? Identity { get; set; }

        public string? TrackId { get; set; }

        public bool ReadFailureLogged { get; set; }

        public string AppName => FriendlyAppName(Identity, Name);

        public void Dispose()
        {
            foreach (var watch in Watches)
            {
                watch.Dispose();
            }

            Watches.Clear();
        }
    }

    /// <summary>A readable player name: its MPRIS Identity, or one derived from the bus name.</summary>
    internal static string FriendlyAppName(string? identity, string busName)
    {
        if (!string.IsNullOrWhiteSpace(identity))
        {
            return identity switch
            {
                "Mozilla Firefox" => "Firefox",
                _ => identity,
            };
        }

        var id = busName.StartsWith(Prefix, StringComparison.Ordinal) ? busName[Prefix.Length..] : busName;
        id = id.Split('.')[0];
        return id.ToLowerInvariant() switch
        {
            "spotify" => "Spotify",
            "firefox" => "Firefox",
            "chromium" => "Chromium",
            "chrome" => "Google Chrome",
            "brave" => "Brave",
            "vlc" => "VLC",
            "mpv" => "mpv",
            "rhythmbox" => "Rhythmbox",
            "tidal-hifi" => "TIDAL",
            _ => id.Length > 0 ? char.ToUpperInvariant(id[0]) + id[1..] : "Media",
        };
    }
}
