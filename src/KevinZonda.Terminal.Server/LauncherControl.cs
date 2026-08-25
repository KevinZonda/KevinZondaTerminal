namespace KevinZonda.Terminal.Server;

internal static class LauncherControl
{
    private const string ShutdownCommand = "shutdown";

    internal static async Task RunAsync(
        IHostApplicationLifetime lifetime,
        ILogger logger)
    {
        logger.LogInformation("Launcher stdin control is enabled.");
        await Task.Yield();
        try
        {
            while (!lifetime.ApplicationStopping.IsCancellationRequested)
            {
                var command = await Console.In
                    .ReadLineAsync(lifetime.ApplicationStopping)
                    .ConfigureAwait(false);
                if (command is null)
                {
                    logger.LogInformation("Launcher input closed; stopping the server.");
                    lifetime.StopApplication();
                    return;
                }

                if (string.Equals(command.Trim(), ShutdownCommand, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Launcher requested server shutdown.");
                    lifetime.StopApplication();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            logger.LogWarning(exception, "Launcher input failed; stopping the server.");
            lifetime.StopApplication();
        }
    }
}
