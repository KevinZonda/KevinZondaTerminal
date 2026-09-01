using System.Diagnostics;
using System.Globalization;

namespace KevinZonda.Terminal.Hosting;

internal static class ApplicationSupervisor
{
    private const string ChildArgument = "--kterm-ui-child";

    internal static bool TryParseChildArguments(
        string[] args,
        out string crashReportPath,
        out string[] applicationArgs)
    {
        if (args.Length >= 2 && string.Equals(args[0], ChildArgument, StringComparison.Ordinal))
        {
            crashReportPath = args[1];
            applicationArgs = args[2..];
            return true;
        }

        crashReportPath = string.Empty;
        applicationArgs = [];
        return false;
    }

    internal static int Run(string[] applicationArgs)
    {
        var crashCount = 0;
        while (true)
        {
            try
            {
                var reportPath = CrashReportStore.CreatePath();
                var childArguments = new List<string>(applicationArgs.Length + 2)
                {
                    ChildArgument,
                    reportPath
                };
                childArguments.AddRange(applicationArgs);

                var startInfo = SelfProcessLauncher.CreateStartInfo(
                    Environment.CurrentDirectory,
                    childArguments);
                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Windows did not start the KTerm UI process.");
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    TryDeleteReport(reportPath);
                    return 0;
                }

                crashCount++;
                EnsureCrashReport(reportPath, process.Id, process.ExitCode);
                using var dialog = new CrashReportForm(reportPath, process.ExitCode, crashCount);
                if (dialog.ShowDialog() != DialogResult.Retry)
                {
                    return process.ExitCode;
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    System.ComponentModel.Win32Exception or
                    InvalidOperationException)
            {
                MessageBox.Show(
                    $"KTerm could not start its UI process.\n\n{exception.Message}",
                    "KTerm startup error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    private static void EnsureCrashReport(string reportPath, int processId, int exitCode)
    {
        if (File.Exists(reportPath))
        {
            return;
        }

        try
        {
            CrashReportStore.Write(
                reportPath,
                "KevinZonda Terminal crash report\n" +
                $"Timestamp (UTC): {DateTimeOffset.UtcNow:O}\n" +
                $"Version: {Application.ProductVersion}\n" +
                "Origin: UI process terminated without a managed crash report\n" +
                $"Process ID: {processId}\n" +
                $"Exit code: {exitCode} (0x{unchecked((uint)exitCode).ToString("X8", CultureInfo.InvariantCulture)})\n");
        }
        catch
        {
        }
    }

    private static void TryDeleteReport(string reportPath)
    {
        try
        {
            if (File.Exists(reportPath))
            {
                File.Delete(reportPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
