using System.Security.Cryptography;
using System.Text;
using KevinZonda.Terminal.Server.UserAuth;

namespace KevinZonda.Terminal.Server.Launcher;

internal sealed record CredentialEntry(string Hash, string Fingerprint);

internal sealed class CredentialManager
{
    private const int MaximumPasswordLength = 4096;
    private const string UppercaseCharacters = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string LowercaseCharacters = "abcdefghijkmnopqrstuvwxyz";
    private const string DigitCharacters = "23456789";
    private const string SymbolCharacters = "!#$%&*+-=?@^_~";
    private const string AllCharacters =
        UppercaseCharacters + LowercaseCharacters + DigitCharacters + SymbolCharacters;

    private readonly Argon2PasswordService _passwords = new();
    private readonly ServerAuthStore _store;

    internal CredentialManager(string? configurationPath = null)
    {
        _store = new ServerAuthStore(configurationPath, _passwords);
    }

    internal string ConfigurationPath => _store.ConfigurationPath;

    internal async Task<ServerAuthConfiguration> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigurationPath))
        {
            return new ServerAuthConfiguration();
        }
        return await _store.LoadAllowingEmptyAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ServerAuthConfiguration> AddPasswordAsync(
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new CredentialManagementException("The password cannot be empty.");
        }
        if (password.Length > MaximumPasswordLength)
        {
            throw new CredentialManagementException(
                $"The password must be {MaximumPasswordLength} characters or fewer.");
        }

        var configuration = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (configuration.AllowedHash.Length >= ServerAuthConfiguration.MaximumAllowedHashes)
        {
            throw new CredentialManagementException(
                $"No more than {ServerAuthConfiguration.MaximumAllowedHashes} credentials are supported.");
        }
        if (await Task.Run(
                () => _passwords.VerifyAny(password, configuration.AllowedHash),
                cancellationToken).ConfigureAwait(false))
        {
            throw new CredentialManagementException("That password is already configured.");
        }

        var hash = await Task.Run(() => _passwords.Hash(password), cancellationToken)
            .ConfigureAwait(false);
        var updated = configuration with
        {
            AllowedHash = [.. configuration.AllowedHash, hash]
        };
        await _store.SaveAllowingEmptyAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    internal async Task<ServerAuthConfiguration> DeleteAsync(
        string hash,
        CancellationToken cancellationToken = default)
    {
        var configuration = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var index = Array.FindIndex(
            configuration.AllowedHash,
            candidate => string.Equals(candidate, hash, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new CredentialManagementException(
                "The selected credential no longer exists. Reload and try again.");
        }

        var updated = configuration with
        {
            AllowedHash = configuration.AllowedHash
                .Where((_, candidateIndex) => candidateIndex != index)
                .ToArray()
        };
        await _store.SaveAllowingEmptyAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    internal static IReadOnlyList<CredentialEntry> GetEntries(ServerAuthConfiguration configuration) =>
        configuration.AllowedHash
            .Select(hash => new CredentialEntry(hash, CreateFingerprint(hash)))
            .ToArray();

    internal static string GenerateRandomPassword(int length = 32)
    {
        if (length < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Random passwords must be at least 16 characters.");
        }

        var characters = new char[length];
        characters[0] = RandomCharacter(UppercaseCharacters);
        characters[1] = RandomCharacter(LowercaseCharacters);
        characters[2] = RandomCharacter(DigitCharacters);
        characters[3] = RandomCharacter(SymbolCharacters);
        for (var index = 4; index < characters.Length; index++)
        {
            characters[index] = RandomCharacter(AllCharacters);
        }
        for (var index = characters.Length - 1; index > 0; index--)
        {
            var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (characters[index], characters[swapIndex]) = (characters[swapIndex], characters[index]);
        }
        return new string(characters);
    }

    private static char RandomCharacter(string characters) =>
        characters[RandomNumberGenerator.GetInt32(characters.Length)];

    private static string CreateFingerprint(string hash)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(hash));
        return Convert.ToHexString(digest)[..16];
    }
}

internal static class ServerAuthFileArgumentResolver
{
    internal static string Resolve(
        IReadOnlyList<string> serverArguments,
        string? serverWorkingDirectory = null)
    {
        string? configuredPath = null;
        for (var index = 0; index < serverArguments.Count; index++)
        {
            var argument = serverArguments[index];
            if (string.Equals(argument, "--auth-file", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= serverArguments.Count || string.IsNullOrWhiteSpace(serverArguments[index]))
                {
                    throw new CredentialManagementException("--auth-file requires a file path.");
                }
                configuredPath = serverArguments[index];
            }
            else if (argument.StartsWith("--auth-file=", StringComparison.OrdinalIgnoreCase))
            {
                configuredPath = argument["--auth-file=".Length..];
                if (string.IsNullOrWhiteSpace(configuredPath))
                {
                    throw new CredentialManagementException("--auth-file requires a file path.");
                }
            }
        }

        try
        {
            var path = configuredPath?.Trim() ?? ServerAuthStore.DefaultConfigurationPath;
            return serverWorkingDirectory is null
                ? Path.GetFullPath(path)
                : Path.GetFullPath(path, serverWorkingDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CredentialManagementException("The authentication file path is invalid.", exception);
        }
    }
}

internal sealed class CredentialManagementException : Exception
{
    internal CredentialManagementException(string message)
        : base(message)
    {
    }

    internal CredentialManagementException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
