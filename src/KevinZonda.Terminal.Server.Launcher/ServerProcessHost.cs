using System.Diagnostics;
using System.Text;

namespace KevinZonda.Terminal.Server.Launcher;

internal sealed class ServerProcessHost : IDisposable
{
    private const string ShutdownCommand = "shutdown";
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

    internal Task StartAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run is not null)
            {
                return Task.CompletedTask;
            }
        }

        if (!File.Exists(_serverExecutable))
        {
            throw new FileNotFoundException(
                "kterm-server.exe was not found next to the Launcher.",
                _serverExecutable);
        }

        var startInfo = new ProcessStartInfo
        {
            CreateNoWindow = true,
            FileName = _serverExecutable,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(_serverExecutable) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("--launcher-control");
        foreach (var argument in _serverArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process
        {
            EnableRaisingEvents = false,
            StartInfo = startInfo
        };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                _logs.Add(LauncherLogSource.StandardOutput, eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                _logs.Add(LauncherLogSource.StandardError, eventArgs.Data);
            }
        };

        var job = ServerProcessJob.Create();
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("kterm-server did not start.");
            }
            job.Assign(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var run = new ServerRun(process, job);
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
            _ = ObserveExitAsync(run);
            return Task.CompletedTask;
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
            await run.Process.StandardInput.WriteLineAsync(ShutdownCommand).ConfigureAwait(false);
            await run.Process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
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
            run.Job.Dispose();
            run.Process.Dispose();
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
        run.Process.Dispose();
        run.Job.Dispose();

        if (isCurrentRun)
        {
            StateChanged?.Invoke();
            if (!stoppedByRequest)
            {
                UnexpectedExit?.Invoke(exitCode);
            }
        }
    }

    private sealed class ServerRun(Process process, ServerProcessJob job)
    {
        internal Process Process { get; } = process;
        internal ServerProcessJob Job { get; } = job;
        internal TaskCompletionSource<int> Exited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool StopRequested { get; set; }
    }
}
