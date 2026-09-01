namespace KevinZonda.Terminal.Interop;

/// <summary>
/// Owns the console-host side of a pseudoconsole session and exposes the HPCON
/// value used to attach client processes. Implementations hide whether the
/// session is backed by the inbox conhost (kernel32 CreatePseudoConsole) or a
/// side-by-side OpenConsole.exe (passthrough ConPTY, like Windows Terminal).
/// </summary>
internal interface IConHost : IDisposable
{
    /// <summary>
    /// The HPCON value to pass with PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE when
    /// spawning a client process. Remains valid until Dispose.
    /// </summary>
    IntPtr PseudoConsoleHandle { get; }

    /// <summary>
    /// Completes when a side-by-side console-host process exits. The inbox
    /// pseudoconsole has no process handle exposed to KTerm and returns null.
    /// </summary>
    Task<uint?>? ExitTask { get; }

    /// <summary>Resizes the pseudoconsole. Throws on failure.</summary>
    void Resize(int columns, int rows);
}
