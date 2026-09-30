using DeveloperIsland.Core.Calendar;
using DeveloperIsland.Core.Modules;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

public class CalendarTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    private static string Calendar(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\n" + string.Join("\r\n", events) + "\r\nEND:VCALENDAR\r\n";

    private static string Event(string body) => "BEGIN:VEVENT\r\n" + body.Replace("\n", "\r\n").Trim() + "\r\nEND:VEVENT";

    private static DateTimeOffset BerlinTime(int y, int mo, int d, int h, int mi) => IcsParser.ToOffset(new DateTime(y, mo, d, h, mi, 0), Berlin);

    [Fact]
    public void Parses_a_timed_event_in_its_time_zone()
    {
        var ics = Calendar(Event("""
            UID:1
            SUMMARY:Robotics
            LOCATION:Lab 2
            DTSTART;TZID=Europe/Berlin:20260930T143000
            DTEND;TZID=Europe/Berlin:20260930T160000
            """));

        var e = Assert.Single(IcsParser.Parse(ics, "Test", BerlinTime(2026, 9, 30, 0, 0), BerlinTime(2026, 10, 1, 0, 0), Berlin));

        Assert.Equal("Robotics", e.Title);
        Assert.Equal("Lab 2", e.Location);
        Assert.Equal(BerlinTime(2026, 9, 30, 14, 30), e.Start);
        Assert.Equal(TimeSpan.FromMinutes(90), e.End - e.Start);
        Assert.False(e.IsAllDay);
    }

    [Fact]
    public void Utc_and_windows_zone_ids_and_folded_lines()
    {
        var ics = Calendar(
            Event("""
                UID:utc
                SUMMARY:Stand
                 up
                DTSTART:20260930T120000Z
                DURATION:PT15M
                """),
            Event("""
                UID:win
                SUMMARY:Review\, final
                DTSTART;TZID="W. Europe Standard Time":20260930T170000
                DTEND;TZID="W. Europe Standard Time":20260930T173000
                """));

        var events = IcsParser.Parse(ics, "Test", BerlinTime(2026, 9, 30, 0, 0), BerlinTime(2026, 10, 1, 0, 0), Berlin);

        Assert.Equal("Standup", events[0].Title);
        Assert.Equal(BerlinTime(2026, 9, 30, 14, 0), events[0].Start);
        Assert.Equal(TimeSpan.FromMinutes(15), events[0].End - events[0].Start);
        Assert.Equal("Review, final", events[1].Title);
    }

    [Fact]
    public void All_day_events_cover_the_local_day()
    {
        var ics = Calendar(Event("""
            UID:holiday
            SUMMARY:Holiday
            DTSTART;VALUE=DATE:20261003
            DTEND;VALUE=DATE:20261004
            """));

        var e = Assert.Single(IcsParser.Parse(ics, "Test", BerlinTime(2026, 10, 3, 0, 0), BerlinTime(2026, 10, 4, 0, 0), Berlin));

        Assert.True(e.IsAllDay);
        Assert.Equal(BerlinTime(2026, 10, 3, 0, 0), e.Start);
    }

    [Fact]
    public void Weekly_rule_with_byday_exdate_and_a_moved_occurrence()
    {
        var ics = Calendar(
            Event("""
                UID:weekly
                SUMMARY:Robotics
                DTSTART;TZID=Europe/Berlin:20260907T143000
                DTEND;TZID=Europe/Berlin:20260907T160000
                RRULE:FREQ=WEEKLY;BYDAY=MO,WE
                EXDATE;TZID=Europe/Berlin:20260914T143000
                """),
            Event("""
                UID:weekly
                RECURRENCE-ID;TZID=Europe/Berlin:20260916T143000
                SUMMARY:Robotics (moved)
                DTSTART;TZID=Europe/Berlin:20260917T090000
                DTEND;TZID=Europe/Berlin:20260917T103000
                """));

        var events = IcsParser.Parse(ics, "Test", BerlinTime(2026, 9, 7, 0, 0), BerlinTime(2026, 9, 21, 0, 0), Berlin);

        Assert.Equal(
            [BerlinTime(2026, 9, 7, 14, 30), BerlinTime(2026, 9, 9, 14, 30), BerlinTime(2026, 9, 17, 9, 0)],
            events.Select(e => e.Start));
        Assert.Equal("Robotics (moved)", events[2].Title);
    }

    [Fact]
    public void Recurrence_keeps_wall_clock_time_across_daylight_saving()
    {
        var ics = Calendar(Event("""
            UID:daily
            SUMMARY:Standup
            DTSTART;TZID=Europe/Berlin:20261023T093000
            DTEND;TZID=Europe/Berlin:20261023T094500
            RRULE:FREQ=DAILY;COUNT=5
            """));

        var events = IcsParser.Parse(ics, "Test", BerlinTime(2026, 10, 20, 0, 0), BerlinTime(2026, 11, 1, 0, 0), Berlin);

        Assert.Equal(5, events.Count);
        Assert.All(events, e => Assert.Equal(new TimeSpan(9, 30, 0), TimeZoneInfo.ConvertTime(e.Start, Berlin).TimeOfDay));
        Assert.Equal(TimeSpan.FromHours(2), events[0].Start.Offset); // CEST
        Assert.Equal(TimeSpan.FromHours(1), events[4].Start.Offset); // CET after 25 Oct
    }

    [Fact]
    public void Long_running_series_reach_today_and_count_and_until_stop_them()
    {
        var ics = Calendar(
            Event("""
                UID:old-daily
                SUMMARY:Journal
                DTSTART:20100101T070000Z
                DTEND:20100101T071500Z
                RRULE:FREQ=DAILY
                """),
            Event("""
                UID:ended
                SUMMARY:Old meeting
                DTSTART:20200106T100000Z
                DTEND:20200106T110000Z
                RRULE:FREQ=WEEKLY;UNTIL=20210101T000000Z
                """),
            Event("""
                UID:counted
                SUMMARY:Course
                DTSTART:20260901T100000Z
                RRULE:FREQ=WEEKLY;COUNT=3
                """));

        var events = IcsParser.Parse(ics, "Test", BerlinTime(2026, 9, 30, 0, 0), BerlinTime(2026, 10, 1, 0, 0), Berlin);

        Assert.Equal(["Journal"], events.Select(e => e.Title));
    }

    [Fact]
    public void Unsatisfiable_monthly_rule_terminates()
    {
        var ics = Calendar(Event("""
            UID:never
            SUMMARY:Never
            DTSTART:20260430T100000Z
            RRULE:FREQ=MONTHLY;INTERVAL=12;BYMONTHDAY=31
            """));

        Assert.Empty(IcsParser.Parse(ics, "Test", BerlinTime(2026, 9, 30, 0, 0), BerlinTime(2026, 10, 1, 0, 0), Berlin));
    }

    [Fact]
    public void Cancelled_events_and_nested_alarms_are_handled()
    {
        var ics = Calendar(
            Event("""
                UID:c
                SUMMARY:Cancelled
                STATUS:CANCELLED
                DTSTART:20260930T100000Z
                """),
            Event("""
                UID:a
                SUMMARY:With alarm
                DTSTART:20260930T110000Z
                DTEND:20260930T120000Z
                BEGIN:VALARM
                TRIGGER:-PT15M
                DESCRIPTION:Not the title
                END:VALARM
                """));

        var e = Assert.Single(IcsParser.Parse(ics, "Test", BerlinTime(2026, 9, 30, 0, 0), BerlinTime(2026, 10, 1, 0, 0), Berlin));
        Assert.Equal("With alarm", e.Title);
    }

    [Fact]
    public void Compact_mentions_events_within_the_hour_or_just_started()
    {
        var events = new[]
        {
            new CalendarEvent("Robotics", BerlinTime(2026, 9, 30, 14, 30), BerlinTime(2026, 9, 30, 16, 0), false, null, "t"),
            new CalendarEvent("Holiday", BerlinTime(2026, 9, 30, 0, 0), BerlinTime(2026, 10, 1, 0, 0), true, null, "t"),
        };

        Assert.Null(CalendarAgenda.Compact(events, BerlinTime(2026, 9, 30, 13, 0), Berlin));
        Assert.Equal("Robotics", CalendarAgenda.Compact(events, BerlinTime(2026, 9, 30, 13, 45), Berlin)?.Title);
        Assert.Equal("Robotics", CalendarAgenda.Compact(events, BerlinTime(2026, 9, 30, 14, 35), Berlin)?.Title);
        Assert.Null(CalendarAgenda.Compact(events, BerlinTime(2026, 9, 30, 14, 45), Berlin));
        Assert.Equal("Robotics", CalendarAgenda.Next(events, BerlinTime(2026, 9, 30, 14, 45), Berlin)?.Title);
        Assert.Equal(["Holiday", "Robotics"], CalendarAgenda.ForDay(events, BerlinTime(2026, 9, 30, 8, 0), Berlin).Select(e => e.Title));
    }

    [Theory]
    [InlineData("https://calendar.google.com/calendar/ical/abc%40group/private-secret/basic.ics", "Google Calendar")]
    [InlineData("webcal://p12-caldav.icloud.com/published/2/secret", "iCloud")]
    [InlineData("https://outlook.office365.com/owa/calendar/secret/calendar.ics", "Outlook")]
    [InlineData(@"C:\Users\me\Documents\uni.ics", "uni")]
    public void Source_names_never_expose_the_private_link(string location, string name)
    {
        Assert.Equal(name, IcsCalendarSource.DisplayName(location));
    }

    [Fact]
    public async Task Service_reports_empty_ready_and_error_states()
    {
        var time = new FakeTimeProvider(BerlinTime(2026, 9, 30, 8, 0));
        var failing = false;
        using var service = new CalendarService(location => new FakeSource(location, () => failing), time);

        service.Configure(true, []);
        Assert.Equal(ModuleState.Empty, service.Status.State);

        service.Configure(true, ["https://example.com/a.ics"]);
        await service.RefreshAsync();
        Assert.Equal(ModuleState.Ready, service.Status.State);
        Assert.Single(service.Events);

        failing = true;
        await service.RefreshAsync();
        Assert.Equal(ModuleState.Error, service.Status.State);
        Assert.Single(service.Events); // last good events stay

        service.Configure(false, ["https://example.com/a.ics"]);
        Assert.Equal(ModuleState.Disabled, service.Status.State);
        Assert.Empty(service.Events);
    }

    private sealed class FakeSource(string location, Func<bool> fail) : ICalendarSource
    {
        public string Name => location;

        public Task<IReadOnlyList<CalendarEvent>> FetchAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            fail()
                ? throw new HttpRequestException("offline")
                : Task.FromResult<IReadOnlyList<CalendarEvent>>([new CalendarEvent("Robotics", from.AddHours(18), from.AddHours(19), false, null, Name)]);
    }
}
