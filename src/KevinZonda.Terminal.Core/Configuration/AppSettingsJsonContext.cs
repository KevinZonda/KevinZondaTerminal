using System.Text.Json;
using System.Text.Json.Serialization;

namespace KevinZonda.Terminal.Configuration;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
