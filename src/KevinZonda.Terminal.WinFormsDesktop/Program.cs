using System.Diagnostics;
using KevinZonda.Terminal.ConPty;
using KevinZonda.Terminal.Hosting;
using KevinZonda.Terminal.Interop;
using KevinZonda.Terminal.WinFormsDesktop.RecentWorkspaces;

namespace KevinZonda.Terminal;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (ConPtyConsoleThemeHelper.TryRun(args, out var helperExitCode))
        {
            return helperExitCode;
        }

#if DEBUG
        // Keep Visual Studio attached to the actual UI process. Non-debugger
        // Debug builds and every Release build still use the supervisor.
        if (Debugger.IsAttached)
        {
            return RunApplication(args, CrashReportStore.CreatePath());
        }
#endif

        if (ApplicationSupervisor.TryParseChildArguments(
                args,
                out var crashReportPath,
                out var applicationArgs))
        {
            return RunApplication(applicationArgs, crashReportPath);
        }

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        return ApplicationSupervisor.Run(args);
    }

    private static int RunApplication(string[] args, string crashReportPath)
    {
        var crashHandlerInstalled = false;
        try
        {
            ManagedCrashHandler.Install(crashReportPath);
            crashHandlerInstalled = true;
            ApplicationConfiguration.Initialize();
            Application.SetColorMode(SystemColorMode.Dark);
            if (!TryGetStartingDirectory(args, out var startingDirectory))
            {
                return 0;
            }

            ConfigureConHostIntegrityPrompt();
            var recentWorkspaceStartInfo = SelfProcessLauncher.CreateStartInfo(
                startingDirectory,
                []);
            RecentWorkspaceService.RecordAndUpdate(
                startingDirectory,
                recentWorkspaceStartInfo.FileName,
                recentWorkspaceStartInfo.ArgumentList);
            Application.Run(new MainForm(startingDirectory));
            return 0;
        }
        catch (Exception exception)
        {
            ManagedCrashHandler.Record(exception, "Application.Run");
            return ManagedCrashHandler.FatalExitCode;
        }
        finally
        {
            if (crashHandlerInstalled)
            {
                ManagedCrashHandler.Uninstall();
            }
        }
    }

    private static bool TryGetStartingDirectory(string[] args, out string startingDirectory)
    {
        startingDirectory = string.Empty;
        try
        {
            if (args.Length > 1)
            {
                throw new ArgumentException("KevinZonda Terminal accepts at most one starting directory.");
            }

            startingDirectory = Path.GetFullPath(
                args.Length == 0 ? Environment.CurrentDirectory : args[0]);
            if (!Directory.Exists(startingDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"The starting directory does not exist:\n\n{startingDirectory}");
            }

            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                DirectoryNotFoundException or
                PathTooLongException)
        {
            MessageBox.Show(
                exception.Message,
                "KevinZonda Terminal startup error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
    }

    private static void ConfigureConHostIntegrityPrompt()
    {
        // Alert when the cached OpenConsole.exe fails its integrity check: the
        // file may be corrupted or tampered with, and it is never executed.
        // A "no" decision is remembered so later sessions in this run don't nag.
        var integrityDeclined = false;
        ConHost.IntegrityConflictHandler = path =>
        {
            if (integrityDeclined)
            {
                return false;
            }

            var choice = MessageBox.Show(
                $"缓存的终端主机文件与 KevinZonda Terminal 内置副本不一致：\n\n{path}\n\n" +
                "可能是磁盘损坏，也可能是被其他程序篡改。KevinZonda Terminal 不会使用这个文件。\n\n" +
                "是否从内置副本重新释放？\n\n" +
                "是：重新释放并继续\n否：本次运行回退到系统控制台（部分终端功能受限）",
                "KevinZonda Terminal security warning",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
            if (choice == DialogResult.No)
            {
                integrityDeclined = true;
                return false;
            }
            return true;
        };
    }
}
