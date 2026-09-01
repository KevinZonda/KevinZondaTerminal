namespace KevinZonda.AgentUsageMonitor;

public interface IAgentUsageMonitorService : IAsyncDisposable
{
    event Action<AgentUsageStatus>? StatusChanged;

    AgentUsageStatus Current { get; }

    void Start();

    void UpdateOptions(AgentUsageMonitorOptions options);

    bool RequestRefresh(UsageProvider provider);
}
