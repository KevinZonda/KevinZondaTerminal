using System.Collections.Concurrent;
using KevinZonda.Terminal.Configuration;

namespace KevinZonda.Terminal.Terminal;

internal sealed class TerminalSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ITerminalSession> _sessions = new();
    private readonly ITerminalSessionFactory _sessionFactory;
    private readonly object _prewarmLock = new();
    private readonly string _startingDirectory;
    private AppSettings _settings;
    private Task<ITerminalSession>? _prewarmedSession;
    private int _disposed;

    internal event Action<string, string>? OutputReceived;

    internal event Action<string, TerminalExitStatus>? SessionExited;

    internal TerminalSessionManager(
        AppSettings settings,
        string startingDirectory,
        ITerminalSessionFactory sessionFactory)
    {
        _settings = settings;
        _startingDirectory = startingDirectory;
        _sessionFactory = sessionFactory;
    }

    internal void Prewarm(int columns, int rows)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_prewarmLock)
        {
            if (_prewarmedSession is not null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var id = Guid.NewGuid().ToString("N");
            var settings = _settings;
            _prewarmedSession = _sessionFactory.StartAsync(
                id,
                columns,
                rows,
                ShellProfileCatalog.Resolve(settings.Shell),
                TerminalThemeCatalog.Find(settings.Theme.Name),
                _startingDirectory,
                settings.ConHost.EnhancedOpenConsole).AsTask();
        }
    }

    internal async Task<TerminalSessionInfo> CreateAsync(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var prewarmedSession = TakePrewarmedSession();
        var settings = GetSettings();
        var session = prewarmedSession is not null
            ? await prewarmedSession.ConfigureAwait(false)
            : await _sessionFactory.StartAsync(
                Guid.NewGuid().ToString("N"),
                columns,
                rows,
                ShellProfileCatalog.Resolve(settings.Shell),
                TerminalThemeCatalog.Find(settings.Theme.Name),
                _startingDirectory,
                settings.ConHost.EnhancedOpenConsole).ConfigureAwait(false);

        if (Volatile.Read(ref _disposed) != 0)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(TerminalSessionManager));
        }

        try
        {
            session.Resize(columns, rows);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

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
            throw new ObjectDisposedException(nameof(TerminalSessionManager));
        }

        session.StartPumps();

        return new TerminalSessionInfo(
            session.Id,
            session.ShellName,
            session.ProcessId);
    }

    internal async Task UpdateSettingsAsync(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Task<ITerminalSession>? previousPrewarm;
        lock (_prewarmLock)
        {
            if (_settings.Shell.HasSameLaunchConfiguration(settings.Shell) &&
                _settings.Theme == settings.Theme &&
                _settings.ConHost == settings.ConHost)
            {
                return;
            }

            _settings = settings;
            previousPrewarm = _prewarmedSession;
            _prewarmedSession = null;
        }

        if (previousPrewarm is not null)
        {
            await DisposePrewarmedSession(previousPrewarm).ConfigureAwait(false);
        }
    }

    private AppSettings GetSettings()
    {
        lock (_prewarmLock)
        {
            return _settings;
        }
    }

    private Task<ITerminalSession>? TakePrewarmedSession()
    {
        lock (_prewarmLock)
        {
            var session = _prewarmedSession;
            _prewarmedSession = null;
            return session;
        }
    }

    internal Task WriteAsync(string sessionId, string data) =>
        Get(sessionId).WriteAsync(data);

    internal Task WriteAsync(string sessionId, ReadOnlyMemory<byte> data) =>
        Get(sessionId).WriteAsync(data);

    internal void Resize(string sessionId, int columns, int rows) =>
        Get(sessionId).Resize(columns, rows);

    internal IReadOnlyList<int> GetSessionProcessIds() =>
        _sessions.Values
            .SelectMany(session => session.GetProcessIds())
            .Where(processId => processId <= int.MaxValue)
            .Select(processId => (int)processId)
            .Distinct()
            .ToArray();

    internal async Task CloseAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.OutputReceived -= HandleOutput;
            session.Exited -= HandleExit;
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ITerminalSession Get(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Terminal session '{sessionId}' does not exist.");
        }

        return session;
    }

    private void HandleOutput(ITerminalSession session, string data) =>
        OutputReceived?.Invoke(session.Id, data);

    private void HandleExit(ITerminalSession session, TerminalExitStatus status) =>
        SessionExited?.Invoke(session.Id, status);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var sessions = _sessions.ToArray();
        _sessions.Clear();
        var prewarmedSession = TakePrewarmedSession();

        var disposalTasks = sessions.Select(async pair =>
        {
            pair.Value.OutputReceived -= HandleOutput;
            pair.Value.Exited -= HandleExit;
            await pair.Value.DisposeAsync().ConfigureAwait(false);
        }).ToList();

        if (prewarmedSession is not null)
        {
            disposalTasks.Add(DisposePrewarmedSession(prewarmedSession));
        }

        await Task.WhenAll(disposalTasks).ConfigureAwait(false);
    }

    private static async Task DisposePrewarmedSession(Task<ITerminalSession> sessionTask)
    {
        try
        {
            var session = await sessionTask.ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Session factories release partially created resources before propagating a failure.
        }
    }
}

internal sealed record TerminalSessionInfo(string Id, string ShellName, uint ProcessId);
