namespace DeveloperIsland.Core.Models;

public enum AiProviderKind
{
    Claude,
    Codex,
}

public enum ProviderState
{
    /// <summary>The provider is starting or running its first scan.</summary>
    Starting,

    /// <summary>The tool is not installed or has never written local data.</summary>
    NotDetected,

    /// <summary>Data was read successfully.</summary>
    Ready,

    /// <summary>Reading failed; the rest of the app keeps working.</summary>
    Error,

    /// <summary>Turned off in Settings.</summary>
    Disabled,
}

/// <summary>One assistant response worth of usage metadata. Contains no prompt or response content.</summary>
public sealed record UsageEvent(
    AiProviderKind Provider,
    string Key,
    DateTimeOffset Timestamp,
    string Model,
    TokenCounts Tokens,
    string? SessionId,
    string? Project);

/// <summary>What the UI shows for one AI tool.</summary>
public sealed record AiUsageSnapshot
{
    public required AiProviderKind Provider { get; init; }

    public ProviderState State { get; init; } = ProviderState.Starting;

    public DateOnly Day { get; init; }

    public TokenCounts Today { get; init; }

    /// <summary>Estimated API equivalent for today in EUR; null when no model could be priced.</summary>
    public decimal? ApiValueEur { get; init; }

    /// <summary>True when some of today's tokens came from models without a known price.</summary>
    public bool ApiValuePartial { get; init; }

    public int SessionsToday { get; init; }

    public string? CurrentModel { get; init; }

    public string? CurrentProject { get; init; }

    /// <summary>True while a session wrote data recently or reports itself busy.</summary>
    public bool IsActive { get; init; }

    public DateTimeOffset? LastActivity { get; init; }

    /// <summary>Usage of the provider's rolling rate-limit window, when the tool reports it locally.</summary>
    public double? LimitPercent { get; init; }

    public int? LimitWindowMinutes { get; init; }

    public DateTimeOffset? LimitResetsAt { get; init; }

    public string? ErrorMessage { get; init; }

    public static AiUsageSnapshot Initial(AiProviderKind provider) => new() { Provider = provider };
}

/// <summary>Aggregated usage of one provider on one local calendar day.</summary>
public sealed record DailyUsage(
    DateOnly Day,
    AiProviderKind Provider,
    TokenCounts Tokens,
    decimal ApiValueEur,
    bool ApiValuePartial,
    int Sessions);

/// <summary>One cell of the usage history graph.</summary>
public sealed record UsageDay(
    DateOnly Day,
    long ClaudeTokens,
    long CodexTokens,
    decimal ClaudeApiValueEur,
    decimal CodexApiValueEur,
    int Sessions,
    bool ApiValuePartial = false)
{
    public long TotalTokens => ClaudeTokens + CodexTokens;

    public decimal TotalApiValueEur => ClaudeApiValueEur + CodexApiValueEur;
}
