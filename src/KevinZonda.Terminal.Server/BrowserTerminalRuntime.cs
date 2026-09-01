using System.Text;
using KevinZonda.AgentUsageMonitor;
using KevinZonda.Terminal.Configuration;
using KevinZonda.Terminal.ConPty;
using KevinZonda.SystemMetrics;
using KevinZonda.Terminal.Terminal;
using KevinZonda.Terminal.WebBridgeProtocol;
using static KevinZonda.Terminal.WebBridgeProtocol.BridgePayloadReader;

namespace KevinZonda.Terminal.Server;

internal sealed class BrowserTerminalRuntime : IAsyncDisposable
{
    private const long MaximumBufferedOutputBytes = 4L * 1024 * 1024;

    private readonly object _sync = new();
    private readonly TerminalSessionManager _sessions;
    private readonly IAgentUsageMonitorService _agentUsage;
    private readonly ISystemMetricsService _systemMetrics;
    private readonly Dictionary<string, SessionRuntimeState> _sessionStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<SessionRuntimeState>> _createOperations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closeOperations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closedSessionIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringBuilder> _pendingOutput = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalExitStatus> _pendingExits = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _createdAtUtc = DateTimeOffset.UtcNow;
    private readonly TimeSpan _runtimeRetention;
    private AppSettings _settings;
    private IBrowserTerminalClient? _client;
    private DateTimeOffset? _lastConnectedAtUtc;
    private DateTimeOffset? _lastDisconnectedAtUtc;
    private long _epoch;
    private long _idleVersion;
    private long _bufferedOutputBytes;
    private bool _disposeStarted;
    private int _disposeInvoked;
    private int _closeAsReplaced;

    internal BrowserTerminalRuntime(string id, AppSettings settings, ServerOptions options)
    {
        Id = id;
        _settings = settings;
        _runtimeRetention = options.RuntimeRetention;
        _sessions = new TerminalSessionManager(
            settings,
            options.StartingDirectory,
            ConPtyTerminalSessionFactory.Instance);
        _agentUsage = new AgentUsageMonitorService(
            _sessions.GetSessionProcessIds,
            CreateAgentUsageOptions(settings));
        _systemMetrics = new SystemMetricsService();

        _sessions.OutputReceived += HandleOutput;
        _sessions.SessionExited += HandleExit;
        _agentUsage.StatusChanged += HandleAgentUsage;
        _systemMetrics.StatusChanged += HandleSystemMetrics;
        _sessions.Prewarm(80, 24);
        _agentUsage.Start();
        _systemMetrics.Start();
    }

    private static AgentUsageMonitorOptions CreateAgentUsageOptions(AppSettings settings) => new()
    {
        AutoRenewKimiToken = settings.Indicators.AutoRenewKimiToken
    };

    internal string Id { get; }

    internal long Attach(
        IBrowserTerminalClient client,
        string? requestId,
        IReadOnlyDictionary<string, BrowserSessionResumeState> resumeStates)
    {
        IBrowserTerminalClient? previousClient;
        long epoch;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            previousClient = _client;
            _client = client;
            _lastConnectedAtUtc = DateTimeOffset.UtcNow;
            _lastDisconnectedAtUtc = null;
            epoch = ++_epoch;
            _idleVersion++;

            foreach (var (sessionId, resumeState) in resumeStates)
            {
                if (_sessionStates.TryGetValue(sessionId, out var state))
                {
                    AcknowledgeCheckpointLocked(state, resumeState.CheckpointOutputSeq);
                }
            }

            client.TryPost(BridgeMessageTypes.RuntimeAttached, requestId, payload: new
            {
                runtimeId = Id,
                epoch,
                sessions = _sessionStates.Values
                    .Where(state => state.Announced)
                    .Select(state => new
                    {
                        sessionId = state.SessionId,
                        shellName = state.ShellName,
                        processId = state.ProcessId,
                        inputAck = state.LastInputSeq,
                        latestOutputSeq = state.NextOutputSeq - 1,
                        checkpointOutputSeq = state.LastCheckpointSeq,
                        cols = state.Columns,
                        rows = state.Rows,
                        exited = state.ExitStatus is not null,
                        exitCode = state.ExitStatus?.ExitCode ?? 0,
                        failure = state.ExitStatus?.Failure
                    })
                    .ToArray()
            });

            foreach (var state in _sessionStates.Values.Where(state => state.Announced))
            {
                var lastApplied = resumeStates.TryGetValue(state.SessionId, out var resumeState)
                    ? resumeState.LastAppliedOutputSeq
                    : state.LastCheckpointSeq;
                ReplaySessionLocked(client, state, lastApplied);
            }

            client.TryPost(
                BridgeMessageTypes.AgentUsageChanged,
                payload: new { agentUsage = _agentUsage.Current });
            client.TryPost(
                BridgeMessageTypes.SystemMetricsChanged,
                payload: new { systemMetrics = _systemMetrics.Current });
        }

        if (previousClient is not null && !ReferenceEquals(previousClient, client))
        {
            previousClient.Supersede(replaced: true);
        }
        return epoch;
    }

    internal long? Detach(long epoch)
    {
        lock (_sync)
        {
            if (_disposeStarted || _client is null || epoch != _epoch)
            {
                return null;
            }

            _client = null;
            _lastDisconnectedAtUtc = DateTimeOffset.UtcNow;
            return ++_idleVersion;
        }
    }

    internal DashboardRuntimeSnapshot GetDashboardSnapshot()
    {
        lock (_sync)
        {
            var sessions = _sessionStates.Values
                .Where(state => state.Announced)
                .OrderBy(state => state.SessionId, StringComparer.Ordinal)
                .Select(state => new DashboardSessionSnapshot(
                    state.SessionId,
                    state.ShellName,
                    state.ProcessId,
                    state.Columns,
                    state.Rows,
                    state.ExitStatus is not null,
                    state.ExitStatus?.ExitCode,
                    state.ExitStatus?.Failure,
                    state.Output.Sum(output => (long)output.ByteCount)))
                .ToArray();
            return new DashboardRuntimeSnapshot(
                Id,
                _createdAtUtc,
                _client is not null,
                _lastConnectedAtUtc,
                _lastDisconnectedAtUtc,
                _client is null && _lastDisconnectedAtUtc is { } disconnectedAt
                    ? disconnectedAt + _runtimeRetention
                    : null,
                _bufferedOutputBytes,
                sessions);
        }
    }

    internal async Task<bool> CloseSessionFromDashboardAsync(string sessionId)
    {
        IBrowserTerminalClient? client;
        long finalOutputSequence;
        lock (_sync)
        {
            if (_disposeStarted || !_sessionStates.TryGetValue(sessionId, out var state))
            {
                return false;
            }
            finalOutputSequence = state.NextOutputSeq - 1;
            RemoveSessionLocked(sessionId);
            client = _client;
        }

        client?.TryPost(BridgeMessageTypes.SessionExited, sessionId: sessionId, payload: new
        {
            exitCode = 0,
            failure = "Session closed from the server dashboard.",
            finalOutputSeq = finalOutputSequence
        });
        await _sessions.CloseAsync(sessionId).ConfigureAwait(false);
        return true;
    }

    internal ValueTask CloseFromDashboardAsync()
    {
        Volatile.Write(ref _closeAsReplaced, 1);
        return DisposeAsync();
    }

    internal bool TryBeginExpiration(long idleVersion)
    {
        lock (_sync)
        {
            if (_disposeStarted || _client is not null || _idleVersion != idleVersion)
            {
                return false;
            }

            _disposeStarted = true;
            Monitor.PulseAll(_sync);
            return true;
        }
    }

    internal async Task HandleMessageAsync(
        IBrowserTerminalClient source,
        BridgeMessage message,
        CancellationToken cancellationToken)
    {
        EnsureActive(source);
        switch (message.Type)
        {
            case BridgeMessageTypes.AppReady:
                source.TryPost(BridgeMessageTypes.AppInitialState, message.RequestId, payload: new
                {
                    application = "KevinZonda Terminal Server",
                    version = typeof(BrowserTerminalRuntime).Assembly.GetName().Version?.ToString(),
                    runtimeId = Id,
                    settings = _settings,
                    agentUsage = _agentUsage.Current,
                    systemMetrics = _systemMetrics.Current
                });
                break;

            case BridgeMessageTypes.SessionCreate:
                await CreateSessionAsync(source, message).ConfigureAwait(false);
                break;

            case BridgeMessageTypes.SessionInput:
                await WriteInputAsync(
                    RequireSessionId(message),
                    GetInt64(message.Payload, "inputSeq", 0),
                    GetString(message.Payload, "data"),
                    null).ConfigureAwait(false);
                break;

            case BridgeMessageTypes.SessionBinaryInput:
                await WriteInputAsync(
                    RequireSessionId(message),
                    GetInt64(message.Payload, "inputSeq", 0),
                    null,
                    Convert.FromBase64String(GetString(message.Payload, "data"))).ConfigureAwait(false);
                break;

            case BridgeMessageTypes.SessionOutputAck:
                // Render acknowledgements let a live page skip duplicate output on
                // transient reconnects. Journal data is retained until the browser
                // has durably committed an xterm checkpoint.
                break;

            case BridgeMessageTypes.SessionCheckpointAck:
                AcknowledgeCheckpoint(
                    RequireSessionId(message),
                    GetInt64(message.Payload, "outputSeq", 0));
                break;

            case BridgeMessageTypes.SessionResize:
                ResizeSession(
                    RequireSessionId(message),
                    GetInt32(message.Payload, "cols", 80),
                    GetInt32(message.Payload, "rows", 24));
                break;

            case BridgeMessageTypes.SessionClose:
                await CloseSessionFromClientAsync(source, message).ConfigureAwait(false);
                break;

            case BridgeMessageTypes.SettingsFontSize:
                _settings = AppSettings.Normalize(
                    _settings with
                    {
                        Font = _settings.Font with
                        {
                            Size = GetDouble(message.Payload, "size", AppSettings.DefaultFontSize)
                        }
                    });
                source.TryPost(
                    BridgeMessageTypes.SettingsSaved,
                    message.RequestId,
                    payload: new { settings = _settings });
                break;

            case BridgeMessageTypes.AgentUsageRefresh:
                var provider = GetString(message.Payload, "provider") switch
                {
                    "codex" => UsageProvider.Codex,
                    "kimi" => UsageProvider.KimiCode,
                    _ => throw new InvalidDataException("Unsupported usage provider.")
                };
                source.TryPost(BridgeMessageTypes.AgentUsageRefreshResult, message.RequestId, payload: new
                {
                    started = _agentUsage.RequestRefresh(provider)
                });
                break;

            default:
                throw new InvalidDataException($"Unknown bridge message type '{message.Type}'.");
        }
    }

    private async Task CreateSessionAsync(IBrowserTerminalClient source, BridgeMessage message)
    {
        var operationId = GetString(message.Payload, "operationId");
        if (string.IsNullOrWhiteSpace(operationId))
        {
            operationId = message.RequestId;
        }
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new InvalidDataException("Session creation requires an operation ID.");
        }

        Task<SessionRuntimeState> createTask;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            if (!_createOperations.TryGetValue(operationId, out createTask!))
            {
                createTask = CreateSessionCoreAsync(
                    GetInt32(message.Payload, "cols", 80),
                    GetInt32(message.Payload, "rows", 24));
                _createOperations.Add(operationId, createTask);
            }
        }

        SessionRuntimeState state;
        try
        {
            state = await createTask.ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                if (_createOperations.TryGetValue(operationId, out var current) && current == createTask)
                {
                    _createOperations.Remove(operationId);
                }
            }
            throw;
        }

        lock (_sync)
        {
            source.TryPost(BridgeMessageTypes.SessionCreated, message.RequestId, state.SessionId, new
            {
                shellName = state.ShellName,
                processId = state.ProcessId,
                inputAck = state.LastInputSeq,
                latestOutputSeq = state.NextOutputSeq - 1
            });
            state.Announced = true;
            ReplaySessionLocked(source, state, state.LastCheckpointSeq);
        }
    }

    private async Task<SessionRuntimeState> CreateSessionCoreAsync(int columns, int rows)
    {
        var session = await _sessions.CreateAsync(columns, rows).ConfigureAwait(false);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            var state = new SessionRuntimeState(
                session.Id,
                session.ShellName,
                session.ProcessId,
                columns,
                rows);
            _sessionStates.Add(session.Id, state);

            if (_pendingOutput.Remove(session.Id, out var pendingOutput) && pendingOutput.Length > 0)
            {
                AppendOutputLocked(state, pendingOutput.ToString());
            }
            if (_pendingExits.Remove(session.Id, out var pendingExit))
            {
                state.ExitStatus = pendingExit;
            }
            return state;
        }
    }

    private async Task WriteInputAsync(
        string sessionId,
        long inputSeq,
        string? text,
        byte[]? bytes)
    {
        SessionRuntimeState state;
        lock (_sync)
        {
            state = GetSessionStateLocked(sessionId);
        }

        if (inputSeq <= 0)
        {
            if (bytes is not null)
            {
                await _sessions.WriteAsync(sessionId, bytes).ConfigureAwait(false);
            }
            else
            {
                await _sessions.WriteAsync(sessionId, text ?? string.Empty).ConfigureAwait(false);
            }
            return;
        }

        await state.InputGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (inputSeq <= state.LastInputSeq)
                {
                    PostInputAckLocked(state);
                    return;
                }
                if (inputSeq != state.LastInputSeq + 1)
                {
                    _client?.TryPost(BridgeMessageTypes.SessionInputNack, sessionId: sessionId, payload: new
                    {
                        expectedInputSeq = state.LastInputSeq + 1
                    });
                    return;
                }
            }

            if (bytes is not null)
            {
                await _sessions.WriteAsync(sessionId, bytes).ConfigureAwait(false);
            }
            else
            {
                await _sessions.WriteAsync(sessionId, text ?? string.Empty).ConfigureAwait(false);
            }

            lock (_sync)
            {
                state.LastInputSeq = inputSeq;
                PostInputAckLocked(state);
            }
        }
        finally
        {
            state.InputGate.Release();
        }
    }

    private void PostInputAckLocked(SessionRuntimeState state) =>
        _client?.TryPost(BridgeMessageTypes.SessionInputAck, sessionId: state.SessionId, payload: new
        {
            inputSeq = state.LastInputSeq
        });

    private void AcknowledgeCheckpoint(string sessionId, long outputSeq)
    {
        lock (_sync)
        {
            if (_sessionStates.TryGetValue(sessionId, out var state))
            {
                AcknowledgeCheckpointLocked(state, outputSeq);
            }
        }
    }

    private void AcknowledgeCheckpointLocked(SessionRuntimeState state, long outputSeq)
    {
        var latest = state.NextOutputSeq - 1;
        var acknowledged = Math.Min(Math.Max(outputSeq, state.LastCheckpointSeq), latest);
        if (acknowledged == state.LastCheckpointSeq)
        {
            return;
        }

        state.LastCheckpointSeq = acknowledged;
        while (state.Output.First is { } first && first.Value.Sequence <= acknowledged)
        {
            state.Output.RemoveFirst();
            _bufferedOutputBytes -= first.Value.ByteCount;
        }
        Monitor.PulseAll(_sync);
    }

    private void ResizeSession(string sessionId, int columns, int rows)
    {
        _sessions.Resize(sessionId, columns, rows);
        lock (_sync)
        {
            var state = GetSessionStateLocked(sessionId);
            state.Columns = columns;
            state.Rows = rows;
        }
    }

    private async Task CloseSessionFromClientAsync(IBrowserTerminalClient source, BridgeMessage message)
    {
        var sessionId = RequireSessionId(message);
        var operationId = GetString(message.Payload, "operationId");
        if (string.IsNullOrWhiteSpace(operationId))
        {
            operationId = message.RequestId ?? $"legacy:{sessionId}";
        }

        var shouldClose = false;
        lock (_sync)
        {
            if (_closeOperations.Add(operationId))
            {
                shouldClose = true;
                RemoveSessionLocked(sessionId);
            }
        }

        if (shouldClose)
        {
            await _sessions.CloseAsync(sessionId).ConfigureAwait(false);
        }
        source.TryPost(BridgeMessageTypes.SessionClosed, message.RequestId, sessionId, new { operationId });
    }

    private bool RemoveSessionLocked(string sessionId)
    {
        var removed = _sessionStates.Remove(sessionId, out var state);
        _closedSessionIds.Add(sessionId);
        if (state is not null)
        {
            foreach (var output in state.Output)
            {
                _bufferedOutputBytes -= output.ByteCount;
            }
        }
        _pendingOutput.Remove(sessionId);
        _pendingExits.Remove(sessionId);
        Monitor.PulseAll(_sync);
        return removed;
    }

    private void HandleOutput(string sessionId, string data)
    {
        lock (_sync)
        {
            if (!_sessionStates.TryGetValue(sessionId, out var state))
            {
                if (_closedSessionIds.Contains(sessionId))
                {
                    return;
                }
                if (!_pendingOutput.TryGetValue(sessionId, out var pending))
                {
                    pending = new StringBuilder();
                    _pendingOutput.Add(sessionId, pending);
                }
                pending.Append(data);
                return;
            }

            var byteCount = Encoding.UTF8.GetByteCount(data);
            while (!_disposeStarted && _sessionStates.ContainsKey(sessionId) &&
                   _bufferedOutputBytes + byteCount > MaximumBufferedOutputBytes)
            {
                Monitor.Wait(_sync);
            }
            if (_disposeStarted || !_sessionStates.ContainsKey(sessionId))
            {
                return;
            }

            var output = AppendOutputLocked(state, data);
            if (state.Announced)
            {
                PostOutputLocked(_client, state.SessionId, output);
            }
        }
    }

    private OutputRecord AppendOutputLocked(SessionRuntimeState state, string data)
    {
        var output = new OutputRecord(
            state.NextOutputSeq++,
            data,
            Encoding.UTF8.GetByteCount(data));
        state.Output.AddLast(output);
        _bufferedOutputBytes += output.ByteCount;
        return output;
    }

    private void HandleExit(string sessionId, TerminalExitStatus status)
    {
        lock (_sync)
        {
            if (!_sessionStates.TryGetValue(sessionId, out var state))
            {
                if (_closedSessionIds.Contains(sessionId))
                {
                    return;
                }
                _pendingExits[sessionId] = status;
                return;
            }

            state.ExitStatus = status;
            if (state.Announced)
            {
                PostExitLocked(_client, state);
            }
        }
    }

    private void ReplaySessionLocked(
        IBrowserTerminalClient? client,
        SessionRuntimeState state,
        long afterOutputSeq)
    {
        if (client is null)
        {
            return;
        }
        foreach (var output in state.Output)
        {
            if (output.Sequence > afterOutputSeq)
            {
                PostOutputLocked(client, state.SessionId, output);
            }
        }
        if (state.ExitStatus is not null)
        {
            PostExitLocked(client, state);
        }
    }

    private static void PostOutputLocked(
        IBrowserTerminalClient? client,
        string sessionId,
        OutputRecord output) =>
        client?.TryPost(BridgeMessageTypes.SessionOutput, sessionId: sessionId, payload: new
        {
            data = output.Data,
            outputSeq = output.Sequence
        });

    private static void PostExitLocked(IBrowserTerminalClient? client, SessionRuntimeState state)
    {
        var status = state.ExitStatus;
        if (status is null)
        {
            return;
        }
        client?.TryPost(BridgeMessageTypes.SessionExited, sessionId: state.SessionId, payload: new
        {
            exitCode = status.ExitCode,
            failure = status.Failure,
            finalOutputSeq = state.NextOutputSeq - 1
        });
    }

    private void HandleAgentUsage(AgentUsageStatus status)
    {
        lock (_sync)
        {
            _client?.TryPost(BridgeMessageTypes.AgentUsageChanged, payload: new { agentUsage = status });
        }
    }

    private void HandleSystemMetrics(SystemMetricsStatus status)
    {
        lock (_sync)
        {
            _client?.TryPost(BridgeMessageTypes.SystemMetricsChanged, payload: new { systemMetrics = status });
        }
    }

    private void EnsureActive(IBrowserTerminalClient source)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            if (!ReferenceEquals(_client, source))
            {
                throw new InvalidOperationException("This WebSocket lease has been superseded.");
            }
        }
    }

    private SessionRuntimeState GetSessionStateLocked(string sessionId)
    {
        if (!_sessionStates.TryGetValue(sessionId, out var state))
        {
            throw new KeyNotFoundException($"Terminal session '{sessionId}' does not exist.");
        }
        return state;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeInvoked, 1) != 0)
        {
            return;
        }

        IBrowserTerminalClient? client;
        Task[] creates;
        lock (_sync)
        {
            if (!_disposeStarted)
            {
                _disposeStarted = true;
            }
            client = _client;
            _client = null;
            creates = _createOperations.Values.ToArray();
            Monitor.PulseAll(_sync);
        }

        client?.Supersede(Volatile.Read(ref _closeAsReplaced) != 0);
        _sessions.OutputReceived -= HandleOutput;
        _sessions.SessionExited -= HandleExit;
        _agentUsage.StatusChanged -= HandleAgentUsage;
        _systemMetrics.StatusChanged -= HandleSystemMetrics;

        try
        {
            await Task.WhenAll(creates).ConfigureAwait(false);
        }
        catch
        {
            // Failed session starts have already released their native resources.
        }
        await _agentUsage.DisposeAsync().ConfigureAwait(false);
        await _systemMetrics.DisposeAsync().ConfigureAwait(false);
        await _sessions.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class SessionRuntimeState
    {
        internal SessionRuntimeState(
            string sessionId,
            string shellName,
            uint processId,
            int columns,
            int rows)
        {
            SessionId = sessionId;
            ShellName = shellName;
            ProcessId = processId;
            Columns = columns;
            Rows = rows;
        }

        internal string SessionId { get; }
        internal string ShellName { get; }
        internal uint ProcessId { get; }
        internal int Columns { get; set; }
        internal int Rows { get; set; }
        internal bool Announced { get; set; }
        internal long LastInputSeq { get; set; }
        internal long LastCheckpointSeq { get; set; }
        internal long NextOutputSeq { get; set; } = 1;
        internal LinkedList<OutputRecord> Output { get; } = [];
        internal TerminalExitStatus? ExitStatus { get; set; }
        internal SemaphoreSlim InputGate { get; } = new(1, 1);
    }

    private sealed record OutputRecord(long Sequence, string Data, int ByteCount);
}
