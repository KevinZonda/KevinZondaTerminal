using System.Net;
using System.Text;
using System.Text.Json;
using KevinZonda.AgentUsageMonitor;
using KevinZonda.AgentUsageMonitor.Codex;
using KevinZonda.AgentUsageMonitor.KimiCode;
using KevinZonda.AgentUsageMonitor.ProcessDetection;

if (args.Contains("--live-codex", StringComparer.OrdinalIgnoreCase)
    || args.Contains("--live-codex-rpc", StringComparer.OrdinalIgnoreCase))
{
    using var http = new HttpClient();
    var client = new CodexUsageClient(http);
    var mode = args.Contains("--live-codex-rpc", StringComparer.OrdinalIgnoreCase)
        ? CodexUsageMode.AppServer
        : CodexUsageMode.Auto;
    var usage = await client.GetUsageAsync(new CodexUsageOptions { Mode = mode });
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        provider = usage.Provider.ToString(),
        source = usage.Source.ToString(),
        plan = usage.Plan,
        primary = usage.Primary,
        secondary = usage.Secondary,
        extraWindows = usage.ExtraWindows,
        credits = usage.Credits,
        budget = usage.Budget,
        updatedAt = usage.UpdatedAt,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (args.Contains("--live-kimi", StringComparer.OrdinalIgnoreCase))
{
    using var http = new HttpClient();
    var client = new KimiCodeUsageClient(http);
    var usage = await client.GetUsageAsync(new KimiCodeUsageOptions { Mode = KimiCodeUsageMode.Auto });
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        provider = usage.Provider.ToString(),
        source = usage.Source.ToString(),
        primary = usage.Primary,
        secondary = usage.Secondary,
        extraWindows = usage.ExtraWindows,
        updatedAt = usage.UpdatedAt,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("Kimi API request and response", TestKimiApiAsync),
    ("Common usage client interface", TestCommonInterfaceAsync),
    ("Kimi auto falls back to CLI credential", TestKimiAutoFallbackAsync),
    ("Kimi leaves expired CLI credentials to the CLI", TestKimiExpiredCredentialAsync),
    ("Kimi retries a rejected token only after CLI credential changes", TestKimiCredentialRetryAsync),
    ("Kimi observes CLI renewal, expiration and logout", TestKimiCredentialChangesAsync),
    ("Kimi stops retrying rejected CLI credentials", TestKimiRetryBoundAsync),
    ("Kimi follows the configured international CLI credential", TestKimiInternationalCredentialAsync),
    ("Kimi rejects invalid CLI configuration without sending credentials", TestKimiInvalidConfigAsync),
    ("Kimi parses all limits and booster wallet", TestKimiCompleteUsageAsync),
    ("Kimi endpoint normalization", TestKimiEndpointAsync),
    ("Codex OAuth request and response", TestCodexOAuthAsync),
    ("Codex endpoint normalization", TestCodexEndpointAsync),
    ("Cross-platform agent process tree detection", TestAgentProcessTreeAsync),
    ("Agent monitor service lifecycle", TestAgentMonitorServiceLifecycleAsync),
    ("Agent monitor detects CLI credential renewal before the usage interval", TestAgentMonitorCredentialPollingAsync),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

return failures == 0 ? 0 : 1;

static Task TestAgentProcessTreeAsync()
{
    var unix = AgentProcessDetector.DetectUnixSnapshot(
        """
          100     1 /bin/zsh
          101   100 /usr/local/bin/codex
          102   101 /usr/bin/helper
          200     1 /bin/zsh
          201   200 /usr/local/bin/kimi-code
          300     1 /usr/local/bin/codex
        """,
        [100, 200]);
    Equal(true, unix.SetEquals([UsageProvider.Codex, UsageProvider.KimiCode]));

    var windows = AgentProcessDetector.DetectProcessTree(
        [
            new AgentProcessDetector.ProcessEntry(500, 1, "powershell.exe"),
            new AgentProcessDetector.ProcessEntry(501, 500, "codex.exe"),
            new AgentProcessDetector.ProcessEntry(502, 501, "helper.exe"),
            new AgentProcessDetector.ProcessEntry(600, 1, "kimi-code.exe")
        ],
        [500]);
    Equal(true, windows.SetEquals([UsageProvider.Codex]));
    Equal(UsageProvider.Codex,
        AgentProcessDetector.Classify("/opt/homebrew/bin/codex-aarch64"));
    Equal(UsageProvider.KimiCode,
        AgentProcessDetector.Classify("/usr/local/bin/kimi_code"));
    Equal<UsageProvider?>(null, AgentProcessDetector.Classify("helper"));
    return Task.CompletedTask;
}

static async Task TestAgentMonitorServiceLifecycleAsync()
{
    await using IAgentUsageMonitorService service = new AgentUsageMonitorService(
        () => Array.Empty<int>());
    Equal(0, service.Current.Providers.Count);
    service.UpdateOptions(new AgentUsageMonitorOptions { AutoRenewKimiToken = true });
    service.Start();
    await Task.Delay(50);
    Equal(0, service.Current.Providers.Count);
    Equal(false, service.RequestRefresh(UsageProvider.Codex));
}

static async Task TestAgentMonitorCredentialPollingAsync()
{
    using var fixture = new KimiCliFixture();
    fixture.WriteCredential("expired-token", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
    var requests = 0;
    using var httpClient = new HttpClient(new StubHandler(request =>
    {
        Equal(HttpMethod.Get, request.Method);
        Equal("Bearer renewed-token", request.Headers.Authorization!.ToString());
        Interlocked.Increment(ref requests);
        return Json("""{"usage":{"limit":100,"used":30}}""");
    }));
    await using var service = new AgentUsageMonitorService(
        () => Array.Empty<int>(),
        httpClient,
        (_, _) => Task.FromResult<IReadOnlySet<UsageProvider>>(new HashSet<UsageProvider> { UsageProvider.KimiCode }),
        new KimiCodeUsageOptions { Mode = KimiCodeUsageMode.CliCredential, KimiCodeHome = fixture.Home });
    var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    service.StatusChanged += status =>
    {
        if (status.Providers.SingleOrDefault() is { } provider)
        {
            if (provider.State == "error") expired.TrySetResult();
            if (provider.State == "ready") recovered.TrySetResult();
        }
    };

    service.Start();
    await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Equal(0, Volatile.Read(ref requests));
    fixture.WriteCredential("renewed-token");
    await recovered.Task.WaitAsync(TimeSpan.FromSeconds(6));
    await Task.Delay(TimeSpan.FromSeconds(3));
    Equal(1, Volatile.Read(ref requests));
}

static async Task TestKimiApiAsync()
{
    const string json = """
        {
          "usage": { "limit": "1000", "used": "250", "reset_time": "2026-08-20T00:00:00Z" },
          "limits": [{
            "window": { "duration": 5, "timeUnit": "TIME_UNIT_HOUR" },
            "detail": { "limit": 100, "remaining": 75, "resetAt": "2026-08-14T12:00:00Z" }
          }]
        }
        """;
    var handler = new StubHandler(request =>
    {
        Equal("https://api.kimi.com/coding/v1/usages", request.RequestUri!.AbsoluteUri);
        Equal("Bearer test-kimi", request.Headers.Authorization!.ToString());
        return Json(json);
    });
    var client = new KimiCodeUsageClient(new HttpClient(handler));
    var usage = await client.GetUsageAsync(new KimiCodeUsageOptions
    {
        Mode = KimiCodeUsageMode.ApiKey,
        ApiKey = "test-kimi",
    });

    Equal(UsageSource.KimiCodeApiKey, usage.Source);
    Equal(25d, usage.Primary!.UsedPercent);
    Equal(TimeSpan.FromDays(7), usage.Primary.Window);
    Equal(25d, usage.Secondary!.UsedPercent);
    Equal(TimeSpan.FromHours(5), usage.Secondary.Window);
}

static async Task TestCommonInterfaceAsync()
{
    const string json = """{"usage":{"limit":100,"used":42},"limits":[]}""";
    IUsageClient client = new KimiCodeUsageClient(
        new HttpClient(new StubHandler(_ => Json(json))),
        new KimiCodeUsageOptions
        {
            Mode = KimiCodeUsageMode.ApiKey,
            ApiKey = "test-kimi",
        });

    Equal(UsageProvider.KimiCode, client.Provider);
    var usage = await client.GetUsageAsync();
    Equal(UsageProvider.KimiCode, usage.Provider);
    Equal(42d, usage.Primary!.UsedPercent);
}

static Task TestKimiEndpointAsync()
{
    Equal(
        "https://example.com/prefix/coding/v1/usages",
        KimiCodeUsageClient.BuildUsageUri(new Uri("https://example.com/prefix")).AbsoluteUri);
    Equal(
        "https://example.com/coding/v1/usages",
        KimiCodeUsageClient.BuildUsageUri(new Uri("https://example.com/coding/v1")).AbsoluteUri);
    return Task.CompletedTask;
}

static async Task TestKimiAutoFallbackAsync()
{
    var temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        "kevinzonda-agent-usage-monitor-tests",
        Guid.NewGuid().ToString("N"));
    var credentialsDirectory = Path.Combine(temporaryRoot, "credentials");
    Directory.CreateDirectory(credentialsDirectory);
    await File.WriteAllTextAsync(Path.Combine(credentialsDirectory, "kimi-code.json"), $$"""
        {
          "access_token": "cli-token",
          "refresh_token": "refresh",
          "expires_at": {{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}
        }
        """);

    try
    {
        var requestCount = 0;
        var handler = new StubHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                Equal("Bearer invalid-api-key", request.Headers.Authorization!.ToString());
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            Equal("Bearer cli-token", request.Headers.Authorization!.ToString());
            Equal("kimi_code_cli", request.Headers.GetValues("X-Msh-Platform").Single());
            return Json("""{"usage":{"limit":100,"used":10},"limits":[]}""");
        });
        var client = new KimiCodeUsageClient(new HttpClient(handler));
        var usage = await client.GetUsageAsync(new KimiCodeUsageOptions
        {
            ApiKey = "invalid-api-key",
            KimiCodeHome = temporaryRoot,
            DeviceId = "test-device",
        });

        Equal(2, requestCount);
        Equal(UsageSource.KimiCodeCliCredential, usage.Source);
    }
    finally
    {
        Directory.Delete(temporaryRoot, recursive: true);
    }
}

static async Task TestKimiExpiredCredentialAsync()
{
    var temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        "kevinzonda-agent-usage-monitor-tests",
        Guid.NewGuid().ToString("N"));
    var credentialsDirectory = Path.Combine(temporaryRoot, "credentials");
    Directory.CreateDirectory(credentialsDirectory);
    var credentialPath = Path.Combine(credentialsDirectory, "kimi-code.json");
    var originalCredential = $$"""
        {
          "access_token": "expired-access",
          "refresh_token": "original-refresh",
          "expires_at": {{DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()}},
          "expires_in": 3600,
          "scope": "openid",
          "token_type": "Bearer"
        }
        """;
    await File.WriteAllTextAsync(credentialPath, originalCredential);

    try
    {
        var requestCount = 0;
        var handler = new StubHandler(_ =>
        {
            requestCount++;
            return Json("""{"usage":{"limit":100,"used":10},"limits":[]}""");
        });
        var client = new KimiCodeUsageClient(
            new HttpClient(handler),
            new KimiCodeUsageOptions
            {
                Mode = KimiCodeUsageMode.CliCredential,
                KimiCodeHome = temporaryRoot,
                DeviceId = "test-device",
                AutoRenewToken = true,
            });

        try
        {
            await client.GetUsageAsync();
            throw new InvalidOperationException("Expired CLI credentials must require CLI login.");
        }
        catch (UsageException exception)
        {
            Equal(UsageErrorCode.InvalidCredential, exception.Code);
            Contains("kimi login", exception.Message);
        }

        Equal(0, requestCount);
        Equal(originalCredential, await File.ReadAllTextAsync(credentialPath));
    }
    finally
    {
        Directory.Delete(temporaryRoot, recursive: true);
    }
}

static async Task TestKimiCredentialRetryAsync()
{
    using var fixture = new KimiCliFixture();
    fixture.WriteCredential("old-token");
    var requestCount = 0;
    var client = new KimiCodeUsageClient(new HttpClient(new StubHandler(request =>
    {
        Equal(HttpMethod.Get, request.Method);
        requestCount++;
        if (requestCount == 1)
        {
            Equal("Bearer old-token", request.Headers.Authorization!.ToString());
            fixture.WriteCredential("new-token");
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        Equal("Bearer new-token", request.Headers.Authorization!.ToString());
        return Json("""{"usage":{"limit":100,"used":10}}""");
    })), new KimiCodeUsageOptions { Mode = KimiCodeUsageMode.CliCredential, KimiCodeHome = fixture.Home });

    Equal(10d, (await client.GetUsageAsync()).Primary!.UsedPercent);
    Equal(2, requestCount);
}

static async Task TestKimiCredentialChangesAsync()
{
    using var fixture = new KimiCliFixture();
    fixture.WriteCredential("initial-token");
    var expectedToken = "initial-token";
    var requests = 0;
    var client = new KimiCodeUsageClient(new HttpClient(new StubHandler(request =>
    {
        requests++;
        Equal(HttpMethod.Get, request.Method);
        Equal($"Bearer {expectedToken}", request.Headers.Authorization!.ToString());
        return Json("""{"usage":{"limit":100,"used":20}}""");
    })), new KimiCodeUsageOptions { Mode = KimiCodeUsageMode.CliCredential, KimiCodeHome = fixture.Home });

    Equal(20d, (await client.GetUsageAsync()).Primary!.UsedPercent);
    fixture.WriteCredential("updated-token");
    expectedToken = "updated-token";
    Equal(20d, (await client.GetUsageAsync()).Primary!.UsedPercent);
    fixture.WriteCredential("expired-token", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
    await ExpectUsageErrorAsync(() => client.GetUsageAsync(), UsageErrorCode.InvalidCredential);
    File.Delete(Path.Combine(fixture.Home, "credentials", "kimi-code.json"));
    await ExpectUsageErrorAsync(() => client.GetUsageAsync(), UsageErrorCode.MissingCredential);
    Equal(2, requests);
}

static async Task TestKimiRetryBoundAsync()
{
    foreach (var changed in new[] { false, true })
    {
        using var fixture = new KimiCliFixture();
        fixture.WriteCredential("rejected-token");
        var requests = 0;
        var client = new KimiCodeUsageClient(new HttpClient(new StubHandler(request =>
        {
            requests++;
            Equal(HttpMethod.Get, request.Method);
            if (changed)
            {
                fixture.WriteCredential($"changed-token-{requests}");
            }
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        })), new KimiCodeUsageOptions { Mode = KimiCodeUsageMode.CliCredential, KimiCodeHome = fixture.Home });

        await ExpectUsageErrorAsync(() => client.GetUsageAsync(), UsageErrorCode.InvalidCredential);
        Equal(changed ? 2 : 1, requests);
    }
}

static async Task TestKimiInvalidConfigAsync()
{
    foreach (var config in new[]
    {
        "invalid = [",
        """
        [providers."managed:kimi-code"]
        oauth = { storage = "file", key = "oauth/../outside" }
        """,
        """
        [providers."managed:kimi-code"]
        oauth = { storage = "file", key = 'oauth/..\outside' }
        """,
        """
        [providers."managed:kimi-code"]
        oauth = { storage = "file" }
        """,
        """
        [providers."managed:kimi-code"]
        base_url = "http://example.test/coding/v1"
        oauth = { storage = "file", key = "oauth/kimi-code" }
        """
    })
    {
        using var fixture = new KimiCliFixture();
        fixture.WriteCredential("example-token");
        File.WriteAllText(Path.Combine(fixture.Home, "config.toml"), config);
        var client = new KimiCodeUsageClient(new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("Invalid configuration must not send credentials."))),
            new KimiCodeUsageOptions { Mode = KimiCodeUsageMode.CliCredential, KimiCodeHome = fixture.Home });
        await ExpectUsageErrorAsync(() => client.GetUsageAsync(), UsageErrorCode.InvalidConfiguration);
    }
}

static async Task TestKimiInternationalCredentialAsync()
{
    using var fixture = new KimiCliFixture();
    fixture.WriteCredential("stale-mainland-token");
    fixture.WriteCredential("global-token", "kimi-code-env-example");
    File.WriteAllText(Path.Combine(fixture.Home, "config.toml"), """
        [providers."managed:kimi-code"]
        type = "kimi"
        base_url = "https://api.kimi.ai/coding/v1"
        oauth = { storage = "file", key = "oauth/kimi-code-env-example", oauth_host = "https://auth.kimi.ai" }
        """);
    var client = new KimiCodeUsageClient(new HttpClient(new StubHandler(request =>
    {
        Equal("Bearer global-token", request.Headers.Authorization!.ToString());
        Equal("https://api.kimi.ai/coding/v1/usages", request.RequestUri!.AbsoluteUri);
        return Json("""{"usage":{"limit":100,"used":15}}""");
    })), new KimiCodeUsageOptions { Mode = KimiCodeUsageMode.CliCredential, KimiCodeHome = fixture.Home });

    Equal(15d, (await client.GetUsageAsync()).Primary!.UsedPercent);
}

static async Task TestKimiCompleteUsageAsync()
{
    const string json = """
        {
          "usage": { "limit": "1000", "used": "250", "resetTime": "2026-08-20T00:00:00Z" },
          "limits": [
            {
              "name": "Burst",
              "window": { "duration": 300, "timeUnit": "TIME_UNIT_MINUTE" },
              "detail": { "limit": "100", "used": "20", "resetTime": "2026-08-14T12:00:00Z" }
            },
            {
              "name": "Weekly messages",
              "window": { "duration": 1, "timeUnit": "TIME_UNIT_WEEK" },
              "detail": { "limit": "50", "used": "5" }
            }
          ],
          "boosterWallet": {
            "balance": {
              "type": "BOOSTER",
              "amount": "500000000",
              "amountLeft": "250000000"
            },
            "monthlyChargeLimitEnabled": true,
            "monthlyChargeLimit": { "priceInCents": "1000", "currency": "USD" },
            "monthlyUsed": { "priceInCents": "250", "currency": "USD" }
          }
        }
        """;
    var client = new KimiCodeUsageClient(
        new HttpClient(new StubHandler(_ => Json(json))),
        new KimiCodeUsageOptions
        {
            Mode = KimiCodeUsageMode.ApiKey,
            ApiKey = "test-kimi",
        });

    var usage = await client.GetUsageAsync();

    Equal("Burst", usage.Secondary!.Name);
    Equal(TimeSpan.FromHours(5), usage.Secondary.Window);
    Equal(1, usage.ExtraWindows.Count);
    Equal("Weekly messages", usage.ExtraWindows[0].Name);
    Equal(TimeSpan.FromDays(7), usage.ExtraWindows[0].Window);
    Equal(2.5d, usage.Credits!.Remaining);
    Equal(5d, usage.Credits.Total);
    Equal("USD", usage.Credits.Currency);
    Equal(10d, usage.Budget!.Limit);
    Equal(2.5d, usage.Budget.Used);
    Equal(75d, usage.Budget.RemainingPercent);
    Equal(false, usage.Budget.IsUnlimited);
    Equal("USD", usage.Budget.Currency);
}

static async Task TestCodexOAuthAsync()
{
    var temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        "kevinzonda-agent-usage-monitor-tests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryRoot);
    var authPath = Path.Combine(temporaryRoot, "auth.json");
    await File.WriteAllTextAsync(authPath, $$"""
        {
          "tokens": {
            "access_token": "test-codex",
            "refresh_token": "refresh",
            "account_id": "account-1"
          },
          "last_refresh": "{{DateTimeOffset.UtcNow:O}}"
        }
        """);

    try
    {
        const string json = """
            {
              "plan_type": "plus",
              "rate_limit": {
                "primary_window": { "used_percent": 12, "reset_at": 1786708800, "limit_window_seconds": 18000 },
                "secondary_window": { "used_percent": 34, "reset_at": 1787137200, "limit_window_seconds": 604800 }
              },
              "credits": { "has_credits": true, "unlimited": false, "balance": "8.5" },
              "additional_rate_limits": [{
                "limit_name": "Codex Spark",
                "rate_limit": { "primary_window": { "used_percent": 22, "reset_at": 1786708800, "limit_window_seconds": 18000 } }
              }]
            }
            """;
        var handler = new StubHandler(request =>
        {
            Equal("https://chatgpt.com/backend-api/wham/usage", request.RequestUri!.AbsoluteUri);
            Equal("Bearer test-codex", request.Headers.Authorization!.ToString());
            Equal("account-1", request.Headers.GetValues("ChatGPT-Account-Id").Single());
            return Json(json);
        });
        var client = new CodexUsageClient(new HttpClient(handler));
        var usage = await client.GetUsageAsync(new CodexUsageOptions
        {
            Mode = CodexUsageMode.OAuth,
            AuthFilePath = authPath,
        });

        Equal(UsageSource.CodexOAuth, usage.Source);
        Equal(12d, usage.Primary!.UsedPercent);
        Equal(TimeSpan.FromHours(5), usage.Primary.Window);
        Equal(34d, usage.Secondary!.UsedPercent);
        Equal(8.5d, usage.Credits!.Remaining);
        Equal("plus", usage.Plan);
        Equal(1, usage.ExtraWindows.Count);
    }
    finally
    {
        Directory.Delete(temporaryRoot, recursive: true);
    }
}

static Task TestCodexEndpointAsync()
{
    Equal(
        "https://chatgpt.com/backend-api/wham/usage",
        CodexUsageClient.ResolveUsageUri(new CodexUsageOptions()).AbsoluteUri);
    Equal(
        "https://chatgpt.com/backend-api/wham/usage",
        CodexUsageClient.ResolveUsageUri(
            new CodexUsageOptions { ApiBaseUri = new Uri("https://chatgpt.com") }).AbsoluteUri);
    Equal(
        "https://example.com/api/codex/usage",
        CodexUsageClient.ResolveUsageUri(new CodexUsageOptions { ApiBaseUri = new Uri("https://example.com") }).AbsoluteUri);
    return Task.CompletedTask;
}

static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
{
    Content = new StringContent(content, Encoding.UTF8, "application/json"),
};

static async Task ExpectUsageErrorAsync(Func<Task<UsageSnapshot>> action, UsageErrorCode expected)
{
    try
    {
        await action();
    }
    catch (UsageException exception)
    {
        Equal(expected, exception.Code);
        return;
    }

    throw new InvalidOperationException($"Expected usage error {expected}.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void Contains(string expected, string actual)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected '{actual}' to contain '{expected}'.");
    }
}

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(handler(request));
}

sealed class KimiCliFixture : IDisposable
{
    public string Home { get; } = Path.Combine(Path.GetTempPath(), "kevinzonda-agent-usage-monitor-tests", Guid.NewGuid().ToString("N"));

    public KimiCliFixture() => Directory.CreateDirectory(Path.Combine(Home, "credentials"));

    public void WriteCredential(string token, string name = "kimi-code", DateTimeOffset? expiresAt = null) =>
        File.WriteAllText(Path.Combine(Home, "credentials", $"{name}.json"), JsonSerializer.Serialize(new
        {
            access_token = token,
            refresh_token = "example-refresh-token",
            expires_at = (expiresAt ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeSeconds(),
            expires_in = 3600
        }));

    public void Dispose() => Directory.Delete(Home, recursive: true);
}
