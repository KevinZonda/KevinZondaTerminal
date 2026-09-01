namespace KevinZonda.SystemMetrics;

public sealed record SystemMetricsStatus(
    double? CpuPercent,
    ulong UsedMemoryBytes,
    ulong AvailableMemoryBytes,
    ulong TotalMemoryBytes,
    DateTimeOffset? UpdatedAt)
{
    public static SystemMetricsStatus Empty { get; } = new(null, 0, 0, 0, null);
}
