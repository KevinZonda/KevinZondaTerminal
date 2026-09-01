using System.Text.Json;

namespace KevinZonda.Terminal.WebBridgeProtocol;

public static class BridgePayloadReader
{
    public static string RequireSessionId(BridgeMessage message) =>
        string.IsNullOrWhiteSpace(message.SessionId)
            ? throw new InvalidDataException("The message is missing a session ID.")
            : message.SessionId;

    public static string GetString(JsonElement payload, string propertyName) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    public static int GetInt32(JsonElement payload, string propertyName, int defaultValue) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt32(out var value)
            ? value
            : defaultValue;

    public static long GetInt64(JsonElement payload, string propertyName, long defaultValue) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt64(out var value)
            ? value
            : defaultValue;

    public static double GetDouble(JsonElement payload, string propertyName, double defaultValue) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(propertyName, out var property) &&
        property.TryGetDouble(out var value)
            ? value
            : defaultValue;
}
