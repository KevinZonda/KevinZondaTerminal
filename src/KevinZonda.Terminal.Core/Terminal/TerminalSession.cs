using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using KevinZonda.Terminal.Configuration;
using KevinZonda.Terminal.Interop;
using Microsoft.Win32.SafeHandles;

namespace KevinZonda.Terminal.Terminal;

internal sealed class TerminalSession : IAsyncDisposable
{
    private const int BufferSize = 16 * 1024;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly IConHost _conHost;
    private readonly ProcessJob _processJob;
    private readonly SafeKernelHandle _process;
    private readonly TerminalThemePreset _theme;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly object _resizeLock = new();
    private Task? _readTask;
    private Task? _waitTask;
    private Task? _paletteTask;
    private int _columns;
    private int _rows;
    private int _disposed;
    private int _exitRaised;

    private TerminalSession(
        string id,
        string shellName,
        uint processId,
        FileStream input,
        FileStream output,
        IConHost conHost,
        ProcessJob processJob,
        SafeKernelHandle process,
        TerminalThemePreset theme,
        int columns,
        int rows)
    {
        Id = id;
        ShellName = shellName;
        ProcessId = processId;
        _input = input;
        _output = output;
        _conHost = conHost;
        _processJob = processJob;
        _process = process;
        _theme = theme;
        _columns = columns;
        _rows = rows;
    }

    internal string Id { get; }

    internal string ShellName { get; }

    internal uint ProcessId { get; }

    internal IReadOnlyList<uint> GetProcessIds() => _processJob.GetProcessIds();

    internal event Action<TerminalSession, string>? OutputReceived;

    internal event Action<TerminalSession, TerminalExitStatus>? Exited;

    internal void StartPumps()
    {
        if (_readTask is not null || _waitTask is not null)
        {
            throw new InvalidOperationException("The terminal session pumps have already started.");
        }

        _readTask = Task.Run(ReadLoop);
        _waitTask = WaitForExit();
        _paletteTask = ApplyConsoleThemeAfterStartup();
    }

    internal static TerminalSession Start(
        string id,
        int columns,
        int rows,
        ShellLaunchSpec shell,
        TerminalThemePreset theme,
        string startingDirectory,
        bool enhancedOpenConsole)
    {
        columns = Math.Clamp(columns, 2, short.MaxValue);
        rows = Math.Clamp(rows, 1, short.MaxValue);

        if (!NativeMethods.CreatePipe(out var pseudoInput, out var hostInput, IntPtr.Zero, 0))
        {
            throw NativeMethods.LastError("Unable to create the ConPTY input pipe.");
        }

        if (!NativeMethods.CreatePipe(out var hostOutput, out var pseudoOutput, IntPtr.Zero, 0))
        {
            pseudoInput.Dispose();
            hostInput.Dispose();
            throw NativeMethods.LastError("Unable to create the ConPTY output pipe.");
        }

        IConHost? conHost = null;
        ProcessJob? processJob = null;
        SafeKernelHandle? process = null;
        FileStream? inputStream = null;
        FileStream? outputStream = null;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;

        try
        {
            processJob = ProcessJob.Create();
            conHost = ConHost.Create(
                columns,
                rows,
                pseudoInput,
                pseudoOutput,
                enhancedOpenConsole,
                processJob);

            pseudoInput.Dispose();
            pseudoOutput.Dispose();

            nuint attributeListSize = 0;
            _ = NativeMethods.InitializeProcThreadAttributeList(
                IntPtr.Zero,
                1,
                0,
                ref attributeListSize);

            attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw NativeMethods.LastError("Unable to initialize the process attribute list.");
            }

            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    NativeMethods.ProcThreadAttributePseudoConsole,
                    conHost.PseudoConsoleHandle,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw NativeMethods.LastError("Unable to attach the pseudoconsole to the child process.");
            }

            var startupInfo = new NativeMethods.StartupInfoEx
            {
                StartupInfo = new NativeMethods.StartupInfo
                {
                    cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>()
                },
                lpAttributeList = attributeList
            };

            var commandLine = new StringBuilder($"\"{shell.ExecutablePath}\"");
            if (!string.IsNullOrWhiteSpace(shell.Arguments))
            {
                commandLine.Append(' ').Append(shell.Arguments);
            }

            environmentBlock = CreateShellEnvironmentBlock(shell);
            var created = NativeMethods.CreateProcessW(
                shell.ExecutablePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.ExtendedStartupInfoPresent |
                    NativeMethods.CreateUnicodeEnvironment |
                    NativeMethods.CreateSuspended,
                environmentBlock,
                startingDirectory,
                ref startupInfo,
                out var processInformation);

            if (!created)
            {
                throw NativeMethods.LastError($"Unable to start shell '{shell.ExecutablePath}'.");
            }

            threadHandle = processInformation.hThread;
            process = new SafeKernelHandle(processInformation.hProcess);
            processJob.Assign(process);
            if (NativeMethods.ResumeThread(threadHandle) == uint.MaxValue)
            {
                throw NativeMethods.LastError("Unable to resume the terminal shell process.");
            }
            _ = NativeMethods.CloseHandle(threadHandle);
            threadHandle = IntPtr.Zero;
            inputStream = new FileStream(hostInput, FileAccess.Write, BufferSize, isAsync: false);
            outputStream = new FileStream(hostOutput, FileAccess.Read, BufferSize, isAsync: false);

            return new TerminalSession(
                id,
                shell.DisplayName,
                processInformation.dwProcessId,
                inputStream,
                outputStream,
                conHost,
                processJob,
                process,
                theme,
                columns,
                rows);
        }
        catch
        {
            if (threadHandle != IntPtr.Zero && process is not null)
            {
                _ = NativeMethods.TerminateProcess(process.DangerousGetHandle(), 1);
            }
            inputStream?.Dispose();
            outputStream?.Dispose();
            process?.Dispose();
            processJob?.Dispose();
            conHost?.Dispose();
            hostInput.Dispose();
            hostOutput.Dispose();
            pseudoInput.Dispose();
            pseudoOutput.Dispose();
            throw;
        }
        finally
        {
            if (threadHandle != IntPtr.Zero)
            {
                _ = NativeMethods.CloseHandle(threadHandle);
            }
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    private async Task ApplyConsoleThemeAfterStartup()
    {
        try
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                await ConsoleThemeHelper.ApplyAfterStartup(
                    ProcessId,
                    _theme,
                    _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private static IntPtr CreateShellEnvironmentBlock(ShellLaunchSpec shell)
    {
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && variable.Value is string value)
            {
                environment[name] = value;
            }
        }

        if (shell.Environment is not null)
        {
            foreach (var variable in shell.Environment)
            {
                environment[variable.Key] = variable.Value;
            }
        }

        if (shell.RemovedEnvironmentVariables is not null)
        {
            foreach (var variable in shell.RemovedEnvironmentVariables)
            {
                environment.Remove(variable);
            }
        }

        environment["TERM"] = "xterm-256color";
        environment["COLORTERM"] = "truecolor";
        var block = string.Join('\0', environment.Select(variable => $"{variable.Key}={variable.Value}"))
            + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    internal async Task WriteAsync(string data)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(data))
        {
            return;
        }

        TerminalProtocolTrace.Observe(Id, "renderer->process", data);
        var bytes = Encoding.UTF8.GetBytes(data);
        await WriteAsync(bytes).ConfigureAwait(false);
        TerminalProtocolTrace.Observe(Id, "renderer->pipe", data);
    }

    internal async Task WriteAsync(ReadOnlyMemory<byte> data)
    {
        if (Volatile.Read(ref _disposed) != 0 || data.IsEmpty)
        {
            return;
        }

        await _inputLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await _input.WriteAsync(data, _lifetime.Token).ConfigureAwait(false);
            await _input.FlushAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _inputLock.Release();
        }
    }

    internal void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        columns = Math.Clamp(columns, 2, short.MaxValue);
        rows = Math.Clamp(rows, 1, short.MaxValue);

        lock (_resizeLock)
        {
            if (_columns == columns && _rows == rows)
            {
                return;
            }

            _conHost.Resize(columns, rows);
            _columns = columns;
            _rows = rows;
        }
    }

    private void ReadLoop()
    {
        var bytes = new byte[BufferSize];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(BufferSize)];
        var decoder = Encoding.UTF8.GetDecoder();

        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var count = _output.Read(bytes, 0, bytes.Length);
                if (count == 0)
                {
                    break;
                }

                decoder.Convert(
                    bytes,
                    0,
                    count,
                    chars,
                    0,
                    chars.Length,
                    flush: false,
                    out _,
                    out var charsUsed,
                    out _);

                if (charsUsed > 0)
                {
                    var data = new string(chars, 0, charsUsed);
                    TerminalProtocolTrace.Observe(Id, "process->renderer", data);
                    OutputReceived?.Invoke(this, data);
                }
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    private async Task WaitForExit()
    {
        var shellExitTask = ProcessWaiter.WaitForExit(_process);
        var hostExitTask = _conHost.ExitTask;
        if (hostExitTask is null)
        {
            var shellExitCode = await shellExitTask.ConfigureAwait(false);
            if (shellExitCode is { } code)
            {
                RaiseExited(new TerminalExitStatus(code, null));
            }
            return;
        }

        var completed = await Task.WhenAny(shellExitTask, hostExitTask).ConfigureAwait(false);
        if (completed == shellExitTask)
        {
            var shellExitCode = await shellExitTask.ConfigureAwait(false);
            if (shellExitCode is { } code)
            {
                RaiseExited(new TerminalExitStatus(code, null));
            }
            return;
        }

        var hostExitCode = await hostExitTask.ConfigureAwait(false) ?? uint.MaxValue;
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        RaiseExited(new TerminalExitStatus(
            hostExitCode,
            $"terminal host exited unexpectedly with code {hostExitCode}"));

        _processJob.Terminate(1);
    }

    private void RaiseExited(TerminalExitStatus status)
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) == 0)
        {
            Exited?.Invoke(this, status);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _input.Dispose();

        await Task.Run(() =>
        {
            _conHost.Dispose();

            var waitResult = NativeMethods.WaitForSingleObject(_process.DangerousGetHandle(), 750);
            if (waitResult == NativeMethods.WaitTimeout)
            {
                _ = NativeMethods.TerminateProcess(_process.DangerousGetHandle(), 1);
                _ = NativeMethods.WaitForSingleObject(_process.DangerousGetHandle(), 750);
            }

            _output.Dispose();
        }).ConfigureAwait(false);

        try
        {
            var pumps = new[] { _readTask, _waitTask, _paletteTask }.OfType<Task>();
            await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _process.Dispose();
        _processJob.Dispose();
        _inputLock.Dispose();
        _lifetime.Dispose();
    }

}

internal sealed record TerminalExitStatus(uint ExitCode, string? Failure);
