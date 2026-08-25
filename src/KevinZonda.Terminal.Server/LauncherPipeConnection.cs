using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace KevinZonda.Terminal.Server;

internal sealed class LauncherPipeConnection : IAsyncDisposable
{
    private const string PipeArgument = "--launcher-pipe";
    private readonly NamedPipeClientStream _pipe;
    private readonly Channel<LauncherLogMessage> _messages;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TextWriter _originalOutput;
    private readonly TextWriter _originalError;
    private readonly PipeLogTextWriter _pipeOutput;
    private readonly PipeLogTextWriter _pipeError;
    private readonly Task _writerTask;
    private Task? _controlTask;
    private int _disposed;

    private LauncherPipeConnection(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _messages = Channel.CreateBounded<LauncherLogMessage>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _originalOutput = Console.Out;
        _originalError = Console.Error;
        _pipeOutput = new PipeLogTextWriter("stdout", _messages.Writer);
        _pipeError = new PipeLogTextWriter("stderr", _messages.Writer);
        Console.SetOut(_pipeOutput);
        Console.SetError(_pipeError);
        _writerTask = WriteMessagesAsync();
    }

    internal static (string? PipeName, string[] ServerArguments) ExtractArguments(string[] arguments)
    {
        string? pipeName = null;
        var serverArguments = new List<string>(arguments.Length);
        for (var index = 0; index < arguments.Length; index++)
        {
            if (!string.Equals(arguments[index], PipeArgument, StringComparison.Ordinal))
            {
                serverArguments.Add(arguments[index]);
                continue;
            }

            if (pipeName is not null)
            {
                throw new ArgumentException($"{PipeArgument} may only be specified once.");
            }
            if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
            {
                throw new ArgumentException($"{PipeArgument} requires a pipe name.");
            }
            pipeName = arguments[index];
        }
        return (pipeName, [.. serverArguments]);
    }

    internal static async Task<LauncherPipeConnection> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            return new LauncherPipeConnection(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal void StartControl(IHostApplicationLifetime lifetime, ILogger logger)
    {
        if (_controlTask is not null)
        {
            throw new InvalidOperationException("Launcher pipe control is already running.");
        }
        logger.LogInformation("Launcher pipe control is enabled.");
        _controlTask = ReadControlAsync(lifetime, logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Console.SetOut(_originalOutput);
        Console.SetError(_originalError);
        _pipeOutput.CompleteLine();
        _pipeError.CompleteLine();
        _messages.Writer.TryComplete();

        try
        {
            await _writerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or TimeoutException)
        {
        }

        _stopping.Cancel();
        await _pipe.DisposeAsync().ConfigureAwait(false);
        if (_controlTask is not null)
        {
            try
            {
                await _controlTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
        _stopping.Dispose();
    }

    private async Task WriteMessagesAsync()
    {
        try
        {
            await using var writer = new StreamWriter(
                _pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true)
            {
                AutoFlush = true
            };
            await foreach (var message in _messages.Reader.ReadAllAsync(_stopping.Token))
            {
                var json = JsonSerializer.Serialize(new { type = message.Type, text = message.Text });
                await writer.WriteLineAsync(json.AsMemory(), _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task ReadControlAsync(IHostApplicationLifetime lifetime, ILogger logger)
    {
        try
        {
            using var reader = new StreamReader(
                _pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            while (!lifetime.ApplicationStopping.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_stopping.Token).ConfigureAwait(false);
                if (line is null)
                {
                    logger.LogInformation("Launcher pipe closed; stopping the server.");
                    lifetime.StopApplication();
                    return;
                }

                using var message = JsonDocument.Parse(line);
                if (message.RootElement.TryGetProperty("type", out var type) &&
                    string.Equals(type.GetString(), "shutdown", StringComparison.Ordinal))
                {
                    logger.LogInformation("Launcher requested server shutdown.");
                    lifetime.StopApplication();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (
            lifetime.ApplicationStopping.IsCancellationRequested || _stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or JsonException)
        {
            logger.LogWarning(exception, "Launcher pipe control failed; stopping the server.");
            lifetime.StopApplication();
        }
    }

    private sealed record LauncherLogMessage(string Type, string Text);

    private sealed class PipeLogTextWriter(
        string source,
        ChannelWriter<LauncherLogMessage> messages) : TextWriter
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_gate)
            {
                Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }
            lock (_gate)
            {
                foreach (var character in value)
                {
                    Append(character);
                }
            }
        }

        public override void Write(char[] buffer, int index, int count) =>
            Write(buffer.AsSpan(index, count));

        public override void Write(ReadOnlySpan<char> buffer)
        {
            lock (_gate)
            {
                foreach (var character in buffer)
                {
                    Append(character);
                }
            }
        }

        internal void CompleteLine()
        {
            lock (_gate)
            {
                PublishLine();
            }
        }

        private void Append(char value)
        {
            if (value == '\n')
            {
                PublishLine();
            }
            else if (value != '\r')
            {
                _line.Append(value);
            }
        }

        private void PublishLine()
        {
            if (_line.Length == 0)
            {
                return;
            }
            messages.TryWrite(new LauncherLogMessage(source, _line.ToString()));
            _line.Clear();
        }
    }
}
