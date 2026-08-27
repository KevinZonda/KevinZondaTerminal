using System.Text;
using System.Text.Json;

namespace KevinZonda.Terminal.Hosting;

internal sealed class RecentWorkspaceStore
{
    internal const int MaximumWorkspaces = 10;
    private const long MaximumConfigurationBytes = 64 * 1024;
    private const string ConfigurationPathEnvironmentVariable =
        "KTERM_RECENT_WORKSPACES_FILE";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _configurationPath;

    internal RecentWorkspaceStore(string? configurationPath = null)
    {
        var environmentPath = Environment.GetEnvironmentVariable(
            ConfigurationPathEnvironmentVariable);
        _configurationPath = Path.GetFullPath(configurationPath ??
            (string.IsNullOrWhiteSpace(environmentPath)
                ? DefaultConfigurationPath
                : environmentPath));
    }

    internal static string DefaultConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".kterm",
        "recent_workspaces.json");

    internal IReadOnlyList<string> Load()
    {
        try
        {
            var file = new FileInfo(_configurationPath);
            if (!file.Exists || file.Length > MaximumConfigurationBytes)
            {
                return [];
            }
            var json = File.ReadAllText(_configurationPath, Encoding.UTF8);
            var configuration = JsonSerializer.Deserialize<RecentWorkspaceConfiguration>(
                json,
                JsonOptions);
            return Normalize(configuration?.Workspaces);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                JsonException or
                ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return [];
        }
    }

    internal void Save(IEnumerable<string> workspaces)
    {
        var normalized = Normalize(workspaces);
        var directory = Path.GetDirectoryName(_configurationPath)
            ?? throw new InvalidOperationException(
                "The recent workspace configuration path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_configurationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var configuration = new RecentWorkspaceConfiguration
            {
                Workspaces = [.. normalized]
            };
            var json = JsonSerializer.Serialize(configuration, JsonOptions) + Environment.NewLine;
            File.WriteAllText(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _configurationPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string>? workspaces)
    {
        if (workspaces is null)
        {
            return [];
        }

        var normalized = new List<string>(MaximumWorkspaces);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workspace in workspaces)
        {
            if (string.IsNullOrWhiteSpace(workspace))
            {
                continue;
            }

            string path;
            try
            {
                path = Path.GetFullPath(workspace);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!Directory.Exists(path) || !unique.Add(path))
            {
                continue;
            }
            normalized.Add(path);
            if (normalized.Count == MaximumWorkspaces)
            {
                break;
            }
        }
        return normalized;
    }

    private sealed record RecentWorkspaceConfiguration
    {
        public string[] Workspaces { get; init; } = [];
    }
}
