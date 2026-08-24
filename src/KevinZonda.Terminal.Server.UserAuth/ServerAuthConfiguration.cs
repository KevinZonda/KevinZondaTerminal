using System.Text.Json.Serialization;

namespace KevinZonda.Terminal.Server.UserAuth;

public sealed record ServerAuthConfiguration
{
    public const int MaximumAllowedHashes = 8;

    [JsonPropertyName("allowedHash")]
    public string[] AllowedHash { get; init; } = [];
}
