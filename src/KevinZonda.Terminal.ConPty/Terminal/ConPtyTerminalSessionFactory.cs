using KevinZonda.Terminal.Configuration;
using KevinZonda.Terminal.Terminal;

namespace KevinZonda.Terminal.ConPty;

internal sealed class ConPtyTerminalSessionFactory : ITerminalSessionFactory
{
    internal static ConPtyTerminalSessionFactory Instance { get; } = new();

    private ConPtyTerminalSessionFactory()
    {
    }

    public ValueTask<ITerminalSession> StartAsync(
        string id,
        int columns,
        int rows,
        ShellLaunchSpec shell,
        TerminalThemePreset theme,
        string startingDirectory,
        bool enhancedOpenConsole) => new(Task.Run<ITerminalSession>(() =>
            ConPtyTerminalSession.Start(
                id,
                columns,
                rows,
                shell,
                theme,
                startingDirectory,
                enhancedOpenConsole)));
}
