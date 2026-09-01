using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KevinZonda.AgentUsageMonitor.Codex;

public sealed class CodexUsageClient : IUsageClient
{
    private static readonly Uri DefaultApiBaseUri = new("https://chatgpt.com/backend-api/");
    private static readonly Uri RefreshUri = new("https://auth.openai.com/oauth/token");
    private const string OAuthClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private readonly HttpClient _httpClient;
    private readonly CodexUsageOptions _options;

    public CodexUsageClient(HttpClient httpClient, CodexUsageOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new CodexUsageOptions();
    }

    public UsageProvider Provider => UsageProvider.Codex;

    public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default) =>
        GetUsageAsync(_options, cancellationToken);

    public async Task<UsageSnapshot> GetUsageAsync(
        CodexUsageOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode == CodexUsageMode.AppServer)
        {
            return await FetchAppServerAsync(options, cancellationToken);
        }

        try
        {
            return await FetchOAuthAsync(options, cancellationToken);
        }
        catch (UsageException exception) when (
            options.Mode == CodexUsageMode.Auto
            && exception.Code is UsageErrorCode.MissingCredential or UsageErrorCode.InvalidCredential)
        {
            return await FetchAppServerAsync(options, cancellationToken);
        }
    }

    private async Task<UsageSnapshot> FetchOAuthAsync(
        CodexUsageOptions options,
        CancellationToken cancellationToken)
    {
        var credential = await CodexCredentialStore.LoadAsync(options, cancellationToken);
        if (credential.NeedsRefresh(DateTimeOffset.UtcNow) && credential.RefreshToken.Length > 0)
        {
            credential = await RefreshAsync(credential, cancellationToken);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, ResolveUsageUri(options));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("KevinZonda.AgentUsageMonitor/1.0");
        if (!string.IsNullOrWhiteSpace(credential.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UsageException(UsageErrorCode.InvalidCredential, "The Codex OAuth credential was rejected.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UsageException(
                UsageErrorCode.RemoteError,
                $"Codex usage API returned HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return CodexUsageParser.ParseOAuth(data, credential, DateTimeOffset.UtcNow);
        }
        catch (UsageException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new UsageException(UsageErrorCode.InvalidResponse, "Invalid Codex usage response.", exception);
        }
    }

    private async Task<UsageSnapshot> FetchAppServerAsync(
        CodexUsageOptions options,
        CancellationToken cancellationToken)
    {
        await using var appServer = await CodexAppServerClient.StartAsync(options, cancellationToken);
        var limits = await appServer.ReadRateLimitsAsync(cancellationToken);
        var account = await appServer.ReadAccountAsync(cancellationToken);
        return CodexUsageParser.ParseRpc(limits, account, DateTimeOffset.UtcNow);
    }

    private async Task<CodexCredential> RefreshAsync(
        CodexCredential credential,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RefreshUri)
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["client_id"] = OAuthClientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = credential.RefreshToken,
                    ["scope"] = "openid profile email"
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        };
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            throw new UsageException(UsageErrorCode.InvalidCredential, "The Codex refresh token is expired or invalid.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UsageException(
                UsageErrorCode.RemoteError,
                $"Codex token refresh returned HTTP {(int)response.StatusCode}.");
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            return credential with
            {
                AccessToken = ReadString(root, "access_token") ?? credential.AccessToken,
                RefreshToken = ReadString(root, "refresh_token") ?? credential.RefreshToken,
                IdToken = ReadString(root, "id_token") ?? credential.IdToken,
                LastRefresh = DateTimeOffset.UtcNow,
            };
        }
        catch (JsonException exception)
        {
            throw new UsageException(UsageErrorCode.InvalidResponse, "Invalid Codex token refresh response.", exception);
        }
    }

    internal static Uri ResolveUsageUri(CodexUsageOptions options)
    {
        var baseUri = options.ApiBaseUri ?? ReadConfiguredApiBaseUri(options) ?? DefaultApiBaseUri;
        if (!baseUri.IsAbsoluteUri || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new UsageException(
                UsageErrorCode.InvalidConfiguration,
                "Codex ApiBaseUri must be an absolute HTTPS URI without user information.");
        }

        var normalized = baseUri.AbsoluteUri.TrimEnd('/');
        if ((baseUri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase)
            || baseUri.Host.Equals("chat.openai.com", StringComparison.OrdinalIgnoreCase))
            && !baseUri.AbsolutePath.Contains("/backend-api", StringComparison.OrdinalIgnoreCase))
        {
            normalized += "/backend-api";
        }

        var path = normalized.Contains("/backend-api", StringComparison.OrdinalIgnoreCase)
            ? "/wham/usage"
            : "/api/codex/usage";
        return new Uri(normalized + path);
    }

    private static Uri? ReadConfiguredApiBaseUri(CodexUsageOptions options)
    {
        var config = Path.Combine(CodexCredentialStore.ResolveHome(options), "config.toml");
        if (!File.Exists(config))
        {
            return null;
        }

        foreach (var rawLine in File.ReadLines(config))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            var parts = line.Split('=', 2);
            if (parts.Length != 2 || parts[0].Trim() != "chatgpt_base_url")
            {
                continue;
            }

            var value = parts[1].Trim().Trim('"', '\'');
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                var normalized = uri.AbsoluteUri.TrimEnd('/');
                if ((uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("chat.openai.com", StringComparison.OrdinalIgnoreCase))
                    && !uri.AbsolutePath.Contains("/backend-api", StringComparison.OrdinalIgnoreCase))
                {
                    normalized += "/backend-api";
                }

                return new Uri(normalized);
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
