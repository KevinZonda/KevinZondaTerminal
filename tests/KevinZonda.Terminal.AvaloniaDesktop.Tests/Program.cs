using KevinZonda.Terminal.AvaloniaDesktop;
using KevinZonda.AgentUsageMonitor;
using KevinZonda.SystemMetrics;
using KevinZonda.Terminal.WebBridgeProtocol;
using KevinZonda.Terminal.Configuration;
using Avalonia.Input;
using System.Text.Json;
using System.Text.Json.Nodes;

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

Require(MainWindow.ResolveMacOSWindowShortcut(Key.M, KeyModifiers.Meta) ==
        MacOSWindowShortcut.Minimize,
    "Command-M was not recognized as Minimize.");
Require(MainWindow.ResolveMacOSWindowShortcut(Key.M, KeyModifiers.Meta | KeyModifiers.Alt) ==
        MacOSWindowShortcut.MinimizeAll,
    "Option-Command-M was not recognized as Minimize All.");
Require(MainWindow.ResolveMacOSWindowShortcut(Key.H, KeyModifiers.Meta) ==
        MacOSWindowShortcut.HideApplication,
    "Command-H was not recognized as Hide Application.");
Require(MainWindow.ResolveMacOSWindowShortcut(Key.F, KeyModifiers.Meta | KeyModifiers.Control) ==
        MacOSWindowShortcut.ToggleFullScreen,
    "Control-Command-F was not recognized as Toggle Full Screen.");
Require(MainWindow.ResolveMacOSWindowShortcut(Key.Q, KeyModifiers.Meta) ==
        MacOSWindowShortcut.QuitApplication,
    "Command-Q was not recognized as Quit Application.");
Require(MainWindow.ResolveMacOSWindowShortcut(Key.OemComma, KeyModifiers.Meta) ==
        MacOSWindowShortcut.OpenSettings,
    "Command-comma was not recognized as Open Settings.");
Require(MainWindow.ResolveMacOSWindowShortcut(Key.M, KeyModifiers.Meta | KeyModifiers.Shift) ==
        MacOSWindowShortcut.None,
    "An unsupported shifted shortcut was intercepted.");
Require(MainWindow.ResolveMacOSWindowShortcut(Key.H, KeyModifiers.Alt) ==
        MacOSWindowShortcut.None,
    "A non-macOS application shortcut was intercepted.");
await TestSettingsStoreAsync();
TestSourceGeneratedBridgeJson();
TestTerminalThemeCatalog();

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
Console.WriteLine("PASS macOS window shortcut mapping");
Console.WriteLine("PASS Avalonia settings persistence");
Console.WriteLine("PASS shared Web bridge protocol");
Console.WriteLine("PASS Avalonia terminal theme palettes");

static void TestTerminalThemeCatalog()
{
    Require(TerminalThemeCatalog.All.Count == 7,
        "The Avalonia terminal theme catalog is incomplete.");
    foreach (var theme in TerminalThemeCatalog.All)
    {
        Require(theme.AnsiColors.Count == 16,
            $"Theme '{theme.Name}' does not define a complete ANSI palette.");
        Require(!string.IsNullOrWhiteSpace(theme.SelectionBackground),
            $"Theme '{theme.Name}' does not define a selection color.");
    }
}

static void TestSourceGeneratedBridgeJson()
{
    var inbound = BridgeJson.Deserialize(
        """
        {"version":1,"type":"session.resize","requestId":"req-1","sessionId":"pty-1","payload":{"cols":120}}
        """) ?? throw new InvalidOperationException("The bridge message was not deserialized.");
    Require(inbound is
    {
        Version: 1,
        Type: BridgeMessageTypes.SessionResize,
        RequestId: "req-1",
        SessionId: "pty-1"
    }, "The source-generated bridge deserializer changed the inbound protocol.");
    Require(BridgePayloadReader.GetInt32(inbound.Payload, "cols", 80) == 120,
        "The source-generated bridge deserializer lost the payload.");

    var outbound = BridgeJson.Serialize(
        BridgeMessageTypes.SessionCreated,
        "req-2",
        "pty-2",
        new BridgePayload { ShellName = "zsh", ProcessId = 42 });
    using var document = JsonDocument.Parse(outbound);
    var root = document.RootElement;
    Require(root.GetProperty("version").GetInt32() == BridgeProtocol.CurrentVersion,
        "The source-generated bridge serializer lost the protocol version.");
    Require(root.GetProperty("type").GetString() == BridgeMessageTypes.SessionCreated,
        "The source-generated bridge serializer changed the event type.");
    var payload = root.GetProperty("payload");
    Require(payload.GetProperty("shellName").GetString() == "zsh"
            && payload.GetProperty("processId").GetInt32() == 42,
        "The source-generated bridge serializer changed the payload shape.");
    Require(!payload.TryGetProperty("settings", out _),
        "The bridge serializer emitted unrelated nullable payload fields.");

    var quoted = BridgeJson.QuoteForJavaScript(outbound);
    using var quotedDocument = JsonDocument.Parse(quoted);
    Require(quotedDocument.RootElement.GetString() == outbound,
        "The bridge JSON was not safely quoted for JavaScript.");

    using var emptyDocument = JsonDocument.Parse(
        BridgeProtocol.Serialize(BridgeMessageTypes.AppReady));
    Require(emptyDocument.RootElement.GetProperty("requestId").ValueKind == JsonValueKind.Null &&
            emptyDocument.RootElement.GetProperty("sessionId").ValueKind == JsonValueKind.Null &&
            emptyDocument.RootElement.GetProperty("payload").ValueKind == JsonValueKind.Object,
        "The shared bridge envelope changed its empty-message shape.");

    try
    {
        BridgeProtocol.Deserialize("""{"version":2,"type":"app.ready","payload":{}}""");
        throw new InvalidOperationException("An unsupported bridge version was accepted.");
    }
    catch (InvalidDataException)
    {
    }
}

static async Task TestSettingsStoreAsync()
{
    var path = Path.Combine(Path.GetTempPath(), $"kterm-settings-{Guid.NewGuid():N}.json");
    try
    {
        await File.WriteAllTextAsync(
            path,
            """
            {
              "font": { "size": 11 },
              "shell": {
                "profile": "Msys2",
                "executable": "C:\\msys64\\usr\\bin\\zsh.exe",
                "exitBehavior": "KeepTab"
              },
              "conHost": { "enhancedOpenConsole": true },
              "custom": { "preserve": 42 }
            }
            """);

        var store = new SettingsStore(path);
        var loaded = store.Load();
        Require(loaded.Shell.Profile == "msys2",
            "The shared settings model did not load the Windows shell profile.");
        Require(loaded.ConHost.EnhancedOpenConsole,
            "The shared settings model did not load the ConHost configuration.");

        var saved = await store.SaveAsync(loaded with
        {
            Font = new FontSettings
            {
                Family = "Menlo",
                Size = 16,
                LineHeight = 1.2,
                EnableLigatures = true
            },
            Theme = new ThemeSettings { Name = "Ubuntu" },
            Cursor = new CursorSettings { Shape = "block", Blink = false },
            Indicators = new IndicatorSettings
            {
                ShowWorkspaceIndicator = false,
                ShowRemainingUsage = true,
                AutoRenewKimiToken = true
            },
            Shell = loaded.Shell with
            {
                ExitBehavior = "CloseTab"
            }
        });

        Require(saved.Theme.Name == "Ubuntu", "The selected terminal theme was not saved.");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject
            ?? throw new InvalidOperationException("The saved settings file is not a JSON object.");
        Require(root["shell"]?["profile"]?.GetValue<string>() == "msys2",
            "Saving Unix settings removed the Windows shell profile.");
        Require(root["shell"]?["exitBehavior"]?.GetValue<string>() == "CloseTab",
            "The shell exit behavior was not updated.");
        Require(root["conHost"]?["enhancedOpenConsole"]?.GetValue<bool>() == true,
            "Saving Unix settings removed enhanced OpenConsole configuration.");
        Require(root["custom"]?["preserve"]?.GetValue<int>() == 42,
            "Saving Unix settings removed an unknown configuration section.");
    }
    finally
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

static async Task TestLiveAgentUsageAsync()
{
    await using var sessions = new UnixTerminalSessionManager(Environment.CurrentDirectory);
    await using var usage = new AgentUsageMonitorService(sessions.GetSessionProcessIds);
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
