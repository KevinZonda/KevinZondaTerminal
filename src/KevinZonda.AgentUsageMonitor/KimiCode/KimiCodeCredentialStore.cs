using System.Text.Json;
using System.Text.Json.Serialization;
using KevinZonda.AgentUsageMonitor.Internal;
using Tomlyn;
using Tomlyn.Serialization;

namespace KevinZonda.AgentUsageMonitor.KimiCode;

internal sealed record KimiCodeCredential(
    string AccessToken,
    DateTimeOffset? ExpiresAt,
    Uri BaseUri);

internal static class KimiCodeCredentialStore
{
    public static string ResolveHome(KimiCodeUsageOptions options)
    {
        var configured = FirstNotEmpty(
            options.KimiCodeHome,
            Environment.GetEnvironmentVariable("KIMI_CODE_HOME"));
        return configured ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code");
    }

    public static async Task<KimiCodeCredential?> LoadAsync(
        KimiCodeUsageOptions options,
        CancellationToken cancellationToken)
    {
        var home = ResolveHome(options);
        var configPath = Path.Combine(home, "config.toml");
        KimiCliProvider? provider = null;
        if (File.Exists(configPath))
        {
            try
            {
                var config = TomlSerializer.Deserialize(
                    await File.ReadAllTextAsync(configPath, cancellationToken),
                    KimiCliConfigContext.Default.KimiCliConfig);
                if (config?.Providers?.TryGetValue("managed:kimi-code", out provider) != true || provider?.OAuth is null)
                {
                    return null;
                }
            }
            catch (TomlException)
            {
                throw new UsageException(UsageErrorCode.InvalidConfiguration, "Unable to read Kimi Code CLI config.toml.");
            }
        }

        var key = provider is null ? "oauth/kimi-code" : provider.OAuth?.Key;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new UsageException(UsageErrorCode.InvalidConfiguration, "Missing Kimi Code CLI credential key.");
        }
        var name = key.StartsWith("oauth/", StringComparison.Ordinal) ? key[6..] : key;
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.') || name.Contains('/') || name.Contains('\\'))
        {
            throw new UsageException(UsageErrorCode.InvalidConfiguration, "Invalid Kimi Code CLI credential key.");
        }

        if (provider?.OAuth?.Storage is { } storage && storage != "file")
        {
            throw new UsageException(UsageErrorCode.InvalidConfiguration, "Only file-based Kimi Code CLI credentials are supported.");
        }

        var baseUrl = FirstNotEmpty(provider?.BaseUrl) ?? "https://api.kimi.com/coding/v1";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var configuredBaseUri))
        {
            throw new UsageException(UsageErrorCode.InvalidConfiguration, "Invalid Kimi Code CLI base URL.");
        }

        var path = Path.Combine(home, "credentials", $"{name}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var accessToken = root.String("access_token", "accessToken")?.Trim() ?? string.Empty;
        DateTimeOffset? expiresAt = root.Double("expires_at", "expiresAt") is { } seconds && double.IsFinite(seconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(seconds * 1000)))
            : null;
        return new KimiCodeCredential(
            accessToken,
            expiresAt,
            options.ConfiguredBaseUri ?? configuredBaseUri);
    }

    public static string ResolveDeviceId(KimiCodeUsageOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DeviceId))
        {
            return options.DeviceId.Trim();
        }

        var path = Path.Combine(ResolveHome(options), "device_id");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length > 0)
            {
                return existing;
            }
        }

        return Guid.NewGuid().ToString("D");
    }

    private static string? FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

internal sealed class KimiCliConfig
{
    [JsonPropertyName("providers")]
    public Dictionary<string, KimiCliProvider>? Providers { get; set; }
}

internal sealed class KimiCliProvider
{
    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("oauth")]
    public KimiCliOAuthRef? OAuth { get; set; }
}

internal sealed class KimiCliOAuthRef
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("storage")]
    public string? Storage { get; set; }
}

[TomlSerializable(typeof(KimiCliConfig))]
internal partial class KimiCliConfigContext : TomlSerializerContext
{
}
