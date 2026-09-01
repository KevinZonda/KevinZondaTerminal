namespace KevinZonda.Terminal.UnixPty;

/// <summary>Describes how the process running in the pseudoterminal exited.</summary>
/// <param name="ExitCode">
/// The conventional process exit code. A signal exit is represented as 128 plus the signal number.
/// </param>
/// <param name="Signal">The terminating Unix signal, or null for a normal exit.</param>
public sealed record PtyExitStatus(int ExitCode, int? Signal);
