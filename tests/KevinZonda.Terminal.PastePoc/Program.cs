using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using KevinZonda.Terminal.UnixPty;

if (!UnixPtyProcess.IsSupported)
{
    Console.WriteLine("SKIP: Unix PTY paste PoC requires macOS or Linux.");
    return;
}

if (!File.Exists("/usr/bin/nano"))
{
    Console.WriteLine("SKIP: /usr/bin/nano was not found.");
    return;
}

Console.WriteLine("KTerm real PTY + nano paste PoC");
Console.WriteLine("Each scenario uses a fresh nano process and an unsaved temporary buffer.");
Console.WriteLine();
Console.WriteLine("mode\tsize\twrite\texit\toutput\tbracketed\tresult");

var scenarios = new[]
{
    new Scenario(0, Chunked: false),
    new Scenario(100, Chunked: false),
    new Scenario(512, Chunked: false),
    new Scenario(1024, Chunked: false),
    new Scenario(4 * 1024, Chunked: false),
    new Scenario(128 * 1024, Chunked: false),
    new Scenario(1024 * 1024, Chunked: false),
    new Scenario(4 * 1024, Chunked: true),
    new Scenario(128 * 1024, Chunked: true)
};

foreach (var scenario in scenarios)
{
    var result = await RunNanoScenarioAsync(scenario);
    Console.WriteLine(string.Join('\t',
        result.Chunked ? "throttled-512B" : "single-frame",
        FormatBytes(result.Size),
        FormatDuration(result.WriteDuration),
        FormatDuration(result.ExitDuration),
        FormatBytes(result.OutputBytes),
        result.BracketedPasteMode ? "yes" : "no",
        result.Outcome));
}

static async Task<Result> RunNanoScenarioAsync(Scenario scenario)
{
    var scratchPath = Path.Combine(Path.GetTempPath(), $"kterm-paste-poc-{Guid.NewGuid():N}.txt");
    await using var process = await UnixPtyProcess.StartAsync(new PtyStartInfo
    {
        FileName = "/usr/bin/nano",
        Arguments = [scratchPath],
        WorkingDirectory = Environment.CurrentDirectory,
        Columns = 120,
        Rows = 40,
        Environment = new Dictionary<string, string?>
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["LANG"] = "C",
            ["LC_ALL"] = "C"
        }
    });

    using var readLifetime = new CancellationTokenSource();
    var initialOutput = new ConcurrentQueue<byte>();
    long outputBytes = 0;
    var readTask = Task.Run(async () =>
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var count = await process.ReadAsync(buffer, readLifetime.Token);
                if (count == 0)
                {
                    return;
                }
                Interlocked.Add(ref outputBytes, count);
                if (initialOutput.Count < 256 * 1024)
                {
                    for (var index = 0; index < count; index++)
                    {
                        initialOutput.Enqueue(buffer[index]);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (readLifetime.IsCancellationRequested)
        {
        }
    });

    await Task.Delay(300);
    var bracketed = Encoding.ASCII.GetString(initialOutput.ToArray())
        .Contains("\x1b[?2004h", StringComparison.Ordinal);
    var body = MakeText(scenario.Size);
    var paste = bracketed
        ? Encoding.UTF8.GetBytes($"\x1b[200~{body}\x1b[201~")
        : Encoding.UTF8.GetBytes(body);

    var writeWatch = Stopwatch.StartNew();
    string outcome;
    try
    {
        using var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if (scenario.Chunked)
        {
            for (var offset = 0; offset < paste.Length; offset += 512)
            {
                var count = Math.Min(512, paste.Length - offset);
                await process.WriteAsync(paste.AsMemory(offset, count), writeTimeout.Token);
                // The current helper has no input acknowledgement. A short pause lets its
                // poll loop drain nano's output before another input frame becomes readable.
                await Task.Delay(10, writeTimeout.Token);
            }
        }
        else
        {
            await process.WriteAsync(paste, writeTimeout.Token);
        }
        outcome = "write-ok";
    }
    catch (OperationCanceledException)
    {
        outcome = "WRITE-TIMEOUT";
    }
    catch (Exception exception)
    {
        outcome = $"{exception.GetType().Name}: {exception.Message}";
    }
    writeWatch.Stop();

    var exitWatch = Stopwatch.StartNew();
    if (outcome == "write-ok")
    {
        try
        {
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WriteAsync(new byte[] { 0x18 }, exitTimeout.Token); // Ctrl+X
            await Task.Delay(50, exitTimeout.Token);
            if (!process.Completion.IsCompleted)
            {
                await process.WriteAsync("n"u8.ToArray(), exitTimeout.Token); // Do not save
            }
            await process.Completion.WaitAsync(exitTimeout.Token);
            outcome = "responsive";
        }
        catch (OperationCanceledException)
        {
            outcome = "EXIT-TIMEOUT";
        }
        catch (Exception exception)
        {
            outcome = $"{exception.GetType().Name}: {exception.Message}";
        }
    }
    exitWatch.Stop();

    readLifetime.Cancel();
    try
    {
        await readTask;
    }
    catch (OperationCanceledException)
    {
    }

    if (File.Exists(scratchPath))
    {
        File.Delete(scratchPath);
    }

    return new Result(
        scenario.Size,
        scenario.Chunked,
        writeWatch.Elapsed,
        exitWatch.Elapsed,
        Interlocked.Read(ref outputBytes),
        bracketed,
        outcome);
}

static string MakeText(int size)
{
    const string Line = "0123456789 abcdefghijklmnopqrstuvwxyz KTerm nano paste probe 0123456789\r";
    var builder = new StringBuilder(size + Line.Length);
    while (builder.Length < size)
    {
        builder.Append(Line);
    }
    return builder.ToString(0, size);
}

static string FormatBytes(long bytes) => bytes >= 1024 * 1024
    ? $"{bytes / 1024d / 1024d:F2} MiB"
    : $"{bytes / 1024d:F1} KiB";

static string FormatDuration(TimeSpan duration) => $"{duration.TotalMilliseconds:F0} ms";

internal sealed record Scenario(int Size, bool Chunked);

internal sealed record Result(
    int Size,
    bool Chunked,
    TimeSpan WriteDuration,
    TimeSpan ExitDuration,
    long OutputBytes,
    bool BracketedPasteMode,
    string Outcome);
