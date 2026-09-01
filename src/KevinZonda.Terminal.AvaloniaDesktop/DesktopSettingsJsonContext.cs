using System.Text.Json;
using System.Text.Json.Serialization;

namespace KevinZonda.Terminal.AvaloniaDesktop;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    WriteIndented = true)]
[JsonSerializable(typeof(DesktopSettings))]
internal sealed partial class DesktopSettingsJsonContext : JsonSerializerContext;
