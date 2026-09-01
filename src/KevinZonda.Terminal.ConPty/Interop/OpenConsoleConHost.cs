using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KevinZonda.Terminal.Interop;

/// <summary>
/// Pseudoconsole host that spawns a side-by-side OpenConsole.exe in headless
/// mode, mirroring Windows Terminal's winconpty (src/winconpty/winconpty.cpp,
/// MIT). Unlike the inbox conhost, this OpenConsole parses VT sequences into
/// its buffer and forwards them verbatim to the terminal, so scroll regions
/// and other sequences survive the trip.
///
/// The HPCON handed to PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE is the address of a
/// three-handle struct { hSignal, hPtyReference, hConPtyProcess }; that layout
/// is an ABI shared with the OS (see winconpty.h).
/// </summary>
internal sealed class OpenConsoleConHost : IConHost
{
    private const ushort PtySignalResizeWindow = 8;

    private readonly IntPtr _pseudoConsole;
    private readonly SafeFileHandle _signal;
    private readonly SafeFileHandle _reference;
    private readonly SafeKernelHandle _process;
    private readonly Task<uint?> _exitTask;
    private int _disposed;

    private OpenConsoleConHost(
        IntPtr pseudoConsole,
        SafeFileHandle signal,
        SafeFileHandle reference,
        SafeKernelHandle process)
    {
        _pseudoConsole = pseudoConsole;
        _signal = signal;
        _reference = reference;
        _process = process;
        _exitTask = ProcessWaiter.WaitForExit(_process);
    }

    public IntPtr PseudoConsoleHandle => _pseudoConsole;

    public Task<uint?>? ExitTask => _exitTask;

    internal static OpenConsoleConHost Create(
        int columns,
        int rows,
        SafeFileHandle input,
        SafeFileHandle output,
        string hostPath,
        ProcessJob processJob)
    {
        SafeFileHandle? server = null;
        SafeFileHandle? reference = null;
        SafeFileHandle? signalHostSide = null;
        SafeFileHandle? signalOurSide = null;
        SafeKernelHandle? process = null;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr inheritedHandlesMem = IntPtr.Zero;

        try
        {
            server = OpenConsoleDevice(
                NativeMethods.GenericAll,
                root: null,
                @"\Device\ConDrv\Server",
                NativeMethods.ObjCaseInsensitive | NativeMethods.ObjInherit,
                openOptions: 0);

            reference = OpenConsoleDevice(
                NativeMethods.GenericRead | NativeMethods.GenericWrite | NativeMethods.Synchronize,
                server,
                @"\Reference",
                NativeMethods.ObjCaseInsensitive,
                NativeMethods.FileSynchronousIoNonAlert);

            if (!NativeMethods.CreatePipe(out signalHostSide, out signalOurSide, IntPtr.Zero, 0))
            {
                throw NativeMethods.LastError("Unable to create the ConPTY signal pipe.");
            }
            // Only the conhost side travels to the child; make the pipe ends the
            // child must inherit inheritable.
            SetInheritable(signalHostSide);
            SetInheritable(input);
            SetInheritable(output);

            var commandLine = new System.Text.StringBuilder(
                $"\"{hostPath}\" --headless --width {columns} --height {rows} " +
                $"--signal 0x{signalHostSide.DangerousGetHandle().ToInt64():x} " +
                $"--server 0x{server.DangerousGetHandle().ToInt64():x}");

            nuint attributeListSize = 0;
            _ = NativeMethods.InitializeProcThreadAttributeList(
                IntPtr.Zero, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw NativeMethods.LastError("Unable to initialize the process attribute list.");
            }

            var inheritedHandles = new[]
            {
                server.DangerousGetHandle(),
                input.DangerousGetHandle(),
                output.DangerousGetHandle(),
                signalHostSide.DangerousGetHandle()
            };
            // The handle array must stay valid until after CreateProcess reads it;
            // it is freed in the outer finally.
            inheritedHandlesMem = Marshal.AllocHGlobal(inheritedHandles.Length * IntPtr.Size);
            for (var i = 0; i < inheritedHandles.Length; i++)
            {
                Marshal.WriteIntPtr(inheritedHandlesMem, i * IntPtr.Size, inheritedHandles[i]);
            }

            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    NativeMethods.ProcThreadAttributeHandleList,
                    inheritedHandlesMem,
                    (nuint)(inheritedHandles.Length * IntPtr.Size),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw NativeMethods.LastError("Unable to set the inherited handle list.");
            }

            var startupInfo = new NativeMethods.StartupInfoEx
            {
                StartupInfo = new NativeMethods.StartupInfo
                {
                    cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>(),
                    dwFlags = NativeMethods.StartfUseStdHandles,
                    hStdInput = input.DangerousGetHandle(),
                    hStdOutput = output.DangerousGetHandle(),
                    hStdError = output.DangerousGetHandle()
                },
                lpAttributeList = attributeList
            };

            if (!NativeMethods.CreateProcessW(
                    hostPath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    NativeMethods.ExtendedStartupInfoPresent,
                    IntPtr.Zero,
                    null,
                    ref startupInfo,
                    out var processInformation))
            {
                throw NativeMethods.LastError($"Unable to start the console host '{hostPath}'.");
            }

            _ = NativeMethods.CloseHandle(processInformation.hThread);
            process = new SafeKernelHandle(processInformation.hProcess);

            // The child inherited its own copies; drop ours.
            signalHostSide.Dispose();
            signalHostSide = null;
            server.Dispose();
            server = null;

            // Pack the HPCON: { hSignal, hPtyReference, hConPtyProcess }.
            pseudoConsole = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(pseudoConsole, 0, signalOurSide.DangerousGetHandle());
            Marshal.WriteIntPtr(pseudoConsole, IntPtr.Size, reference.DangerousGetHandle());
            Marshal.WriteIntPtr(pseudoConsole, 2 * IntPtr.Size, process.DangerousGetHandle());

            processJob.Assign(process);

            var host = new OpenConsoleConHost(pseudoConsole, signalOurSide, reference, process);
            signalOurSide = null;
            reference = null;
            process = null;
            pseudoConsole = IntPtr.Zero;
            return host;
        }
        catch
        {
            if (process is not null && !process.IsInvalid && !process.IsClosed)
            {
                _ = NativeMethods.TerminateProcess(process.DangerousGetHandle(), 1);
            }
            throw;
        }
        finally
        {
            server?.Dispose();
            reference?.Dispose();
            signalHostSide?.Dispose();
            signalOurSide?.Dispose();
            process?.Dispose();
            if (pseudoConsole != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pseudoConsole);
            }
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (inheritedHandlesMem != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inheritedHandlesMem);
            }
        }
    }

    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var packet = new byte[6];
        BitConverter.TryWriteBytes(packet.AsSpan(0, 2), PtySignalResizeWindow);
        BitConverter.TryWriteBytes(packet.AsSpan(2, 2), (ushort)columns);
        BitConverter.TryWriteBytes(packet.AsSpan(4, 2), (ushort)rows);

        if (!NativeMethods.WriteFile(_signal, packet, (uint)packet.Length, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resize the pseudoconsole.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _signal.Dispose();
        // Closing the reference lets OpenConsole exit once every client is gone.
        _reference.Dispose();
        Marshal.FreeHGlobal(_pseudoConsole);

        var waitResult = NativeMethods.WaitForSingleObject(_process.DangerousGetHandle(), 750);
        if (waitResult == NativeMethods.WaitTimeout)
        {
            _ = NativeMethods.TerminateProcess(_process.DangerousGetHandle(), 1);
            _ = NativeMethods.WaitForSingleObject(_process.DangerousGetHandle(), 750);
        }
        _process.Dispose();
    }

    private static void SetInheritable(SafeFileHandle handle)
    {
        if (!NativeMethods.SetHandleInformation(
                handle,
                NativeMethods.HandleFlagInherit,
                NativeMethods.HandleFlagInherit))
        {
            throw NativeMethods.LastError("Unable to mark a pipe handle inheritable.");
        }
    }

    private static SafeFileHandle OpenConsoleDevice(
        uint desiredAccess,
        SafeFileHandle? root,
        string name,
        uint attributes,
        uint openOptions)
    {
        var status = NativeMethods.NtOpenFile(
            out var handle,
            desiredAccess,
            root,
            name,
            attributes,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            openOptions,
            out _);

        if (status < 0 && root is null)
        {
            // First server handle on this machine: load ConDrv and retry, like winconpty.
            NativeMethods.EnsureConsoleDriverLoaded();
            status = NativeMethods.NtOpenFile(
                out handle,
                desiredAccess,
                root,
                name,
                attributes,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
                openOptions,
                out _);
        }

        if (status < 0)
        {
            throw new Win32Exception($"NtOpenFile({name}) failed with NTSTATUS 0x{status:x8}.");
        }
        return handle;
    }
}
