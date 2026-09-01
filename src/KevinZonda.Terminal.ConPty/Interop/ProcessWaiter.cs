using Microsoft.Win32.SafeHandles;

namespace KevinZonda.Terminal.Interop;

internal static class ProcessWaiter
{
    internal static Task<uint?> WaitForExit(SafeKernelHandle process)
    {
        var handleRetained = false;
        EventWaitHandle? waitHandle = null;
        RegisteredWaitHandle? registration = null;
        try
        {
            process.DangerousAddRef(ref handleRetained);
            waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset)
            {
                SafeWaitHandle = new SafeWaitHandle(
                    process.DangerousGetHandle(),
                    ownsHandle: false)
            };

            var completion = new TaskCompletionSource<uint?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            registration = ThreadPool.RegisterWaitForSingleObject(
                waitHandle,
                static (state, _) =>
                {
                    var context = (WaitContext)state!;
                    var exitCode = NativeMethods.GetExitCodeProcess(
                        context.ProcessHandle,
                        out var code)
                        ? code
                        : (uint?)null;
                    context.Completion.TrySetResult(exitCode);
                },
                new WaitContext(process.DangerousGetHandle(), completion),
                Timeout.Infinite,
                executeOnlyOnce: true);

            return AwaitAndRelease(completion.Task, registration, waitHandle, process);
        }
        catch
        {
            registration?.Unregister(null);
            waitHandle?.Dispose();
            if (handleRetained)
            {
                process.DangerousRelease();
            }
            throw;
        }
    }

    private static async Task<uint?> AwaitAndRelease(
        Task<uint?> completion,
        RegisteredWaitHandle registration,
        WaitHandle waitHandle,
        SafeKernelHandle process)
    {
        try
        {
            return await completion.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
            waitHandle.Dispose();
            process.DangerousRelease();
        }
    }

    private sealed record WaitContext(
        IntPtr ProcessHandle,
        TaskCompletionSource<uint?> Completion);
}
