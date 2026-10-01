using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Plan;

/// <summary>One usage window of a Claude subscription, as Claude Code reports it.</summary>
/// <param name="UsedPercent">0 to 100.</param>
public sealed record PlanWindow(double UsedPercent, DateTimeOffset ResetsAt);

/// <summary>
/// Claude plan usage, measured by Claude Code and handed to its status line: the 5-hour window
/// ("current") and the 7-day window ("weekly"). Never computed from local token counts.
/// </summary>
public sealed record PlanUsage(PlanWindow? Current, PlanWindow? Weekly, DateTimeOffset MeasuredAt)
{
    /// <summary>The current window, or null once it has reset (its old percentage no longer holds).</summary>
    public PlanWindow? CurrentAt(DateTimeOffset now) => Current is { } c && c.ResetsAt > now ? c : null;

    public PlanWindow? WeeklyAt(DateTimeOffset now) => Weekly is { } w && w.ResetsAt > now ? w : null;
}

/// <summary>
/// Reads the documented status line input of Claude Code. Only <c>rate_limits</c> is used:
/// <c>five_hour</c> and <c>seven_day</c>, each with <c>used_percentage</c> (0-100) and
/// <c>resets_at</c> (Unix seconds). It is present only for Claude.ai subscribers, after the first
/// response of a session. Everything else in the input (paths, model, cost, session) is ignored.
/// </summary>
public static class ClaudeStatusLine
{
    public static PlanUsage? Extract(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var current = Window(limits, "five_hour");
            var weekly = Window(limits, "seven_day");
            return current is null && weekly is null ? null : new PlanUsage(current, weekly, now);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PlanWindow? Window(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent)
            || !window.TryGetProperty("resets_at", out var resets) || resets.ValueKind != JsonValueKind.Number || !resets.TryGetInt64(out var seconds))
        {
            return null;
        }

        if (!double.IsFinite(percent) || seconds <= 0)
        {
            return null;
        }

        return new PlanWindow(Math.Clamp(percent, 0, 100), DateTimeOffset.FromUnixTimeSeconds(seconds));
    }
}

/// <summary>The last plan usage Claude Code reported, kept in a small file (numbers only).</summary>
public sealed class PlanUsageStore(string path)
{
    public string Path => path;

    public PlanUsage? Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.PlanUsage) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Debug("plan", "Plan usage file unreadable", new { error = ex.GetType().Name });
            return null;
        }
    }

    public void Save(PlanUsage usage)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(usage, CoreJsonContext.Default.PlanUsage));
        File.Move(temp, path, overwrite: true);
    }
}

public enum PlanConnection
{
    /// <summary>Claude Code's settings do not run Developer Island as the status line.</summary>
    NotConnected,

    /// <summary>Developer Island is the status line; plan usage arrives with the next response.</summary>
    Connected,

    /// <summary>The user has their own status line; it is left alone.</summary>
    OtherStatusLine,

    /// <summary>Claude Code's settings could not be read.</summary>
    Unreadable,
}

/// <summary>
/// Connects Developer Island as Claude Code's status line command, which is how Claude Code hands
/// plan usage to other programs. Edits only the <c>statusLine</c> entry of Claude Code's
/// <c>settings.json</c>, keeps every other setting, writes a backup first, and never replaces a
/// status line the user set up themselves. Disconnecting removes only our own entry.
/// </summary>
public static class ClaudeStatusLineSetup
{
    public const string Argument = "--claude-statusline";

    public static string SettingsPath(string? configDir = null)
    {
        var dir = configDir ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }

        return System.IO.Path.Combine(dir, "settings.json");
    }

    /// <summary>The command Claude Code runs: our executable with the status line argument.</summary>
    public static string CommandFor(string executable) => $"\"{executable}\" {Argument}";

    public static bool IsOurs(string? command) => command?.Contains(Argument, StringComparison.Ordinal) == true;

    public static PlanConnection Inspect(string settingsPath)
    {
        try
        {
            var statusLine = ReadSettings(settingsPath)?["statusLine"];
            if (statusLine is null)
            {
                return PlanConnection.NotConnected;
            }

            var command = statusLine["command"]?.GetValue<string>();
            return IsOurs(command) ? PlanConnection.Connected : PlanConnection.OtherStatusLine;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return PlanConnection.Unreadable;
        }
    }

    /// <summary>Adds our status line unless another one exists. Returns the resulting state.</summary>
    public static PlanConnection Connect(string settingsPath, string executable)
    {
        var state = Inspect(settingsPath);
        if (state is PlanConnection.Connected or PlanConnection.OtherStatusLine or PlanConnection.Unreadable)
        {
            return state;
        }

        var root = ReadSettings(settingsPath) ?? new JsonObject();
        root["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = CommandFor(executable),
        };
        Write(settingsPath, root);
        return PlanConnection.Connected;
    }

    /// <summary>Removes our status line (and only ours).</summary>
    public static PlanConnection Disconnect(string settingsPath)
    {
        if (Inspect(settingsPath) != PlanConnection.Connected)
        {
            return Inspect(settingsPath);
        }

        var root = ReadSettings(settingsPath)!;
        root.Remove("statusLine");
        Write(settingsPath, root);
        return PlanConnection.NotConnected;
    }

    private static JsonObject? ReadSettings(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        var text = File.ReadAllText(settingsPath);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new JsonException("settings.json is not a JSON object");
    }

    private static void Write(string settingsPath, JsonObject root)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsPath)!);
        if (File.Exists(settingsPath))
        {
            File.Copy(settingsPath, settingsPath + ".developer-island.bak", overwrite: true);
        }

        var temp = settingsPath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, settingsPath, overwrite: true);
    }
}

/// <summary>
/// Follows the plan usage file (written by the status line bridge) with a file watcher; no polling.
/// </summary>
public sealed class PlanUsageService : IDisposable
{
    private readonly PlanUsageStore _store;
    private FileSystemWatcher? _watcher;
    private PlanUsage? _usage;
    private bool _enabled;

    public PlanUsageService(PlanUsageStore store)
    {
        _store = store;
    }

    /// <summary>Raised on a background thread when new plan usage arrived.</summary>
    public event Action<PlanUsage?>? Changed;

    public PlanUsage? Usage => _usage;

    public void Configure(bool enabled)
    {
        if (enabled == _enabled)
        {
            return;
        }

        _enabled = enabled;
        _watcher?.Dispose();
        _watcher = null;
        if (!enabled)
        {
            _usage = null;
            Changed?.Invoke(null);
            return;
        }

        _usage = _store.Load();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_store.Path)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(_store.Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            _watcher.Changed += (_, _) => Reload();
            _watcher.Created += (_, _) => Reload();
            _watcher.Renamed += (_, _) => Reload();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Debug("plan", "Plan usage not watched", new { error = ex.GetType().Name });
        }

        Changed?.Invoke(_usage);
    }

    public void Dispose()
    {
        _enabled = false;
        _watcher?.Dispose();
        _watcher = null;
    }

    private void Reload()
    {
        var usage = _store.Load();
        if (usage is null || usage == _usage)
        {
            return;
        }

        _usage = usage;
        Changed?.Invoke(usage);
    }
}
