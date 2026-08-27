using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KevinZonda.Terminal.Server.Launcher;

internal sealed record LauncherConfiguration
{
    internal static LauncherConfiguration Default { get; } = new();

    internal static string DefaultWorkingDirectory
    {
        get
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return !string.IsNullOrWhiteSpace(userProfile) && Directory.Exists(userProfile)
                ? Path.GetFullPath(userProfile)
                : Environment.CurrentDirectory;
        }
    }

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
        var customUsername = Server.CustomUsername?.Trim();
        if (string.IsNullOrEmpty(customUsername))
        {
            customUsername = "kterm";
        }
        if (customUsername.Length > 128)
        {
            throw new LauncherConfigurationException(
                "The custom username must be 128 characters or fewer.");
        }
        if (customUsername.Contains(':') || customUsername.Any(char.IsControl))
        {
            throw new LauncherConfigurationException(
                "The custom username cannot contain a colon or control characters.");
        }
        var icpRegistration = Server.IcpRegistration?.Trim();
        if (string.IsNullOrEmpty(icpRegistration))
        {
            icpRegistration = null;
        }
        else if (icpRegistration.Length > 128)
        {
            throw new LauncherConfigurationException(
                "The ICP registration number must be 128 characters or fewer.");
        }
        else if (icpRegistration.Any(char.IsControl))
        {
            throw new LauncherConfigurationException(
                "The ICP registration number cannot contain control characters.");
        }
        if (!double.IsFinite(Server.RuntimeRetentionMinutes) ||
            Server.RuntimeRetentionMinutes is < 0.1 or > 1440)
        {
            throw new LauncherConfigurationException(
                "Runtime retention must be between 0.1 and 1440 minutes.");
        }

        var certificate = NormalizeCertificate(Server.Certificate);

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
                CustomUsername = customUsername,
                IcpRegistration = icpRegistration,
                WorkingDirectory = workingDirectory,
                Certificate = certificate,
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
        if (normalized.Server.Certificate.PublicCertificatePath is not null &&
            normalized.Server.Certificate.PrivateKeyPath is not null)
        {
            arguments.Add("--Kestrel:Certificates:Default:Path");
            arguments.Add(normalized.Server.Certificate.PublicCertificatePath);
            arguments.Add("--Kestrel:Certificates:Default:KeyPath");
            arguments.Add(normalized.Server.Certificate.PrivateKeyPath);
        }
        arguments.Add("--urls");
        arguments.Add(normalized.Server.Urls);
        arguments.Add("--auth-mode");
        arguments.Add(normalized.Server.AuthMode);
        arguments.Add("--custom-username");
        arguments.Add(normalized.Server.CustomUsername);
        if (normalized.Server.IcpRegistration is not null)
        {
            arguments.Add("--icp-registration");
            arguments.Add(normalized.Server.IcpRegistration);
        }
        arguments.Add("--working-directory");
        arguments.Add(normalized.Server.WorkingDirectory ?? DefaultWorkingDirectory);
        arguments.Add("--runtime-retention-minutes");
        arguments.Add(normalized.Server.RuntimeRetentionMinutes.ToString(CultureInfo.InvariantCulture));
        arguments.AddRange(commandLineArguments);
        return [.. arguments];
    }

    private static LauncherCertificateConfiguration NormalizeCertificate(
        LauncherCertificateConfiguration? certificate)
    {
        certificate ??= new LauncherCertificateConfiguration();
        var publicCertificatePath = NormalizeOptionalPath(certificate.PublicCertificatePath);
        var privateKeyPath = NormalizeOptionalPath(certificate.PrivateKeyPath);
        if ((publicCertificatePath is null) != (privateKeyPath is null))
        {
            throw new LauncherConfigurationException(
                "Both the public certificate and private key paths are required.");
        }
        if (publicCertificatePath is null || privateKeyPath is null)
        {
            return new LauncherCertificateConfiguration();
        }
        if (!File.Exists(publicCertificatePath))
        {
            throw new LauncherConfigurationException(
                $"The public certificate does not exist: {publicCertificatePath}");
        }
        if (!File.Exists(privateKeyPath))
        {
            throw new LauncherConfigurationException(
                $"The private key does not exist: {privateKeyPath}");
        }

        try
        {
            using var loaded = X509Certificate2.CreateFromPemFile(
                publicCertificatePath,
                privateKeyPath);
            if (!loaded.HasPrivateKey)
            {
                throw new CryptographicException("The certificate has no matching private key.");
            }
        }
        catch (Exception exception) when (
            exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new LauncherConfigurationException(
                "Unable to load the PEM certificate and private key. " +
                "Encrypted private keys are not supported.",
                exception);
        }

        return new LauncherCertificateConfiguration
        {
            PublicCertificatePath = publicCertificatePath,
            PrivateKeyPath = privateKeyPath
        };
    }

    private static string? NormalizeOptionalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(value.Trim());
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherConfigurationException("A certificate path is invalid.", exception);
        }
    }
}

internal sealed record LauncherServerConfiguration
{
    public string Urls { get; init; } = "http://0.0.0.0:7132";
    public string AuthMode { get; init; } = "auto";
    public string CustomUsername { get; init; } = "kterm";
    public string? IcpRegistration { get; init; }
    public string? WorkingDirectory { get; init; }
    public double RuntimeRetentionMinutes { get; init; } = 30;
    public LauncherCertificateConfiguration Certificate { get; init; } = new();
    public string[] AdditionalArguments { get; init; } = [];
}

internal sealed record LauncherCertificateConfiguration
{
    public string? PublicCertificatePath { get; init; }
    public string? PrivateKeyPath { get; init; }
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
