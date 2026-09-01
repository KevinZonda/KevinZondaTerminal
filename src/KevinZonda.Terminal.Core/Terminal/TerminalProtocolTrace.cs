using System.Diagnostics;
using System.Text.RegularExpressions;

namespace KevinZonda.Terminal.Terminal;

internal static partial class TerminalProtocolTrace
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("KTERM_OSC_TRACE"), "1", StringComparison.Ordinal);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object FileLock = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".kterm",
        "osc-trace.log");

    internal static void Observe(string sessionId, string direction, string data)
    {
        if (!Enabled || string.IsNullOrEmpty(data))
        {
            return;
        }

        foreach (Match match in OscDefaultColorPattern().Matches(data))
        {
            var slot = match.Groups["slot"].Value;
            var value = match.Groups["value"].Value;
            var kind = value == "?" ? "query" : "reply";
            Write($"{Clock.Elapsed.TotalMilliseconds,9:F1} ms pid={Environment.ProcessId} " +
                $"session={sessionId} {direction} OSC {slot} {kind} {value}");
        }
    }

    private static void Write(string message)
    {
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never interfere with terminal I/O.
        }
    }

    [GeneratedRegex("\\x1B\\](?<slot>10|11);(?<value>\\?|(?:rgb|rgba):[0-9a-fA-F/]+)(?:\\x07|\\x1B\\\\)")]
    private static partial Regex OscDefaultColorPattern();
}
