namespace KevinZonda.AgentUsageMonitor;

public enum UsageProvider
{
    Codex,
    KimiCode,
}

public enum UsageSource
{
    CodexOAuth,
    CodexAppServer,
    KimiCodeApiKey,
    KimiCodeCliCredential,
    KimiCodeManagedOAuth,
}

public sealed record UsageWindow(
    string Name,
    double UsedPercent,
    TimeSpan? Window,
    DateTimeOffset? ResetsAt,
    double? Used = null,
    double? Limit = null);

public sealed record UsageCredits(double? Remaining, bool IsUnlimited = false)
{
    public double? Total { get; init; }

    public string? Currency { get; init; }
}

public sealed record UsageBudget(
    string Name,
    double Limit,
    double Used,
    double RemainingPercent,
    DateTimeOffset? ResetsAt)
{
    public bool IsUnlimited { get; init; }

    public string? Currency { get; init; }
}

public sealed record UsageSnapshot(
    UsageProvider Provider,
    UsageSource Source,
    UsageWindow? Primary,
    UsageWindow? Secondary,
    IReadOnlyList<UsageWindow> ExtraWindows,
    UsageCredits? Credits,
    UsageBudget? Budget,
    string? AccountEmail,
    string? Plan,
    DateTimeOffset UpdatedAt);
