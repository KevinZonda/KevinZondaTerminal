using KevinZonda.AgentUsageMonitor.KimiCode;

namespace KevinZonda.AgentUsageMonitor;

public sealed record AgentUsageMonitorOptions
{
    public KimiUsageAuthenticationMode KimiAuthenticationMode { get; init; }
    public KimiOAuthRegion KimiRegion { get; init; }
    public string? KimiActiveTokenPath { get; init; }
    /// <summary>Legacy option, ignored. OAuth credentials are renewed only by Kimi Code CLI.</summary>
    public bool AutoRenewKimiToken { get; init; }
}
