using System.Collections.Concurrent;
using KevinZonda.Terminal.Configuration;

namespace KevinZonda.Terminal.Server;

internal sealed class BrowserTerminalRuntimeRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, BrowserTerminalRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly SettingsStore _settingsStore;
    private readonly ServerOptions _options;
    private readonly CancellationTokenSource _lifetime = new();

    internal BrowserTerminalRuntimeRegistry(SettingsStore settingsStore, ServerOptions options)
    {
        _settingsStore = settingsStore;
        _options = options;
    }

    internal BrowserRuntimeLease Attach(
        string runtimeId,
        IBrowserTerminalClient client,
        string? requestId,
        IReadOnlyDictionary<string, long> outputAcks)
    {
        while (true)
        {
            var runtime = _runtimes.GetOrAdd(
                runtimeId,
                id => new BrowserTerminalRuntime(id, _settingsStore.Load(), _options));
            try
            {
                return new BrowserRuntimeLease(runtime, runtime.Attach(client, requestId, outputAcks));
            }
            catch (ObjectDisposedException)
            {
                _runtimes.TryRemove(new KeyValuePair<string, BrowserTerminalRuntime>(runtimeId, runtime));
            }
        }
    }

    internal void Detach(BrowserTerminalRuntime runtime, long epoch)
    {
        var idleVersion = runtime.Detach(epoch);
        if (idleVersion is not null)
        {
            _ = ExpireAsync(runtime, idleVersion.Value);
        }
    }

    private async Task ExpireAsync(BrowserTerminalRuntime runtime, long idleVersion)
    {
        try
        {
            await Task.Delay(_options.RuntimeRetention, _lifetime.Token).ConfigureAwait(false);
            if (!runtime.TryBeginExpiration(idleVersion) ||
                !_runtimes.TryRemove(new KeyValuePair<string, BrowserTerminalRuntime>(runtime.Id, runtime)))
            {
                return;
            }

            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        var runtimes = _runtimes.ToArray();
        _runtimes.Clear();
        foreach (var (_, runtime) in runtimes)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        _lifetime.Dispose();
    }
}

internal sealed record BrowserRuntimeLease(BrowserTerminalRuntime Runtime, long Epoch);
