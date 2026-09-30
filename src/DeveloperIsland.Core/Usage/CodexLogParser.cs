using System.Text.Json;
using DeveloperIsland.Core.Models;

namespace DeveloperIsland.Core.Usage;

/// <summary>Rate-limit window usage as reported locally by Codex.</summary>
public sealed record CodexRateLimit(double UsedPercent, int WindowMinutes, DateTimeOffset? ResetsAt, DateTimeOffset ObservedAt);

/// <summary>Per-file parser state that must survive incremental reads and restarts.</summary>
public sealed record CodexFileState
{
    // Zero identifies cursors written before rate limits were persisted.
    public int Version { get; init; }

    public CodexRateLimit? LastRateLimit { get; init; }

    public string? SessionId { get; init; }

    public string? Model { get; init; }

    public string? Cwd { get; init; }

    /// <summary>Newer Codex builds write <c>token_usage_record</c> lines with a unique response id.</summary>
    public bool HasUsageRecords { get; init; }

    /// <summary>Last cumulative total seen in a <c>token_count</c> event (older format).</summary>
    public long LastTotal { get; init; }
}

/// <summary>
/// Extracts usage metadata from Codex rollout files (<c>~/.codex/sessions/yyyy/MM/dd/rollout-*.jsonl</c>).
/// Reads session metadata, model, token counters and rate limits only. Message and tool content is ignored.
/// </summary>
public sealed class CodexLogParser
{
    public CodexLogParser(CodexFileState? state = null)
    {
        State = state ?? new CodexFileState { Version = 1 };
    }

    public CodexFileState State { get; private set; }

    /// <summary>Most recent rate limit seen by this parser instance.</summary>
    public CodexRateLimit? LastRateLimit => State.LastRateLimit;

    public bool TryParse(string line, out UsageEvent usageEvent)
    {
        usageEvent = null!;
        if (line.Length < 16)
        {
            return false;
        }

        // Skip the large content lines (messages, reasoning, tool output) without parsing them.
        var interesting =
            line.Contains("\"token_usage_record\"", StringComparison.Ordinal)
            || line.Contains("\"token_count\"", StringComparison.Ordinal)
            || line.Contains("\"session_meta\"", StringComparison.Ordinal)
            || line.Contains("\"turn_context\"", StringComparison.Ordinal);
        if (!interesting)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var type = ClaudeLogParser.GetString(root, "type");
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            switch (type)
            {
                case "session_meta":
                    State = State with
                    {
                        SessionId = ClaudeLogParser.GetString(payload, "id") ?? ClaudeLogParser.GetString(payload, "session_id") ?? State.SessionId,
                        Cwd = ClaudeLogParser.GetString(payload, "cwd") ?? State.Cwd,
                        Model = ClaudeLogParser.GetString(payload, "model") ?? State.Model,
                    };
                    return false;

                case "turn_context":
                    State = State with
                    {
                        Model = ClaudeLogParser.GetString(payload, "model") ?? State.Model,
                        Cwd = ClaudeLogParser.GetString(payload, "cwd") ?? State.Cwd,
                    };
                    return false;

                case "token_usage_record":
                    return TryParseUsageRecord(root, payload, out usageEvent);

                case "event_msg" when ClaudeLogParser.GetString(payload, "type") == "token_count":
                    return TryParseTokenCount(root, payload, out usageEvent);

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool TryParseUsageRecord(JsonElement root, JsonElement payload, out UsageEvent usageEvent)
    {
        usageEvent = null!;
        if (!payload.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
            || !ClaudeLogParser.TryGetTimestamp(root, out var ts))
        {
            return false;
        }

        var tokens = ReadTokens(usage);
        if (tokens.IsZero)
        {
            return false;
        }

        State = State with
        {
            HasUsageRecords = true,
            SessionId = State.SessionId ?? ClaudeLogParser.GetString(payload, "session_id"),
        };

        var key = ClaudeLogParser.GetString(payload, "response_id")
            ?? $"{ClaudeLogParser.GetString(payload, "turn_id")}:{ts.ToUnixTimeMilliseconds()}";
        usageEvent = CreateEvent(key, ts, tokens);
        return true;
    }

    private bool TryParseTokenCount(JsonElement root, JsonElement payload, out UsageEvent usageEvent)
    {
        usageEvent = null!;
        ClaudeLogParser.TryGetTimestamp(root, out var ts);
        ReadRateLimit(payload, ts);

        if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var total = info.TryGetProperty("total_token_usage", out var totalUsage) ? ClaudeLogParser.GetLong(totalUsage, "total_tokens") : 0;

        // Newer files carry token_usage_record lines; token_count then only contributes rate limits.
        // Codex also re-emits token_count with an unchanged total, which must not be counted twice.
        if (State.HasUsageRecords || total == 0 || total == State.LastTotal)
        {
            return false;
        }

        if (!info.TryGetProperty("last_token_usage", out var last) || last.ValueKind != JsonValueKind.Object || ts == default)
        {
            return false;
        }

        var tokens = ReadTokens(last);
        if (tokens.IsZero)
        {
            return false;
        }

        State = State with { LastTotal = total };
        usageEvent = CreateEvent($"{State.SessionId}:total:{total}", ts, tokens);
        return true;
    }

    private void ReadRateLimit(JsonElement payload, DateTimeOffset observedAt)
    {
        if (!payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var limitId = ClaudeLogParser.GetString(limits, "limit_id");
        if (limitId is not null && limitId != "codex")
        {
            return;
        }

        if (!limits.TryGetProperty("primary", out var primary) || primary.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (observedAt == default || !primary.TryGetProperty("used_percent", out var used)
            || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent)
            || !double.IsFinite(percent) || percent < 0 || percent > 100)
        {
            return;
        }

        DateTimeOffset? resetsAt = null;
        if (primary.TryGetProperty("resets_at", out var resets) && resets.ValueKind != JsonValueKind.Null)
        {
            if (resets.ValueKind != JsonValueKind.Number || !resets.TryGetInt64(out var unix)
                || unix < DateTimeOffset.MinValue.ToUnixTimeSeconds() || unix > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            {
                return;
            }
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        if (!primary.TryGetProperty("window_minutes", out var w) || w.ValueKind != JsonValueKind.Number
            || !w.TryGetInt32(out var window) || window <= 0)
        {
            return;
        }

        if (LastRateLimit is null || observedAt >= LastRateLimit.ObservedAt)
        {
            State = State with { LastRateLimit = new CodexRateLimit(percent, window, resetsAt, observedAt) };
        }
    }

    private UsageEvent CreateEvent(string key, DateTimeOffset ts, TokenCounts tokens) => new(
        AiProviderKind.Codex,
        key,
        ts,
        State.Model ?? "unknown",
        tokens,
        State.SessionId,
        ClaudeLogParser.ProjectNameFromPath(State.Cwd),
        State.Cwd);

    /// <summary>
    /// OpenAI reports cached (and cache-write) tokens as part of input; they are split out here so the
    /// total is not double counted. Reasoning tokens are already part of output.
    /// </summary>
    internal static TokenCounts ReadTokens(JsonElement usage)
    {
        var input = ClaudeLogParser.GetLong(usage, "input_tokens");
        var cached = Math.Min(ClaudeLogParser.GetLong(usage, "cached_input_tokens"), input);
        var cacheWrite = Math.Min(ClaudeLogParser.GetLong(usage, "cache_write_input_tokens"), input - cached);
        var output = ClaudeLogParser.GetLong(usage, "output_tokens");
        return new TokenCounts(input - cached - cacheWrite, output, cacheWrite, 0, cached);
    }
}
