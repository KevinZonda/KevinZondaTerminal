using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KevinZonda.Terminal.Interop;

internal sealed class ProcessJob : IDisposable
{
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int ErrorMoreData = 234;
    private readonly SafeKernelHandle _handle;
    private int _disposed;

    private ProcessJob(SafeKernelHandle handle)
    {
        _handle = handle;
    }

    internal static ProcessJob Create()
    {
        var rawHandle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (rawHandle == IntPtr.Zero)
        {
            throw NativeMethods.LastError("Unable to create a terminal process job.");
        }

        var handle = new SafeKernelHandle(rawHandle);
        var limits = new NativeMethods.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
            {
                LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose
            }
        };
        if (!NativeMethods.SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformation,
                ref limits,
                (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>()))
        {
            var exception = NativeMethods.LastError("Unable to configure the terminal process job.");
            handle.Dispose();
            throw exception;
        }

        return new ProcessJob(handle);
    }

    internal void Assign(SafeKernelHandle process)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!NativeMethods.AssignProcessToJobObject(_handle, process))
        {
            throw NativeMethods.LastError("Unable to assign the shell to its terminal process job.");
        }
    }

    internal void Terminate(uint exitCode)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _ = NativeMethods.TerminateJobObject(_handle, exitCode);
        }
    }

    internal IReadOnlyList<uint> GetProcessIds()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return [];
        }

        var capacity = 16;
        while (capacity <= 16_384)
        {
            var size = checked(8 + capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(buffer, 0, 0);
                Marshal.WriteInt32(buffer, 4, 0);
                var succeeded = NativeMethods.QueryInformationJobObject(
                    _handle,
                    JobObjectBasicProcessIdList,
                    buffer,
                    (uint)size,
                    out _);
                var assigned = Marshal.ReadInt32(buffer, 0);
                var listed = Marshal.ReadInt32(buffer, 4);
                if (assigned > capacity)
                {
                    capacity = assigned;
                    continue;
                }
                if (!succeeded)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorMoreData)
                    {
                        capacity *= 2;
                        continue;
                    }
                    return [];
                }

                var count = Math.Min(Math.Max(listed, 0), capacity);
                var result = new uint[count];
                for (var index = 0; index < count; index++)
                {
                    var processId = Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64();
                    result[index] = checked((uint)processId);
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new Win32Exception("The terminal process job contains too many processes.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _handle.Dispose();
        }
    }
}
