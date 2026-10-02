using System.Diagnostics;
using System.Text;

namespace DeveloperIsland.Core.Diagnostics;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs a command-line tool (git, gh) without a window, with arguments passed as a list (never a
/// shell string) and a hard timeout. Output is returned to the caller and never logged.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (workingDirectory is not null)
        {
            info.WorkingDirectory = workingDirectory;
        }

        // Tools must never wait for input or open a pager or prompt.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GH_PROMPT_DISABLED"] = "1";
        info.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        info.Environment["NO_COLOR"] = "1";

        using var process = new Process { StartInfo = info };
        process.Start();
        HelperProcessJob.Adopt(process); // ends with the app, even if the app quits mid-run
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var error = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            return new ProcessResult(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }
    }

    /// <summary>Finds an executable on PATH or in the given well-known locations; null if absent.</summary>
    public static string? FindExecutable(string name, params string[] knownLocations)
    {
        var file = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), file);
                if (directory.Length > 0 && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        return knownLocations.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists);
    }
}
