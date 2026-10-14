using DeveloperIsland.Core.Awake;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Platform.Power;

namespace DeveloperIsland;

/// <summary>
/// <c>DeveloperIsland.exe --awake-restore</c>: the scheduled backstop of a timed Awake session (see
/// <see cref="ScheduledTaskBackstop"/>). If the island is running, it owns the session and this exits
/// at once. Otherwise it puts the lid-close action back if the session's end has passed, and leaves a
/// session that is still running alone. Runs before any UI and always exits 0.
/// </summary>
internal static class AwakeRestore
{
    public static bool IsRequested(string[] args) => args.Contains(ScheduledTaskBackstop.Argument, StringComparer.OrdinalIgnoreCase);

    public static int Run()
    {
        try
        {
            if (Mutex.TryOpenExisting(@"Local\DeveloperIsland.Instance", out var running))
            {
                running.Dispose();
                return 0;
            }

            Log.Initialize(Path.Combine(AppPaths.Logs, "awake-restore"));
            var restored = AwakeController.RestoreIfEnded(new AwakeJournalStore(AppPaths.AwakeSession), new WindowsPowerControl(), new DeletingBackstop(), DateTimeOffset.UtcNow);
            Log.Info("awake", restored ? "Backstop restored the lid setting" : "Backstop found nothing to restore");
        }
        catch (Exception ex)
        {
            Log.Error("awake", "Backstop run failed", ex);
        }
        finally
        {
            Log.Flush();
        }

        return 0;
    }

    /// <summary>Removes the task once the restore is done; this run never schedules one.</summary>
    private sealed class DeletingBackstop : IAwakeBackstop
    {
        public void Schedule(DateTimeOffset endsAt)
        {
        }

        public void Cancel() => ScheduledTaskBackstop.DeleteNow();
    }
}
