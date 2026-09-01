using System.Diagnostics;
using KevinZonda.AgentUsageMonitor;

namespace KevinZonda.Terminal.AvaloniaDesktop;

internal sealed class UnixAgentProcessDetector
{
    internal async Task<IReadOnlySet<UsageProvider>> DetectAsync(
        IReadOnlyCollection<int> sessionProcessIds,
        CancellationToken cancellationToken)
    {
        if (sessionProcessIds.Count == 0)
        {
            return new HashSet<UsageProvider>();
        }

        var output = await CaptureProcessSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return DetectSnapshot(output, sessionProcessIds);
    }

    internal static IReadOnlySet<UsageProvider> DetectSnapshot(
        string snapshot,
        IReadOnlyCollection<int> sessionProcessIds)
    {
        var entries = ParseSnapshot(snapshot);
        var entriesById = entries.ToDictionary(entry => entry.ProcessId);
        var children = entries
            .GroupBy(entry => entry.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var providers = new HashSet<UsageProvider>();
        var visited = new HashSet<int>();
        var pending = new Queue<int>(sessionProcessIds);

        while (pending.TryDequeue(out var processId))
        {
            if (!visited.Add(processId))
            {
                continue;
            }

            if (entriesById.TryGetValue(processId, out var entry)
                && Classify(entry.Command) is { } provider)
            {
                providers.Add(provider);
            }

            if (children.TryGetValue(processId, out var descendants))
            {
                foreach (var descendant in descendants)
                {
                    pending.Enqueue(descendant.ProcessId);
                }
            }
        }

        return providers;
    }

    internal static UsageProvider? Classify(string command)
    {
        var name = Path.GetFileNameWithoutExtension(command.Trim().TrimStart('-'));
        if (name.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("codex-", StringComparison.OrdinalIgnoreCase))
        {
            return UsageProvider.Codex;
        }

        if (name.Equals("kimi", StringComparison.OrdinalIgnoreCase)
            || name.Equals("kimi-code", StringComparison.OrdinalIgnoreCase)
            || name.Equals("kimi_code", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("kimi-code-", StringComparison.OrdinalIgnoreCase))
        {
            return UsageProvider.KimiCode;
        }

        return null;
    }

    private static IReadOnlyList<ProcessEntry> ParseSnapshot(string snapshot)
    {
        var entries = new List<ProcessEntry>();
        foreach (var line in snapshot.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(
                (char[]?)null,
                3,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 3
                && int.TryParse(parts[0], out var processId)
                && int.TryParse(parts[1], out var parentProcessId))
            {
                entries.Add(new ProcessEntry(processId, parentProcessId, parts[2]));
            }
        }
        return entries;
    }

    private static async Task<string> CaptureProcessSnapshotAsync(CancellationToken cancellationToken)
    {
        var executable = File.Exists("/bin/ps") ? "/bin/ps" : "/usr/bin/ps";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-axo");
        process.StartInfo.ArgumentList.Add("pid=,ppid=,comm=");

        try
        {
            if (!process.Start())
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : string.Empty;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return string.Empty;
        }
    }

    private sealed record ProcessEntry(int ProcessId, int ParentProcessId, string Command);
}
