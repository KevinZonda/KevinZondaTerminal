namespace KevinZonda.AgentUsageMonitor.KimiCode;

public enum KimiCodeUsageMode
{
    Auto,
    ApiKey,
    CliCredential,
}

public sealed class KimiCodeUsageOptions
{
    private Uri? _baseUri;

    public KimiCodeUsageMode Mode { get; init; } = KimiCodeUsageMode.Auto;

    public string? ApiKey { get; init; }

    public Uri BaseUri
    {
        get => _baseUri ?? new Uri("https://api.kimi.com");
        init => _baseUri = value;
    }

    internal Uri? ConfiguredBaseUri => _baseUri;

    /// <summary>Legacy option, ignored. The monitor does not send OAuth requests.</summary>
    public Uri OAuthBaseUri { get; init; } = new("https://auth.kimi.com");

    public string? KimiCodeHome { get; init; }

    public string? DeviceId { get; init; }

    /// <summary>
    /// Legacy option, ignored. OAuth credentials are renewed only by Kimi Code CLI.
    /// </summary>
    public bool AutoRenewToken { get; init; }

    public string UserAgent { get; init; } = "KevinZonda.AgentUsageMonitor/1.0";
}
