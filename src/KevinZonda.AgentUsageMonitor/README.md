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

Kimi CLI OAuth renewal is opt-in. Set `AutoRenewToken = true` to refresh an expiring token for the lifetime of the
`KimiCodeUsageClient` instance. Renewed access and refresh tokens remain in memory only; the CLI credential file and all
other files are left unchanged.

Applications that own terminal or process sessions can use `AgentUsageMonitorService` to detect provider processes,
refresh active providers, and publish UI-ready status updates. The application supplies only its current root process
IDs, so the monitor has no dependency on a terminal implementation or settings model:

```csharp
await using IAgentUsageMonitorService monitor = new AgentUsageMonitorService(
    () => terminalSessions.GetProcessIds(),
    new AgentUsageMonitorOptions { AutoRenewKimiToken = true });

monitor.StatusChanged += status => Render(status);
monitor.Start();
```

The monitor follows descendant process trees on Windows, macOS, and Linux. This allows a shell process to remain the
registered root while `codex` or `kimi-code` runs as a child process.

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
