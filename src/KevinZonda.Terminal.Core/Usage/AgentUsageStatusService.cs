using KevinZonda.AgentUsageMonitor;
using KevinZonda.AgentUsageMonitor.Codex;
using KevinZonda.AgentUsageMonitor.KimiCode;
using KevinZonda.Terminal.Configuration;
using KevinZonda.Terminal.Terminal;

namespace KevinZonda.Terminal.Usage;

internal sealed class AgentUsageStatusService : IAsyncDisposable
{
    private static readonly TimeSpan DetectionInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ManualRefreshCooldown = TimeSpan.FromSeconds(15);

    private readonly TerminalSessionManager _sessions;
    private readonly AgentProcessDetector _detector;
    private readonly HttpClient _httpClient;
    private volatile IReadOnlyDictionary<UsageProvider, IUsageClient> _clients;
    private readonly Dictionary<UsageProvider, ProviderRuntime> _providers = new()
    {
        [UsageProvider.Codex] = new(),
        [UsageProvider.KimiCode] = new()
    };
    private readonly HashSet<Task> _refreshTasks = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateLock = new();
    private Task? _monitorTask;
    private int _disposed;

    internal AgentUsageStatusService(TerminalSessionManager sessions, AppSettings settings)
    {
        _sessions = sessions;
        _detector = new AgentProcessDetector();
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _clients = CreateClients(settings);
    }

    internal event Action<AgentUsageStatus>? StatusChanged;

    internal void UpdateSettings(AppSettings settings)
    {
        var autoRenew = settings.Indicators.AutoRenewKimiToken;
        var current = _clients[UsageProvider.KimiCode] as KimiCodeUsageClient;
        if (current?.AutoRenewToken == autoRenew)
        {
            return;
        }

        _clients = CreateClients(settings);
        lock (_stateLock)
        {
            _providers[UsageProvider.KimiCode].LastAttempt = null;
        }
        _ = DetectAndRefresh();
    }

    internal AgentUsageStatus Current
    {
        get
        {
            lock (_stateLock)
            {
                return BuildStatus();
            }
        }
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _monitorTask ??= Task.Run(MonitorAsync);
    }

    internal bool RequestRefresh(UsageProvider provider)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_clients.TryGetValue(provider, out var client))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_stateLock)
        {
            var runtime = _providers[provider];
            if (!runtime.Active || runtime.RefreshInProgress
                || runtime.LastAttempt is { } lastAttempt && now - lastAttempt < ManualRefreshCooldown)
            {
                return false;
            }

            runtime.LastAttempt = now;
            runtime.RefreshInProgress = true;
            runtime.Error = null;
        }

        RaiseStatusChanged();
        TrackRefresh(RefreshAsync(provider, client));
        return true;
    }

    private async Task MonitorAsync()
    {
        await DetectAndRefresh().ConfigureAwait(false);
        using var timer = new PeriodicTimer(DetectionInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await DetectAndRefresh().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private IReadOnlyDictionary<UsageProvider, IUsageClient> CreateClients(AppSettings settings) =>
        new Dictionary<UsageProvider, IUsageClient>
        {
            [UsageProvider.Codex] = new CodexUsageClient(_httpClient),
            [UsageProvider.KimiCode] = new KimiCodeUsageClient(
                _httpClient,
                new KimiCodeUsageOptions
                {
                    AutoRenewToken = settings.Indicators.AutoRenewKimiToken,
                })
        };

    private Task DetectAndRefresh()
    {
        var activeProviders = _detector.Detect(_sessions.GetSessionProcessIds());
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        var refreshes = new List<(UsageProvider Provider, IUsageClient Client)>();

        lock (_stateLock)
        {
            foreach (var (provider, runtime) in _providers)
            {
                var active = activeProviders.Contains(provider);
                if (runtime.Active != active)
                {
                    runtime.Active = active;
                    changed = true;
                }

                if (!active || runtime.RefreshInProgress
                    || runtime.LastAttempt is { } lastAttempt && now - lastAttempt < RefreshInterval)
                {
                    continue;
                }

                runtime.LastAttempt = now;
                runtime.RefreshInProgress = true;
                runtime.Error = null;
                refreshes.Add((provider, _clients[provider]));
                changed = true;
            }
        }

        if (changed)
        {
            RaiseStatusChanged();
        }

        foreach (var (provider, client) in refreshes)
        {
            TrackRefresh(RefreshAsync(provider, client));
        }

        return Task.CompletedTask;
    }

    private void TrackRefresh(Task refreshTask)
    {
        lock (_stateLock)
        {
            _refreshTasks.Add(refreshTask);
        }

        _ = refreshTask.ContinueWith(
            completedTask =>
            {
                lock (_stateLock)
                {
                    _refreshTasks.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RefreshAsync(UsageProvider provider, IUsageClient client)
    {
        try
        {
            var snapshot = await client.GetUsageAsync(_lifetime.Token).ConfigureAwait(false);
            lock (_stateLock)
            {
                var runtime = _providers[provider];
                runtime.Snapshot = snapshot;
                runtime.Error = null;
                runtime.RefreshInProgress = false;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            lock (_stateLock)
            {
                _providers[provider].RefreshInProgress = false;
            }
            return;
        }
        catch (Exception exception)
        {
            lock (_stateLock)
            {
                var runtime = _providers[provider];
                runtime.Error = FriendlyError(exception);
                runtime.RefreshInProgress = false;
            }
        }

        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        AgentUsageStatus status;
        lock (_stateLock)
        {
            status = BuildStatus();
        }
        StatusChanged?.Invoke(status);
    }

    private AgentUsageStatus BuildStatus()
    {
        var providers = _providers
            .Where(pair => pair.Value.Active)
            .OrderBy(pair => pair.Key)
            .Select(pair => ToStatus(pair.Key, pair.Value))
            .ToArray();
        return providers.Length == 0 ? AgentUsageStatus.Empty : new AgentUsageStatus(providers);
    }

    private static AgentProviderUsageStatus ToStatus(UsageProvider provider, ProviderRuntime runtime)
    {
        var snapshot = runtime.Snapshot;
        var state = runtime.RefreshInProgress && runtime.Snapshot is null
            ? "loading"
            : runtime.Error is not null
                ? runtime.Snapshot is null ? "error" : "stale"
                : runtime.Snapshot is null ? "loading" : "ready";
        var windows = snapshot is null
            ? []
            : new[] { snapshot.Primary, snapshot.Secondary }
                .OfType<UsageWindow>()
                .Concat(snapshot.ExtraWindows)
                .OrderBy(window => window.Window)
                .Select(window => new AgentUsageWindowStatus(
                    window.Name,
                    FormatWindowLabel(window),
                    window.UsedPercent,
                    window.ResetsAt,
                    window.Used,
                    window.Limit))
                .ToArray();

        return new AgentProviderUsageStatus(
            provider == UsageProvider.Codex ? "codex" : "kimi",
            state,
            runtime.RefreshInProgress,
            snapshot is null ? null : FormatSource(snapshot.Source),
            snapshot?.Plan,
            windows,
            snapshot?.Credits is { } credits
                ? new AgentUsageCreditsStatus(
                    credits.Remaining,
                    credits.IsUnlimited,
                    credits.Total,
                    credits.Currency)
                : null,
            snapshot?.Budget is { } budget
                ? new AgentUsageBudgetStatus(
                    budget.Name,
                    budget.Limit,
                    budget.Used,
                    budget.RemainingPercent,
                    budget.ResetsAt,
                    budget.IsUnlimited,
                    budget.Currency)
                : null,
            snapshot?.UpdatedAt,
            runtime.LastAttempt + RefreshInterval,
            runtime.Error);
    }

    private static string FormatSource(UsageSource source) => source switch
    {
        UsageSource.CodexOAuth => "OAuth",
        UsageSource.CodexAppServer => "App server",
        UsageSource.KimiCodeApiKey => "API key",
        UsageSource.KimiCodeCliCredential => "CLI credential",
        _ => source.ToString()
    };

    private static string FormatWindowLabel(UsageWindow window)
    {
        if (window.Window is not { } duration)
        {
            return window.Name;
        }
        if (duration.TotalDays >= 1 && duration.TotalDays == Math.Truncate(duration.TotalDays))
        {
            return $"{duration.TotalDays:0}d";
        }
        if (duration.TotalHours >= 1 && duration.TotalHours == Math.Truncate(duration.TotalHours))
        {
            return $"{duration.TotalHours:0}h";
        }
        return window.Name;
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        UsageException usage => usage.Message,
        HttpRequestException => "Unable to reach the usage service.",
        TaskCanceledException => "The usage request timed out.",
        _ => "Unable to read usage."
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        if (_monitorTask is not null)
        {
            await _monitorTask.ConfigureAwait(false);
        }

        Task[] refreshTasks;
        lock (_stateLock)
        {
            refreshTasks = _refreshTasks.ToArray();
        }
        await Task.WhenAll(refreshTasks).ConfigureAwait(false);

        _httpClient.Dispose();
        _lifetime.Dispose();
    }

    private sealed class ProviderRuntime
    {
        internal bool Active { get; set; }
        internal bool RefreshInProgress { get; set; }
        internal DateTimeOffset? LastAttempt { get; set; }
        internal UsageSnapshot? Snapshot { get; set; }
        internal string? Error { get; set; }
    }
}
