using System.Diagnostics;
using System.Text;

namespace KevinZonda.Terminal.Hosting;

internal static class ManagedCrashHandler
{
    internal const int FatalExitCode = 70;

    private static string? _reportPath;
    private static int _recorded;
    private static int _terminating;

    internal static void Install(string reportPath)
    {
        _reportPath = reportPath;
        Volatile.Write(ref _recorded, 0);
        Volatile.Write(ref _terminating, 0);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += HandleThreadException;
        AppDomain.CurrentDomain.UnhandledException += HandleDomainException;
    }

    internal static void Uninstall()
    {
        Application.ThreadException -= HandleThreadException;
        AppDomain.CurrentDomain.UnhandledException -= HandleDomainException;
        _reportPath = null;
    }

    internal static void Record(Exception exception, string origin)
    {
        if (Interlocked.Exchange(ref _recorded, 1) != 0)
        {
            return;
        }

        var reportPath = _reportPath;
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            var report = new StringBuilder()
                .AppendLine("KevinZonda Terminal crash report")
                .AppendLine($"Timestamp (UTC): {DateTimeOffset.UtcNow:O}")
                .AppendLine($"Version: {Application.ProductVersion}")
                .AppendLine($"Origin: {origin}")
                .AppendLine($"Process ID: {Environment.ProcessId}")
                .AppendLine($"Process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}")
                .AppendLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}")
                .AppendLine($"Working set: {process.WorkingSet64}")
                .AppendLine()
                .AppendLine(exception.ToString())
                .ToString();

            CrashReportStore.Write(reportPath, report);
        }
        catch
        {
            // A crash recorder must never replace the original failure.
        }
    }

    private static void HandleThreadException(object sender, ThreadExceptionEventArgs eventArgs)
    {
        Record(eventArgs.Exception, "WinForms UI thread");
        if (Interlocked.Exchange(ref _terminating, 1) == 0)
        {
            Environment.Exit(FatalExitCode);
        }
    }

    private static void HandleDomainException(object? sender, UnhandledExceptionEventArgs eventArgs)
    {
        var exception = eventArgs.ExceptionObject as Exception
            ?? new InvalidOperationException($"Unhandled exception object: {eventArgs.ExceptionObject}");
        Record(exception, "AppDomain unhandled exception");
        Environment.ExitCode = FatalExitCode;
    }
}
