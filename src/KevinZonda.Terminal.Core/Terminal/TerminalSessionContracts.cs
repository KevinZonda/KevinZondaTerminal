using KevinZonda.Terminal.Configuration;

namespace KevinZonda.Terminal.Terminal;

internal interface ITerminalSessionFactory
{
    ValueTask<ITerminalSession> StartAsync(
        string id,
        int columns,
        int rows,
        ShellLaunchSpec shell,
        TerminalThemePreset theme,
        string startingDirectory,
        bool enhancedOpenConsole);
}

internal interface ITerminalSession : IAsyncDisposable
{
    string Id { get; }

    string ShellName { get; }

    uint ProcessId { get; }

    IReadOnlyList<uint> GetProcessIds();

    event Action<ITerminalSession, string>? OutputReceived;

    event Action<ITerminalSession, TerminalExitStatus>? Exited;

    void StartPumps();

    Task WriteAsync(string data);

    Task WriteAsync(ReadOnlyMemory<byte> data);

    void Resize(int columns, int rows);
}

internal sealed record TerminalExitStatus(uint ExitCode, string? Failure);
