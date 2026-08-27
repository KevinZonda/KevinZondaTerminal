namespace KevinZonda.Terminal.RecentWorkspaces;

internal sealed record WorkspaceLaunchCommand(
    string ExecutablePath,
    IReadOnlyList<string> ArgumentPrefix)
{
    internal static WorkspaceLaunchCommand Create(
        string executablePath,
        IEnumerable<string> argumentPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(argumentPrefix);

        var normalizedExecutablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(normalizedExecutablePath))
        {
            throw new FileNotFoundException(
                "Unable to locate the KevinZonda Terminal executable.",
                normalizedExecutablePath);
        }

        return new WorkspaceLaunchCommand(
            normalizedExecutablePath,
            argumentPrefix.ToArray());
    }
}
