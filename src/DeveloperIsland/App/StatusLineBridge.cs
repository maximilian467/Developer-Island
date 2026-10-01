using DeveloperIsland.Core.Plan;

namespace DeveloperIsland;

/// <summary>
/// <c>DeveloperIsland.exe --claude-statusline</c>: Claude Code's status line command (see
/// <see cref="ClaudeStatusLineSetup"/>). Claude Code writes its documented status JSON to stdin;
/// this keeps only the plan usage numbers in a small file the running island watches, prints
/// nothing (no status line is shown) and always exits 0, so it can never disturb Claude Code.
/// Runs before any UI, single-instance or logging setup and returns within milliseconds.
/// </summary>
internal static class StatusLineBridge
{
    private const int MaxInputChars = 1_000_000;

    public static bool IsRequested(string[] args) => args.Contains(ClaudeStatusLineSetup.Argument, StringComparer.Ordinal);

    public static int Run()
    {
        try
        {
            var input = ReadInput();
            if (ClaudeStatusLine.Extract(input, DateTimeOffset.UtcNow) is { } usage)
            {
                new PlanUsageStore(AppPaths.ClaudePlan).Save(usage);
            }
        }
        catch
        {
            // Never fail Claude Code's status line: no output, no error code.
        }

        return 0;
    }

    private static string ReadInput()
    {
        var reader = Console.In;
        var buffer = new char[8192];
        var text = new System.Text.StringBuilder();
        var read = reader.ReadAsync(buffer, 0, buffer.Length);
        while (read.Wait(TimeSpan.FromSeconds(2)) && read.Result > 0 && text.Length < MaxInputChars)
        {
            text.Append(buffer, 0, read.Result);
            read = reader.ReadAsync(buffer, 0, buffer.Length);
        }

        return text.ToString();
    }
}
