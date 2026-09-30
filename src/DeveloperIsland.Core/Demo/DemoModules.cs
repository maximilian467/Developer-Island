using DeveloperIsland.Core.Calendar;
using DeveloperIsland.Core.Git;
using DeveloperIsland.Core.GitHub;
using DeveloperIsland.Core.SystemInfo;
using DeveloperIsland.Core.Tasks;

namespace DeveloperIsland.Core.Demo;

/// <summary>
/// Believable module content for demo mode (<c>--demo</c>) and screenshots. Never used otherwise:
/// in normal mode every module shows only real data or an honest empty state.
/// </summary>
public static class DemoModules
{
    public static IReadOnlyList<GitRepoStatus> Repositories(DateTimeOffset now) =>
    [
        new GitRepoStatus
        {
            Root = @"C:\code\developer-island",
            Branch = "feature/browser-notch",
            HasUpstream = true,
            Ahead = 2,
            Modified = 3,
            Untracked = 1,
            LastCommitSubject = "Retract into a notch over maximized browsers",
            LastCommitAt = now.AddMinutes(-38),
            GitHubRepository = "maximilian467/Developer-Island",
            UpdatedAt = now,
        },
        new GitRepoStatus { Root = @"C:\code\robotics-lab", Branch = "main", HasUpstream = true, UpdatedAt = now },
        new GitRepoStatus { Root = @"C:\code\thesis", Branch = "draft", HasUpstream = true, Modified = 5, Staged = 2, UpdatedAt = now },
    ];

    public static GitHubSnapshot GitHub(DateTimeOffset now) => new()
    {
        Login = "maximilian467",
        Notifications = 4,
        MyOpenPullRequests = 2,
        ReviewRequests = 1,
        Repository = new GitHubRepo("maximilian467/Developer-Island", 128, 6, 2),
        Ci = new CiRun(CiState.Passed, "CI", "feature/browser-notch", now.AddMinutes(-31)),
        UpdatedAt = now,
    };

    /// <summary>Today's agenda around <paramref name="now"/>: the next event starts in 25 minutes.</summary>
    public static IReadOnlyList<CalendarEvent> Events(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var next = local.DateTime.AddMinutes(25);
        next = next.AddMinutes(-(next.Minute % 5)).AddSeconds(-next.Second).AddMilliseconds(-next.Millisecond);
        DateTimeOffset At(DateTime wall) => IcsParser.ToOffset(wall, zone);
        var day = local.Date;
        return
        [
            new CalendarEvent("Stand-up", At(day.AddHours(9)), At(day.AddHours(9).AddMinutes(15)), false, null, "Demo"),
            new CalendarEvent("Robotics", At(next), At(next.AddMinutes(90)), false, "Lab 2", "Demo"),
            new CalendarEvent("Thesis review", At(next.AddHours(3)), At(next.AddHours(4)), false, "Room 114", "Demo"),
            new CalendarEvent("Robotics", At(day.AddDays(1).AddHours(10)), At(day.AddDays(1).AddHours(11)), false, "Lab 2", "Demo"),
        ];
    }

    public static void SeedTasks(TaskStore store)
    {
        var review = store.Add("Review Codex pull request");
        store.Add("Write notch release notes");
        store.Add("Book lab for Thursday");
        if (review is not null)
        {
            store.SetDone(review.Id, true);
        }
    }

    public static SystemSample System => new(23, 11_400UL * 1024 * 1024, 32UL * 1024 * 1024 * 1024, 84, true);
}
