using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace KevinZonda.Terminal.Server.Launcher;

internal sealed class ServerProcessHost : IDisposable
{
    private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ForcedShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private readonly string _serverExecutable;
    private readonly string[] _serverArguments;
    private readonly LauncherLogBuffer _logs;
    private ServerRun? _run;
    private bool _disposed;

    internal ServerProcessHost(
        string serverExecutable,
        string[] serverArguments,
        LauncherLogBuffer logs)
    {
        _serverExecutable = serverExecutable;
        _serverArguments = serverArguments;
        _logs = logs;
    }

    internal event Action? StateChanged;
    internal event Action<int>? UnexpectedExit;

    internal bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _run is not null;
            }
        }
    }

    internal async Task StartAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run is not null)
            {
                return;
            }
        }

        if (!File.Exists(_serverExecutable))
        {
            throw new FileNotFoundException(
                "kterm-server.exe was not found next to the Launcher.",
                _serverExecutable);
        }

        var pipeName = $"kterm-server-launcher-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var startInfo = new ProcessStartInfo
        {
            CreateNoWindow = false,
            FileName = _serverExecutable,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(_serverExecutable) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("--launcher-pipe");
        startInfo.ArgumentList.Add(pipeName);
        foreach (var argument in _serverArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process
        {
            EnableRaisingEvents = false,
            StartInfo = startInfo
        };
        var job = ServerProcessJob.Create();
        try
        {
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var connection = pipe.WaitForConnectionAsync(connectTimeout.Token);
            if (!process.Start())
            {
                throw new InvalidOperationException("kterm-server did not start.");
            }
            job.Assign(process);

            var processExit = process.WaitForExitAsync();
            if (await Task.WhenAny(connection, processExit).ConfigureAwait(false) == processExit)
            {
                throw new InvalidOperationException(
                    $"kterm-server exited with code {process.ExitCode} before connecting to the Launcher.");
            }
            await connection.ConfigureAwait(false);

            var run = new ServerRun(process, job, pipe);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_run is not null)
                {
                    throw new InvalidOperationException("kterm-server is already running.");
                }
                _run = run;
            }

            _logs.Add(
                LauncherLogSource.System,
                $"Started kterm-server (PID {process.Id}).");
            StateChanged?.Invoke();
            _ = ObservePipeAsync(run);
            _ = ObserveExitAsync(run);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            process.Dispose();
            job.Dispose();
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task StopAsync()
    {
        ServerRun? run;
        lock (_gate)
        {
            run = _run;
            if (run is null)
            {
                return;
            }
            run.StopRequested = true;
        }

        _logs.Add(LauncherLogSource.System, "Requesting graceful Server shutdown.");
        try
        {
            await run.SendShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logs.Add(
                LauncherLogSource.System,
                $"Unable to send the shutdown command: {exception.Message}");
        }

        if (await Task.WhenAny(
                run.Exited.Task,
                Task.Delay(GracefulShutdownTimeout)).ConfigureAwait(false) != run.Exited.Task)
        {
            _logs.Add(
                LauncherLogSource.System,
                "Server did not stop within 10 seconds; terminating its process job.");
            run.Job.Terminate(1);
            if (await Task.WhenAny(
                    run.Exited.Task,
                    Task.Delay(ForcedShutdownTimeout)).ConfigureAwait(false) != run.Exited.Task)
            {
                try
                {
                    run.Process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        await run.Exited.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        ServerRun? run;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            run = _run;
            if (run is not null)
            {
                run.StopRequested = true;
            }
            _run = null;
        }

        if (run is not null)
        {
            run.Dispose();
        }
    }

    private async Task ObserveExitAsync(ServerRun run)
    {
        int exitCode;
        try
        {
            await run.Process.WaitForExitAsync().ConfigureAwait(false);
            run.Process.WaitForExit();
            exitCode = run.Process.ExitCode;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            exitCode = -1;
        }

        bool isCurrentRun;
        bool stoppedByRequest;
        lock (_gate)
        {
            isCurrentRun = ReferenceEquals(_run, run);
            stoppedByRequest = run.StopRequested;
            if (isCurrentRun)
            {
                _run = null;
            }
        }

        _logs.Add(
            LauncherLogSource.System,
            $"kterm-server exited with code {exitCode}.");
        run.Exited.TrySetResult(exitCode);
        run.Dispose();

        if (isCurrentRun)
        {
            StateChanged?.Invoke();
            if (!stoppedByRequest)
            {
                UnexpectedExit?.Invoke(exitCode);
            }
        }
    }

    private async Task ObservePipeAsync(ServerRun run)
    {
        try
        {
            while (true)
            {
                var line = await run.Reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                using var message = JsonDocument.Parse(line);
                if (!message.RootElement.TryGetProperty("type", out var type) ||
                    !message.RootElement.TryGetProperty("text", out var text))
                {
                    continue;
                }
                var source = type.GetString() switch
                {
                    "stdout" => LauncherLogSource.StandardOutput,
                    "stderr" => LauncherLogSource.StandardError,
                    _ => LauncherLogSource.System
                };
                _logs.Add(source, text.GetString() ?? string.Empty);
            }
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or JsonException)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_run, run) || run.StopRequested)
                {
                    return;
                }
            }
            _logs.Add(LauncherLogSource.System, $"Launcher log pipe failed: {exception.Message}");
        }
    }

    private sealed class ServerRun : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _disposed;

        internal ServerRun(Process process, ServerProcessJob job, NamedPipeServerStream pipe)
        {
            Process = process;
            Job = job;
            Pipe = pipe;
            Reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            Writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true)
            {
                AutoFlush = true
            };
        }

        internal Process Process { get; }
        internal ServerProcessJob Job { get; }
        internal NamedPipeServerStream Pipe { get; }
        internal StreamReader Reader { get; }
        internal StreamWriter Writer { get; }
        internal TaskCompletionSource<int> Exited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool StopRequested { get; set; }

        internal async Task SendShutdownAsync()
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var message = JsonSerializer.Serialize(new { type = "shutdown" });
                await Writer.WriteLineAsync(message).ConfigureAwait(false);
                await Writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            Reader.Dispose();
            Writer.Dispose();
            Pipe.Dispose();
            _writeLock.Dispose();
            Job.Dispose();
            Process.Dispose();
        }
    }
}
