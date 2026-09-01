using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KevinZonda.AgentUsageMonitor.ProcessDetection;

internal sealed class AgentProcessDetector
{
    private static readonly IntPtr InvalidHandleValue = new(-1);

    internal async Task<IReadOnlySet<UsageProvider>> DetectAsync(
        IReadOnlyCollection<int> rootProcessIds,
        CancellationToken cancellationToken)
    {
        if (rootProcessIds.Count == 0)
        {
            return new HashSet<UsageProvider>();
        }

        IReadOnlyList<ProcessEntry> entries;
        if (OperatingSystem.IsWindows())
        {
            entries = CaptureWindowsProcessEntries();
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            entries = ParseUnixSnapshot(
                await CaptureUnixProcessSnapshotAsync(cancellationToken).ConfigureAwait(false));
        }
        else
        {
            return new HashSet<UsageProvider>();
        }

        return DetectProcessTree(entries, rootProcessIds);
    }

    internal static IReadOnlySet<UsageProvider> DetectUnixSnapshot(
        string snapshot,
        IReadOnlyCollection<int> rootProcessIds) =>
        DetectProcessTree(ParseUnixSnapshot(snapshot), rootProcessIds);

    internal static IReadOnlySet<UsageProvider> DetectProcessTree(
        IReadOnlyCollection<ProcessEntry> entries,
        IReadOnlyCollection<int> rootProcessIds)
    {
        var entriesById = entries.ToDictionary(entry => entry.ProcessId);
        var children = entries
            .GroupBy(entry => entry.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var providers = new HashSet<UsageProvider>();
        var visited = new HashSet<int>();
        var pending = new Queue<int>(rootProcessIds);

        while (pending.TryDequeue(out var processId))
        {
            if (!visited.Add(processId))
            {
                continue;
            }

            if (entriesById.TryGetValue(processId, out var entry) &&
                Classify(entry.Command) is { } provider)
            {
                providers.Add(provider);
            }

            if (children.TryGetValue(processId, out var descendants))
            {
                foreach (var descendant in descendants)
                {
                    pending.Enqueue(descendant.ProcessId);
                }
            }
        }

        return providers;
    }

    internal static UsageProvider? Classify(string command)
    {
        var name = Path.GetFileNameWithoutExtension(command.Trim().TrimStart('-'));
        if (name.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("codex-", StringComparison.OrdinalIgnoreCase))
        {
            return UsageProvider.Codex;
        }

        if (name.Equals("kimi", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("kimi-code", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("kimi_code", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("kimi-code-", StringComparison.OrdinalIgnoreCase))
        {
            return UsageProvider.KimiCode;
        }

        return null;
    }

    private static IReadOnlyList<ProcessEntry> ParseUnixSnapshot(string snapshot)
    {
        var entries = new List<ProcessEntry>();
        foreach (var line in snapshot.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(
                (char[]?)null,
                3,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out var processId) &&
                int.TryParse(parts[1], out var parentProcessId))
            {
                entries.Add(new ProcessEntry(processId, parentProcessId, parts[2]));
            }
        }
        return entries;
    }

    private static async Task<string> CaptureUnixProcessSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var executable = File.Exists("/bin/ps") ? "/bin/ps" : "/usr/bin/ps";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-axo");
        process.StartInfo.ArgumentList.Add("pid=,ppid=,comm=");

        try
        {
            if (!process.Start())
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : string.Empty;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                IOException)
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<ProcessEntry> CaptureWindowsProcessEntries()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return [];
        }

        try
        {
            var nativeEntry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
                ExecutableFile = string.Empty
            };
            if (!Process32FirstW(snapshot, ref nativeEntry))
            {
                return [];
            }

            var result = new List<ProcessEntry>();
            do
            {
                if (nativeEntry.ProcessId <= int.MaxValue &&
                    nativeEntry.ParentProcessId <= int.MaxValue)
                {
                    result.Add(new ProcessEntry(
                        (int)nativeEntry.ProcessId,
                        (int)nativeEntry.ParentProcessId,
                        nativeEntry.ExecutableFile));
                }
                nativeEntry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32NextW(snapshot, ref nativeEntry));

            return result;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    internal sealed record ProcessEntry(int ProcessId, int ParentProcessId, string Command);

    private const uint Th32csSnapProcess = 0x0000_0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        internal uint Size;
        internal uint UsageCount;
        internal uint ProcessId;
        internal IntPtr DefaultHeapId;
        internal uint ModuleId;
        internal uint ThreadCount;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
