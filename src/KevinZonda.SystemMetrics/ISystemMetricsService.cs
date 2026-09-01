namespace KevinZonda.SystemMetrics;

public interface ISystemMetricsService : IAsyncDisposable
{
    event Action<SystemMetricsStatus>? StatusChanged;

    SystemMetricsStatus Current { get; }

    void Start();
}
