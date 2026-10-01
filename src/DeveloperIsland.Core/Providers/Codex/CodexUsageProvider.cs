using System.Text.Json;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Providers.Codex;

/// <summary>
/// Codex usage from rollout files in <c>~/.codex/sessions</c> (or <c>CODEX_HOME</c>).
/// Codex records its own 5-hour rate-limit usage locally, so a real percentage is shown when available.
/// </summary>
public sealed class CodexUsageProvider : LogFileUsageProvider
{
    private static readonly double[] Thresholds = [50, 75, 90];
    private readonly object _limitGate = new();
    private CodexRateLimit? _limit;
    private CodexRateLimit? _weekly;
    private double _lastAnnounced = -1;
    private bool _scanCompleted;
    private ITimer? _resetTimer;
    private bool _disposed;

    public CodexUsageProvider(string rootDirectory, UsageHistoryService history, UsageDatabase database, TimeProvider? time = null)
        : base(AiProviderKind.Codex, rootDirectory, history, database, time)
    {
    }

    protected override string LogDirectory => Path.Combine(RootDirectory, "sessions");

    public static string DefaultRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    protected override ParseResult ParseFile(string path, long offset, string? state, List<UsageEvent> events, CancellationToken cancellationToken)
    {
        CodexFileState? fileState = null;
        if (state is not null)
        {
            try
            {
                fileState = JsonSerializer.Deserialize(state, CoreJsonContext.Default.CodexFileState);
            }
            catch (JsonException)
            {
                // Unknown state layout (older version): continue without it.
            }
        }

        if (fileState is not { Version: 1 })
        {
            // Upgrade old cursors once. Existing event keys prevent double counting.
            offset = 0;
            fileState = null;
        }

        var parser = new CodexLogParser(fileState);
        var newOffset = JsonlTailReader.ReadLines(path, offset, line =>
        {
            if (parser.TryParse(line, out var e))
            {
                events.Add(e);
            }
        }, cancellationToken);

        if (parser.LastRateLimit is { } limit)
        {
            UpdateLimit(limit);
        }

        if (parser.LastWeeklyRateLimit is { } weekly)
        {
            UpdateWeekly(weekly);
        }

        return new ParseResult(newOffset, JsonSerializer.Serialize(parser.State, CoreJsonContext.Default.CodexFileState));
    }

    protected override bool TryRestoreFileState(FileCursor cursor)
    {
        try
        {
            var state = cursor.State is null ? null : JsonSerializer.Deserialize(cursor.State, CoreJsonContext.Default.CodexFileState);
            if (state is not { Version: 1 }) return false;
            if (state.LastRateLimit is { } limit) UpdateLimit(limit);
            if (state.LastWeeklyRateLimit is { } weekly) UpdateWeekly(weekly);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    protected override void OnDisposing()
    {
        lock (_limitGate)
        {
            _disposed = true;
            _resetTimer?.Dispose();
        }
    }

    protected override void OnInitialScanCompleted()
    {
        lock (_limitGate)
        {
            _scanCompleted = true;
            _lastAnnounced = _limit?.UsedPercent ?? -1;
        }
    }

    protected override AiUsageSnapshot Enrich(AiUsageSnapshot snapshot)
    {
        CodexRateLimit? limit;
        CodexRateLimit? weeklyLimit;
        lock (_limitGate)
        {
            limit = _limit;
            weeklyLimit = _weekly;
            var now = Time.GetUtcNow();
            _resetTimer?.Dispose();
            _resetTimer = null;
            if (!_disposed && limit?.ResetsAt is { } reset && reset > now)
            {
                var due = reset - now + TimeSpan.FromMilliseconds(1);
                // Timer implementations cap very long delays; re-evaluate if a distant date is reported.
                due = due > TimeSpan.FromDays(30) ? TimeSpan.FromDays(30) : due;
                _resetTimer = Time.CreateTimer(_ => RefreshSnapshot(), null, due, Timeout.InfiniteTimeSpan);
            }
        }

        // The weekly window, like the primary one, only while it has not reset.
        if (weeklyLimit is not null && (weeklyLimit.ResetsAt is not { } weeklyReset || weeklyReset > Time.GetUtcNow()))
        {
            snapshot = snapshot with
            {
                WeeklyLimitPercent = weeklyLimit.UsedPercent,
                WeeklyLimitResetsAt = weeklyLimit.ResetsAt,
            };
        }

        // A reading from a window that has already reset says nothing about the current window.
        if (limit is null || (limit.ResetsAt is { } resets && resets <= Time.GetUtcNow()))
        {
            return snapshot;
        }

        return snapshot with
        {
            LimitPercent = limit.UsedPercent,
            LimitWindowMinutes = limit.WindowMinutes,
            LimitResetsAt = limit.ResetsAt,
        };
    }

    private void UpdateWeekly(CodexRateLimit weekly)
    {
        lock (_limitGate)
        {
            if (_weekly is null || weekly.ObservedAt >= _weekly.ObservedAt)
            {
                _weekly = weekly;
            }
        }
    }

    private void UpdateLimit(CodexRateLimit limit)
    {
        double? crossed = null;
        lock (_limitGate)
        {
            if (_limit is not null && limit.ObservedAt < _limit.ObservedAt)
            {
                return;
            }

            _limit = limit;
            if (_scanCompleted)
            {
                foreach (var threshold in Thresholds)
                {
                    if (limit.UsedPercent >= threshold && _lastAnnounced < threshold)
                    {
                        crossed = threshold;
                    }
                }

                _lastAnnounced = limit.UsedPercent;
            }
        }

        if (crossed is not null)
        {
            var hours = limit.WindowMinutes >= 60 ? $"{limit.WindowMinutes / 60}-hour" : $"{limit.WindowMinutes}-minute";
            RaiseActivity(new AiActivity(
                AiProviderKind.Codex,
                AiActivityKind.LimitThreshold,
                $"Codex at {Math.Round(limit.UsedPercent)}%",
                $"of the {hours} limit"));
        }
    }
}
