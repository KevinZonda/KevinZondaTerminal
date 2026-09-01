using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace KevinZonda.Terminal.UnixPty;

/// <summary>
/// Runs a child process inside a native Unix pseudoterminal.
/// </summary>
/// <remarks>
/// A small native helper owns <c>forkpty</c>, process-group signaling, and the
/// PTY master descriptor. The managed process communicates with it through a
/// framed byte protocol so no managed code executes in the post-fork child.
/// Output is intentionally byte-oriented; terminal emulation and UTF-8 decoding
/// belong to the caller.
/// </remarks>
public sealed class UnixPtyProcess : IAsyncDisposable
{
    private const int HeaderLength = 5;
    private const int MaximumFrameBytes = 16 * 1024 * 1024;
    private const byte InputFrame = 1;
    private const byte ResizeFrame = 2;
    private const byte CloseFrame = 3;
    private const byte OutputFrame = 1;
    private const byte ExitFrame = 2;
    private const byte ErrorFrame = 3;
    private const byte ReadyFrame = 4;

    private readonly Process _helper;
    private readonly Stream _helperInput;
    private readonly Stream _helperOutput;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(256)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly TaskCompletionSource<int> _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<PtyExitStatus> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<string> _stderrTask;
    private readonly Task _pumpTask;
    private readonly Task _observeTask;
    private byte[]? _currentOutput;
    private int _currentOutputOffset;
    private int _disposed;

    private UnixPtyProcess(Process helper)
    {
        _helper = helper;
        _helperInput = helper.StandardInput.BaseStream;
        _helperOutput = helper.StandardOutput.BaseStream;
        _stderrTask = helper.StandardError.ReadToEndAsync();
        _pumpTask = PumpOutputAsync();
        _observeTask = ObserveHelperAsync();
    }

    /// <summary>Whether the current operating system can run this implementation.</summary>
    public static bool IsSupported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>The PID of the process running inside the pseudoterminal.</summary>
    public int ProcessId { get; private set; }

    /// <summary>Completes when the process running inside the pseudoterminal exits.</summary>
    public Task<PtyExitStatus> Completion => _completion.Task;

    /// <summary>Starts a process inside a new Unix pseudoterminal.</summary>
    public static async Task<UnixPtyProcess> StartAsync(
        PtyStartInfo startInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        Validate(startInfo);
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Unix PTY processes are supported only on macOS and Linux.");
        }

        var helperPath = ResolveHelperPath(startInfo.HelperPath);
        var processStartInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            WorkingDirectory = Path.GetFullPath(startInfo.WorkingDirectory),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        processStartInfo.ArgumentList.Add("--cols");
        processStartInfo.ArgumentList.Add(startInfo.Columns.ToString(System.Globalization.CultureInfo.InvariantCulture));
        processStartInfo.ArgumentList.Add("--rows");
        processStartInfo.ArgumentList.Add(startInfo.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture));
        processStartInfo.ArgumentList.Add("--");
        processStartInfo.ArgumentList.Add(startInfo.FileName);
        foreach (var argument in startInfo.Arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in startInfo.Environment)
        {
            if (value is null)
            {
                processStartInfo.Environment.Remove(name);
            }
            else
            {
                processStartInfo.Environment[name] = value;
            }
        }

        var helper = new Process { StartInfo = processStartInfo };
        try
        {
            if (!helper.Start())
            {
                throw new InvalidOperationException("Unable to start the Unix PTY helper.");
            }

            var process = new UnixPtyProcess(helper);
            try
            {
                process.ProcessId = await process._ready.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return process;
            }
            catch
            {
                await process.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            helper.Dispose();
            throw;
        }
    }

    /// <summary>Reads raw bytes emitted by the pseudoterminal.</summary>
    /// <returns>Zero after all output has been consumed.</returns>
    public async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        await _readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                if (_currentOutput is { } current)
                {
                    var count = Math.Min(buffer.Length, current.Length - _currentOutputOffset);
                    current.AsSpan(_currentOutputOffset, count).CopyTo(buffer.Span);
                    _currentOutputOffset += count;
                    if (_currentOutputOffset == current.Length)
                    {
                        _currentOutput = null;
                        _currentOutputOffset = 0;
                    }
                    return count;
                }

                if (!await _output.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }
                if (_output.Reader.TryRead(out var next) && next.Length > 0)
                {
                    _currentOutput = next;
                }
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    /// <summary>Writes raw terminal input bytes.</summary>
    public ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return data.IsEmpty
            ? ValueTask.CompletedTask
            : WriteFrameAsync(InputFrame, data, cancellationToken);
    }

    /// <summary>Changes the pseudoterminal's character-cell dimensions.</summary>
    public ValueTask ResizeAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ValidateSize(columns, rows);
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, checked((ushort)columns));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), checked((ushort)rows));
        return WriteFrameAsync(ResizeFrame, payload, cancellationToken);
    }

    private async ValueTask WriteFrameAsync(
        byte type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length > MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"A Unix PTY protocol frame cannot exceed {MaximumFrameBytes} bytes.");
        }

        var frame = GC.AllocateUninitializedArray<byte>(HeaderLength + payload.Length);
        frame[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1, 4), payload.Length);
        payload.Span.CopyTo(frame.AsSpan(HeaderLength));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _helperInput.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _helperInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PumpOutputAsync()
    {
        Exception? failure = null;
        try
        {
            var header = new byte[HeaderLength];
            while (await ReadExactlyOrEndAsync(_helperOutput, header, _lifetime.Token).ConfigureAwait(false))
            {
                var type = header[0];
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
                if (length < 0 || length > MaximumFrameBytes)
                {
                    throw new InvalidDataException($"The Unix PTY helper sent an invalid frame length: {length}.");
                }

                var payload = GC.AllocateUninitializedArray<byte>(length);
                if (length > 0 &&
                    !await ReadExactlyOrEndAsync(_helperOutput, payload, _lifetime.Token).ConfigureAwait(false))
                {
                    throw new EndOfStreamException("The Unix PTY helper ended in the middle of a frame.");
                }

                switch (type)
                {
                    case OutputFrame:
                        if (payload.Length > 0)
                        {
                            await _output.Writer.WriteAsync(payload, _lifetime.Token).ConfigureAwait(false);
                        }
                        break;

                    case ExitFrame when payload.Length == 8:
                        var exitCode = BinaryPrimitives.ReadInt32LittleEndian(payload);
                        var signal = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4));
                        _completion.TrySetResult(new PtyExitStatus(
                            exitCode,
                            signal == 0 ? null : signal));
                        break;

                    case ErrorFrame:
                        var message = System.Text.Encoding.UTF8.GetString(payload);
                        var exception = new InvalidOperationException(
                            string.IsNullOrWhiteSpace(message)
                                ? "The Unix PTY helper reported an error."
                                : message);
                        _ready.TrySetException(exception);
                        _completion.TrySetException(exception);
                        break;

                    case ReadyFrame when payload.Length == 4:
                        var processId = BinaryPrimitives.ReadInt32LittleEndian(payload);
                        if (processId <= 0)
                        {
                            throw new InvalidDataException("The Unix PTY helper sent an invalid process ID.");
                        }
                        _ready.TrySetResult(processId);
                        break;

                    default:
                        throw new InvalidDataException(
                            $"The Unix PTY helper sent an unknown frame type or payload: {type}/{length}.");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            _ready.TrySetException(exception);
            _completion.TrySetException(exception);
        }
        finally
        {
            _output.Writer.TryComplete(failure);
        }
    }

    private async Task ObserveHelperAsync()
    {
        try
        {
            await _helper.WaitForExitAsync().ConfigureAwait(false);
            await _pumpTask.ConfigureAwait(false);
            var stderr = await _stderrTask.ConfigureAwait(false);
            if (!_ready.Task.IsCompleted)
            {
                _ready.TrySetException(new InvalidOperationException(HelperFailureMessage(stderr)));
            }
            if (!_completion.Task.IsCompleted)
            {
                _completion.TrySetException(new InvalidOperationException(HelperFailureMessage(stderr)));
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            _completion.TrySetException(exception);
        }
    }

    private string HelperFailureMessage(string stderr)
    {
        var detail = stderr.Trim();
        return detail.Length == 0
            ? $"The Unix PTY helper exited unexpectedly with code {_helper.ExitCode}."
            : $"The Unix PTY helper exited unexpectedly with code {_helper.ExitCode}: {detail}";
    }

    private static async Task<bool> ReadExactlyOrEndAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (offset == 0)
                {
                    return false;
                }
                throw new EndOfStreamException("The Unix PTY helper ended in the middle of a frame.");
            }
            offset += count;
        }
        return true;
    }

    private static void Validate(PtyStartInfo startInfo)
    {
        if (string.IsNullOrWhiteSpace(startInfo.FileName))
        {
            throw new ArgumentException("A PTY executable is required.", nameof(startInfo));
        }
        if (string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ||
            !Directory.Exists(startInfo.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The PTY working directory does not exist: {startInfo.WorkingDirectory}");
        }
        ValidateSize(startInfo.Columns, startInfo.Rows);
        if (startInfo.Arguments.Any(argument => argument is null))
        {
            throw new ArgumentException("PTY arguments cannot contain null values.", nameof(startInfo));
        }
        foreach (var name in startInfo.Environment.Keys)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('='))
            {
                throw new ArgumentException($"Invalid environment variable name: '{name}'.", nameof(startInfo));
            }
        }
    }

    private static void ValidateSize(int columns, int rows)
    {
        if (columns is < 2 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "Columns must be between 2 and 65535.");
        }
        if (rows is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), "Rows must be between 1 and 65535.");
        }
    }

    private static string ResolveHelperPath(string? configuredPath)
    {
        var candidates = new[]
        {
            configuredPath,
            Path.Combine(AppContext.BaseDirectory, "kterm-pty-helper"),
            Path.Combine(Path.GetDirectoryName(typeof(UnixPtyProcess).Assembly.Location) ?? string.Empty,
                "kterm-pty-helper")
        };
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException(
            "The native kterm-pty-helper was not found next to the application. " +
            "Build the UnixPty project on macOS/Linux or set PtyStartInfo.HelperPath.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await WriteFrameAsync(CloseFrame, ReadOnlyMemory<byte>.Empty, closeTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }

        try
        {
            _helperInput.Dispose();
            await _completion.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is TimeoutException or IOException or InvalidOperationException)
        {
            TryKillHelper();
        }

        try
        {
            await _helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TryKillHelper();
            await _helper.WaitForExitAsync().ConfigureAwait(false);
        }

        _lifetime.Cancel();
        try
        {
            await Task.WhenAll(_pumpTask, _observeTask).ConfigureAwait(false);
        }
        catch
        {
            // Completion exposes protocol or helper failures to callers.
        }

        _helperOutput.Dispose();
        _helper.Dispose();
        _writeLock.Dispose();
        _readLock.Dispose();
        _lifetime.Dispose();
    }

    private void TryKillHelper()
    {
        try
        {
            if (!_helper.HasExited)
            {
                _helper.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
