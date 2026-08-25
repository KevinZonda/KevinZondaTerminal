namespace KevinZonda.Terminal.Server.Launcher;

internal static class Program
{
    private const string SingleInstanceMutexName =
        @"Local\KevinZonda.Terminal.Server.Launcher";

    [STAThread]
    private static void Main(string[] args)
    {
        var mutexSuffix = Environment.GetEnvironmentVariable("KTERM_LAUNCHER_MUTEX_SUFFIX");
        var mutexName = string.IsNullOrWhiteSpace(mutexSuffix)
            ? SingleInstanceMutexName
            : $"{SingleInstanceMutexName}.{mutexSuffix}";
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            mutexName,
            out var ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show(
                "KTerm Server Launcher is already running.",
                "KTerm Server Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        try
        {
            using var context = new ServerLauncherContext(args);
            Application.Run(context);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.ToString(),
                "KTerm Server Launcher error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        GC.KeepAlive(singleInstance);
    }
}
