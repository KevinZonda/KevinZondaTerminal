namespace KevinZonda.Terminal.Monitoring;

internal sealed record SystemMetricsStatus(
    double? CpuPercent,
    ulong UsedMemoryBytes,
    ulong AvailableMemoryBytes,
    ulong TotalMemoryBytes,
    DateTimeOffset? UpdatedAt)
{
    internal static SystemMetricsStatus Empty { get; } = new(null, 0, 0, 0, null);
}
