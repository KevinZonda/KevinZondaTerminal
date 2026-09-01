using System.Text;
using KevinZonda.Terminal.UnixPty;

namespace KevinZonda.Terminal.AvaloniaDesktop;

internal sealed class UnixTerminalSession : IAsyncDisposable
{
    private const int BufferSize = 16 * 1024;
    private readonly UnixPtyProcess _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Decoder _decoder = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false).GetDecoder();
    private Task? _readTask;
    private Task? _waitTask;
    private int _disposed;

    private UnixTerminalSession(string id, string shellName, UnixPtyProcess process)
    {
        Id = id;
        ShellName = shellName;
        _process = process;
    }

    internal string Id { get; }

    internal string ShellName { get; }

    internal int ProcessId => _process.ProcessId;

    internal event Action<UnixTerminalSession, string>? OutputReceived;

    internal event Action<UnixTerminalSession, PtyExitStatus, string?>? Exited;

    internal void StartPumps()
    {
        if (_readTask is not null || _waitTask is not null)
        {
            throw new InvalidOperationException("The terminal session pumps have already started.");
        }

        _readTask = ReadLoopAsync();
        _waitTask = WaitForExitAsync();
    }

    internal static async Task<UnixTerminalSession> StartAsync(
        string workingDirectory,
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        var shell = ResolveShell();
        var process = await UnixPtyProcess.StartAsync(new PtyStartInfo
        {
            FileName = shell,
            Arguments = ["-l"],
            WorkingDirectory = workingDirectory,
            Columns = Math.Clamp(columns, 2, ushort.MaxValue),
            Rows = Math.Clamp(rows, 1, ushort.MaxValue),
            Environment = new Dictionary<string, string?>
            {
                ["TERM"] = "xterm-256color",
                ["COLORTERM"] = "truecolor",
                ["TERM_PROGRAM"] = "KevinZondaTerminal"
            }
        }, cancellationToken).ConfigureAwait(false);

        return new UnixTerminalSession(
            Guid.NewGuid().ToString("N"),
            Path.GetFileName(shell),
            process);
    }

    internal ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default) =>
        _process.WriteAsync(data, cancellationToken);

    internal ValueTask WriteAsync(
        string data,
        CancellationToken cancellationToken = default) =>
        WriteAsync(Encoding.UTF8.GetBytes(data), cancellationToken);

    internal ValueTask ResizeAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken = default) =>
        _process.ResizeAsync(
            Math.Clamp(columns, 2, ushort.MaxValue),
            Math.Clamp(rows, 1, ushort.MaxValue),
            cancellationToken);

    private async Task ReadLoopAsync()
    {
        var bytes = new byte[BufferSize];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(BufferSize)];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var count = await _process.ReadAsync(bytes, _lifetime.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var charCount = _decoder.GetChars(bytes.AsSpan(0, count), chars, flush: false);
                if (charCount > 0)
                {
                    OutputReceived?.Invoke(this, new string(chars, 0, charCount));
                }
            }

            var remaining = _decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
            if (remaining > 0)
            {
                OutputReceived?.Invoke(this, new string(chars, 0, remaining));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task WaitForExitAsync()
    {
        try
        {
            var status = await _process.Completion.ConfigureAwait(false);
            await (_readTask ?? throw new InvalidOperationException(
                "The terminal read pump has not started.")).ConfigureAwait(false);
            Exited?.Invoke(this, status, null);
        }
        catch (Exception exception) when (Volatile.Read(ref _disposed) == 0)
        {
            OutputReceived?.Invoke(this, $"\r\n[kterm: {exception.Message}]\r\n");
            Exited?.Invoke(this, new PtyExitStatus(1, null), exception.Message);
        }
    }

    private static string ResolveShell()
    {
        var configured = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var fallback = OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";
        if (File.Exists(fallback))
        {
            return fallback;
        }

        return "/bin/sh";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        await _process.DisposeAsync().ConfigureAwait(false);
        try
        {
            var tasks = new[] { _readTask, _waitTask }.OfType<Task>().ToArray();
            if (tasks.Length > 0)
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        _lifetime.Dispose();
    }
}
