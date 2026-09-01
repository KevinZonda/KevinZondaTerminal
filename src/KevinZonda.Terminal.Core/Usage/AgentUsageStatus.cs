namespace KevinZonda.Terminal.Usage;

internal sealed record AgentUsageStatus(IReadOnlyList<AgentProviderUsageStatus> Providers)
{
    internal static AgentUsageStatus Empty { get; } = new([]);
}

internal sealed record AgentProviderUsageStatus(
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

internal sealed record AgentUsageWindowStatus(
    string Name,
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    double? Used,
    double? Limit);

internal sealed record AgentUsageCreditsStatus(
    double? Remaining,
    bool IsUnlimited,
    double? Total,
    string? Currency);

internal sealed record AgentUsageBudgetStatus(
    string Name,
    double Limit,
    double Used,
    double RemainingPercent,
    DateTimeOffset? ResetsAt,
    bool IsUnlimited,
    string? Currency);
