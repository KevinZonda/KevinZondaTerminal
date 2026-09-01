using System.Collections.Concurrent;

namespace KevinZonda.Terminal.AvaloniaDesktop;

internal sealed class UnixTerminalSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, UnixTerminalSession> _sessions = new();
    private readonly string _workingDirectory;
    private int _disposed;

    internal UnixTerminalSessionManager(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    internal event Action<string, string>? OutputReceived;

    internal event Action<string, int, int?, string?>? SessionExited;

    internal async Task<UnixTerminalSessionInfo> CreateAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var session = await UnixTerminalSession.StartAsync(
            _workingDirectory,
            columns,
            rows,
            cancellationToken).ConfigureAwait(false);

        session.OutputReceived += HandleOutput;
        session.Exited += HandleExit;
        if (!_sessions.TryAdd(session.Id, session))
        {
            session.OutputReceived -= HandleOutput;
            session.Exited -= HandleExit;
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Unable to register the new terminal session.");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            await CloseAsync(session.Id).ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(UnixTerminalSessionManager));
        }

        session.StartPumps();

        return new UnixTerminalSessionInfo(session.Id, session.ShellName, session.ProcessId);
    }

    internal ValueTask WriteAsync(
        string sessionId,
        string data,
        CancellationToken cancellationToken = default) =>
        Get(sessionId).WriteAsync(data, cancellationToken);

    internal ValueTask WriteAsync(
        string sessionId,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default) =>
        Get(sessionId).WriteAsync(data, cancellationToken);

    internal ValueTask ResizeAsync(
        string sessionId,
        int columns,
        int rows,
        CancellationToken cancellationToken = default) =>
        Get(sessionId).ResizeAsync(columns, rows, cancellationToken);

    internal IReadOnlyList<int> GetSessionProcessIds() =>
        _sessions.Values.Select(session => session.ProcessId).ToArray();

    internal async Task CloseAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.OutputReceived -= HandleOutput;
            session.Exited -= HandleExit;
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private UnixTerminalSession Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw new KeyNotFoundException($"Terminal session '{sessionId}' does not exist.");

    private void HandleOutput(UnixTerminalSession session, string data) =>
        OutputReceived?.Invoke(session.Id, data);

    private void HandleExit(
        UnixTerminalSession session,
        KevinZonda.Terminal.UnixPty.PtyExitStatus status,
        string? failure) =>
        SessionExited?.Invoke(session.Id, status.ExitCode, status.Signal, failure);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var sessions = _sessions.ToArray();
        _sessions.Clear();
        await Task.WhenAll(sessions.Select(async pair =>
        {
            pair.Value.OutputReceived -= HandleOutput;
            pair.Value.Exited -= HandleExit;
            await pair.Value.DisposeAsync().ConfigureAwait(false);
        })).ConfigureAwait(false);
    }
}

internal sealed record UnixTerminalSessionInfo(string Id, string ShellName, int ProcessId);
