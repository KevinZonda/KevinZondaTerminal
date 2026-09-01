using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KevinZonda.Terminal.WinFormsDesktop.RecentWorkspaces;

public static class RecentWorkspaceService
{
    private const string MutexName = @"Local\KevinZonda.Terminal.RecentWorkspaces";
    private const string DisableJumpListEnvironmentVariable = "KTERM_DISABLE_JUMP_LIST";

    public static void RecordAndUpdate(
        string startingDirectory,
        string executablePath,
        IEnumerable<string> argumentPrefix)
    {
        var mutexEntered = false;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
            try
            {
                mutexEntered = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                mutexEntered = true;
            }
            if (!mutexEntered)
            {
                return;
            }

            var store = new RecentWorkspaceStore();
            var workspaces = store.Load()
                .Where(workspace => !string.Equals(
                    workspace,
                    startingDirectory,
                    StringComparison.OrdinalIgnoreCase))
                .Prepend(startingDirectory)
                .Take(RecentWorkspaceStore.MaximumWorkspaces)
                .ToList();
            store.Save(workspaces);

            if (string.Equals(
                    Environment.GetEnvironmentVariable(DisableJumpListEnvironmentVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                return;
            }

            var launchCommand = WorkspaceLaunchCommand.Create(
                executablePath,
                argumentPrefix);
            var removed = TaskbarJumpList.Update(workspaces, launchCommand);
            if (removed.Count == 0)
            {
                return;
            }

            workspaces.RemoveAll(removed.Contains);
            store.Save(workspaces);
        }
        // Recent Workspaces is optional shell integration and must never keep
        // the terminal UI from starting, including unexpected COM/RCW faults.
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                InvalidCastException or
                NullReferenceException or
                ArgumentException or
                COMException)
        {
            Debug.WriteLine($"Unable to update recent workspaces: {exception}");
        }
        finally
        {
            if (mutexEntered)
            {
                try
                {
                    mutex!.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }
            mutex?.Dispose();
        }
    }
}
