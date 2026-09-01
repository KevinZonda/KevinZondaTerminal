using System.Text.Json;

namespace KevinZonda.Terminal.WebBridgeProtocol;

public sealed class BridgeMessage
{
    public int Version { get; init; }

    public string Type { get; init; } = string.Empty;

    public string? RequestId { get; init; }

    public string? SessionId { get; init; }

    public JsonElement Payload { get; init; }
}
