using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using KevinZonda.AgentUsageMonitor;
using KevinZonda.Terminal.Configuration;
using KevinZonda.SystemMetrics;
using KevinZonda.Terminal.Terminal;
using KevinZonda.Terminal.WebBridgeProtocol;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using static KevinZonda.Terminal.WebBridgeProtocol.BridgePayloadReader;

namespace KevinZonda.Terminal.Messaging;

internal sealed class WebViewBridge : IDisposable
{
    private const int MaxOutputBatchChars = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebView2 _webView;
    private readonly TerminalSessionManager _sessions;
    private readonly IAgentUsageMonitorService _agentUsage;
    private readonly ISystemMetricsService _systemMetrics;
    private readonly Action _openSettings;
    private readonly Action _openNewInstance;
    private readonly Action<string> _openExternal;
    private readonly Func<double, Task<AppSettings>> _saveFontSize;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _outputQueues = new();
    private readonly System.Windows.Forms.Timer _outputTimer;
    private AppSettings _settings;
    private int _disposed;

    internal WebViewBridge(
        WebView2 webView,
        TerminalSessionManager sessions,
        IAgentUsageMonitorService agentUsage,
        ISystemMetricsService systemMetrics,
        Action openSettings,
        Action openNewInstance,
        Action<string> openExternal,
        Func<double, Task<AppSettings>> saveFontSize,
        AppSettings settings)
    {
        _webView = webView;
        _sessions = sessions;
        _agentUsage = agentUsage;
        _systemMetrics = systemMetrics;
        _openSettings = openSettings;
        _openNewInstance = openNewInstance;
        _openExternal = openExternal;
        _saveFontSize = saveFontSize;
        _settings = settings;
        _sessions.OutputReceived += QueueOutput;
        _sessions.SessionExited += QueueExit;
        _agentUsage.StatusChanged += QueueAgentUsage;
        _systemMetrics.StatusChanged += QueueSystemMetrics;
        _webView.CoreWebView2.WebMessageReceived += HandleMessage;

        _outputTimer = new System.Windows.Forms.Timer
        {
            Interval = 12,
            Enabled = true
        };
        _outputTimer.Tick += FlushOutput;
    }

    internal void NotifyRuntimeFailure(string kind) =>
        Post(BridgeMessageTypes.AppRuntimeFailed, payload: new { kind });

    internal void SendWorkspaceCommand(string command) =>
        Post(BridgeMessageTypes.WorkspaceCommand, payload: new { command });

    internal void SendSettingsChanged(AppSettings settings)
    {
        _settings = settings;
        Post(BridgeMessageTypes.AppSettingsChanged, payload: new { settings });
    }

    private async void HandleMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        BridgeMessage? message = null;

        try
        {
            message = BridgeProtocol.Deserialize(eventArgs.WebMessageAsJson);

            switch (message.Type)
            {
                case BridgeMessageTypes.AppReady:
                    Post(BridgeMessageTypes.AppInitialState, message.RequestId, payload: new
                    {
                        application = "KevinZonda Terminal",
                        version = Application.ProductVersion,
                        settings = _settings,
                        agentUsage = _agentUsage.Current,
                        systemMetrics = _systemMetrics.Current
                    });
                    break;

                case BridgeMessageTypes.SessionCreate:
                    await CreateSession(message);
                    break;

                case BridgeMessageTypes.SessionInput:
                    await _sessions.WriteAsync(
                        RequireSessionId(message),
                        GetString(message.Payload, "data"));
                    break;

                case BridgeMessageTypes.SessionBinaryInput:
                    await _sessions.WriteAsync(
                        RequireSessionId(message),
                        Convert.FromBase64String(GetString(message.Payload, "data")));
                    break;

                case BridgeMessageTypes.SessionResize:
                    _sessions.Resize(
                        RequireSessionId(message),
                        GetInt32(message.Payload, "cols", 80),
                        GetInt32(message.Payload, "rows", 24));
                    break;

                case BridgeMessageTypes.SessionClose:
                    await _sessions.CloseAsync(RequireSessionId(message));
                    break;

                case BridgeMessageTypes.ClipboardRead:
                    Post(BridgeMessageTypes.ClipboardValue, message.RequestId, payload: new
                    {
                        text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty
                    });
                    break;

                case BridgeMessageTypes.ClipboardWrite:
                    var text = GetString(message.Payload, "text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        Clipboard.SetText(text);
                    }
                    break;

                case BridgeMessageTypes.WindowSettings:
                    _webView.BeginInvoke(_openSettings);
                    break;

                case BridgeMessageTypes.WindowNewInstance:
                    _webView.BeginInvoke(_openNewInstance);
                    break;

                case BridgeMessageTypes.WindowOpenExternal:
                    _openExternal(GetString(message.Payload, "uri"));
                    break;

                case BridgeMessageTypes.SettingsFontSize:
                    _settings = await _saveFontSize(GetDouble(message.Payload, "size", 14));
                    Post(BridgeMessageTypes.SettingsSaved, message.RequestId, payload: new { settings = _settings });
                    break;

                case BridgeMessageTypes.AgentUsageRefresh:
                    var provider = GetString(message.Payload, "provider") switch
                    {
                        "codex" => KevinZonda.AgentUsageMonitor.UsageProvider.Codex,
                        "kimi" => KevinZonda.AgentUsageMonitor.UsageProvider.KimiCode,
                        _ => throw new InvalidDataException("Unsupported usage provider.")
                    };
                    Post(
                        BridgeMessageTypes.AgentUsageRefreshResult,
                        message.RequestId,
                        payload: new { started = _agentUsage.RequestRefresh(provider) });
                    break;

                default:
                    throw new InvalidDataException($"Unknown bridge message type '{message.Type}'.");
            }
        }
        catch (Exception exception)
        {
            Post(
                BridgeMessageTypes.SessionError,
                message?.RequestId,
                message?.SessionId,
                new { message = exception.Message });
        }
    }

    private async Task CreateSession(BridgeMessage message)
    {
        var session = await _sessions.CreateAsync(
            GetInt32(message.Payload, "cols", 80),
            GetInt32(message.Payload, "rows", 24));

        Post(BridgeMessageTypes.SessionCreated, message.RequestId, session.Id, new
        {
            shellName = session.ShellName,
            processId = session.ProcessId
        });
    }

    private void QueueOutput(string sessionId, string data)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _outputQueues.GetOrAdd(sessionId, static _ => new ConcurrentQueue<string>()).Enqueue(data);
    }

    private void QueueExit(string sessionId, TerminalExitStatus status)
    {
        if (Volatile.Read(ref _disposed) != 0 || _webView.IsDisposed)
        {
            return;
        }

        try
        {
            _webView.BeginInvoke(() =>
            {
                FlushSessionOutput(sessionId, drain: true);
                Post(BridgeMessageTypes.SessionExited, sessionId: sessionId, payload: new
                {
                    exitCode = status.ExitCode,
                    failure = status.Failure
                });
            });
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void QueueAgentUsage(AgentUsageStatus status)
    {
        if (Volatile.Read(ref _disposed) != 0 || _webView.IsDisposed)
        {
            return;
        }

        try
        {
            _webView.BeginInvoke(() =>
                Post(BridgeMessageTypes.AgentUsageChanged, payload: new { agentUsage = status }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void QueueSystemMetrics(SystemMetricsStatus status)
    {
        if (Volatile.Read(ref _disposed) != 0 || _webView.IsDisposed)
        {
            return;
        }

        try
        {
            _webView.BeginInvoke(() =>
                Post(BridgeMessageTypes.SystemMetricsChanged, payload: new { systemMetrics = status }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void FlushOutput(object? sender, EventArgs eventArgs)
    {
        foreach (var sessionId in _outputQueues.Keys)
        {
            FlushSessionOutput(sessionId);
        }
    }

    private void FlushSessionOutput(string sessionId, bool drain = false)
    {
        if (!_outputQueues.TryGetValue(sessionId, out var queue) || queue.IsEmpty)
        {
            return;
        }

        do
        {
            var builder = new StringBuilder();
            while (builder.Length < MaxOutputBatchChars && queue.TryDequeue(out var chunk))
            {
                builder.Append(chunk);
            }

            if (builder.Length > 0)
            {
                Post(BridgeMessageTypes.SessionOutput, sessionId: sessionId, payload: new { data = builder.ToString() });
            }
        } while (drain && !queue.IsEmpty);

        if (queue.IsEmpty)
        {
            _outputQueues.TryRemove(new KeyValuePair<string, ConcurrentQueue<string>>(sessionId, queue));
        }
    }

    private void Post(
        string type,
        string? requestId = null,
        string? sessionId = null,
        object? payload = null)
    {
        if (Volatile.Read(ref _disposed) != 0 || _webView.IsDisposed || _webView.CoreWebView2 is null)
        {
            return;
        }

        var json = BridgeProtocol.Serialize(
            type,
            requestId,
            sessionId,
            writer => JsonSerializer.Serialize(writer, payload ?? new { }, JsonOptions));
        _webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _outputTimer.Stop();
        _outputTimer.Tick -= FlushOutput;
        _outputTimer.Dispose();
        _sessions.OutputReceived -= QueueOutput;
        _sessions.SessionExited -= QueueExit;
        _agentUsage.StatusChanged -= QueueAgentUsage;
        _systemMetrics.StatusChanged -= QueueSystemMetrics;

        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.WebMessageReceived -= HandleMessage;
        }
    }
}
