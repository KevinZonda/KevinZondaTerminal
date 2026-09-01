using KevinZonda.Terminal.AvaloniaDesktop;
using KevinZonda.AgentUsageMonitor;

if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
{
    Console.WriteLine("SKIP system metrics are implemented for macOS and Linux");
    return;
}

if (args.Contains("--live-agent-usage", StringComparer.OrdinalIgnoreCase))
{
    await TestLiveAgentUsageAsync();
    return;
}

var detected = UnixAgentProcessDetector.DetectSnapshot(
    """
      100     1 /bin/zsh
      101   100 /usr/local/bin/codex
      102   101 /usr/bin/helper
      200     1 /bin/zsh
      201   200 /usr/local/bin/kimi-code
      300     1 /usr/local/bin/codex
    """,
    [100, 200]);
Require(detected.SetEquals([UsageProvider.Codex, UsageProvider.KimiCode]),
    "Agent process detection did not follow both terminal process trees.");
Require(UnixAgentProcessDetector.Classify("/opt/homebrew/bin/codex-aarch64") == UsageProvider.Codex,
    "A platform-specific Codex executable was not recognized.");
Require(UnixAgentProcessDetector.Classify("/usr/local/bin/kimi_code") == UsageProvider.KimiCode,
    "The Kimi Code executable was not recognized.");
if (OperatingSystem.IsMacOS())
{
    Require(UnixTerminalSession.NeedsMacOSUtf8Locale(null, null, null),
        "Finder-style macOS launches should receive a UTF-8 locale fallback.");
    Require(!UnixTerminalSession.NeedsMacOSUtf8Locale(null, null, "en_US.UTF-8"),
        "An explicitly configured locale should not be replaced.");
    Require(!UnixTerminalSession.NeedsMacOSUtf8Locale("C", null, null),
        "An explicitly configured LC_ALL should not be replaced.");
}

await using var service = new SystemMetricsService();
var updateCount = 0;
service.StatusChanged += _ => Interlocked.Increment(ref updateCount);
service.Start();
await Task.Delay(TimeSpan.FromMilliseconds(2_500));

var status = service.Current;
Require(updateCount >= 2, "Expected an immediate sample and a periodic sample.");
Require(status.TotalMemoryBytes > 0, "Total physical memory was not detected.");
Require(status.AvailableMemoryBytes <= status.TotalMemoryBytes,
    "Available memory exceeds total physical memory.");
Require(status.UsedMemoryBytes + status.AvailableMemoryBytes == status.TotalMemoryBytes,
    "Used and available memory do not add up to total physical memory.");
Require(status.CpuPercent is >= 0 and <= 100,
    "A valid CPU percentage was not produced after the second sample.");
Require(status.UpdatedAt is not null, "The metrics timestamp is missing.");

Console.WriteLine(
    $"PASS system metrics: CPU {status.CpuPercent:F1}%, " +
    $"memory {status.UsedMemoryBytes}/{status.TotalMemoryBytes} bytes");
Console.WriteLine("PASS Unix agent process detection");

static async Task TestLiveAgentUsageAsync()
{
    await using var sessions = new UnixTerminalSessionManager(Environment.CurrentDirectory);
    await using var usage = new AgentUsageStatusService(sessions, new DesktopSettings());
    var completed = new TaskCompletionSource<AgentProviderUsageStatus>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    usage.StatusChanged += status =>
    {
        var codex = status.Providers.FirstOrDefault(provider => provider.Provider == "codex");
        if (codex?.State is "ready" or "error" or "stale")
        {
            completed.TrySetResult(codex);
        }
    };

    var session = await sessions.CreateAsync(80, 24);
    usage.Start();
    await sessions.WriteAsync(session.Id, "codex\n");

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var result = await completed.Task.WaitAsync(timeout.Token);
    Require(result.State == "ready", result.Error ?? $"Unexpected Codex usage state: {result.State}");
    Require(result.Windows.Count > 0, "Codex usage returned no quota windows.");
    Console.WriteLine(
        $"PASS live Avalonia agent usage: {result.Plan ?? "unknown plan"}, " +
        $"{result.Windows.Count} quota window(s)");

    await sessions.CloseAsync(session.Id);
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
