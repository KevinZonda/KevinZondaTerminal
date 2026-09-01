using System.Runtime.InteropServices;
using KevinZonda.AgentUsageMonitor;

namespace KevinZonda.Terminal.Usage;

internal sealed class AgentProcessDetector
{
    private static readonly IntPtr InvalidHandleValue = new(-1);

    internal IReadOnlySet<UsageProvider> Detect(IReadOnlyCollection<uint> sessionProcessIds)
    {
        if (sessionProcessIds.Count == 0)
        {
            return new HashSet<UsageProvider>();
        }

        var entries = CaptureProcessEntries();
        var sessionProcesses = sessionProcessIds.ToHashSet();
        var providers = new HashSet<UsageProvider>();

        foreach (var entry in entries)
        {
            if (!sessionProcesses.Contains(entry.ProcessId))
            {
                continue;
            }

            if (Classify(entry.ExecutableName) is { } provider)
            {
                providers.Add(provider);
            }
        }

        return providers;
    }

    internal static UsageProvider? Classify(string executableName)
    {
        var name = Path.GetFileNameWithoutExtension(executableName);
        if (name.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("codex-", StringComparison.OrdinalIgnoreCase))
        {
            return UsageProvider.Codex;
        }

        if (name.Equals("kimi", StringComparison.OrdinalIgnoreCase)
            || name.Equals("kimi-code", StringComparison.OrdinalIgnoreCase)
            || name.Equals("kimi_code", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("kimi-code-", StringComparison.OrdinalIgnoreCase))
        {
            return UsageProvider.KimiCode;
        }

        return null;
    }

    private static IReadOnlyList<ProcessEntry> CaptureProcessEntries()
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return [];
        }

        try
        {
            var nativeEntry = new NativeMethods.ProcessEntry32
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>(),
                szExeFile = string.Empty
            };
            if (!NativeMethods.Process32FirstW(snapshot, ref nativeEntry))
            {
                return [];
            }

            var result = new List<ProcessEntry>();
            do
            {
                result.Add(new ProcessEntry(
                    nativeEntry.th32ProcessID,
                    nativeEntry.szExeFile));
                nativeEntry.dwSize = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>();
            }
            while (NativeMethods.Process32NextW(snapshot, ref nativeEntry));

            return result;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(snapshot);
        }
    }

    private sealed record ProcessEntry(uint ProcessId, string ExecutableName);

    private static class NativeMethods
    {
        internal const uint Th32csSnapProcess = 0x0000_0002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessEntry32
        {
            internal uint dwSize;
            internal uint cntUsage;
            internal uint th32ProcessID;
            internal IntPtr th32DefaultHeapID;
            internal uint th32ModuleID;
            internal uint cntThreads;
            internal uint th32ParentProcessID;
            internal int pcPriClassBase;
            internal uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateToolhelp32Snapshot(
            uint dwFlags,
            uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32FirstW(
            IntPtr hSnapshot,
            ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32NextW(
            IntPtr hSnapshot,
            ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);
    }
}
