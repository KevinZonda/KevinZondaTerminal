using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KevinZonda.AgentUsageMonitor.Codex;

internal sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly TimeSpan _requestTimeout;
    private int _nextId;

    private CodexAppServerClient(Process process, TimeSpan requestTimeout)
    {
        _process = process;
        _requestTimeout = requestTimeout;
    }

    public static async Task<CodexAppServerClient> StartAsync(
        CodexUsageOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await StartAsync(options, "never", cancellationToken);
        }
        catch (UsageException exception) when (ShouldRetryWithLegacyApprovalPolicy(exception))
        {
            return await StartAsync(options, "untrusted", cancellationToken);
        }
    }

    private static async Task<CodexAppServerClient> StartAsync(
        CodexUsageOptions options,
        string approvalPolicy,
        CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable(options.CodexExecutable);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        AddLaunchArguments(startInfo, executable, approvalPolicy);
        if (!string.IsNullOrWhiteSpace(options.CodexHome))
        {
            startInfo.Environment["CODEX_HOME"] = options.CodexHome;
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new UsageException(UsageErrorCode.ProcessError, "Failed to start codex app-server.");
            }
        }
        catch (Exception exception) when (exception is not UsageException)
        {
            process.Dispose();
            throw new UsageException(UsageErrorCode.ProcessError, "Failed to start codex app-server.", exception);
        }

        var client = new CodexAppServerClient(process, options.RpcRequestTimeout);
        try
        {
            await client.RequestAsync(
                "initialize",
                new JsonObject
                {
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "kevinzonda-agent-usage-monitor",
                        ["version"] = "1.0.0"
                    }
                },
                options.RpcInitializeTimeout,
                cancellationToken);
            await client.SendAsync(
                new JsonObject
                {
                    ["method"] = "initialized",
                    ["params"] = new JsonObject()
                },
                cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static bool ShouldRetryWithLegacyApprovalPolicy(UsageException exception) =>
        exception.Code == UsageErrorCode.ProcessError
        && exception.Message.Contains("invalid value", StringComparison.OrdinalIgnoreCase)
        && exception.Message.Contains("never", StringComparison.OrdinalIgnoreCase)
        && exception.Message.Contains("--ask-for-approval", StringComparison.OrdinalIgnoreCase)
        && exception.Message.Contains("untrusted", StringComparison.OrdinalIgnoreCase);

    private static string ResolveExecutable(string configured)
    {
        if (!OperatingSystem.IsWindows() || Path.IsPathRooted(configured))
        {
            return configured;
        }

        var extension = Path.GetExtension(configured);
        var extensions = extension.Length > 0
            ? [string.Empty]
            : new[] { ".exe", ".com", ".cmd", ".bat", ".ps1" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidateExtension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), configured + candidateExtension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return configured;
    }

    private static void AddLaunchArguments(
        ProcessStartInfo startInfo,
        string executable,
        string approvalPolicy)
    {
        var arguments = new[] { "-s", "read-only", "-a", approvalPolicy, "app-server" };
        if (OperatingSystem.IsWindows()
            && Path.GetExtension(executable) is ".cmd" or ".bat")
        {
            var packageScript = Path.Combine(
                Path.GetDirectoryName(executable)!,
                "node_modules",
                "@openai",
                "codex",
                "bin",
                "codex.js");
            if (File.Exists(packageScript))
            {
                startInfo.FileName = ResolveExecutable("node");
                startInfo.ArgumentList.Add(packageScript);
            }
            else
            {
                startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/s");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add($"\"\"{executable}\" {string.Join(' ', arguments)}\"");
                return;
            }
        }

        if (OperatingSystem.IsWindows()
            && string.Equals(Path.GetExtension(executable), ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(executable);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    public Task<JsonElement> ReadRateLimitsAsync(CancellationToken cancellationToken) =>
        RequestAsync("account/rateLimits/read", new JsonObject(), _requestTimeout, cancellationToken);

    public async Task<JsonElement?> ReadAccountAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RequestAsync("account/read", new JsonObject(), _requestTimeout, cancellationToken);
        }
        catch (UsageException)
        {
            return null;
        }
    }

    private async Task<JsonElement> RequestAsync(
        string method,
        JsonObject parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        await SendAsync(
            new JsonObject
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters
            },
            cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(linked.Token);
                if (line is null)
                {
                    var stderr = await _process.StandardError.ReadToEndAsync(cancellationToken);
                    throw new UsageException(
                        UsageErrorCode.ProcessError,
                        $"codex app-server closed its output. {stderr}".Trim());
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var text) ? text.GetString() : error.GetRawText();
                    throw new UsageException(UsageErrorCode.RemoteError, $"Codex RPC {method} failed: {message}");
                }

                if (!root.TryGetProperty("result", out var result))
                {
                    throw new UsageException(UsageErrorCode.InvalidResponse, $"Codex RPC {method} returned no result.");
                }

                return result.Clone();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UsageException(UsageErrorCode.Timeout, $"Codex RPC {method} timed out.");
        }
        catch (JsonException exception)
        {
            throw new UsageException(UsageErrorCode.InvalidResponse, "Codex app-server returned invalid JSON.", exception);
        }
    }

    private async Task SendAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var json = payload.ToJsonString();
        await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _process.Dispose();
        }
    }
}
