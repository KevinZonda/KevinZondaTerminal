using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace KevinZonda.Terminal.AvaloniaDesktop;

internal sealed class AvaloniaWebViewBridge : IDisposable
{
    private const int MaxOutputBatchChars = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NativeWebView _webView;
    private readonly UnixTerminalSessionManager _sessions;
    private readonly MainWindow _owner;
    private readonly string _workingDirectory;
    private readonly DesktopSettingsStore _settingsStore = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _outputQueues = new();
    private readonly DispatcherTimer _outputTimer;
    private DesktopSettings _settings;
    private int _disposed;

    internal AvaloniaWebViewBridge(
        NativeWebView webView,
        UnixTerminalSessionManager sessions,
        MainWindow owner,
        string workingDirectory)
    {
        _webView = webView;
        _sessions = sessions;
        _owner = owner;
        _workingDirectory = workingDirectory;
        _settings = _settingsStore.Load();
        _sessions.OutputReceived += QueueOutput;
        _sessions.SessionExited += QueueExit;
        _webView.WebMessageReceived += HandleMessage;
        _outputTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(12)
        };
        _outputTimer.Tick += FlushOutput;
        _outputTimer.Start();
    }

    private async void HandleMessage(object? sender, WebMessageReceivedEventArgs eventArgs)
    {
        BridgeMessage? message = null;
        try
        {
            message = JsonSerializer.Deserialize<BridgeMessage>(eventArgs.Body ?? string.Empty, JsonOptions);
            if (message is null || message.Version != 1 || string.IsNullOrWhiteSpace(message.Type))
            {
                throw new InvalidDataException("Unsupported bridge message.");
            }

            switch (message.Type)
            {
                case "app.ready":
                    Post("app.initialState", message.RequestId, payload: new
                    {
                        application = "KevinZonda Terminal",
                        version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0",
                        settings = _settings,
                        agentUsage = new { providers = Array.Empty<object>() },
                        systemMetrics = new
                        {
                            usedMemoryBytes = 0,
                            availableMemoryBytes = 0,
                            totalMemoryBytes = 0
                        }
                    });
                    break;

                case "session.create":
                    await CreateSessionAsync(message);
                    break;

                case "session.input":
                    await _sessions.WriteAsync(
                        RequireSessionId(message),
                        GetString(message.Payload, "data"));
                    break;

                case "session.binaryInput":
                    await _sessions.WriteAsync(
                        RequireSessionId(message),
                        Convert.FromBase64String(GetString(message.Payload, "data")));
                    break;

                case "session.resize":
                    await _sessions.ResizeAsync(
                        RequireSessionId(message),
                        GetInt32(message.Payload, "cols", 80),
                        GetInt32(message.Payload, "rows", 24));
                    break;

                case "session.close":
                    await _sessions.CloseAsync(RequireSessionId(message));
                    break;

                case "clipboard.read":
                    var clipboard = TopLevel.GetTopLevel(_webView)?.Clipboard;
                    Post("clipboard.value", message.RequestId, payload: new
                    {
                        text = clipboard is null
                            ? string.Empty
                            : await clipboard.TryGetTextAsync() ?? string.Empty
                    });
                    break;

                case "clipboard.write":
                    var clipboardText = GetString(message.Payload, "text");
                    var targetClipboard = TopLevel.GetTopLevel(_webView)?.Clipboard;
                    if (targetClipboard is not null && clipboardText.Length > 0)
                    {
                        await targetClipboard.SetTextAsync(clipboardText);
                    }
                    break;

                case "window.settings":
                    await _owner.ShowSettingsPlaceholderAsync();
                    break;

                case "window.newInstance":
                    LaunchNewInstance();
                    break;

                case "window.openExternal":
                    OpenExternal(GetString(message.Payload, "uri"));
                    break;

                case "settings.fontSize":
                    _settings = await _settingsStore.SaveFontSizeAsync(
                        _settings,
                        GetDouble(message.Payload, "size", 14));
                    Post("settings.saved", message.RequestId, payload: new { settings = _settings });
                    break;

                case "agentUsage.refresh":
                    Post("agentUsage.refreshResult", message.RequestId, payload: new { started = false });
                    break;

                default:
                    throw new InvalidDataException($"Unknown bridge message type '{message.Type}'.");
            }
        }
        catch (Exception exception)
        {
            Post(
                "session.error",
                message?.RequestId,
                message?.SessionId,
                new { message = exception.Message });
        }
    }

    private async Task CreateSessionAsync(BridgeMessage message)
    {
        var session = await _sessions.CreateAsync(
            GetInt32(message.Payload, "cols", 80),
            GetInt32(message.Payload, "rows", 24));
        Post("session.created", message.RequestId, session.Id, new
        {
            shellName = session.ShellName,
            processId = session.ProcessId
        });
    }

    private void QueueOutput(string sessionId, string data)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _outputQueues.GetOrAdd(sessionId, static _ => new ConcurrentQueue<string>()).Enqueue(data);
        }
    }

    private void QueueExit(string sessionId, int exitCode, int? signal, string? failure)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            FlushSessionOutput(sessionId, drain: true);
            Post("session.exited", sessionId: sessionId, payload: new
            {
                exitCode,
                failure = failure ?? (signal is null ? null : $"Terminated by signal {signal}.")
            });
        });
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
                Post("session.output", sessionId: sessionId, payload: new { data = builder.ToString() });
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
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            type,
            requestId,
            sessionId,
            payload = payload ?? new { }
        }, JsonOptions);
        var script = $"window.__ktermReceiveNativeMessage?.({JsonSerializer.Serialize(json)});";
        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _ = _webView.InvokeScript(script);
            }
        });
    }

    private void LaunchNewInstance()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }
        startInfo.ArgumentList.Add("--working-directory");
        startInfo.ArgumentList.Add(_workingDirectory);
        Process.Start(startInfo);
    }

    private static void OpenExternal(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto"))
        {
            throw new InvalidDataException("Only HTTP, HTTPS, and mail links can be opened externally.");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static string RequireSessionId(BridgeMessage message) =>
        string.IsNullOrWhiteSpace(message.SessionId)
            ? throw new InvalidDataException("The message is missing a session ID.")
            : message.SessionId;

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _outputTimer.Stop();
        _outputTimer.Tick -= FlushOutput;
        _webView.WebMessageReceived -= HandleMessage;
        _sessions.OutputReceived -= QueueOutput;
        _sessions.SessionExited -= QueueExit;
        _outputQueues.Clear();
    }
}
