using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KevinZonda.Terminal.Interop;

/// <summary>
/// Pseudoconsole host backed by the inbox Windows conhost via kernel32
/// CreatePseudoConsole. On Windows 10 this renderer consumes VT sequences
/// (scroll regions, reverse index) and repaints the screen instead of
/// forwarding them, so it is only a fallback for OpenConsoleConHost.
/// </summary>
internal sealed class KernelConHost : IConHost
{
    private readonly SafePseudoConsoleHandle _handle;

    private KernelConHost(SafePseudoConsoleHandle handle)
    {
        _handle = handle;
    }

    public IntPtr PseudoConsoleHandle => _handle.DangerousGetHandle();

    public Task<uint?>? ExitTask => null;

    internal static KernelConHost Create(int columns, int rows, SafeFileHandle input, SafeFileHandle output)
    {
        var result = NativeMethods.CreatePseudoConsole(
            new NativeMethods.Coord(columns, rows),
            input.DangerousGetHandle(),
            output.DangerousGetHandle(),
            0,
            out var pseudoConsole);
        Marshal.ThrowExceptionForHR(result);
        return new KernelConHost(new SafePseudoConsoleHandle(pseudoConsole));
    }

    public void Resize(int columns, int rows)
    {
        var result = NativeMethods.ResizePseudoConsole(
            _handle.DangerousGetHandle(),
            new NativeMethods.Coord(columns, rows));
        Marshal.ThrowExceptionForHR(result);
    }

    public void Dispose() => _handle.Dispose();
}
