using System.Text.Json.Serialization;

namespace KevinZonda.AgentUsageMonitor.KimiCode;

public enum KimiUsageAuthenticationMode
{
    Passive,
    Active
}

public enum KimiOAuthRegion
{
    MainlandChina,
    Global
}

public sealed record KimiDeviceAuthorization(string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt);

public sealed record KimiOAuthStatus(bool IsLoggedIn, KimiOAuthRegion? Region, DateTimeOffset? ExpiresAt);

internal sealed record KimiManagedToken(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    long ExpiresIn,
    string Scope,
    string TokenType);

internal sealed record KimiStoredAuthorization
{
    public int Version { get; init; } = 1;
    public string Revision { get; init; } = Guid.NewGuid().ToString("D");
    public string Generation { get; init; } = Guid.NewGuid().ToString("D");
    public string DeviceId { get; init; } = Guid.NewGuid().ToString("D");
    public string Region { get; init; } = "mainland-cn";
    public KimiManagedToken? Token { get; init; }
    public string? ProtectedToken { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(KimiStoredAuthorization))]
[JsonSerializable(typeof(KimiManagedToken))]
internal partial class KimiTokenJsonContext : JsonSerializerContext;
