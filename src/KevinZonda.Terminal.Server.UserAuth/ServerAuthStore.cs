using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KevinZonda.Terminal.Server.UserAuth;

public sealed class ServerAuthStore
{
    private const long MaximumConfigurationBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly Argon2PasswordService _passwords;

    public ServerAuthStore(string? configurationPath = null, Argon2PasswordService? passwords = null)
    {
        ConfigurationPath = Path.GetFullPath(configurationPath ?? DefaultConfigurationPath);
        _passwords = passwords ?? new Argon2PasswordService();
    }

    public static string DefaultConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".kterm",
        "server_auth.json");

    public string ConfigurationPath { get; }

    public async Task<ServerAuthConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var file = new FileInfo(ConfigurationPath);
            if (!file.Exists)
            {
                throw new FileNotFoundException(null, ConfigurationPath);
            }
            if (file.Length > MaximumConfigurationBytes)
            {
                throw new AuthConfigurationException(
                    $"The server authentication configuration is larger than {MaximumConfigurationBytes} bytes.");
            }

            var json = await File.ReadAllTextAsync(ConfigurationPath, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            var configuration = JsonSerializer.Deserialize<ServerAuthConfiguration>(json, JsonOptions)
                ?? throw new AuthConfigurationException("The server authentication configuration is empty.");
            Validate(configuration);
            return configuration;
        }
        catch (AuthConfigurationException)
        {
            throw;
        }
        catch (FileNotFoundException exception)
        {
            throw new AuthConfigurationException(
                $"The server authentication configuration does not exist: {ConfigurationPath}",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new AuthConfigurationException(
                $"Unable to read the server authentication configuration: {ConfigurationPath}",
                exception);
        }
    }

    public Task CreateAsync(
        ServerAuthConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        WriteAsync(configuration, overwrite: false, cancellationToken);

    public Task SaveAsync(
        ServerAuthConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        WriteAsync(configuration, overwrite: true, cancellationToken);

    public void Validate(ServerAuthConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.AllowedHash is null || configuration.AllowedHash.Length == 0)
        {
            throw new AuthConfigurationException("At least one allowed hash is required.");
        }
        if (configuration.AllowedHash.Length > ServerAuthConfiguration.MaximumAllowedHashes)
        {
            throw new AuthConfigurationException(
                $"No more than {ServerAuthConfiguration.MaximumAllowedHashes} allowed hashes are supported.");
        }

        var uniqueHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hash in configuration.AllowedHash)
        {
            _passwords.ValidateEncodedHash(hash);
            if (!uniqueHashes.Add(hash))
            {
                throw new AuthConfigurationException("The server authentication configuration contains a duplicate hash.");
            }
        }
    }

    private async Task WriteAsync(
        ServerAuthConfiguration configuration,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        Validate(configuration);
        var directory = Path.GetDirectoryName(ConfigurationPath)
            ?? throw new AuthConfigurationException("The server authentication path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(ConfigurationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(configuration, JsonOptions) + Environment.NewLine;
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            RestrictToCurrentUser(temporaryPath);

            try
            {
                File.Move(temporaryPath, ConfigurationPath, overwrite);
            }
            catch (IOException exception) when (!overwrite && File.Exists(ConfigurationPath))
            {
                throw new AuthConfigurationException(
                    $"The server authentication configuration already exists: {ConfigurationPath}",
                    exception);
            }
        }
        catch (AuthConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AuthConfigurationException(
                $"Unable to write the server authentication configuration: {ConfigurationPath}",
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

    private static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new AuthConfigurationException("Unable to determine the current Windows user.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
