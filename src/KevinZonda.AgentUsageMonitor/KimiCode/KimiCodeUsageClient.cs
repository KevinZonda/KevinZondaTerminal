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

    public bool AutoRenewToken => _options.AuthenticationMode == KimiUsageAuthenticationMode.Active;

    internal string? LastCredentialVersion { get; private set; }
    internal KimiCodeUsageOptions Options => _options;

    public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default) =>
        GetUsageAsync(_options, cancellationToken);

    internal async Task<string> GetCredentialVersionAsync(CancellationToken cancellationToken)
    {
        if (_options.AuthenticationMode == KimiUsageAuthenticationMode.Active)
            return await new KimiOAuthManager(_httpClient, _options.ActiveTokenPath).GetCredentialVersionAsync(cancellationToken)
                .ConfigureAwait(false);
        var credential = await KimiCodeCredentialStore.LoadAsync(_options, cancellationToken);
        if (credential is null)
        {
            return "missing";
        }

        return CredentialVersion(credential);
    }

    public async Task<UsageSnapshot> GetUsageAsync(
        KimiCodeUsageOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AuthenticationMode == KimiUsageAuthenticationMode.Active)
        {
            var oauth = new KimiOAuthManager(_httpClient, options.ActiveTokenPath);
            var active = await oauth.GetCredentialAsync(options.ActiveRegion, null, cancellationToken).ConfigureAwait(false);
            LastCredentialVersion = active.State.Revision;
            try
            {
                return await FetchAsync(active.State.Token!.AccessToken, UsageSource.KimiCodeManagedOAuth, options, true,
                    active.BaseUri, cancellationToken, active.State.DeviceId).ConfigureAwait(false);
            }
            catch (UsageException exception) when (exception.Code == UsageErrorCode.InvalidCredential)
            {
                active = await oauth.GetCredentialAsync(options.ActiveRegion, active.State.Token!.AccessToken, cancellationToken)
                    .ConfigureAwait(false);
                LastCredentialVersion = active.State.Revision;
                return await FetchAsync(active.State.Token!.AccessToken, UsageSource.KimiCodeManagedOAuth, options, true,
                    active.BaseUri, cancellationToken, active.State.DeviceId).ConfigureAwait(false);
            }
        }
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
            LastCredentialVersion = CredentialVersion(credential);
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

            LastCredentialVersion = CredentialVersion(latest);
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
        CancellationToken cancellationToken,
        string? deviceId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUsageUri(baseUri));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(options.UserAgent);

        if (addCliIdentity)
        {
            if (deviceId is null) KimiCodeRequestHeaders.AddCliIdentity(request, options);
            else KimiCodeRequestHeaders.AddIdentity(request, deviceId);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UsageException(UsageErrorCode.InvalidCredential, source == UsageSource.KimiCodeManagedOAuth
                ? "Kimi Active authorization was rejected. Log in again in Settings."
                : "The Kimi Code credential was rejected. Run kimi login to renew it.");
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

    private static string CredentialVersion(KimiCodeCredential credential)
    {
        var identity = $"{credential.AccessToken}\n{credential.ExpiresAt:O}\n{credential.BaseUri}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

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
