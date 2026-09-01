using System.Text.Json;
using System.Text.Json.Serialization;
using KevinZonda.AgentUsageMonitor;
using KevinZonda.SystemMetrics;
using KevinZonda.Terminal.Configuration;
using KevinZonda.Terminal.WebBridgeProtocol;

namespace KevinZonda.Terminal.AvaloniaDesktop;

internal sealed record BridgePayload
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Application { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AppSettings? Settings { get; init; }

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
[JsonSerializable(typeof(BridgePayload))]
internal sealed partial class BridgeJsonContext : JsonSerializerContext;

internal static class BridgeJson
{
    internal static BridgeMessage Deserialize(string value) =>
        BridgeProtocol.Deserialize(value);

    internal static string Serialize(
        string type,
        string? requestId = null,
        string? sessionId = null,
        BridgePayload? payload = null)
    {
        return BridgeProtocol.Serialize(
            type,
            requestId,
            sessionId,
            writer => JsonSerializer.Serialize(
                writer,
                payload ?? new BridgePayload(),
                BridgeJsonContext.Default.BridgePayload));
    }

    internal static string QuoteForJavaScript(string value) =>
        BridgeProtocol.QuoteForJavaScript(value);
}
