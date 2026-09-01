using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KevinZonda.Terminal.Configuration;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private readonly string _configurationPath;

    internal SettingsStore(string? configurationPath = null)
    {
        _configurationPath = configurationPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".kterm",
            "config.json");
    }

    internal AppSettings Load()
    {
        try
        {
            if (!File.Exists(_configurationPath))
            {
                return AppSettings.Normalize(null);
            }

            var json = File.ReadAllText(_configurationPath, Encoding.UTF8);
            return AppSettings.Normalize(JsonSerializer.Deserialize(
                json,
                AppSettingsJsonContext.Default.AppSettings));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return AppSettings.Normalize(null);
        }
    }

    internal async Task<AppSettings> SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = AppSettings.Normalize(settings);
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

        var shell = GetObject(root, "shell");
        shell["profile"] = normalized.Shell.Profile;
        shell["executable"] = normalized.Shell.Executable;
        shell["arguments"] = normalized.Shell.Arguments;
        shell["msys2Environment"] = normalized.Shell.Msys2Environment;
        shell["inheritWindowsPath"] = normalized.Shell.InheritWindowsPath;
        shell["exitBehavior"] = normalized.Shell.ExitBehavior;

        GetObject(root, "conHost")["enhancedOpenConsole"] =
            normalized.ConHost.EnhancedOpenConsole;

        await SaveRootAsync(root, cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    internal async Task<AppSettings> SaveFontSizeAsync(
        AppSettings current,
        double size,
        CancellationToken cancellationToken = default)
    {
        var settings = AppSettings.Normalize(current with
        {
            Font = current.Font with { Size = size }
        });
        var root = await LoadRootAsync(cancellationToken).ConfigureAwait(false);
        GetObject(root, "font")["size"] = settings.Font.Size;
        await SaveRootAsync(root, cancellationToken).ConfigureAwait(false);
        return settings;
    }

    private async Task<JsonObject> LoadRootAsync(CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(_configurationPath)
                ? JsonNode.Parse(await File.ReadAllTextAsync(_configurationPath, cancellationToken)
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
        var directory = Path.GetDirectoryName(_configurationPath)
            ?? throw new InvalidOperationException("The KevinZonda Terminal configuration path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_configurationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToJsonString(WriteOptions) + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, _configurationPath, overwrite: true);
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
