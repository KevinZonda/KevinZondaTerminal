using System.Text;

namespace KevinZonda.Terminal.Hosting;

internal static class CrashReportStore
{
    internal static string CreatePath()
    {
        var root = TryCreateDirectory(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KTerm",
                "CrashReports"))
            ?? TryCreateDirectory(Path.Combine(Path.GetTempPath(), "KTerm", "CrashReports"))
            ?? throw new IOException("KTerm could not create a crash-report directory.");
        return Path.Combine(
            root,
            $"crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
    }

    private static string? TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void Write(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The crash report path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                contents + (contents.EndsWith(Environment.NewLine, StringComparison.Ordinal)
                    ? string.Empty
                    : Environment.NewLine),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
