using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using KevinZonda.Terminal.WebBridgeProtocol;
using static KevinZonda.Terminal.WebBridgeProtocol.BridgePayloadReader;

namespace KevinZonda.Terminal.Server;

internal interface IBrowserTerminalClient
{
    bool TryPost(string type, string? requestId = null, string? sessionId = null, object? payload = null);

    void Supersede(bool replaced = false);
}

internal sealed class BrowserTerminalConnection : IBrowserTerminalClient
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private const long MaximumQueuedBytes = 32L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebSocket _socket;
    private readonly BrowserTerminalRuntimeRegistry _runtimes;
    private readonly Channel<OutboundFrame> _outbound = Channel.CreateUnbounded<OutboundFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private CancellationTokenSource? _connectionLifetime;
    private BrowserTerminalRuntime? _runtime;
    private long _runtimeEpoch;
    private long _queuedBytes;
    private int _closed;
    private int _superseded;
    private int _replacementClose;

    internal BrowserTerminalConnection(WebSocket socket, BrowserTerminalRuntimeRegistry runtimes)
    {
        _socket = socket;
        _runtimes = runtimes;
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _connectionLifetime = connectionLifetime;
        var sendTask = SendLoopAsync(connectionLifetime.Token);
        try
        {
            await ReceiveLoopAsync(connectionLifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connectionLifetime.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _closed, 1);
            connectionLifetime.Cancel();
            _outbound.Writer.TryComplete();
            if (_runtime is not null)
            {
                _runtimes.Detach(_runtime, _runtimeEpoch);
            }

            await sendTask.ConfigureAwait(false);
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    var replaced = Volatile.Read(ref _replacementClose) != 0;
                    await _socket.CloseOutputAsync(
                        replaced ? (WebSocketCloseStatus)4001 : WebSocketCloseStatus.NormalClosure,
                        replaced ? "KTerm runtime opened in another page." : "KTerm connection closed.",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }
            }
        }
    }

    public bool TryPost(
        string type,
        string? requestId = null,
        string? sessionId = null,
        object? payload = null) =>
        TryPostCore(type, requestId, sessionId, payload, closeAfterSend: false, allowSuperseded: false);

    public void Supersede(bool replaced = false)
    {
        if (Interlocked.Exchange(ref _superseded, 1) != 0)
        {
            return;
        }

        if (!replaced)
        {
            _connectionLifetime?.Cancel();
            return;
        }

        Volatile.Write(ref _replacementClose, 1);
        if (!TryPostCore(
                BridgeMessageTypes.RuntimeReplaced,
                requestId: null,
                sessionId: null,
                payload: new { },
                closeAfterSend: true,
                allowSuperseded: true))
        {
            _connectionLifetime?.Cancel();
        }
    }

    private bool TryPostCore(
        string type,
        string? requestId,
        string? sessionId,
        object? payload,
        bool closeAfterSend,
        bool allowSuperseded)
    {
        if (Volatile.Read(ref _closed) != 0 ||
            (!allowSuperseded && Volatile.Read(ref _superseded) != 0))
        {
            return false;
        }

        var json = BridgeProtocol.Serialize(
            type,
            requestId,
            sessionId,
            writer => JsonSerializer.Serialize(writer, payload ?? new { }, JsonOptions));
        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (Interlocked.Add(ref _queuedBytes, byteCount) > MaximumQueuedBytes)
        {
            Interlocked.Add(ref _queuedBytes, -byteCount);
            _connectionLifetime?.Cancel();
            return false;
        }

        if (_outbound.Writer.TryWrite(new OutboundFrame(json, byteCount, closeAfterSend)))
        {
            return true;
        }

        Interlocked.Add(ref _queuedBytes, -byteCount);
        return false;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var messageBuffer = new MemoryStream();

        while (_socket.State == WebSocketState.Open)
        {
            messageBuffer.SetLength(0);
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
                if (messageBuffer.Length + result.Count > MaximumMessageBytes)
                {
                    throw new InvalidDataException("The WebSocket message is too large.");
                }
                messageBuffer.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            BridgeMessage? message = null;
            try
            {
                message = BridgeProtocol.Deserialize(
                    messageBuffer.GetBuffer().AsSpan(0, (int)messageBuffer.Length));

                if (message.Type == BridgeMessageTypes.RuntimeAttach)
                {
                    AttachRuntime(message);
                }
                else if (_runtime is null)
                {
                    throw new InvalidDataException("Attach a browser runtime before sending terminal messages.");
                }
                else
                {
                    await _runtime.HandleMessageAsync(this, message, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                TryPost(BridgeMessageTypes.SessionError, message?.RequestId, message?.SessionId, new
                {
                    message = exception.Message
                });
            }
        }
    }

    private void AttachRuntime(BridgeMessage message)
    {
        if (_runtime is not null)
        {
            throw new InvalidDataException("This WebSocket is already attached to a browser runtime.");
        }

        var runtimeId = GetString(message.Payload, "runtimeId");
        if (string.IsNullOrWhiteSpace(runtimeId) || runtimeId.Length > 128)
        {
            throw new InvalidDataException("The runtime ID is missing or invalid.");
        }

        var resumeStates = new Dictionary<string, BrowserSessionResumeState>(StringComparer.Ordinal);
        if (message.Payload.ValueKind == JsonValueKind.Object &&
            message.Payload.TryGetProperty("sessions", out var sessions) &&
            sessions.ValueKind == JsonValueKind.Array)
        {
            foreach (var session in sessions.EnumerateArray())
            {
                var sessionId = GetString(session, "sessionId");
                var outputSeq = GetInt64(session, "lastAppliedOutputSeq", 0);
                var checkpointSeq = GetInt64(session, "checkpointOutputSeq", 0);
                if (!string.IsNullOrWhiteSpace(sessionId) && outputSeq >= 0)
                {
                    resumeStates[sessionId] = new BrowserSessionResumeState(
                        outputSeq,
                        Math.Clamp(checkpointSeq, 0, outputSeq));
                }
            }
        }

        var lease = _runtimes.Attach(runtimeId, this, message.RequestId, resumeStates);
        _runtime = lease.Runtime;
        _runtimeEpoch = lease.Epoch;
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_socket.State != WebSocketState.Open)
                {
                    break;
                }

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(frame.Json);
                    await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
                        .ConfigureAwait(false);
                    if (frame.CloseAfterSend)
                    {
                        _connectionLifetime?.Cancel();
                        break;
                    }
                }
                finally
                {
                    Interlocked.Add(ref _queuedBytes, -frame.ByteCount);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            _connectionLifetime?.Cancel();
        }
    }

    private sealed record OutboundFrame(string Json, int ByteCount, bool CloseAfterSend);
}

internal sealed record BrowserSessionResumeState(long LastAppliedOutputSeq, long CheckpointOutputSeq);
