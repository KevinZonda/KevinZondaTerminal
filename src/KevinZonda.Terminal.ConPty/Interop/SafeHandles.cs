using Microsoft.Win32.SafeHandles;

namespace KevinZonda.Terminal.Interop;

internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeKernelHandle(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

internal sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafePseudoConsoleHandle(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.ClosePseudoConsole(handle);
        return true;
    }
}

