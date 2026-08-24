using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using KevinZonda.AgentUsageMonitor;
using KevinZonda.Terminal.Configuration;
using KevinZonda.Terminal.Messaging;
using KevinZonda.Terminal.Monitoring;
using KevinZonda.Terminal.Terminal;
using KevinZonda.Terminal.Usage;

namespace KevinZonda.Terminal.Server;

internal sealed class BrowserTerminalConnection
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebSocket _socket;
    private readonly SettingsStore _settingsStore;
    private readonly TerminalSessionManager _sessions;
    private readonly AgentUsageStatusService _agentUsage;
    private readonly SystemMetricsService _systemMetrics;
    private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _sessionEventLock = new();
    private readonly HashSet<string> _announcedSessions = [];
    private readonly Dictionary<string, StringBuilder> _pendingOutput = [];
    private readonly Dictionary<string, TerminalExitStatus> _pendingExits = [];
    private AppSettings _settings;

    internal BrowserTerminalConnection(
        WebSocket socket,
        SettingsStore settingsStore,
        ServerOptions options)
    {
        _socket = socket;
        _settingsStore = settingsStore;
        _settings = settingsStore.Load();
        _sessions = new TerminalSessionManager(_settings, options.StartingDirectory);
        _agentUsage = new AgentUsageStatusService(_sessions, _settings);
        _systemMetrics = new SystemMetricsService();
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        _sessions.OutputReceived += HandleOutput;
        _sessions.SessionExited += HandleExit;
        _agentUsage.StatusChanged += HandleAgentUsage;
        _systemMetrics.StatusChanged += HandleSystemMetrics;
        _sessions.Prewarm(80, 24);
        _agentUsage.Start();
        _systemMetrics.Start();

        var sendTask = SendLoopAsync(cancellationToken);
        try
        {
            await ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _sessions.OutputReceived -= HandleOutput;
            _sessions.SessionExited -= HandleExit;
            _agentUsage.StatusChanged -= HandleAgentUsage;
            _systemMetrics.StatusChanged -= HandleSystemMetrics;

            await _agentUsage.DisposeAsync().ConfigureAwait(false);
            await _systemMetrics.DisposeAsync().ConfigureAwait(false);
            await _sessions.DisposeAsync().ConfigureAwait(false);
            _outbound.Writer.TryComplete();
            await sendTask.ConfigureAwait(false);

            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await _socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "KTerm connection closed.",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (_socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("Only text WebSocket messages are supported.");
                }
                if (message.Length + result.Count > MaximumMessageBytes)
                {
                    throw new InvalidDataException("The WebSocket message is too large.");
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            BridgeMessage? bridgeMessage = null;
            try
            {
                bridgeMessage = JsonSerializer.Deserialize<BridgeMessage>(message.GetBuffer().AsSpan(0, (int)message.Length), JsonOptions);
                if (bridgeMessage is null || bridgeMessage.Version != 1 || string.IsNullOrWhiteSpace(bridgeMessage.Type))
                {
                    throw new InvalidDataException("Unsupported bridge message.");
                }
                await HandleMessageAsync(bridgeMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Post("session.error", bridgeMessage?.RequestId, bridgeMessage?.SessionId, new
                {
                    message = exception.Message
                });
            }
        }
    }

    private async Task HandleMessageAsync(BridgeMessage message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case "app.ready":
                Post("app.initialState", message.RequestId, payload: new
                {
                    application = "KevinZonda Terminal Server",
                    version = typeof(BrowserTerminalConnection).Assembly.GetName().Version?.ToString(),
                    settings = _settings,
                    agentUsage = _agentUsage.Current,
                    systemMetrics = _systemMetrics.Current
                });
                break;

            case "session.create":
                var session = await _sessions.CreateAsync(
                    GetInt32(message.Payload, "cols", 80),
                    GetInt32(message.Payload, "rows", 24)).ConfigureAwait(false);
                lock (_sessionEventLock)
                {
                    Post("session.created", message.RequestId, session.Id, new
                    {
                        shellName = session.ShellName,
                        processId = session.ProcessId
                    });
                    _announcedSessions.Add(session.Id);
                    if (_pendingOutput.Remove(session.Id, out var pendingOutput) && pendingOutput.Length > 0)
                    {
                        Post("session.output", sessionId: session.Id, payload: new
                        {
                            data = pendingOutput.ToString()
                        });
                    }
                    if (_pendingExits.Remove(session.Id, out var pendingExit))
                    {
                        PostExit(session.Id, pendingExit);
                    }
                }
                break;

            case "session.input":
                await _sessions.WriteAsync(
                    RequireSessionId(message),
                    GetString(message.Payload, "data")).ConfigureAwait(false);
                break;

            case "session.binaryInput":
                await _sessions.WriteAsync(
                    RequireSessionId(message),
                    Convert.FromBase64String(GetString(message.Payload, "data"))).ConfigureAwait(false);
                break;

            case "session.resize":
                _sessions.Resize(
                    RequireSessionId(message),
                    GetInt32(message.Payload, "cols", 80),
                    GetInt32(message.Payload, "rows", 24));
                break;

            case "session.close":
                var sessionId = RequireSessionId(message);
                lock (_sessionEventLock)
                {
                    _announcedSessions.Remove(sessionId);
                    _pendingOutput.Remove(sessionId);
                    _pendingExits.Remove(sessionId);
                }
                await _sessions.CloseAsync(sessionId).ConfigureAwait(false);
                break;

            case "settings.fontSize":
                _settings = await _settingsStore.SaveAsync(
                    _settings with
                    {
                        Font = _settings.Font with
                        {
                            Size = GetDouble(message.Payload, "size", AppSettings.DefaultFontSize)
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
                await _sessions.UpdateSettingsAsync(_settings).ConfigureAwait(false);
                _agentUsage.UpdateSettings(_settings);
                Post("settings.saved", message.RequestId, payload: new { settings = _settings });
                break;

            case "agentUsage.refresh":
                var provider = GetString(message.Payload, "provider") switch
                {
                    "codex" => UsageProvider.Codex,
                    "kimi" => UsageProvider.KimiCode,
                    _ => throw new InvalidDataException("Unsupported usage provider.")
                };
                Post("agentUsage.refreshResult", message.RequestId, payload: new
                {
                    started = _agentUsage.RequestRefresh(provider)
                });
                break;

            default:
                throw new InvalidDataException($"Unknown bridge message type '{message.Type}'.");
        }
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var json in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_socket.State != WebSocketState.Open)
                {
                    break;
                }
                var bytes = Encoding.UTF8.GetBytes(json);
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private void HandleOutput(string sessionId, string data)
    {
        lock (_sessionEventLock)
        {
            if (_announcedSessions.Contains(sessionId))
            {
                Post("session.output", sessionId: sessionId, payload: new { data });
                return;
            }

            if (!_pendingOutput.TryGetValue(sessionId, out var output))
            {
                output = new StringBuilder();
                _pendingOutput.Add(sessionId, output);
            }
            output.Append(data);
        }
    }

    private void HandleExit(string sessionId, TerminalExitStatus status)
    {
        lock (_sessionEventLock)
        {
            if (_announcedSessions.Contains(sessionId))
            {
                PostExit(sessionId, status);
            }
            else
            {
                _pendingExits[sessionId] = status;
            }
        }
    }

    private void PostExit(string sessionId, TerminalExitStatus status) =>
        Post("session.exited", sessionId: sessionId, payload: new
        {
            exitCode = status.ExitCode,
            failure = status.Failure
        });

    private void HandleAgentUsage(AgentUsageStatus status) =>
        Post("agentUsage.changed", payload: new { agentUsage = status });

    private void HandleSystemMetrics(SystemMetricsStatus status) =>
        Post("systemMetrics.changed", payload: new { systemMetrics = status });

    private void Post(
        string type,
        string? requestId = null,
        string? sessionId = null,
        object? payload = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            type,
            requestId,
            sessionId,
            payload = payload ?? new { }
        }, JsonOptions);
        _outbound.Writer.TryWrite(json);
    }

    private static string RequireSessionId(BridgeMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.SessionId))
        {
            throw new InvalidDataException("The message is missing a session ID.");
        }
        return message.SessionId;
    }

    private static string GetString(JsonElement payload, string propertyName) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(JsonElement payload, string propertyName, int defaultValue) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt32(out var value)
            ? value
            : defaultValue;

    private static double GetDouble(JsonElement payload, string propertyName, double defaultValue) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(propertyName, out var property) &&
        property.TryGetDouble(out var value)
            ? value
            : defaultValue;
}
