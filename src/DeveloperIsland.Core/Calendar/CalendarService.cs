using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.Core.Calendar;

/// <summary>
/// A source of calendar events. ICS (a published calendar link or an .ics file) is the first one;
/// account-based providers (Outlook, Google) can be added later behind the same interface without
/// changing the module.
/// </summary>
public interface ICalendarSource
{
    string Name { get; }

    Task<IReadOnlyList<CalendarEvent>> FetchAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

/// <summary>
/// An iCalendar feed: an https/webcal link (the "secret address in iCal format" that Google, Outlook
/// and iCloud offer) or a local .ics file. Read-only; no sign-in, no credentials stored beyond the
/// link the user pasted.
/// </summary>
public sealed class IcsCalendarSource(string location, HttpClient http) : ICalendarSource
{
    public const long MaxBytes = 16 * 1024 * 1024;

    public string Location { get; } = location.Trim();

    public string Name => DisplayName(Location);

    public async Task<IReadOnlyList<CalendarEvent>> FetchAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        string text;
        if (IsWebAddress(Location))
        {
            var url = Location.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + Location["webcal://".Length..] : Location;
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new InvalidDataException("Calendar too large");
            }

            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        else
        {
            var info = new FileInfo(Location);
            if (info.Length > MaxBytes)
            {
                throw new InvalidDataException("Calendar too large");
            }

            text = await File.ReadAllTextAsync(Location, cancellationToken);
        }

        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Not an iCalendar file");
        }

        return IcsParser.Parse(text, Name, from, to);
    }

    public static bool IsWebAddress(string location) =>
        location.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || location.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)
        || location.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>A short, non-secret name: the host for links (the path often contains a private token), the file name for files.</summary>
    public static string DisplayName(string location)
    {
        if (IsWebAddress(location) && Uri.TryCreate(location.Replace("webcal://", "https://", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out var uri))
        {
            return uri.Host switch
            {
                "calendar.google.com" => "Google Calendar",
                "outlook.office365.com" or "outlook.live.com" => "Outlook",
                var h when h.EndsWith("icloud.com", StringComparison.OrdinalIgnoreCase) => "iCloud",
                var h => h,
            };
        }

        return Path.GetFileNameWithoutExtension(location);
    }

    /// <summary>Accepts https, http and webcal links and existing .ics files.</summary>
    public static bool IsValidLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        var text = location.Trim();
        if (IsWebAddress(text))
        {
            return Uri.TryCreate(text.Replace("webcal://", "https://", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out _);
        }

        try
        {
            return Path.IsPathRooted(text) && text.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) && File.Exists(text);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>What the calendar module shows, derived from the fetched events.</summary>
public static class CalendarAgenda
{
    /// <summary>How soon an event must start to appear in the compact island.</summary>
    public static readonly TimeSpan CompactLead = TimeSpan.FromMinutes(60);

    /// <summary>How long a started event stays in the compact island.</summary>
    public static readonly TimeSpan CompactTail = TimeSpan.FromMinutes(10);

    /// <summary>The event in progress or the next one today (timed events only), or null.</summary>
    public static CalendarEvent? Next(IEnumerable<CalendarEvent> events, DateTimeOffset now, TimeZoneInfo zone)
    {
        var endOfDay = EndOfLocalDay(now, zone);
        return events
            .Where(e => !e.IsAllDay && e.End > now && e.Start < endOfDay)
            .OrderBy(e => e.Start)
            .FirstOrDefault();
    }

    /// <summary>The event the compact island mentions: starting within the hour or just started.</summary>
    public static CalendarEvent? Compact(IEnumerable<CalendarEvent> events, DateTimeOffset now, TimeZoneInfo zone)
    {
        var next = Next(events, now, zone);
        if (next is null)
        {
            return null;
        }

        return next.Start - now <= CompactLead && now - next.Start <= CompactTail ? next : null;
    }

    /// <summary>Events of the local day containing <paramref name="day"/>, all-day events first.</summary>
    public static List<CalendarEvent> ForDay(IEnumerable<CalendarEvent> events, DateTimeOffset day, TimeZoneInfo zone)
    {
        var start = StartOfLocalDay(day, zone);
        var end = EndOfLocalDay(day, zone);
        return events
            .Where(e => e.Start < end && (e.End > start || e.End == e.Start && e.Start >= start))
            .OrderByDescending(e => e.IsAllDay)
            .ThenBy(e => e.Start)
            .ToList();
    }

    public static DateTimeOffset StartOfLocalDay(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return IcsParser.ToOffset(local.Date, zone);
    }

    public static DateTimeOffset EndOfLocalDay(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return IcsParser.ToOffset(local.Date.AddDays(1), zone);
    }
}

/// <summary>
/// Keeps the next two days of events from all sources. Refreshes every 15 minutes while enabled and
/// on demand; a source that fails keeps its last good events and reports the error. Event titles
/// are shown only in the island and never logged.
/// </summary>
public sealed class CalendarService : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private readonly Func<string, ICalendarSource> _createSource;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<CalendarEvent>> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private List<string> _locations = [];
    private ITimer? _timer;
    private bool _enabled;
    private bool _fetchedOnce;
    private CancellationTokenSource _cts = new();

    public CalendarService(Func<string, ICalendarSource> createSource, TimeProvider? time = null)
    {
        _createSource = createSource;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on a background thread after a refresh.</summary>
    public event Action? Changed;

    public ModuleStatus Status
    {
        get
        {
            lock (_gate)
            {
                if (!_enabled)
                {
                    return ModuleStatus.Disabled;
                }

                if (_locations.Count == 0)
                {
                    return new ModuleStatus(ModuleState.Empty, "Add a calendar link (iCal format) or an .ics file in Settings.");
                }

                if (!_fetchedOnce)
                {
                    return ModuleStatus.Loading;
                }

                if (_locations.All(_errors.ContainsKey))
                {
                    return new ModuleStatus(ModuleState.Error, "Calendars could not be loaded. Check the link or your connection.");
                }

                return ModuleStatus.Ready;
            }
        }
    }

    /// <summary>Names of sources whose last refresh failed.</summary>
    public IReadOnlyList<string> FailedSources
    {
        get
        {
            lock (_gate)
            {
                return _errors.Keys.Select(IcsCalendarSource.DisplayName).ToList();
            }
        }
    }

    public IReadOnlyList<CalendarEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.Values.SelectMany(e => e).OrderBy(e => e.Start).ToList();
            }
        }
    }

    public void Configure(bool enabled, IEnumerable<string> locations)
    {
        var clean = locations.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        bool changed;
        lock (_gate)
        {
            changed = enabled != _enabled || !clean.SequenceEqual(_locations);
            _enabled = enabled;
            _locations = clean;
            foreach (var removed in _events.Keys.Except(clean).ToList())
            {
                _events.Remove(removed);
                _errors.Remove(removed);
            }

            _timer?.Dispose();
            _timer = null;
            if (enabled && clean.Count > 0)
            {
                _timer = _time.CreateTimer(_ => _ = RefreshAsync(), null, changed ? TimeSpan.Zero : RefreshInterval, RefreshInterval);
            }
            else
            {
                _events.Clear();
                _errors.Clear();
                _fetchedOnce = false;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public async Task RefreshAsync()
    {
        List<string> locations;
        CancellationToken token;
        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            locations = _locations.ToList();
            token = _cts.Token;
        }

        if (!await _fetchLock.WaitAsync(0, token))
        {
            return; // a refresh is already running
        }

        try
        {
            var now = _time.GetUtcNow();
            var from = now.AddHours(-12);
            var to = now.AddDays(2);
            foreach (var location in locations)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    var events = await _createSource(location).FetchAsync(from, to, timeout.Token);
                    lock (_gate)
                    {
                        _events[location] = events;
                        _errors.Remove(location);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        _errors[location] = ex.GetType().Name;
                    }

                    // The location may contain a private token: log the source kind only.
                    Log.Warn("calendar", "Calendar refresh failed", new { source = IcsCalendarSource.DisplayName(location), error = ex.GetType().Name });
                }
            }

            lock (_gate)
            {
                _fetchedOnce = true;
            }

            Changed?.Invoke();
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _enabled = false;
            _timer?.Dispose();
            _timer = null;
            _cts.Cancel();
        }
    }
}
