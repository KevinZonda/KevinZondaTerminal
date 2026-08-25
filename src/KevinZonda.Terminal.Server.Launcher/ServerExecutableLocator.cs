namespace KevinZonda.Terminal.Server.Launcher;

internal static class ServerExecutableLocator
{
    private const string ServerExecutableName = "kterm-server.exe";

    internal static string Find()
    {
        var adjacent = Path.Combine(AppContext.BaseDirectory, ServerExecutableName);
        if (File.Exists(adjacent))
        {
            return adjacent;
        }

        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configurationDirectory = outputDirectory.Parent;
        var binDirectory = configurationDirectory?.Parent;
        var launcherProjectDirectory = binDirectory?.Parent;
        var sourceDirectory = launcherProjectDirectory?.Parent;
        if (configurationDirectory is not null && sourceDirectory is not null)
        {
            var developmentServer = Path.Combine(
                sourceDirectory.FullName,
                "KevinZonda.Terminal.Server",
                "bin",
                configurationDirectory.Name,
                outputDirectory.Name,
                ServerExecutableName);
            if (File.Exists(developmentServer))
            {
                return developmentServer;
            }
        }

        return adjacent;
    }
}
