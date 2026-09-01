using System.Globalization;
using System.Runtime.InteropServices;

namespace KevinZonda.SystemMetrics;

public sealed class SystemMetricsService : ISystemMetricsService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateLock = new();
    private SystemMetricsStatus _current = SystemMetricsStatus.Empty;
    private UnixCpuCounters? _previousUnixCpu;
    private WindowsCpuTimes? _previousWindowsCpu;
    private Task? _monitorTask;
    private int _disposed;

    public event Action<SystemMetricsStatus>? StatusChanged;

    public SystemMetricsStatus Current
    {
        get
        {
            lock (_stateLock)
            {
                return _current;
            }
        }
    }

    public void Start()
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
        if (!TryReadMemory(out var totalMemory, out var availableMemory))
        {
            return;
        }

        var cpuPercent = TryReadCpuPercent();
        availableMemory = Math.Min(availableMemory, totalMemory);
        var status = new SystemMetricsStatus(
            cpuPercent,
            totalMemory - availableMemory,
            availableMemory,
            totalMemory,
            DateTimeOffset.UtcNow);
        lock (_stateLock)
        {
            _current = status;
        }
        StatusChanged?.Invoke(status);
    }

    private double? TryReadCpuPercent()
    {
        if (OperatingSystem.IsWindows())
        {
            return TryReadWindowsCpuPercent();
        }

        if (!TryReadUnixCpu(out var cpu))
        {
            return null;
        }

        lock (_stateLock)
        {
            double? cpuPercent = null;
            if (_previousUnixCpu is { } previous)
            {
                var idleDelta = CounterDelta(cpu.Idle, previous.Idle, cpu.Is32Bit) +
                                CounterDelta(cpu.IoWait, previous.IoWait, cpu.Is32Bit);
                var totalDelta = idleDelta +
                                 CounterDelta(cpu.User, previous.User, cpu.Is32Bit) +
                                 CounterDelta(cpu.Nice, previous.Nice, cpu.Is32Bit) +
                                 CounterDelta(cpu.System, previous.System, cpu.Is32Bit) +
                                 CounterDelta(cpu.Irq, previous.Irq, cpu.Is32Bit) +
                                 CounterDelta(cpu.SoftIrq, previous.SoftIrq, cpu.Is32Bit) +
                                 CounterDelta(cpu.Steal, previous.Steal, cpu.Is32Bit);
                if (totalDelta > 0)
                {
                    cpuPercent = Math.Clamp(
                        (double)(totalDelta - Math.Min(idleDelta, totalDelta)) / totalDelta * 100,
                        0,
                        100);
                }
            }
            _previousUnixCpu = cpu;
            return cpuPercent;
        }
    }

    private double? TryReadWindowsCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        var cpu = new WindowsCpuTimes(idle.Value, kernel.Value + user.Value);
        lock (_stateLock)
        {
            double? cpuPercent = null;
            if (_previousWindowsCpu is { } previous)
            {
                var totalDelta = cpu.Total - previous.Total;
                var idleDelta = cpu.Idle - previous.Idle;
                if (totalDelta > 0)
                {
                    cpuPercent = Math.Clamp(
                        (double)(totalDelta - Math.Min(idleDelta, totalDelta)) / totalDelta * 100,
                        0,
                        100);
                }
            }
            _previousWindowsCpu = cpu;
            return cpuPercent;
        }
    }

    private static ulong CounterDelta(ulong current, ulong previous, bool is32Bit)
    {
        if (current >= previous)
        {
            return current - previous;
        }
        return is32Bit
            ? (ulong)uint.MaxValue - previous + current + 1
            : 0;
    }

    private static bool TryReadUnixCpu(out UnixCpuCounters counters)
    {
        if (OperatingSystem.IsMacOS())
        {
            return TryReadMacCpu(out counters);
        }
        if (OperatingSystem.IsLinux())
        {
            return TryReadLinuxCpu(out counters);
        }

        counters = default;
        return false;
    }

    private static bool TryReadMemory(out ulong totalBytes, out ulong availableBytes)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryReadWindowsMemory(out totalBytes, out availableBytes);
        }
        if (OperatingSystem.IsMacOS())
        {
            return TryReadMacMemory(out totalBytes, out availableBytes);
        }
        if (OperatingSystem.IsLinux())
        {
            return TryReadLinuxMemory(out totalBytes, out availableBytes);
        }

        totalBytes = 0;
        availableBytes = 0;
        return false;
    }

    private static bool TryReadWindowsMemory(out ulong totalBytes, out ulong availableBytes)
    {
        var memory = new WindowsMemoryStatus
        {
            Length = (uint)Marshal.SizeOf<WindowsMemoryStatus>()
        };
        if (!GlobalMemoryStatusEx(ref memory))
        {
            totalBytes = 0;
            availableBytes = 0;
            return false;
        }

        totalBytes = memory.TotalPhysical;
        availableBytes = memory.AvailablePhysical;
        return true;
    }

    private static bool TryReadLinuxCpu(out UnixCpuCounters counters)
    {
        counters = default;
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault();
            if (line is null)
            {
                return false;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || !string.Equals(fields[0], "cpu", StringComparison.Ordinal))
            {
                return false;
            }

            Span<ulong> values = stackalloc ulong[8];
            for (var index = 0; index < values.Length; index++)
            {
                if (index + 1 >= fields.Length ||
                    !ulong.TryParse(
                        fields[index + 1],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out values[index]))
                {
                    values[index] = 0;
                }
            }

            counters = new UnixCpuCounters(
                values[0],
                values[1],
                values[2],
                values[3],
                values[4],
                values[5],
                values[6],
                values[7],
                Is32Bit: false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryReadLinuxMemory(out ulong totalBytes, out ulong availableBytes)
    {
        totalBytes = 0;
        availableBytes = 0;
        try
        {
            var values = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var fields = line[(separator + 1)..]
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 0 && ulong.TryParse(
                        fields[0],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var kibibytes))
                {
                    values[line[..separator]] = kibibytes * 1024;
                }
            }

            if (!values.TryGetValue("MemTotal", out totalBytes) || totalBytes == 0)
            {
                return false;
            }

            if (!values.TryGetValue("MemAvailable", out availableBytes))
            {
                availableBytes = ValueOrZero(values, "MemFree") +
                                 ValueOrZero(values, "Buffers") +
                                 ValueOrZero(values, "Cached") +
                                 ValueOrZero(values, "SReclaimable");
                availableBytes -= Math.Min(availableBytes, ValueOrZero(values, "Shmem"));
            }
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ulong ValueOrZero(IReadOnlyDictionary<string, ulong> values, string name) =>
        values.TryGetValue(name, out var value) ? value : 0;

    private static bool TryReadMacCpu(out UnixCpuCounters counters)
    {
        counters = default;
        try
        {
            var count = (uint)(Marshal.SizeOf<HostCpuLoadInfo>() / sizeof(uint));
            if (HostStatistics(MachHostSelf(), HostCpuLoadInfoFlavor, out var info, ref count) != 0)
            {
                return false;
            }

            counters = new UnixCpuCounters(
                info.User,
                info.Nice,
                info.System,
                info.Idle,
                0,
                0,
                0,
                0,
                Is32Bit: true);
            return true;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryReadMacMemory(out ulong totalBytes, out ulong availableBytes)
    {
        totalBytes = 0;
        availableBytes = 0;
        try
        {
            var totalSize = (nuint)sizeof(ulong);
            if (SysctlByName("hw.memsize", out totalBytes, ref totalSize, IntPtr.Zero, 0) != 0 ||
                totalBytes == 0)
            {
                return false;
            }

            var host = MachHostSelf();
            if (HostPageSize(host, out var pageSize) != 0)
            {
                return false;
            }

            var count = (uint)(Marshal.SizeOf<VmStatistics64>() / sizeof(uint));
            if (HostStatistics64(host, HostVmInfo64Flavor, out var statistics, ref count) != 0)
            {
                return false;
            }

            var availablePages = (ulong)statistics.FreeCount +
                                 statistics.InactiveCount +
                                 statistics.SpeculativeCount;
            availableBytes = availablePages * pageSize;
            return true;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException)
        {
            totalBytes = 0;
            availableBytes = 0;
            return false;
        }
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

    private readonly record struct UnixCpuCounters(
        ulong User,
        ulong Nice,
        ulong System,
        ulong Idle,
        ulong IoWait,
        ulong Irq,
        ulong SoftIrq,
        ulong Steal,
        bool Is32Bit);

    private readonly record struct WindowsCpuTimes(ulong Idle, ulong Total);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileTime
    {
        internal uint Low;
        internal uint High;

        internal readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsMemoryStatus
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

    [StructLayout(LayoutKind.Sequential)]
    private struct HostCpuLoadInfo
    {
        internal uint User;
        internal uint System;
        internal uint Idle;
        internal uint Nice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VmStatistics64
    {
        internal uint FreeCount;
        internal uint ActiveCount;
        internal uint InactiveCount;
        internal uint WireCount;
        internal ulong ZeroFillCount;
        internal ulong Reactivations;
        internal ulong PageIns;
        internal ulong PageOuts;
        internal ulong Faults;
        internal ulong CopyOnWriteFaults;
        internal ulong Lookups;
        internal ulong Hits;
        internal ulong Purges;
        internal uint PurgeableCount;
        internal uint SpeculativeCount;
        internal ulong Decompressions;
        internal ulong Compressions;
        internal ulong SwapIns;
        internal ulong SwapOuts;
        internal uint CompressorPageCount;
        internal uint ThrottledCount;
        internal uint ExternalPageCount;
        internal uint InternalPageCount;
        internal ulong TotalUncompressedPagesInCompressor;
    }

    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int HostCpuLoadInfoFlavor = 3;
    private const int HostVmInfo64Flavor = 4;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out WindowsFileTime idleTime,
        out WindowsFileTime kernelTime,
        out WindowsFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref WindowsMemoryStatus buffer);

    [DllImport(LibSystem, EntryPoint = "mach_host_self")]
    private static extern uint MachHostSelf();

    [DllImport(LibSystem, EntryPoint = "host_statistics")]
    private static extern int HostStatistics(
        uint host,
        int flavor,
        out HostCpuLoadInfo info,
        ref uint count);

    [DllImport(LibSystem, EntryPoint = "host_page_size")]
    private static extern int HostPageSize(uint host, out uint pageSize);

    [DllImport(LibSystem, EntryPoint = "host_statistics64")]
    private static extern int HostStatistics64(
        uint host,
        int flavor,
        out VmStatistics64 statistics,
        ref uint count);

    [DllImport(LibSystem, EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out ulong oldValue,
        ref nuint oldLength,
        IntPtr newValue,
        nuint newLength);
}
