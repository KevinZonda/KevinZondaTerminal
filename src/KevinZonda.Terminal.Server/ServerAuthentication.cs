using KevinZonda.Terminal.Server.UserAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace KevinZonda.Terminal.Server;

internal enum ServerAuthenticationMode
{
    Auto,
    Required,
    Disabled
}

internal sealed record ServerAuthenticationState(
    ServerAuthenticationMode Mode,
    string UserName,
    string ConfigurationPath,
    ServerAuthConfiguration? Configuration,
    string? ConfigurationFingerprint,
    bool FellBackToNoPassword)
{
    internal bool Enabled => Configuration is not null;
}

internal static class ServerAuthentication
{
    internal const string CookieScheme = "KTerm.Cookie";
    internal const string DefaultUserName = "kterm";
    internal const string ConfigurationFingerprintClaim = "kterm:auth-config";

    internal static async Task<ServerAuthenticationState> LoadAsync(
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var mode = ParseMode(configuration["auth-mode"]);
        var userName = ParseUserName(configuration["custom-username"]);
        var configuredPath = configuration["auth-file"];
        var store = new ServerAuthStore(configuredPath);
        if (mode == ServerAuthenticationMode.Disabled)
        {
            return new ServerAuthenticationState(
                mode,
                userName,
                store.ConfigurationPath,
                null,
                null,
                false);
        }

        if (!File.Exists(store.ConfigurationPath))
        {
            if (mode == ServerAuthenticationMode.Required)
            {
                throw new AuthConfigurationException(
                    $"The server authentication configuration does not exist: {store.ConfigurationPath}");
            }
            return new ServerAuthenticationState(
                mode,
                userName,
                store.ConfigurationPath,
                null,
                null,
                true);
        }

        var authConfiguration = await store.LoadAllowingEmptyAsync(cancellationToken).ConfigureAwait(false);
        if (authConfiguration.AllowedHash.Length == 0)
        {
            if (mode == ServerAuthenticationMode.Required)
            {
                throw new AuthConfigurationException("At least one allowed hash is required in required auth mode.");
            }
            return new ServerAuthenticationState(
                mode,
                userName,
                store.ConfigurationPath,
                null,
                null,
                true);
        }

        return new ServerAuthenticationState(
            mode,
            userName,
            store.ConfigurationPath,
            authConfiguration,
            ComputeConfigurationFingerprint(userName, authConfiguration.AllowedHash),
            false);
    }

    internal static void AddServices(IServiceCollection services, ServerAuthenticationState state)
    {
        if (!state.Enabled)
        {
            return;
        }

        services.AddSingleton(state);
        services.AddSingleton<Argon2PasswordService>();
        services.AddSingleton<ServerPasswordVerifier>();
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = CookieScheme;
                options.DefaultChallengeScheme = CookieScheme;
                options.DefaultSignInScheme = CookieScheme;
            })
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = "kterm.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.SlidingExpiration = true;
                options.LoginPath = "/auth/login";
                options.ReturnUrlParameter = "returnUrl";
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/ws") ||
                        context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    }
                    else
                    {
                        context.Response.Redirect(context.RedirectUri);
                    }
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = context =>
                {
                    var cookieFingerprint = context.Principal?
                        .FindFirst(ConfigurationFingerprintClaim)?
                        .Value;
                    if (!string.Equals(
                            cookieFingerprint,
                            state.ConfigurationFingerprint,
                            StringComparison.Ordinal))
                    {
                        context.RejectPrincipal();
                    }
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization();
    }

    internal static ClaimsPrincipal CreatePrincipal(ServerAuthenticationState state)
    {
        if (!state.Enabled || state.ConfigurationFingerprint is null)
        {
            throw new InvalidOperationException("Password authentication is not enabled.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, state.UserName),
                new Claim(ClaimTypes.Name, state.UserName),
                new Claim(ConfigurationFingerprintClaim, state.ConfigurationFingerprint)
            ],
            CookieScheme);
        return new ClaimsPrincipal(identity);
    }

    internal static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) ||
            !returnUrl.StartsWith("/", StringComparison.Ordinal) ||
            returnUrl.StartsWith("//", StringComparison.Ordinal) ||
            returnUrl.StartsWith("/\\", StringComparison.Ordinal))
        {
            return "/";
        }
        return returnUrl;
    }

    private static ServerAuthenticationMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => ServerAuthenticationMode.Auto,
        "required" => ServerAuthenticationMode.Required,
        "disabled" => ServerAuthenticationMode.Disabled,
        _ => throw new AuthConfigurationException(
            $"Unsupported auth mode '{value}'. Expected auto, required, or disabled.")
    };

    private static string ParseUserName(string? value)
    {
        var userName = value?.Trim();
        if (string.IsNullOrEmpty(userName))
        {
            return DefaultUserName;
        }
        if (userName.Length > 128)
        {
            throw new AuthConfigurationException("The custom username must be 128 characters or fewer.");
        }
        if (userName.Contains(':') || userName.Any(char.IsControl))
        {
            throw new AuthConfigurationException(
                "The custom username cannot contain a colon or control characters.");
        }
        return userName;
    }

    private static string ComputeConfigurationFingerprint(
        string userName,
        IEnumerable<string> hashes)
    {
        var serializedConfiguration = userName + '\0' + string.Join('\0', hashes);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serializedConfiguration)));
    }
}

internal sealed class ServerPasswordVerifier
{
    private const int MaximumConcurrentVerifications = 2;

    private readonly ServerAuthenticationState _state;
    private readonly Argon2PasswordService _passwords;
    private readonly SemaphoreSlim _verificationGate = new(
        MaximumConcurrentVerifications,
        MaximumConcurrentVerifications);

    public ServerPasswordVerifier(
        ServerAuthenticationState state,
        Argon2PasswordService passwords)
    {
        _state = state;
        _passwords = passwords;
    }

    internal async Task<bool> VerifyAsync(string password, CancellationToken cancellationToken)
    {
        var hashes = _state.Configuration?.AllowedHash;
        if (hashes is null || hashes.Length == 0)
        {
            return false;
        }

        await _verificationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => _passwords.VerifyAny(password, hashes)).ConfigureAwait(false);
        }
        finally
        {
            _verificationGate.Release();
        }
    }
}
