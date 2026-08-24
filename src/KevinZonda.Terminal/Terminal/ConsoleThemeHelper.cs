using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using KevinZonda.Terminal.Configuration;
using KevinZonda.Terminal.Interop;

namespace KevinZonda.Terminal.Terminal;

internal static class ConsoleThemeHelper
{
    private const string HelperArgument = "--kterm-console-theme-helper";

    internal static bool TryRun(string[] args, out int exitCode)
    {
        if (args.Length == 0 || !string.Equals(args[0], HelperArgument, StringComparison.Ordinal))
        {
            exitCode = 0;
            return false;
        }

        exitCode = args.Length == 3 && uint.TryParse(args[1], out var processId)
            ? Apply(processId, TerminalThemeCatalog.Find(args[2])) ? 0 : 1
            : 2;
        return true;
    }

    internal static async Task ApplyAfterStartup(
        uint processId,
        TerminalThemePreset theme,
        CancellationToken cancellationToken)
    {
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath))
        {
            return;
        }

        var startInfo = new ProcessStartInfo(executablePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(HelperArgument);
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(theme.Name);

        using var helper = Process.Start(startInfo);
        if (helper is not null)
        {
            await helper.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool Apply(uint processId, TerminalThemePreset theme)
    {
        _ = NativeMethods.FreeConsole();

        var attached = false;
        for (var attempt = 0; attempt < 10 && !attached; attempt++)
        {
            attached = NativeMethods.AttachConsole(processId);
            if (!attached)
            {
                Thread.Sleep(5);
            }
        }

        if (!attached)
        {
            return false;
        }

        try
        {
            using var output = NativeMethods.CreateFileW(
                "CONOUT$",
                NativeMethods.GenericRead | NativeMethods.GenericWrite,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                0,
                IntPtr.Zero);
            if (output.IsInvalid)
            {
                return false;
            }

            var info = new NativeMethods.ConsoleScreenBufferInfoEx
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.ConsoleScreenBufferInfoEx>(),
                ColorTable = new uint[16]
            };
            if (!NativeMethods.GetConsoleScreenBufferInfoEx(output, ref info))
            {
                return false;
            }

            const int backgroundIndex = 0;
            const int foregroundIndex = 7;
            info.ColorTable[backgroundIndex] = ToColorRef(theme.Background);
            info.ColorTable[foregroundIndex] = ToColorRef(theme.Foreground);
            info.wAttributes = (ushort)((backgroundIndex << 4) | foregroundIndex);

            // The setter treats the rectangle as exclusive although the getter returns inclusive.
            info.srWindow.Right++;
            info.srWindow.Bottom++;
            return NativeMethods.SetConsoleScreenBufferInfoEx(output, ref info);
        }
        finally
        {
            _ = NativeMethods.FreeConsole();
        }
    }

    private static uint ToColorRef(string htmlColor)
    {
        if (htmlColor.Length != 7 || htmlColor[0] != '#' ||
            !uint.TryParse(htmlColor.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            throw new FormatException($"Invalid terminal color '{htmlColor}'.");
        }

        var red = (rgb >> 16) & 0xff;
        var green = (rgb >> 8) & 0xff;
        var blue = rgb & 0xff;
        return red | (green << 8) | (blue << 16);
    }
}
