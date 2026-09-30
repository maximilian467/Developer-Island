using System.Text.Json;
using DeveloperIsland.Core.Models;

namespace DeveloperIsland.Core.Usage;

/// <summary>
/// Extracts usage metadata from one line of a Claude Code transcript (<c>~/.claude/projects/**/*.jsonl</c>).
/// Only the <c>usage</c>, <c>model</c>, identifiers, timestamp and working directory are read.
/// Message content is never touched.
/// </summary>
/// <remarks>
/// Claude Code writes one line per content block of a response, and every line repeats the same
/// <c>message.id</c> and usage. Events are therefore keyed by <c>message.id</c> + <c>requestId</c> and
/// must be de-duplicated by the store.
/// </remarks>
public static class ClaudeLogParser
{
    public static bool TryParse(string line, out UsageEvent usageEvent)
    {
        usageEvent = null!;

        // Cheap pre-filter: most lines are user turns, tool results and snapshots.
        if (line.Length < 32
            || !line.Contains("\"usage\"", StringComparison.Ordinal)
            || !line.Contains("\"assistant\"", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "assistant"
                || !root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var model = GetString(message, "model");
            if (string.IsNullOrEmpty(model) || model == "<synthetic>")
            {
                return false;
            }

            if (!TryGetTimestamp(root, out var timestamp))
            {
                return false;
            }

            var input = GetLong(usage, "input_tokens");
            var output = GetLong(usage, "output_tokens");
            var cacheCreation = GetLong(usage, "cache_creation_input_tokens");
            var cacheRead = GetLong(usage, "cache_read_input_tokens");

            long write5m = cacheCreation, write1h = 0;
            if (usage.TryGetProperty("cache_creation", out var split) && split.ValueKind == JsonValueKind.Object)
            {
                var s5 = GetLong(split, "ephemeral_5m_input_tokens");
                var s1 = GetLong(split, "ephemeral_1h_input_tokens");
                if (s5 + s1 > 0)
                {
                    write5m = s5;
                    write1h = s1;
                }
            }

            var tokens = new TokenCounts(input, output, write5m, write1h, cacheRead);
            if (tokens.IsZero)
            {
                return false;
            }

            var messageId = GetString(message, "id");
            var requestId = GetString(root, "requestId");
            var key = messageId is null && requestId is null
                ? GetString(root, "uuid") ?? $"{timestamp.ToUnixTimeMilliseconds()}:{tokens.Processed}"
                : $"{messageId}:{requestId}";

            usageEvent = new UsageEvent(
                AiProviderKind.Claude,
                key,
                timestamp,
                model,
                tokens,
                GetString(root, "sessionId"),
                ProjectNameFromPath(GetString(root, "cwd")),
                GetString(root, "cwd"));
            return true;
        }
        catch (JsonException)
        {
            // Partially written trailing line or foreign format: skip.
            return false;
        }
    }

    /// <summary>The last folder of a working directory, e.g. <c>developer_island</c>.</summary>
    public static string? ProjectNameFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.Length == 0 ? null : name;
    }

    internal static bool TryGetTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var ts)
            && ts.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(ts.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out timestamp);
    }

    internal static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static long GetLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n > 0 ? n : 0;
}
