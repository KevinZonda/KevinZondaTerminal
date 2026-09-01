using KevinZonda.Terminal.AvaloniaDesktop;

if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
{
    Console.WriteLine("SKIP system metrics are implemented for macOS and Linux");
    return;
}

await using var service = new SystemMetricsService();
var updateCount = 0;
service.StatusChanged += _ => Interlocked.Increment(ref updateCount);
service.Start();
await Task.Delay(TimeSpan.FromMilliseconds(2_500));

var status = service.Current;
Require(updateCount >= 2, "Expected an immediate sample and a periodic sample.");
Require(status.TotalMemoryBytes > 0, "Total physical memory was not detected.");
Require(status.AvailableMemoryBytes <= status.TotalMemoryBytes,
    "Available memory exceeds total physical memory.");
Require(status.UsedMemoryBytes + status.AvailableMemoryBytes == status.TotalMemoryBytes,
    "Used and available memory do not add up to total physical memory.");
Require(status.CpuPercent is >= 0 and <= 100,
    "A valid CPU percentage was not produced after the second sample.");
Require(status.UpdatedAt is not null, "The metrics timestamp is missing.");

Console.WriteLine(
    $"PASS system metrics: CPU {status.CpuPercent:F1}%, " +
    $"memory {status.UsedMemoryBytes}/{status.TotalMemoryBytes} bytes");

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
