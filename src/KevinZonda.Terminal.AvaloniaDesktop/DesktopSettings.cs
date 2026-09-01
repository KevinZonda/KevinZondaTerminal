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
        var font = source.Font ?? new DesktopFontSettings();
        var theme = source.Theme ?? new DesktopThemeSettings();
        var cursor = source.Cursor ?? new DesktopCursorSettings();
        var indicators = source.Indicators ?? new DesktopIndicatorSettings();
        var shell = source.Shell ?? new DesktopShellSettings();
        var family = font.Family?.Trim();
        return source with
        {
            Font = font with
            {
                Family = family?.Length is > 0 and <= 256
                    ? family
                    : DesktopFontSettings.DefaultFamily,
                Size = double.IsFinite(font.Size)
                    ? Math.Clamp(font.Size, 8, 72)
                    : 14,
                LineHeight = double.IsFinite(font.LineHeight)
                    ? Math.Clamp(font.LineHeight, 0.8, 2)
                    : 1.12
            },
            Theme = theme with
            {
                Name = DesktopTerminalThemeCatalog.Find(theme.Name).Name
            },
            Cursor = cursor with
            {
                Shape = cursor.Shape is "block" or "underline" ? cursor.Shape : "bar"
            },
            Indicators = indicators,
            Shell = shell with
            {
                ExitBehavior = shell.ExitBehavior == "CloseTab" ? "CloseTab" : "KeepTab"
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
    public string Name { get; init; } = DesktopTerminalThemeCatalog.DefaultName;
}

internal sealed record DesktopTerminalThemePreset(
    string Name,
    string Background,
    string Foreground,
    string Cursor);

internal static class DesktopTerminalThemeCatalog
{
    internal const string DefaultName = "KevinZonda Terminal Dark";

    internal static IReadOnlyList<DesktopTerminalThemePreset> All { get; } =
    [
        new(DefaultName, "#0c0f14", "#d8dee9", "#8fbcbb"),
        new("Pro", "#000000", "#f2f2f2", "#4d4d4d"),
        new("Ubuntu", "#300a24", "#eeeeec", "#bbbbbb"),
        new("Campbell Powershell", "#012456", "#CCCCCC", "#FFFFFF"),
        new("Builtin Tango Dark", "#000000", "#ffffff", "#ffffff"),
        new("Campbell", "#0C0C0C", "#CCCCCC", "#FFFFFF"),
        new("IBM 5153", "#000000", "#AAAAAA", "#00AA00")
    ];

    internal static DesktopTerminalThemePreset Find(string? name) =>
        All.FirstOrDefault(theme => string.Equals(
            theme.Name,
            name,
            StringComparison.OrdinalIgnoreCase)) ?? All[0];
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

    private readonly string _path;

    internal DesktopSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".kterm",
            "config.json");
    }

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
        var root = await LoadRootAsync(cancellationToken).ConfigureAwait(false);
        GetObject(root, "font")["size"] = settings.Font.Size;
        await SaveRootAsync(root, cancellationToken).ConfigureAwait(false);
        return settings;
    }

    internal async Task<DesktopSettings> SaveAsync(
        DesktopSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = DesktopSettings.Normalize(settings);
        var root = await LoadRootAsync(cancellationToken).ConfigureAwait(false);

        var font = GetObject(root, "font");
        font["family"] = normalized.Font.Family;
        font["size"] = normalized.Font.Size;
        font["lineHeight"] = normalized.Font.LineHeight;
        font["enableLigatures"] = normalized.Font.EnableLigatures;

        GetObject(root, "theme")["name"] = normalized.Theme.Name;

        var cursor = GetObject(root, "cursor");
        cursor["shape"] = normalized.Cursor.Shape;
        cursor["blink"] = normalized.Cursor.Blink;

        var indicators = GetObject(root, "indicators");
        indicators["showWorkspaceIndicator"] = normalized.Indicators.ShowWorkspaceIndicator;
        indicators["showRemainingUsage"] = normalized.Indicators.ShowRemainingUsage;
        indicators["autoRenewKimiToken"] = normalized.Indicators.AutoRenewKimiToken;

        GetObject(root, "shell")["exitBehavior"] = normalized.Shell.ExitBehavior;

        await SaveRootAsync(root, cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    private async Task<JsonObject> LoadRootAsync(CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(_path)
                ? JsonNode.Parse(await File.ReadAllTextAsync(_path, cancellationToken)
                    .ConfigureAwait(false)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private async Task SaveRootAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The settings path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".config.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToJsonString(JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static JsonObject GetObject(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonObject value)
        {
            return value;
        }

        value = new JsonObject();
        root[propertyName] = value;
        return value;
    }
}
