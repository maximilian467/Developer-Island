using System.Security;
using System.Security.Principal;
using System.Text;
using DeveloperIsland.Core.Awake;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Platform.Power;

/// <summary>
/// The crash backstop of a timed awake session: a one-time task in the user's Task Scheduler that runs
/// <c>DeveloperIsland.exe --awake-restore</c> at the session's end. If the app is still running the
/// call exits at once; otherwise it puts the lid-close action back. The task runs as the user without
/// elevation, also on battery and late (after sleep or a restart), and Windows deletes it a week after
/// its time. Calls are queued in order on a background task, so the UI never waits for schtasks.
/// </summary>
internal sealed class ScheduledTaskBackstop(string exePath, string tempFolder) : IAwakeBackstop
{
    public const string TaskName = "DeveloperIsland Awake Restore";
    public const string Argument = "--awake-restore";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly object _gate = new();
    private Task _chain = Task.CompletedTask;

    public void Schedule(DateTimeOffset endsAt) => Enqueue(() => CreateAsync(endsAt));

    public void Cancel() => Enqueue(DeleteAsync);

    /// <summary>Waits for queued calls (Quit, before the process ends).</summary>
    public void Flush(TimeSpan limit)
    {
        Task chain;
        lock (_gate)
        {
            chain = _chain;
        }

        try
        {
            chain.Wait(limit);
        }
        catch (AggregateException)
        {
            // Failures are logged where they happen.
        }
    }

    /// <summary>Deletes the task right away (the restore run, which ends immediately after).</summary>
    public static void DeleteNow() => DeleteAsync().GetAwaiter().GetResult();

    private void Enqueue(Func<Task> work)
    {
        lock (_gate)
        {
            _chain = _chain.ContinueWith(_ => work(), TaskScheduler.Default).Unwrap();
        }
    }

    private async Task CreateAsync(DateTimeOffset endsAt)
    {
        var file = Path.Combine(tempFolder, "awake-restore-task.xml");
        try
        {
            Directory.CreateDirectory(tempFolder);

            // schtasks reads task XML as UTF-16.
            await File.WriteAllTextAsync(file, TaskXml(endsAt.ToLocalTime(), exePath), Encoding.Unicode);
            var result = await ProcessRunner.RunAsync("schtasks.exe", ["/Create", "/TN", TaskName, "/XML", file, "/F"], timeout: Timeout);
            if (result.Succeeded)
            {
                Log.Info("awake", "Backstop scheduled", new { at = endsAt.ToLocalTime().ToString("u") });
            }
            else
            {
                Log.Warn("awake", "Backstop could not be scheduled", new { exitCode = result.ExitCode });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("awake", "Backstop could not be scheduled", new { error = ex.GetType().Name });
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task DeleteAsync()
    {
        try
        {
            // Exit code 1 when there is no such task: nothing to do.
            var result = await ProcessRunner.RunAsync("schtasks.exe", ["/Delete", "/TN", TaskName, "/F"], timeout: Timeout);
            Log.Debug("awake", "Backstop removed", new { exitCode = result.ExitCode });
        }
        catch (Exception ex)
        {
            Log.Warn("awake", "Backstop could not be removed", new { error = ex.GetType().Name });
        }
    }

    internal static string TaskXml(DateTimeOffset at, string exePath)
    {
        static string Time(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Developer Island</Author>
                <Description>Restores the lid-close action after a Developer Island Awake session if the app is not running. Deleted automatically.</Description>
              </RegistrationInfo>
              <Triggers>
                <TimeTrigger>
                  <StartBoundary>{Time(at)}</StartBoundary>
                  <EndBoundary>{Time(at.AddDays(7))}</EndBoundary>
                  <Enabled>true</Enabled>
                </TimeTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
                <DeleteExpiredTaskAfter>PT0S</DeleteExpiredTaskAfter>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>
                  <Arguments>{Argument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
