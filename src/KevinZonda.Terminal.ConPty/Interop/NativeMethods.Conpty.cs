using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KevinZonda.Terminal.Interop;

// Pseudoconsole-host plumbing for OpenConsoleConHost: NT device handles, handle
// inheritance and pipe writes, mirroring winconpty.cpp / DeviceHandle.cpp (MIT).
internal static partial class NativeMethods
{
    internal const nuint ProcThreadAttributeHandleList = 0x0002_0002;
    internal const uint StartfUseStdHandles = 0x0000_0100;
    internal const uint GenericAll = 0x1000_0000;
    internal const uint Synchronize = 0x0010_0000;
    internal const uint FileShareDelete = 0x0000_0004;
    internal const uint HandleFlagInherit = 0x0000_0001;
    internal const uint ObjInherit = 0x0000_0002;
    internal const uint ObjCaseInsensitive = 0x0000_0040;
    internal const uint FileSynchronousIoNonAlert = 0x0000_0020;
    private const uint SystemConsoleInformationClass = 132;

    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ObjectAttributes
    {
        internal uint Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoStatusBlock
    {
        internal IntPtr Status;
        internal IntPtr Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtOpenFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        uint shareAccess,
        uint openOptions);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(
        uint systemInformationClass,
        ref uint systemInformation,
        int systemInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetHandleInformation(
        SafeFileHandle hObject,
        uint dwMask,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    // Opens a console device object (\Device\ConDrv\Server or a child such as
    // \Reference) through the NT namespace, like DeviceHandle::_CreateHandle.
    internal static int NtOpenFile(
        out SafeFileHandle handle,
        uint desiredAccess,
        SafeFileHandle? root,
        string name,
        uint attributes,
        uint shareAccess,
        uint openOptions,
        out int ioStatus)
    {
        var pinnedName = GCHandle.Alloc(name, GCHandleType.Pinned);
        GCHandle pinnedString = default;
        try
        {
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                Buffer = pinnedName.AddrOfPinnedObject()
            };
            pinnedString = GCHandle.Alloc(unicodeString, GCHandleType.Pinned);

            var objectAttributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = root?.DangerousGetHandle() ?? IntPtr.Zero,
                ObjectName = pinnedString.AddrOfPinnedObject(),
                Attributes = attributes
            };

            var status = NtOpenFile(
                out handle,
                desiredAccess,
                ref objectAttributes,
                out var statusBlock,
                shareAccess,
                openOptions);
            ioStatus = statusBlock.Status.ToInt32();
            return status;
        }
        finally
        {
            if (pinnedString.IsAllocated)
            {
                pinnedString.Free();
            }
            pinnedName.Free();
        }
    }

    // Loads the ConDrv driver so the first console server handle can be created,
    // like winconpty's _EnsureDriverIsLoaded (SystemConsoleInformation = 132).
    internal static void EnsureConsoleDriverLoaded()
    {
        var consoleInformation = 1u; // SYSTEM_CONSOLE_INFORMATION.DriverLoaded = TRUE
        _ = NtSetSystemInformation(
            SystemConsoleInformationClass,
            ref consoleInformation,
            sizeof(uint));
    }
}
