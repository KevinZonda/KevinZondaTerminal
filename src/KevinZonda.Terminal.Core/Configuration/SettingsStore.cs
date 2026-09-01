using System.Text;
using System.Text.Json;

namespace KevinZonda.Terminal.Configuration;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
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
            return AppSettings.Normalize(JsonSerializer.Deserialize<AppSettings>(json, JsonOptions));
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
        var directory = Path.GetDirectoryName(_configurationPath)
            ?? throw new InvalidOperationException("The KevinZonda Terminal configuration path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_configurationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(normalized, JsonOptions) + Environment.NewLine;
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, _configurationPath, overwrite: true);
            return normalized;
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
