using System.Text.Json;
using System.Text.Json.Serialization;

namespace KevinZonda.Terminal.AvaloniaDesktop;

internal sealed record BridgeOutboundMessage
{
    public int Version { get; init; } = 1;

    public required string Type { get; init; }

    public string? RequestId { get; init; }

    public string? SessionId { get; init; }

    public BridgePayload Payload { get; init; } = new();
}

internal sealed record BridgePayload
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Application { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DesktopSettings? Settings { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentUsageStatus? AgentUsage { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SystemMetricsStatus? SystemMetrics { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Started { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShellName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProcessId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Failure { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BridgeMessage))]
[JsonSerializable(typeof(BridgeOutboundMessage))]
[JsonSerializable(typeof(string))]
internal sealed partial class BridgeJsonContext : JsonSerializerContext;

internal static class BridgeJson
{
    internal static BridgeMessage? Deserialize(string value) =>
        JsonSerializer.Deserialize(value, BridgeJsonContext.Default.BridgeMessage);

    internal static string Serialize(
        string type,
        string? requestId = null,
        string? sessionId = null,
        BridgePayload? payload = null) =>
        JsonSerializer.Serialize(
            new BridgeOutboundMessage
            {
                Type = type,
                RequestId = requestId,
                SessionId = sessionId,
                Payload = payload ?? new BridgePayload()
            },
            BridgeJsonContext.Default.BridgeOutboundMessage);

    internal static string QuoteForJavaScript(string value) =>
        JsonSerializer.Serialize(value, BridgeJsonContext.Default.String);
}
