namespace KevinZonda.AgentUsageMonitor;

public sealed record AgentUsageStatus(IReadOnlyList<AgentProviderUsageStatus> Providers)
{
    public static AgentUsageStatus Empty { get; } = new([]);
}

public sealed record AgentProviderUsageStatus(
    string Provider,
    string State,
    bool Refreshing,
    string? Source,
    string? Plan,
    IReadOnlyList<AgentUsageWindowStatus> Windows,
    AgentUsageCreditsStatus? Credits,
    AgentUsageBudgetStatus? Budget,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? NextRefreshAt,
    string? Error);

public sealed record AgentUsageWindowStatus(
    string Name,
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    double? Used,
    double? Limit);

public sealed record AgentUsageCreditsStatus(
    double? Remaining,
    bool IsUnlimited,
    double? Total,
    string? Currency);

public sealed record AgentUsageBudgetStatus(
    string Name,
    double Limit,
    double Used,
    double RemainingPercent,
    DateTimeOffset? ResetsAt,
    bool IsUnlimited,
    string? Currency);
