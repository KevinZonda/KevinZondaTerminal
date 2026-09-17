# KevinZonda.AgentUsageMonitor

`KevinZonda.AgentUsageMonitor` is a dependency-free, cross-platform .NET library for detecting active Codex and Kimi
Code processes and reading their quota usage.

Supported sources:

- Kimi Code API key (`KIMI_CODE_API_KEY`)
- Kimi Code CLI OAuth credential (`~/.kimi-code/credentials/kimi-code.json`)
- Codex OAuth credential (`$CODEX_HOME/auth.json` or `~/.codex/auth.json`)
- Codex `app-server` JSON-RPC (`account/rateLimits/read`)

Explicitly not implemented:

- Kimi Desktop databases or `kimi-auth` browser cookies
- ChatGPT browser cookies, WebView, page scraping, or other ChatGPT Web integration
- Codex `/status` terminal-screen parsing

The Codex OAuth provider calls the authenticated usage API with the Bearer token from `auth.json`. This is an HTTP API
integration, not ChatGPT Web/browser automation.

## Usage

```csharp
using KevinZonda.AgentUsageMonitor;
using KevinZonda.AgentUsageMonitor.Codex;
using KevinZonda.AgentUsageMonitor.KimiCode;

using var http = new HttpClient();

var kimi = new KimiCodeUsageClient(http);
var kimiUsage = await kimi.GetUsageAsync();

var codex = new CodexUsageClient(http);
var codexUsage = await codex.GetUsageAsync();
```

Both providers implement the common `IUsageClient` contract. Provider-specific options can be injected once and the
clients can then be refreshed uniformly:

```csharp
IUsageClient[] clients =
[
    new CodexUsageClient(http, new CodexUsageOptions { Mode = CodexUsageMode.Auto }),
    new KimiCodeUsageClient(http, new KimiCodeUsageOptions { Mode = KimiCodeUsageMode.Auto }),
];

foreach (var client in clients)
{
    UsageSnapshot snapshot = await client.GetUsageAsync();
    Console.WriteLine($"{client.Provider}: {snapshot.Primary?.UsedPercent}%");
}
```

Both clients default to `Auto` mode. Kimi tries an API key and then the Kimi Code CLI credential. Codex tries its OAuth
credential and falls back to `codex app-server` only for missing or rejected credentials. Network and malformed-response
errors are surfaced instead of silently launching another process.

Kimi usage authentication has two modes. **Passive** (the default) reads fresh CLI credentials on each usage request
without refreshing or modifying them. After a 401, it retries once only when the CLI has saved a different, unexpired
access token. Run `kimi login` if Passive credentials expire or are rejected. The legacy `AutoRenewToken` and
`AutoRenewKimiToken` options do not enable renewal of CLI credentials.

In Passive mode, the monitor reads the managed Kimi provider's OAuth credential key and API base URL from `config.toml`, including
international logins. Without a CLI config, it uses `credentials/kimi-code.json` and the mainland API. An explicitly
supplied `BaseUri` overrides the configured API address.

**Active** completes a separate device-code OAuth authorization and manages its own access and refresh tokens in
`~/.kterm/kimi-token.json`. It uses its own stable device identity, renews credentials before usage requests when needed,
and retries a rejected access token once after renewal. It never imports CLI tokens or falls back to Passive. Windows
token contents are encrypted with DPAPI for the current user; Unix credential files are created with mode 0600.
Token rotation is serialized across instances and written atomically. Logout invalidates pending login commits.

Both desktop Settings dialogs have a **Kimi Usage** tab. Select **Active**, select Mainland China or Global, click
**Log in**, and authorize in the browser using the displayed code. Save Settings to apply the selected mode and region.
**Cancel login** stops pending authorization, and **Log out** removes only the Active authorization. Switching modes
does not delete either authorization.

```csharp
var oauth = new KimiOAuthManager(http);
await oauth.LoginAsync(KimiOAuthRegion.MainlandChina, code =>
{
    Console.WriteLine($"Open {code.VerificationUri}; code: {code.UserCode}");
    return Task.CompletedTask;
});
var active = new KimiCodeUsageClient(http, new KimiCodeUsageOptions
{
    AuthenticationMode = KimiUsageAuthenticationMode.Active,
    ActiveRegion = KimiOAuthRegion.MainlandChina
});
UsageSnapshot usage = await active.GetUsageAsync();
```

Applications that own terminal or process sessions can use `AgentUsageMonitorService` to detect provider processes,
refresh active providers, and publish UI-ready status updates. The application supplies only its current root process
IDs, so the monitor has no dependency on a terminal implementation or settings model:

```csharp
await using IAgentUsageMonitorService monitor = new AgentUsageMonitorService(
    () => terminalSessions.GetProcessIds());

monitor.StatusChanged += status => Render(status);
monitor.Start();
```

The monitor follows descendant process trees on Windows, macOS, and Linux. This allows a shell process to remain the
registered root while `codex` or `kimi-code` runs as a child process.

While a Kimi process is active, the monitor checks the selected mode's local credential changes every two seconds.
A changed token or API configuration triggers a usage request without waiting for the normal five-minute usage
interval. Unchanged credentials do not trigger additional usage requests. The monitor accepts both the current
`usages` quota response and the older `usage`/`limits` response.

## Build and test

```powershell
dotnet build .\KevinZonda.Terminal.slnx
dotnet run --project .\tests\KevinZonda.AgentUsageMonitor.Tests\KevinZonda.AgentUsageMonitor.Tests.csproj
```

An explicit live Codex smoke probe is available when local credentials may be used:

```powershell
dotnet run --project .\tests\KevinZonda.AgentUsageMonitor.Tests\KevinZonda.AgentUsageMonitor.Tests.csproj -- --live-codex
dotnet run --project .\tests\KevinZonda.AgentUsageMonitor.Tests\KevinZonda.AgentUsageMonitor.Tests.csproj -- --live-codex-rpc
dotnet run --project .\tests\KevinZonda.AgentUsageMonitor.Tests\KevinZonda.AgentUsageMonitor.Tests.csproj -- --live-kimi
```

The live probe prints quota data but excludes tokens and account email.
