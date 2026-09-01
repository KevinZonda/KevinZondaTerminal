using System.Diagnostics;

namespace KevinZonda.Terminal.Hosting;

internal static class SelfProcessLauncher
{
    internal static ProcessStartInfo CreateStartInfo(
        string workingDirectory,
        IEnumerable<string> arguments)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new FileNotFoundException("Unable to locate the KevinZonda Terminal executable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };

        // Framework-dependent debug runs are hosted by dotnet.exe. Published
        // apphost/single-file runs point directly at zt.exe and need no prefix.
        if (string.Equals(
                Path.GetFileNameWithoutExtension(executablePath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = typeof(Program).Assembly.GetName().Name
                ?? "KevinZonda.Terminal";
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException(
                    "Unable to locate the KevinZonda Terminal assembly.",
                    assemblyPath);
            }
            startInfo.ArgumentList.Add(assemblyPath);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
