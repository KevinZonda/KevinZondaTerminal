using System.Runtime.InteropServices;

namespace KevinZonda.Terminal.Monitoring;

internal sealed class SystemMetricsService : IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateLock = new();
    private SystemMetricsStatus _current = SystemMetricsStatus.Empty;
    private CpuTimes? _previousCpuTimes;
    private Task? _monitorTask;
    private int _disposed;

    internal event Action<SystemMetricsStatus>? StatusChanged;

    internal SystemMetricsStatus Current
    {
        get
        {
            lock (_stateLock)
            {
                return _current;
            }
        }
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_monitorTask is not null)
        {
            return;
        }

        SampleAndRaise();
        _monitorTask = Task.Run(MonitorAsync);
    }

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                SampleAndRaise();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void SampleAndRaise()
    {
        if (!TryReadMemory(out var memory))
        {
            return;
        }

        double? cpuPercent = null;
        if (TryReadCpuTimes(out var cpuTimes))
        {
            lock (_stateLock)
            {
                if (_previousCpuTimes is { } previous)
                {
                    var totalDelta = cpuTimes.Total - previous.Total;
                    var idleDelta = cpuTimes.Idle - previous.Idle;
                    if (totalDelta > 0)
                    {
                        cpuPercent = Math.Clamp((double)(totalDelta - Math.Min(idleDelta, totalDelta))
                            / totalDelta * 100, 0, 100);
                    }
                }
                _previousCpuTimes = cpuTimes;
            }
        }

        var status = new SystemMetricsStatus(
            cpuPercent,
            memory.TotalPhysical - Math.Min(memory.AvailablePhysical, memory.TotalPhysical),
            memory.AvailablePhysical,
            memory.TotalPhysical,
            DateTimeOffset.UtcNow);
        lock (_stateLock)
        {
            _current = status;
        }
        StatusChanged?.Invoke(status);
    }

    private static bool TryReadCpuTimes(out CpuTimes times)
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            times = default;
            return false;
        }

        times = new CpuTimes(idle.Value, kernel.Value + user.Value);
        return true;
    }

    private static bool TryReadMemory(out MemoryStatus memory)
    {
        memory = new MemoryStatus
        {
            Length = (uint)Marshal.SizeOf<MemoryStatus>()
        };
        return GlobalMemoryStatusEx(ref memory);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        if (_monitorTask is not null)
        {
            await _monitorTask.ConfigureAwait(false);
        }
        _lifetime.Dispose();
    }

    private readonly record struct CpuTimes(ulong Idle, ulong Total);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;

        internal readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
