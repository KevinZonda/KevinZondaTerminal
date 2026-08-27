using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace KevinZonda.Terminal.Server;

internal sealed class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const int MaximumAuthorizationHeaderLength = 8 * 1024;
    private const int MaximumDecodedCredentialBytes = 4 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ServerPasswordVerifier _passwords;
    private readonly ServerAuthenticationState _state;

    public BasicAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ServerPasswordVerifier passwords,
        ServerAuthenticationState state)
        : base(options, logger, encoder)
    {
        _passwords = passwords;
        _state = state;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var rawHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(rawHeader))
        {
            return AuthenticateResult.NoResult();
        }
        if (rawHeader.Length > MaximumAuthorizationHeaderLength ||
            !AuthenticationHeaderValue.TryParse(rawHeader, out var header) ||
            !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
        {
            return AuthenticateResult.Fail("Invalid Basic authentication header.");
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(header.Parameter);
        }
        catch (FormatException)
        {
            return AuthenticateResult.Fail("Invalid Basic authentication header.");
        }

        try
        {
            if (decoded.Length == 0 || decoded.Length > MaximumDecodedCredentialBytes)
            {
                return AuthenticateResult.Fail("Invalid Basic authentication credentials.");
            }

            string credentials;
            try
            {
                credentials = StrictUtf8.GetString(decoded);
            }
            catch (DecoderFallbackException)
            {
                return AuthenticateResult.Fail("Invalid Basic authentication credentials.");
            }

            var separator = credentials.IndexOf(':');
            if (separator <= 0 ||
                !string.Equals(
                    credentials[..separator],
                    _state.UserName,
                    StringComparison.Ordinal))
            {
                return AuthenticateResult.Fail("Invalid Basic authentication credentials.");
            }

            var password = credentials[(separator + 1)..];
            if (!await _passwords.VerifyAsync(password, Context.RequestAborted).ConfigureAwait(false))
            {
                return AuthenticateResult.Fail("Invalid Basic authentication credentials.");
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, _state.UserName),
                    new Claim(ClaimTypes.Name, _state.UserName),
                    new Claim(
                        ServerAuthentication.ConfigurationFingerprintClaim,
                        _state.ConfigurationFingerprint!)
                ],
                Scheme.Name);
            return AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Basic realm=\"KTerm\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }
}
