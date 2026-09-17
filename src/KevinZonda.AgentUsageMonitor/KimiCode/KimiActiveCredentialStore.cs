using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace KevinZonda.AgentUsageMonitor.KimiCode;

internal sealed class KimiActiveCredentialStore
{
    internal string TokenPath { get; }

    internal KimiActiveCredentialStore(string? tokenPath)
    {
        TokenPath = Path.GetFullPath(tokenPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kterm", "kimi-token.json"));
    }

    internal async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(TokenPath)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                return new FileStream($"{TokenPath}.lock", options);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<KimiStoredAuthorization?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(TokenPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var state = await JsonSerializer.DeserializeAsync(stream, KimiTokenJsonContext.Default.KimiStoredAuthorization, cancellationToken)
                .ConfigureAwait(false);
            if (state is null || state.Version != 1 || !Guid.TryParse(state.Revision, out _) || !Guid.TryParse(state.Generation, out _)
                || !Guid.TryParse(state.DeviceId, out _) || state.Region is not ("mainland-cn" or "global"))
            {
                throw new UsageException(UsageErrorCode.InvalidCredential, "Kimi Active credentials are invalid. Log in again in Settings.");
            }
            if (state.ProtectedToken is not null)
            {
                if (!OperatingSystem.IsWindows())
                    throw new UsageException(UsageErrorCode.InvalidCredential, "Kimi Active credentials belong to a Windows user. Log in again in Settings.");
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(state.ProtectedToken), null, DataProtectionScope.CurrentUser);
                try
                {
                    state = state with { Token = JsonSerializer.Deserialize(bytes, KimiTokenJsonContext.Default.KimiManagedToken), ProtectedToken = null };
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            if (state.Token is { } token && (string.IsNullOrWhiteSpace(token.AccessToken)
                || string.IsNullOrWhiteSpace(token.RefreshToken) || token.ExpiresIn <= 0))
                throw new UsageException(UsageErrorCode.InvalidCredential, "Kimi Active credentials are incomplete. Log in again in Settings.");
            return state;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException)
        {
            throw new UsageException(UsageErrorCode.InvalidCredential, "Unable to read Kimi Active credentials. Log in again in Settings.");
        }
    }

    internal async Task SaveAsync(KimiStoredAuthorization state, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() && state.Token is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state.Token, KimiTokenJsonContext.Default.KimiManagedToken);
            try
            {
                state = state with
                {
                    ProtectedToken = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)),
                    Token = null
                };
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var temporaryPath = $"{TokenPath}.tmp.{Guid.NewGuid():N}";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(stream, state, KimiTokenJsonContext.Default.KimiStoredAuthorization, cancellationToken)
                    .ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, TokenPath, overwrite: true);
        }
        finally { File.Delete(temporaryPath); }
    }
}
