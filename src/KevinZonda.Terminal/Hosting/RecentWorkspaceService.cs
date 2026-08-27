using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KevinZonda.Terminal.Hosting;

internal static class RecentWorkspaceService
{
    private const string MutexName = @"Local\KevinZonda.Terminal.RecentWorkspaces";
    private const string DisableJumpListEnvironmentVariable = "KTERM_DISABLE_JUMP_LIST";

    internal static void RecordAndUpdate(string startingDirectory)
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

            var removed = TaskbarJumpList.Update(workspaces);
            if (removed.Count == 0)
            {
                return;
            }

            var currentWasRemoved = removed.Contains(startingDirectory);
            workspaces.RemoveAll(removed.Contains);
            if (currentWasRemoved)
            {
                workspaces.Insert(0, startingDirectory);
            }
            store.Save(workspaces);

            // BeginList reports removals from the preceding Jump List. Once
            // that removal has been honored and committed, an explicitly
            // reopened workspace is eligible to appear again.
            if (currentWasRemoved)
            {
                _ = TaskbarJumpList.Update(workspaces);
            }
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                InvalidCastException or
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
