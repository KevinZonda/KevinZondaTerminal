using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KevinZonda.Terminal.Server.Launcher;

internal sealed class ServerProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x0000_2000;
    private readonly SafeFileHandle _handle;
    private int _disposed;

    private ServerProcessJob(SafeFileHandle handle)
    {
        _handle = handle;
    }

    internal static ServerProcessJob Create()
    {
        var rawHandle = CreateJobObjectW(IntPtr.Zero, null);
        if (rawHandle == IntPtr.Zero)
        {
            throw LastError("Unable to create the Server process job.");
        }

        var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
        var limits = new JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        if (!SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformation,
                ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationData>()))
        {
            var exception = LastError("Unable to configure the Server process job.");
            handle.Dispose();
            throw exception;
        }
        return new ServerProcessJob(handle);
    }

    internal void Assign(Process process)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw LastError("Unable to assign kterm-server to its process job.");
        }
    }

    internal void Terminate(uint exitCode)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _ = TerminateJobObject(_handle, exitCode);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _handle.Dispose();
        }
    }

    private static Win32Exception LastError(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformationData information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
