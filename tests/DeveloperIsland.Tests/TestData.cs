using System.Text.Json;
using DeveloperIsland.Core.Models;

namespace DeveloperIsland.Tests;

/// <summary>Builders for realistic log lines (shape copied from real Claude Code / Codex files, content removed).</summary>
internal static class TestData
{
    public static string ClaudeAssistantLine(
        string messageId,
        string requestId,
        DateTimeOffset timestamp,
        string model = "claude-opus-5-5",
        int input = 10,
        int output = 200,
        int cacheCreation = 1000,
        int cacheRead = 5000,
        int? cache5m = null,
        int? cache1h = null,
        string sessionId = "s-1",
        string cwd = @"C:\Users\dev\code\island")
    {
        var usage = new Dictionary<string, object>
        {
            ["input_tokens"] = input,
            ["output_tokens"] = output,
            ["cache_creation_input_tokens"] = cacheCreation,
            ["cache_read_input_tokens"] = cacheRead,
            ["service_tier"] = "standard",
        };
        if (cache5m is not null || cache1h is not null)
        {
            usage["cache_creation"] = new { ephemeral_5m_input_tokens = cache5m ?? 0, ephemeral_1h_input_tokens = cache1h ?? 0 };
        }

        return JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            sessionId,
            cwd,
            requestId,
            message = new
            {
                model,
                id = messageId,
                role = "assistant",
                content = new object[] { new { type = "text", text = "SECRET PROMPT CONTENT" } },
                usage,
            },
        });
    }

    public static string CodexMeta(string sessionId, string cwd) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-09-23T20:30:11.789Z",
        type = "session_meta",
        payload = new { id = sessionId, cwd, originator = "Codex Desktop", base_instructions = new { text = "SECRET" } },
    });

    public static string CodexTurnContext(string model, string cwd) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-09-23T20:30:12.000Z",
        type = "turn_context",
        payload = new { model, cwd },
    });

    public static string CodexUsageRecord(string responseId, DateTimeOffset ts, int input, int cached, int output) => JsonSerializer.Serialize(new
    {
        timestamp = ts.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        type = "token_usage_record",
        payload = new
        {
            response_id = responseId,
            usage = new { input_tokens = input, cached_input_tokens = cached, cache_write_input_tokens = 0, output_tokens = output, reasoning_output_tokens = 0, total_tokens = input + output },
        },
    });

    public static string CodexTokenCount(DateTimeOffset ts, long total, int lastInput, int lastCached, int lastOutput, double? usedPercent = 12.5, long? resetsAt = null) => JsonSerializer.Serialize(new
    {
        timestamp = ts.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            info = new
            {
                total_token_usage = new { input_tokens = total, cached_input_tokens = 0, output_tokens = 0, total_tokens = total },
                last_token_usage = new { input_tokens = lastInput, cached_input_tokens = lastCached, output_tokens = lastOutput, total_tokens = lastInput + lastOutput },
            },
            rate_limits = usedPercent is null ? null : new
            {
                limit_id = "codex",
                primary = new { used_percent = usedPercent, window_minutes = 300, resets_at = resetsAt ?? 1_900_000_000L },
            },
        },
    });

    public static UsageEvent Event(AiProviderKind provider, string key, DateTimeOffset ts, long tokens, string model = "claude-opus-5-5", string? session = "s1") =>
        new(provider, key, ts, model, new TokenCounts(tokens, 0, 0, 0, 0), session, "proj");

    public static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "di-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
