using DeveloperIsland.Core.Calendar;
using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.ViewModels;

public sealed record AgendaRow(string Time, string Title, string Detail, bool IsNow, bool IsPast)
{
    public bool IsNotNow => !IsNow;

    public double RowOpacity => IsPast ? 0.5 : 1.0;
}

/// <summary>Today's agenda and the next event. The compact island shows "14:30 · Robotics" within the hour.</summary>
public sealed class CalendarViewModel : ModuleViewModel
{
    public const int MaxRows = 5;

    private IReadOnlyList<CalendarEvent> _events = [];
    private IReadOnlyList<string> _failed = [];
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private TimeZoneInfo _zone = TimeZoneInfo.Local;
    private CalendarEvent? _next;
    private CalendarEvent? _compact;

    public CalendarViewModel()
        : base(ModuleId.Calendar)
    {
    }

    public bool HasNext => _next is not null;

    public string NextTitle => _next?.Title ?? "Nothing else today";

    public string NextWhen
    {
        get
        {
            if (_next is null)
            {
                return TomorrowSummary;
            }

            if (_next.Start <= _now)
            {
                return $"Now · until {DisplayFormat.TimeOfDay(TimeZoneInfo.ConvertTime(_next.End, _zone))}";
            }

            var until = _next.Start - _now;
            var time = DisplayFormat.TimeOfDay(TimeZoneInfo.ConvertTime(_next.Start, _zone));
            return until < TimeSpan.FromHours(1) ? $"In {Math.Max(1, (int)Math.Ceiling(until.TotalMinutes))} min · {time}" : time;
        }
    }

    /// <summary>"In 12 min · 14:30 · Lab 2".</summary>
    public string NextLine => _next?.Location is { Length: > 0 } location ? $"{NextWhen} · {location}" : NextWhen;

    public IReadOnlyList<AgendaRow> Today { get; private set; } = [];

    public bool HasToday => Today.Count > 0;

    public string TodayHeader => Today.Count == 0 ? "No events today" : "Today";

    public string MoreText { get; private set; } = string.Empty;

    public bool HasMore => MoreText.Length > 0;

    public string FailedText => _failed.Count == 0 ? string.Empty : $"Could not refresh {string.Join(", ", _failed)}.";

    public bool HasFailed => _failed.Count > 0;

    public bool HasCompact => IsReady && _compact is not null;

    public string CompactText => _compact is null ? string.Empty
        : $"{DisplayFormat.TimeOfDay(TimeZoneInfo.ConvertTime(_compact.Start, _zone))} · {_compact.Title}";

    /// <summary>The featured calendar: "14:30 Robotics", or what the rest of the day looks like.</summary>
    public string CompactNextText => _next is null
        ? "No more events"
        : _next.Start <= _now ? $"Now {_next.Title}" : $"{DisplayFormat.TimeOfDay(TimeZoneInfo.ConvertTime(_next.Start, _zone))} {_next.Title}";

    public string AccessibleSummary => _next is null ? "No more events today" : $"Next: {NextTitle}, {NextWhen}";

    private string TomorrowSummary
    {
        get
        {
            var tomorrow = CalendarAgenda.ForDay(_events, CalendarAgenda.EndOfLocalDay(_now, _zone).AddHours(1), _zone);
            var first = tomorrow.FirstOrDefault(e => !e.IsAllDay);
            return first is null ? "Tomorrow is free" : $"Tomorrow {DisplayFormat.TimeOfDay(TimeZoneInfo.ConvertTime(first.Start, _zone))} · {first.Title}";
        }
    }

    public void Update(ModuleStatus status, IReadOnlyList<CalendarEvent> events, IReadOnlyList<string> failed, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        _events = events;
        _failed = failed;
        _zone = zone ?? TimeZoneInfo.Local;
        SetStatus(status);
        Tick(now);
    }

    /// <summary>Re-evaluates "next" and "in 12 min" as time passes (called every 30 s while enabled).</summary>
    public void Tick(DateTimeOffset now)
    {
        _now = now;
        _next = CalendarAgenda.Next(_events, now, _zone);
        _compact = CalendarAgenda.Compact(_events, now, _zone);

        var day = CalendarAgenda.ForDay(_events, now, _zone);
        Today = day.Take(MaxRows).Select(e => new AgendaRow(
            e.IsAllDay ? "All day" : DisplayFormat.TimeOfDay(TimeZoneInfo.ConvertTime(e.Start, _zone)),
            e.Title,
            e.Location ?? string.Empty,
            e.Start <= now && e.End > now,
            e.End <= now && !e.IsAllDay)).ToList();
        MoreText = day.Count > MaxRows ? $"{day.Count - MaxRows} more today" : string.Empty;
        OnAllPropertiesChanged();
    }

    protected override string EmptyTitle => "No calendar yet";
}
