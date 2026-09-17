namespace KevinZonda.AgentUsageMonitor;

public sealed record AgentUsageMonitorOptions
{
    /// <summary>Legacy option, ignored. OAuth credentials are renewed only by Kimi Code CLI.</summary>
    public bool AutoRenewKimiToken { get; init; }
}
