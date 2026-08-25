using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KevinZonda.Terminal.Server.Launcher;

internal sealed record LauncherConfiguration
{
    internal static LauncherConfiguration Default { get; } = new();

    public bool AutoStart { get; init; } = true;
    public LauncherServerConfiguration Server { get; init; } = new();

    internal LauncherConfiguration Normalize()
    {
        if (Server is null)
        {
            throw new LauncherConfigurationException("The server configuration cannot be null.");
        }

        var urls = Server.Urls?.Trim();
        if (string.IsNullOrWhiteSpace(urls))
        {
            throw new LauncherConfigurationException("Server URLs cannot be empty.");
        }
        var serverUrls = urls.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (serverUrls.Length == 0)
        {
            throw new LauncherConfigurationException("Server URLs cannot be empty.");
        }
        foreach (var url in serverUrls)
        {
            var validationUrl = NormalizeWildcardHost(url);
            if ((!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ||
                !Uri.TryCreate(validationUrl, UriKind.Absolute, out _))
            {
                throw new LauncherConfigurationException($"Invalid Server URL: {url}");
            }
        }

        var authMode = Server.AuthMode?.Trim().ToLowerInvariant();
        if (authMode is not ("auto" or "required" or "disabled"))
        {
            throw new LauncherConfigurationException(
                "Authentication mode must be auto, required, or disabled.");
        }
        if (!double.IsFinite(Server.RuntimeRetentionMinutes) ||
            Server.RuntimeRetentionMinutes is < 0.1 or > 1440)
        {
            throw new LauncherConfigurationException(
                "Runtime retention must be between 0.1 and 1440 minutes.");
        }

        string? workingDirectory = null;
        if (!string.IsNullOrWhiteSpace(Server.WorkingDirectory))
        {
            try
            {
                workingDirectory = Path.GetFullPath(Server.WorkingDirectory.Trim());
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new LauncherConfigurationException("The working directory is invalid.", exception);
            }
            if (!Directory.Exists(workingDirectory))
            {
                throw new LauncherConfigurationException(
                    $"The working directory does not exist: {workingDirectory}");
            }
        }

        if (Server.AdditionalArguments is null)
        {
            throw new LauncherConfigurationException("Additional arguments cannot be null.");
        }
        if (Server.AdditionalArguments.Length > 256)
        {
            throw new LauncherConfigurationException("No more than 256 additional arguments are supported.");
        }
        var additionalArguments = new string[Server.AdditionalArguments.Length];
        for (var index = 0; index < Server.AdditionalArguments.Length; index++)
        {
            var argument = Server.AdditionalArguments[index];
            if (string.IsNullOrWhiteSpace(argument))
            {
                throw new LauncherConfigurationException("Additional arguments cannot be empty.");
            }
            if (argument.Length > 8192)
            {
                throw new LauncherConfigurationException("An additional argument is too long.");
            }
            if (string.Equals(argument, "--launcher-pipe", StringComparison.Ordinal) ||
                argument.StartsWith("--launcher-pipe=", StringComparison.Ordinal))
            {
                throw new LauncherConfigurationException(
                    "--launcher-pipe is reserved for communication with the Launcher.");
            }
            additionalArguments[index] = argument;
        }

        return this with
        {
            Server = Server with
            {
                Urls = urls,
                AuthMode = authMode,
                WorkingDirectory = workingDirectory,
                AdditionalArguments = additionalArguments
            }
        };
    }

    private static string NormalizeWildcardHost(string url)
    {
        foreach (var wildcardPrefix in new[]
        {
            "http://*", "http://+", "https://*", "https://+"
        })
        {
            if (url.StartsWith(wildcardPrefix, StringComparison.OrdinalIgnoreCase) &&
                (url.Length == wildcardPrefix.Length || url[wildcardPrefix.Length] is ':' or '/'))
            {
                var hostStart = wildcardPrefix.IndexOf("://", StringComparison.Ordinal) + 3;
                return $"{wildcardPrefix[..hostStart]}localhost" +
                    url[wildcardPrefix.Length..];
            }
        }
        return url;
    }

    internal string[] BuildServerArguments(IReadOnlyList<string> commandLineArguments)
    {
        var normalized = Normalize();
        var arguments = new List<string>();
        arguments.AddRange(normalized.Server.AdditionalArguments);
        arguments.Add("--urls");
        arguments.Add(normalized.Server.Urls);
        arguments.Add("--auth-mode");
        arguments.Add(normalized.Server.AuthMode);
        if (normalized.Server.WorkingDirectory is not null)
        {
            arguments.Add("--working-directory");
            arguments.Add(normalized.Server.WorkingDirectory);
        }
        arguments.Add("--runtime-retention-minutes");
        arguments.Add(normalized.Server.RuntimeRetentionMinutes.ToString(CultureInfo.InvariantCulture));
        arguments.AddRange(commandLineArguments);
        return [.. arguments];
    }
}

internal sealed record LauncherServerConfiguration
{
    public string Urls { get; init; } = "http://0.0.0.0:7132";
    public string AuthMode { get; init; } = "auto";
    public string? WorkingDirectory { get; init; }
    public double RuntimeRetentionMinutes { get; init; } = 30;
    public string[] AdditionalArguments { get; init; } = [];
}

internal sealed class LauncherConfigurationStore
{
    private const long MaximumConfigurationBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    internal LauncherConfigurationStore(string? configurationPath = null)
    {
        ConfigurationPath = Path.GetFullPath(configurationPath ?? DefaultConfigurationPath);
    }

    internal static string DefaultConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".kterm",
        "server_launcher.json");

    internal string ConfigurationPath { get; }

    internal LauncherConfiguration Load()
    {
        if (!File.Exists(ConfigurationPath))
        {
            return LauncherConfiguration.Default;
        }

        try
        {
            var file = new FileInfo(ConfigurationPath);
            if (file.Length > MaximumConfigurationBytes)
            {
                throw new LauncherConfigurationException(
                    $"The Launcher configuration is larger than {MaximumConfigurationBytes} bytes.");
            }
            var json = File.ReadAllText(ConfigurationPath, Encoding.UTF8);
            return (JsonSerializer.Deserialize<LauncherConfiguration>(json, JsonOptions)
                ?? throw new LauncherConfigurationException("The Launcher configuration is empty."))
                .Normalize();
        }
        catch (LauncherConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new LauncherConfigurationException(
                $"Unable to read the Launcher configuration: {ConfigurationPath}",
                exception);
        }
    }

    internal void Save(LauncherConfiguration configuration)
    {
        var normalized = configuration.Normalize();
        var directory = Path.GetDirectoryName(ConfigurationPath)
            ?? throw new LauncherConfigurationException("The Launcher configuration path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(ConfigurationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(normalized, JsonOptions) + Environment.NewLine;
            File.WriteAllText(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, ConfigurationPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LauncherConfigurationException(
                $"Unable to write the Launcher configuration: {ConfigurationPath}",
                exception);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

internal sealed record LauncherStartupOptions(
    string ConfigurationPath,
    string[] ServerArguments)
{
    internal static LauncherStartupOptions Parse(string[] arguments)
    {
        string? configurationPath = null;
        var serverArguments = new List<string>(arguments.Length);
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            string? value = null;
            if (string.Equals(argument, "--config", StringComparison.Ordinal))
            {
                if (++index >= arguments.Length)
                {
                    throw new LauncherConfigurationException("--config requires a file path.");
                }
                value = arguments[index];
            }
            else if (argument.StartsWith("--config=", StringComparison.Ordinal))
            {
                value = argument["--config=".Length..];
            }
            else
            {
                serverArguments.Add(argument);
                continue;
            }

            if (configurationPath is not null)
            {
                throw new LauncherConfigurationException("--config may only be specified once.");
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new LauncherConfigurationException("--config requires a file path.");
            }
            configurationPath = Path.GetFullPath(value);
        }

        return new LauncherStartupOptions(
            configurationPath ?? LauncherConfigurationStore.DefaultConfigurationPath,
            [.. serverArguments]);
    }
}

internal sealed class LauncherConfigurationException : Exception
{
    internal LauncherConfigurationException(string message)
        : base(message)
    {
    }

    internal LauncherConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
