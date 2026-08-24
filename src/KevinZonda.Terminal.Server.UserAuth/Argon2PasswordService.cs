using System.Globalization;
using System.Text.RegularExpressions;
using Isopoh.Cryptography.Argon2;

namespace KevinZonda.Terminal.Server.UserAuth;

public sealed partial class Argon2PasswordService
{
    public const int MemoryCostKiB = 64 * 1024;
    public const int TimeCost = 3;
    public const int Parallelism = 1;
    public const int HashLengthBytes = 32;

    private const int MinimumMemoryCostKiB = 19 * 1024;
    private const int MaximumMemoryCostKiB = 256 * 1024;
    private const int MinimumTimeCost = 2;
    private const int MaximumTimeCost = 10;
    private const int MinimumParallelism = 1;
    private const int MaximumParallelism = 4;
    private const int MinimumSaltLengthBytes = 16;
    private const int MaximumSaltLengthBytes = 64;
    private const int MinimumHashLengthBytes = 16;
    private const int MaximumHashLengthBytes = 64;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return Argon2.Hash(
            password,
            timeCost: TimeCost,
            memoryCost: MemoryCostKiB,
            parallelism: Parallelism,
            type: Argon2Type.HybridAddressing,
            hashLength: HashLengthBytes);
    }

    public bool Verify(string password, string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!TryValidateEncodedHash(encodedHash, out _))
        {
            return false;
        }

        try
        {
            return Argon2.Verify(encodedHash, password);
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    public bool VerifyAny(string password, IReadOnlyList<string> allowedHashes)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(allowedHashes);
        return allowedHashes.Any(hash => Verify(password, hash));
    }

    public void ValidateEncodedHash(string encodedHash)
    {
        if (!TryValidateEncodedHash(encodedHash, out var error))
        {
            throw new AuthConfigurationException(error);
        }
    }

    public bool TryValidateEncodedHash(string? encodedHash, out string error)
    {
        if (string.IsNullOrWhiteSpace(encodedHash))
        {
            error = "The allowed hash is empty.";
            return false;
        }

        var match = Argon2IdPhcPattern().Match(encodedHash);
        if (!match.Success)
        {
            error = "The allowed hash must be an Argon2id v=19 PHC string.";
            return false;
        }

        if (!TryParseParameter(match, "memory", out var memoryCost) ||
            memoryCost is < MinimumMemoryCostKiB or > MaximumMemoryCostKiB)
        {
            error = $"The Argon2id memory cost must be between {MinimumMemoryCostKiB} and {MaximumMemoryCostKiB} KiB.";
            return false;
        }
        if (!TryParseParameter(match, "time", out var timeCost) ||
            timeCost is < MinimumTimeCost or > MaximumTimeCost)
        {
            error = $"The Argon2id time cost must be between {MinimumTimeCost} and {MaximumTimeCost}.";
            return false;
        }
        if (!TryParseParameter(match, "parallelism", out var parallelism) ||
            parallelism is < MinimumParallelism or > MaximumParallelism)
        {
            error = $"The Argon2id parallelism must be between {MinimumParallelism} and {MaximumParallelism}.";
            return false;
        }

        if (!TryDecodePhcBase64(match.Groups["salt"].Value, out var salt) ||
            salt.Length is < MinimumSaltLengthBytes or > MaximumSaltLengthBytes)
        {
            error = $"The Argon2id salt must contain between {MinimumSaltLengthBytes} and {MaximumSaltLengthBytes} bytes.";
            return false;
        }
        if (!TryDecodePhcBase64(match.Groups["hash"].Value, out var hash) ||
            hash.Length is < MinimumHashLengthBytes or > MaximumHashLengthBytes)
        {
            error = $"The Argon2id output must contain between {MinimumHashLengthBytes} and {MaximumHashLengthBytes} bytes.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseParameter(Match match, string groupName, out int value) =>
        int.TryParse(
            match.Groups[groupName].Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);

    private static bool TryDecodePhcBase64(string encoded, out byte[] bytes)
    {
        try
        {
            var padded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    [GeneratedRegex(
        @"^\$argon2id\$v=19\$m=(?<memory>[0-9]+),t=(?<time>[0-9]+),p=(?<parallelism>[0-9]+)\$(?<salt>[A-Za-z0-9+/]+)\$(?<hash>[A-Za-z0-9+/]+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Argon2IdPhcPattern();
}
