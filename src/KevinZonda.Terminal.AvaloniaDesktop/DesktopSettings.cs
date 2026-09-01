using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KevinZonda.Terminal.AvaloniaDesktop;

internal sealed record DesktopSettings
{
    public DesktopFontSettings Font { get; init; } = new();

    public DesktopThemeSettings Theme { get; init; } = new();

    public DesktopCursorSettings Cursor { get; init; } = new();

    public DesktopIndicatorSettings Indicators { get; init; } = new();

    public DesktopShellSettings Shell { get; init; } = new();

    internal static DesktopSettings Normalize(DesktopSettings? settings)
    {
        var source = settings ?? new DesktopSettings();
        var family = source.Font.Family.Trim();
        return source with
        {
            Font = source.Font with
            {
                Family = family.Length is > 0 and <= 256
                    ? family
                    : DesktopFontSettings.DefaultFamily,
                Size = double.IsFinite(source.Font.Size)
                    ? Math.Clamp(source.Font.Size, 8, 72)
                    : 14,
                LineHeight = double.IsFinite(source.Font.LineHeight)
                    ? Math.Clamp(source.Font.LineHeight, 0.8, 2)
                    : 1.12
            },
            Cursor = source.Cursor with
            {
                Shape = source.Cursor.Shape is "block" or "underline" ? source.Cursor.Shape : "bar"
            },
            Shell = source.Shell with
            {
                ExitBehavior = source.Shell.ExitBehavior == "CloseTab" ? "CloseTab" : "KeepTab"
            }
        };
    }
}

internal sealed record DesktopFontSettings
{
    internal const string DefaultFamily =
        "Cascadia Mono, Cascadia Code, Menlo, Consolas, Microsoft YaHei, monospace";

    public string Family { get; init; } = DefaultFamily;

    public double Size { get; init; } = 14;

    public double LineHeight { get; init; } = 1.12;

    public bool EnableLigatures { get; init; }
}

internal sealed record DesktopThemeSettings
{
    public string Name { get; init; } = "KevinZonda Terminal Dark";
}

internal sealed record DesktopCursorSettings
{
    public string Shape { get; init; } = "bar";

    public bool Blink { get; init; } = true;
}

internal sealed record DesktopIndicatorSettings
{
    public bool ShowWorkspaceIndicator { get; init; } = true;

    public bool ShowRemainingUsage { get; init; }

    public bool AutoRenewKimiToken { get; init; }
}

internal sealed record DesktopShellSettings
{
    public string ExitBehavior { get; init; } = "KeepTab";
}

internal sealed class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".kterm",
        "config.json");

    internal DesktopSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? DesktopSettings.Normalize(JsonSerializer.Deserialize<DesktopSettings>(
                    File.ReadAllText(_path, Encoding.UTF8),
                    JsonOptions))
                : DesktopSettings.Normalize(null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return DesktopSettings.Normalize(null);
        }
    }

    internal async Task<DesktopSettings> SaveFontSizeAsync(
        DesktopSettings current,
        double size,
        CancellationToken cancellationToken = default)
    {
        var settings = DesktopSettings.Normalize(current with
        {
            Font = current.Font with { Size = size }
        });
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".config.{Guid.NewGuid():N}.tmp");
        try
        {
            JsonObject root;
            try
            {
                root = File.Exists(_path)
                    ? JsonNode.Parse(await File.ReadAllTextAsync(_path, cancellationToken)
                        .ConfigureAwait(false)) as JsonObject ?? new JsonObject()
                    : new JsonObject();
            }
            catch (JsonException)
            {
                root = new JsonObject();
            }

            var font = root["font"] as JsonObject ?? new JsonObject();
            font["size"] = settings.Font.Size;
            root["font"] = font;
            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToJsonString(JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _path, overwrite: true);
            return settings;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
