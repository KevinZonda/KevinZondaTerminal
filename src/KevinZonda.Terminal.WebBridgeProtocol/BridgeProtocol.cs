using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KevinZonda.Terminal.WebBridgeProtocol;

public static class BridgeProtocol
{
    public const int CurrentVersion = 1;

    public static BridgeMessage Deserialize(string json) =>
        Validate(JsonSerializer.Deserialize(json, BridgeProtocolJsonContext.Default.BridgeMessage));

    public static BridgeMessage Deserialize(ReadOnlySpan<byte> utf8Json) =>
        Validate(JsonSerializer.Deserialize(utf8Json, BridgeProtocolJsonContext.Default.BridgeMessage));

    public static string Serialize(
        string type,
        string? requestId = null,
        string? sessionId = null,
        Action<Utf8JsonWriter>? writePayload = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteString("type", type);
            writer.WriteString("requestId", requestId);
            writer.WriteString("sessionId", sessionId);
            writer.WritePropertyName("payload");
            if (writePayload is null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                writePayload(writer);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string QuoteForJavaScript(string value) =>
        JsonSerializer.Serialize(value, BridgeProtocolJsonContext.Default.String);

    private static BridgeMessage Validate(BridgeMessage? message) =>
        message is null ||
        message.Version != CurrentVersion ||
        string.IsNullOrWhiteSpace(message.Type)
            ? throw new InvalidDataException("Unsupported bridge message.")
            : message;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BridgeMessage))]
[JsonSerializable(typeof(string))]
internal sealed partial class BridgeProtocolJsonContext : JsonSerializerContext;
