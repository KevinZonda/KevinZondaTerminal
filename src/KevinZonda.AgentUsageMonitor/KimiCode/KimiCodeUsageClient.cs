using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KevinZonda.AgentUsageMonitor.KimiCode;

public sealed class KimiCodeUsageClient : IUsageClient
{
    private readonly HttpClient _httpClient;
    private readonly KimiCodeUsageOptions _options;

    public KimiCodeUsageClient(HttpClient httpClient, KimiCodeUsageOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new KimiCodeUsageOptions();
    }

    public UsageProvider Provider => UsageProvider.KimiCode;

    public bool AutoRenewToken => false;

    public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default) =>
        GetUsageAsync(_options, cancellationToken);

    internal async Task<string> GetCliCredentialVersionAsync(CancellationToken cancellationToken)
    {
        var credential = await KimiCodeCredentialStore.LoadAsync(_options, cancellationToken);
        if (credential is null)
        {
            return "missing";
        }

        var identity = $"{credential.AccessToken}\n{credential.ExpiresAt:O}\n{credential.BaseUri}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public async Task<UsageSnapshot> GetUsageAsync(
        KimiCodeUsageOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Exception? apiFailure = null;

        if (options.Mode is KimiCodeUsageMode.Auto or KimiCodeUsageMode.ApiKey)
        {
            var apiKey = FirstNotEmpty(options.ApiKey, Environment.GetEnvironmentVariable("KIMI_CODE_API_KEY"));
            if (apiKey is not null)
            {
                try
                {
                    return await FetchAsync(apiKey, UsageSource.KimiCodeApiKey, options, false, options.BaseUri, cancellationToken);
                }
                catch (Exception exception) when (options.Mode == KimiCodeUsageMode.Auto && CanFallback(exception))
                {
                    apiFailure = exception;
                }
            }

            if (options.Mode == KimiCodeUsageMode.ApiKey)
            {
                throw new UsageException(UsageErrorCode.MissingCredential, "KIMI_CODE_API_KEY is not configured.");
            }
        }

        var credential = await KimiCodeCredentialStore.LoadAsync(options, cancellationToken);
        if (credential is null || string.IsNullOrWhiteSpace(credential.AccessToken))
        {
            Rethrow(apiFailure);
            throw new UsageException(
                UsageErrorCode.MissingCredential,
                "Kimi Code credentials were not found. Run Kimi Code login or configure KIMI_CODE_API_KEY.");
        }

        if (credential.ExpiresAt is null || credential.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Rethrow(apiFailure);
            throw new UsageException(
                UsageErrorCode.InvalidCredential,
                "The Kimi Code CLI access token is expired. Run kimi login to renew it.");
        }

        try
        {
            return await FetchAsync(
                credential.AccessToken,
                UsageSource.KimiCodeCliCredential,
                options,
                true,
                credential.BaseUri,
                cancellationToken);
        }
        catch (UsageException exception) when (exception.Code == UsageErrorCode.InvalidCredential)
        {
            var latest = await KimiCodeCredentialStore.LoadAsync(options, cancellationToken);
            if (latest is null || string.IsNullOrWhiteSpace(latest.AccessToken)
                || latest.AccessToken == credential.AccessToken
                || latest.ExpiresAt is null || latest.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw;
            }

            return await FetchAsync(latest.AccessToken, UsageSource.KimiCodeCliCredential, options, true, latest.BaseUri, cancellationToken);
        }
    }

    internal static Uri BuildUsageUri(Uri baseUri)
    {
        if (!baseUri.IsAbsoluteUri || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new UsageException(
                UsageErrorCode.InvalidConfiguration,
                "Kimi Code BaseUri must be an absolute HTTPS URI without user information.");
        }

        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/coding/v1", StringComparison.OrdinalIgnoreCase))
        {
            path += "/usages";
        }
        else if (path.EndsWith("/coding", StringComparison.OrdinalIgnoreCase))
        {
            path += "/v1/usages";
        }
        else
        {
            path += "/coding/v1/usages";
        }

        builder.Path = path;
        return builder.Uri;
    }

    private async Task<UsageSnapshot> FetchAsync(
        string token,
        UsageSource source,
        KimiCodeUsageOptions options,
        bool addCliIdentity,
        Uri baseUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUsageUri(baseUri));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(options.UserAgent);

        if (addCliIdentity)
        {
            KimiCodeRequestHeaders.AddCliIdentity(request, options);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UsageException(UsageErrorCode.InvalidCredential, "The Kimi Code credential was rejected. Run kimi login to renew it.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UsageException(
                UsageErrorCode.RemoteError,
                $"Kimi Code usage API returned HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return KimiCodeUsageParser.Parse(data, source, DateTimeOffset.UtcNow);
        }
        catch (UsageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            throw new UsageException(UsageErrorCode.InvalidResponse, "Invalid Kimi Code usage response.", exception);
        }
    }

    private static string? FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static bool CanFallback(Exception exception) => exception switch
    {
        HttpRequestException => true,
        UsageException usageException when usageException.Code is
            UsageErrorCode.InvalidCredential or
            UsageErrorCode.InvalidResponse or
            UsageErrorCode.RemoteError => true,
        _ => false,
    };

    private static void Rethrow(Exception? exception)
    {
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
