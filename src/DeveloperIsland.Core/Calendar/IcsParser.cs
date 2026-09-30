using System.Globalization;
using System.Text;

namespace DeveloperIsland.Core.Calendar;

/// <summary>One occurrence of a calendar event, in absolute time.</summary>
public sealed record CalendarEvent(string Title, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, string? Location, string Source);

/// <summary>
/// Reads iCalendar (RFC 5545) files, as exported or published by Google Calendar, Outlook, iCloud
/// and most other calendars. Supports what real calendars use: time zones (Windows and IANA ids),
/// all-day events, RRULE with FREQ DAILY/WEEKLY/MONTHLY/YEARLY, INTERVAL, COUNT, UNTIL and BYDAY,
/// EXDATE, moved occurrences (RECURRENCE-ID) and cancelled events.
/// </summary>
public static class IcsParser
{
    /// <summary>Occurrences that overlap [from, to), sorted by start.</summary>
    public static List<CalendarEvent> Parse(string ics, string source, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo? local = null)
    {
        local ??= TimeZoneInfo.Local;
        var components = ReadEvents(Unfold(ics));
        var overrides = components
            .Where(c => c.RecurrenceId is not null && c.Uid is not null)
            .ToLookup(c => c.Uid!);

        var results = new List<CalendarEvent>();
        foreach (var e in components.Where(c => c.RecurrenceId is null))
        {
            if (e.Start is null || e.Cancelled)
            {
                continue;
            }

            var start = Resolve(e.Start, local);
            var end = e.End is not null ? Resolve(e.End, local)
                : e.Duration is { } d ? start + d
                : e.Start.IsDate ? start.AddDays(1) : start;
            var length = end - start;
            if (length < TimeSpan.Zero)
            {
                length = TimeSpan.Zero;
            }

            var moved = e.Uid is null ? [] : overrides[e.Uid].ToList();
            var replaced = new HashSet<DateTimeOffset>(moved.Where(m => m.RecurrenceId is not null).Select(m => Resolve(m.RecurrenceId!, local)));
            var excluded = new HashSet<DateTimeOffset>(e.ExDates.Select(x => Resolve(x, local)));

            foreach (var occurrence in Occurrences(e, start, local, from - length, to))
            {
                if (excluded.Contains(occurrence) || replaced.Contains(occurrence))
                {
                    continue;
                }

                if (occurrence < to && occurrence + length > from || length == TimeSpan.Zero && occurrence >= from && occurrence < to)
                {
                    results.Add(new CalendarEvent(e.Summary ?? "Busy", occurrence, occurrence + length, e.Start.IsDate, e.Location, source));
                }
            }

            foreach (var m in moved)
            {
                if (m.Cancelled || m.Start is null)
                {
                    continue;
                }

                var mStart = Resolve(m.Start, local);
                var mEnd = m.End is not null ? Resolve(m.End, local) : m.Duration is { } md ? mStart + md : mStart + length;
                if (mStart < to && mEnd > from)
                {
                    results.Add(new CalendarEvent(m.Summary ?? e.Summary ?? "Busy", mStart, mEnd, m.Start.IsDate, m.Location ?? e.Location, source));
                }
            }
        }

        results.Sort((a, b) => a.Start.CompareTo(b.Start));
        return results;
    }

    private static IEnumerable<DateTimeOffset> Occurrences(VEvent e, DateTimeOffset start, TimeZoneInfo local, DateTimeOffset from, DateTimeOffset to)
    {
        if (e.RRule is null)
        {
            yield return start;
            yield break;
        }

        var rule = RecurrenceRule.Parse(e.RRule);
        if (rule is null)
        {
            // Unsupported rule: show the first occurrence rather than guessing.
            yield return start;
            yield break;
        }

        var zone = e.Start!.Zone(local);
        var wallStart = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        var until = rule.Until is { } u ? Resolve(u, local) : (DateTimeOffset?)null;
        var count = 0;
        var skipBefore = from.UtcDateTime.AddDays(-2); // wall time within 2 days of UTC is safe to skip
        const int SafetyLimit = 200_000;

        foreach (var wall in rule.Expand(wallStart).Take(SafetyLimit))
        {
            if (wall < skipBefore)
            {
                // Long-running series: count old occurrences without converting them.
                count++;
                if (rule.Count is { } cap && count >= cap)
                {
                    yield break;
                }

                continue;
            }

            var at = e.Start.IsDate ? new DateTimeOffset(wall.Date, local.GetUtcOffset(wall.Date)) : ToOffset(wall, zone);
            if (until is { } limit && at > limit || at >= to)
            {
                yield break;
            }

            if (rule.Count is { } max && count >= max)
            {
                yield break;
            }

            count++;
            yield return at;
        }
    }

    internal static DateTimeOffset ToOffset(DateTime wall, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            // Inside a spring-forward gap: move to the first valid minute after it.
            unspecified = unspecified.AddHours(1);
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    private static DateTimeOffset Resolve(IcsDate date, TimeZoneInfo local)
    {
        if (date.IsUtc)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(date.Value, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        return date.IsDate
            ? new DateTimeOffset(date.Value.Date, local.GetUtcOffset(date.Value.Date))
            : ToOffset(date.Value, date.Zone(local));
    }

    private static IEnumerable<string> Unfold(string ics)
    {
        var current = new StringBuilder();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                current.Append(raw, 1, raw.Length - 1);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }

            current.Clear().Append(raw.TrimEnd('\r'));
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static List<VEvent> ReadEvents(IEnumerable<string> lines)
    {
        var events = new List<VEvent>();
        VEvent? current = null;
        var depth = 0;
        foreach (var line in lines)
        {
            var colon = FindValueColon(line);
            if (colon < 0)
            {
                continue;
            }

            var head = line[..colon];
            var value = line[(colon + 1)..];
            var parameters = head.Split(';');
            var name = parameters[0].ToUpperInvariant();

            if (name == "BEGIN")
            {
                if (value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    current = new VEvent();
                    depth = 0;
                }
                else if (current is not null)
                {
                    depth++; // VALARM and other nested components
                }

                continue;
            }

            if (name == "END")
            {
                if (current is not null && value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(current);
                    current = null;
                }
                else if (current is not null)
                {
                    depth--;
                }

                continue;
            }

            if (current is null || depth > 0)
            {
                continue;
            }

            switch (name)
            {
                case "UID": current.Uid = value.Trim(); break;
                case "SUMMARY": current.Summary = Unescape(value); break;
                case "LOCATION": current.Location = Unescape(value); break;
                case "STATUS": current.Cancelled = value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                case "DTSTART": current.Start = IcsDate.Parse(value, parameters); break;
                case "DTEND": current.End = IcsDate.Parse(value, parameters); break;
                case "DURATION": current.Duration = ParseDuration(value); break;
                case "RRULE": current.RRule = value.Trim(); break;
                case "RECURRENCE-ID": current.RecurrenceId = IcsDate.Parse(value, parameters); break;
                case "EXDATE":
                    foreach (var part in value.Split(','))
                    {
                        if (IcsDate.Parse(part, parameters) is { } ex)
                        {
                            current.ExDates.Add(ex);
                        }
                    }

                    break;
            }
        }

        return events;
    }

    /// <summary>The colon that separates value from name and parameters (parameters may be quoted).</summary>
    private static int FindValueColon(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Unescape(string value)
    {
        var text = value.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
        return text.Length > 120 ? text[..120] : text;
    }

    internal static TimeSpan? ParseDuration(string value)
    {
        var text = value.Trim().ToUpperInvariant();
        var negative = text.StartsWith('-');
        text = text.TrimStart('+', '-');
        if (!text.StartsWith('P'))
        {
            return null;
        }

        var total = TimeSpan.Zero;
        var number = 0;
        var inTime = false;
        foreach (var c in text[1..])
        {
            if (char.IsAsciiDigit(c))
            {
                number = number * 10 + (c - '0');
                continue;
            }

            switch (c)
            {
                case 'T': inTime = true; break;
                case 'W': total += TimeSpan.FromDays(7 * number); break;
                case 'D': total += TimeSpan.FromDays(number); break;
                case 'H' when inTime: total += TimeSpan.FromHours(number); break;
                case 'M' when inTime: total += TimeSpan.FromMinutes(number); break;
                case 'S' when inTime: total += TimeSpan.FromSeconds(number); break;
                default: return null;
            }

            number = 0;
        }

        return negative ? -total : total;
    }

    private sealed class VEvent
    {
        public string? Uid { get; set; }

        public string? Summary { get; set; }

        public string? Location { get; set; }

        public bool Cancelled { get; set; }

        public IcsDate? Start { get; set; }

        public IcsDate? End { get; set; }

        public TimeSpan? Duration { get; set; }

        public string? RRule { get; set; }

        public IcsDate? RecurrenceId { get; set; }

        public List<IcsDate> ExDates { get; } = [];
    }
}

/// <summary>A DATE or DATE-TIME value with its time zone reference.</summary>
internal sealed record IcsDate(DateTime Value, bool IsDate, bool IsUtc, string? TimeZoneId)
{
    public static IcsDate? Parse(string value, string[] parameters)
    {
        var text = value.Trim();
        var tzid = parameters.Skip(1)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Equals("TZID", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim('"'))
            .FirstOrDefault();

        if (text.Length == 8 && DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new IcsDate(date, IsDate: true, IsUtc: false, null);
        }

        var utc = text.EndsWith('Z');
        if (DateTime.TryParseExact(text.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime))
        {
            return new IcsDate(dateTime, IsDate: false, utc, utc ? null : tzid);
        }

        return null;
    }

    /// <summary>The event's zone; floating times and unknown zones use the local zone.</summary>
    public TimeZoneInfo Zone(TimeZoneInfo local)
    {
        if (IsUtc)
        {
            return TimeZoneInfo.Utc;
        }

        return TimeZoneId is { Length: > 0 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : local;
    }
}

/// <summary>The subset of RRULE that covers nearly all real-world recurring events.</summary>
internal sealed record RecurrenceRule(string Frequency, int Interval, int? Count, IcsDate? Until, IReadOnlyList<DayOfWeek> ByDay, IReadOnlyList<int> ByMonthDay)
{
    public static RecurrenceRule? Parse(string rrule)
    {
        var parts = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.Ordinal);

        if (!parts.TryGetValue("FREQ", out var freq) || freq is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"))
        {
            return null;
        }

        var supported = new HashSet<string> { "FREQ", "INTERVAL", "COUNT", "UNTIL", "BYDAY", "WKST", "BYMONTHDAY", "BYMONTH" };
        if (parts.Keys.Any(k => !supported.Contains(k)))
        {
            return null;
        }

        var byDay = new List<DayOfWeek>();
        if (parts.TryGetValue("BYDAY", out var days))
        {
            foreach (var d in days.Split(','))
            {
                // Ordinal forms (2MO, -1FR) only make sense for MONTHLY/YEARLY; not supported.
                if (d.Length != 2)
                {
                    return null;
                }

                byDay.Add(d switch
                {
                    "MO" => DayOfWeek.Monday,
                    "TU" => DayOfWeek.Tuesday,
                    "WE" => DayOfWeek.Wednesday,
                    "TH" => DayOfWeek.Thursday,
                    "FR" => DayOfWeek.Friday,
                    "SA" => DayOfWeek.Saturday,
                    _ => DayOfWeek.Sunday,
                });
            }
        }

        var byMonthDay = parts.TryGetValue("BYMONTHDAY", out var monthDays)
            ? monthDays.Split(',').Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0).Where(v => v is >= 1 and <= 31).ToList()
            : [];

        return new RecurrenceRule(
            freq,
            parts.TryGetValue("INTERVAL", out var i) && int.TryParse(i, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval) && interval > 0 ? interval : 1,
            parts.TryGetValue("COUNT", out var c) && int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0 ? count : null,
            parts.TryGetValue("UNTIL", out var until) ? IcsDate.Parse(until, []) : null,
            byDay,
            byMonthDay);
    }

    /// <summary>Wall-clock occurrences in order, starting with the event's own start.</summary>
    public IEnumerable<DateTime> Expand(DateTime start)
    {
        switch (Frequency)
        {
            case "DAILY":
                for (var d = start; ; d = d.AddDays(Interval))
                {
                    if (ByDay.Count == 0 || ByDay.Contains(d.DayOfWeek))
                    {
                        yield return d;
                    }
                }

            case "WEEKLY":
                var days = ByDay.Count > 0 ? ByDay : [start.DayOfWeek];

                // Weeks start on Monday (WKST default); walk week by week.
                var weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                for (var week = weekStart; ; week = week.AddDays(7 * Interval))
                {
                    foreach (var day in days.OrderBy(d => ((int)d + 6) % 7))
                    {
                        var at = week.AddDays(((int)day + 6) % 7) + start.TimeOfDay;
                        if (at >= start)
                        {
                            yield return at;
                        }
                    }
                }

            case "MONTHLY":
                var monthDays = ByMonthDay.Count > 0 ? ByMonthDay : [start.Day];
                var month = new DateTime(start.Year, start.Month, 1);
                for (var n = 0; n < 12_000 && month.Year < 9_999; n++, month = month.AddMonths(Interval))
                {
                    foreach (var day in monthDays.Order())
                    {
                        if (day <= DateTime.DaysInMonth(month.Year, month.Month))
                        {
                            var at = month.AddDays(day - 1) + start.TimeOfDay;
                            if (at >= start)
                            {
                                yield return at;
                            }
                        }
                    }
                }

                yield break;

            default: // YEARLY
                for (var year = start.Year; year < 10_000; year += Interval)
                {
                    if (start.Month == 2 && start.Day == 29 && !DateTime.IsLeapYear(year))
                    {
                        continue;
                    }

                    yield return new DateTime(year, start.Month, start.Day) + start.TimeOfDay;
                }

                yield break;
        }
    }
}
