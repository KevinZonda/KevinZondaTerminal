using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using KevinZonda.AgentUsageMonitor.Internal;

namespace KevinZonda.AgentUsageMonitor.KimiCode;

public sealed class KimiOAuthManager
{
    private const string ClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";
    private readonly HttpClient _httpClient;
    private readonly KimiActiveCredentialStore _store;

    public KimiOAuthManager(HttpClient httpClient, string? tokenPath = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _store = new KimiActiveCredentialStore(tokenPath);
    }

    public async Task<KimiOAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new KimiOAuthStatus(state?.Token is not null,
            state is null ? null : ParseRegion(state.Region), state?.Token?.ExpiresAt);
    }

    public async Task LoginAsync(KimiOAuthRegion region, Func<KimiDeviceAuthorization, Task> onDeviceCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onDeviceCode);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var signal = timeout.Token;
        KimiStoredAuthorization initial;
        await using (var gate = await _store.AcquireLockAsync(signal).ConfigureAwait(false))
        {
            try { initial = await _store.LoadAsync(signal).ConfigureAwait(false) ?? new KimiStoredAuthorization(); }
            catch (UsageException) { initial = new KimiStoredAuthorization(); }
            initial = initial with { Revision = Guid.NewGuid().ToString("D"), Generation = Guid.NewGuid().ToString("D") };
            await _store.SaveAsync(initial, signal).ConfigureAwait(false);
        }

        var auth = await PostFormAsync(region, "/api/oauth/device_authorization", initial.DeviceId,
            new() { ["client_id"] = ClientId }, signal).ConfigureAwait(false);
        using var authorization = auth.Document;
        var root = authorization.RootElement;
        var deviceCode = root.String("device_code");
        var userCode = root.String("user_code");
        var verification = root.String("verification_uri_complete", "verification_uri");
        if (!auth.Status.IsSuccess() || string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(userCode)
            || !Uri.TryCreate(verification, UriKind.Absolute, out var verificationUri)
            || verificationUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(verificationUri.UserInfo))
            throw new UsageException(UsageErrorCode.InvalidResponse, "Kimi device authorization failed.");
        var interval = Math.Max(root.Int64("interval") ?? 5, 1);
        var lifetime = Math.Clamp(root.Int64("expires_in") ?? 900, 1, 900);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime);
        await onDeviceCode(new KimiDeviceAuthorization(userCode, verificationUri, expiresAt)).ConfigureAwait(false);

        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(interval, 900)), signal).ConfigureAwait(false);
            var response = await PostFormAsync(region, "/api/oauth/token", initial.DeviceId, new()
            {
                ["client_id"] = ClientId,
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }, signal).ConfigureAwait(false);
            using var document = response.Document;
            if (response.Status.IsSuccess())
            {
                var token = ParseToken(document.RootElement);
                await using var gate = await _store.AcquireLockAsync(signal).ConfigureAwait(false);
                var current = await _store.LoadAsync(signal).ConfigureAwait(false);
                if (current?.Generation != initial.Generation)
                    throw new UsageException(UsageErrorCode.InvalidCredential, "Kimi login was superseded or signed out. Start login again.");
                await _store.SaveAsync(current with
                {
                    Revision = Guid.NewGuid().ToString("D"), Generation = Guid.NewGuid().ToString("D"), Region = RegionKey(region), Token = token
                }, signal).ConfigureAwait(false);
                return;
            }
            switch (document.RootElement.String("error"))
            {
                case "authorization_pending": break;
                case "slow_down": interval = Math.Min(interval + 5, 900); break;
                case "access_denied": throw new UsageException(UsageErrorCode.InvalidCredential, "Kimi login was denied.");
                case "expired_token": throw new UsageException(UsageErrorCode.InvalidCredential, "Kimi login code expired. Start login again.");
                default: throw new UsageException(UsageErrorCode.RemoteError, $"Kimi login returned HTTP {(int)response.Status}.");
            }
        }
        throw new UsageException(UsageErrorCode.InvalidCredential, "Kimi login code expired. Start login again.");
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await using var gate = await _store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        KimiStoredAuthorization state;
        try { state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? new KimiStoredAuthorization(); }
        catch (UsageException) { state = new KimiStoredAuthorization(); }
        await _store.SaveAsync(state with
        {
            Token = null, ProtectedToken = null, Revision = Guid.NewGuid().ToString("D"), Generation = Guid.NewGuid().ToString("D")
        }, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<string> GetCredentialVersionAsync(CancellationToken cancellationToken) =>
        (await _store.LoadAsync(cancellationToken).ConfigureAwait(false))?.Revision ?? "missing";

    internal async Task<(KimiStoredAuthorization State, Uri BaseUri)> GetCredentialAsync(KimiOAuthRegion region,
        string? rejectedAccessToken, CancellationToken cancellationToken)
    {
        await using var gate = await _store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state?.Token is not { } token || state.Region != RegionKey(region)
            || string.IsNullOrWhiteSpace(token.AccessToken) || string.IsNullOrWhiteSpace(token.RefreshToken))
            throw new UsageException(UsageErrorCode.MissingCredential, "Log in to Kimi Active for this region in Settings.");
        var refresh = rejectedAccessToken is null
            ? token.ExpiresAt - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(Math.Max(300, token.ExpiresIn / 2d))
            : token.AccessToken == rejectedAccessToken;
        if (refresh)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var response = await PostFormAsync(region, "/api/oauth/token", state.DeviceId, new()
                    {
                        ["client_id"] = ClientId, ["grant_type"] = "refresh_token", ["refresh_token"] = token.RefreshToken
                    }, cancellationToken).ConfigureAwait(false);
                    using var document = response.Document;
                    if (response.Status.IsSuccess())
                    {
                        state = state with { Token = ParseToken(document.RootElement), Revision = Guid.NewGuid().ToString("D") };
                        await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        || document.RootElement.String("error") == "invalid_grant")
                    {
                        await _store.SaveAsync(state with { Token = null, Revision = Guid.NewGuid().ToString("D") }, CancellationToken.None)
                            .ConfigureAwait(false);
                        throw new UsageException(UsageErrorCode.InvalidCredential, "Kimi Active authorization expired. Log in again in Settings.");
                    }
                    if (attempt >= 2 || response.Status != HttpStatusCode.TooManyRequests && (int)response.Status < 500)
                        throw new UsageException(UsageErrorCode.RemoteError, $"Kimi token refresh returned HTTP {(int)response.Status}.");
                }
                catch (HttpRequestException) when (attempt < 2) { }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 2) { }
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        return (state, ApiBaseUri(region));
    }

    private async Task<(HttpStatusCode Status, JsonDocument Document)> PostFormAsync(KimiOAuthRegion region,
        string path, string deviceId, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(OAuthBaseUri(region), path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("KevinZonda.AgentUsageMonitor/1.0");
        KimiCodeRequestHeaders.AddIdentity(request, deviceId);
        request.Content = new FormUrlEncodedContent(form);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        try
        {
            return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false)));
        }
        catch (JsonException)
        {
            if (!response.IsSuccessStatusCode) return (response.StatusCode, JsonDocument.Parse("{}"));
            throw new UsageException(UsageErrorCode.InvalidResponse, "Kimi OAuth returned an invalid response.");
        }
    }

    private static KimiManagedToken ParseToken(JsonElement root)
    {
        var access = root.String("access_token");
        var refresh = root.String("refresh_token");
        var expiresIn = root.Int64("expires_in");
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh) || expiresIn is null or <= 0 or > 31_536_000)
            throw new UsageException(UsageErrorCode.InvalidResponse, "Kimi OAuth returned an incomplete credential.");
        return new KimiManagedToken(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value), expiresIn.Value,
            root.String("scope") ?? string.Empty, root.String("token_type") ?? "Bearer");
    }

    private static string RegionKey(KimiOAuthRegion region) => region switch
    {
        KimiOAuthRegion.MainlandChina => "mainland-cn",
        KimiOAuthRegion.Global => "global",
        _ => throw new ArgumentOutOfRangeException(nameof(region))
    };

    private static KimiOAuthRegion ParseRegion(string region) => region == "global" ? KimiOAuthRegion.Global : KimiOAuthRegion.MainlandChina;
    private static Uri OAuthBaseUri(KimiOAuthRegion region) => new(region == KimiOAuthRegion.Global ? "https://auth.kimi.ai" : "https://auth.kimi.com");
    internal static Uri ApiBaseUri(KimiOAuthRegion region) => new(region == KimiOAuthRegion.Global ? "https://api.kimi.ai/coding/v1" : "https://api.kimi.com/coding/v1");
}

internal static class KimiOAuthHttpStatus
{
    internal static bool IsSuccess(this HttpStatusCode status) => (int)status is >= 200 and < 300;
}
