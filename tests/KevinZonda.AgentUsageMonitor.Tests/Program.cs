using System.Net;
using System.Text;
using System.Text.Json;
using KevinZonda.AgentUsageMonitor;
using KevinZonda.AgentUsageMonitor.Codex;
using KevinZonda.AgentUsageMonitor.KimiCode;

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
    ("Kimi renews CLI token in memory only", TestKimiInMemoryRenewalAsync),
    ("Kimi parses all limits and booster wallet", TestKimiCompleteUsageAsync),
    ("Kimi endpoint normalization", TestKimiEndpointAsync),
    ("Codex OAuth request and response", TestCodexOAuthAsync),
    ("Codex endpoint normalization", TestCodexEndpointAsync),
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

static async Task TestKimiInMemoryRenewalAsync()
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
        var refreshCount = 0;
        var usageCount = 0;
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                refreshCount++;
                Equal("https://auth.kimi.com/api/oauth/token", request.RequestUri!.AbsoluteUri);
                var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Contains("grant_type=refresh_token", form);
                Contains("refresh_token=original-refresh", form);
                return Json("""
                    {
                      "access_token": "renewed-access",
                      "refresh_token": "renewed-refresh",
                      "expires_in": 3600,
                      "scope": "openid",
                      "token_type": "Bearer"
                    }
                    """);
            }

            usageCount++;
            Equal("Bearer renewed-access", request.Headers.Authorization!.ToString());
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

        await client.GetUsageAsync();
        await client.GetUsageAsync();

        Equal(1, refreshCount);
        Equal(2, usageCount);
        Equal(originalCredential, await File.ReadAllTextAsync(credentialPath));
    }
    finally
    {
        Directory.Delete(temporaryRoot, recursive: true);
    }
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
