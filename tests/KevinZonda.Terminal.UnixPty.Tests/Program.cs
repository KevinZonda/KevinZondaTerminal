using System.Text;
using KevinZonda.Terminal.UnixPty;

if (!UnixPtyProcess.IsSupported)
{
    Console.WriteLine("SKIP Unix PTY integration test is supported only on macOS and Linux.");
    return;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
await using var process = await UnixPtyProcess.StartAsync(
    new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments =
        [
            "-c",
            "test -t 0 && test -t 1 || exit 90; " +
            "printf 'PTY_READY\\n'; read value; stty size; printf 'INPUT:%s\\n' \"$value\"; exit 7"
        ],
        WorkingDirectory = Environment.CurrentDirectory,
        Environment = new Dictionary<string, string?>
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor"
        },
        Columns = 80,
        Rows = 24
    },
    timeout.Token);

if (process.ProcessId <= 0)
{
    throw new InvalidOperationException("The PTY did not expose a valid child process ID.");
}

var outputTask = ReadAllOutputAsync(process, timeout.Token);
await process.ResizeAsync(101, 37, timeout.Token);
await process.WriteAsync("hello from kterm\n"u8.ToArray(), timeout.Token);

var status = await process.Completion.WaitAsync(timeout.Token);
var output = (await outputTask).Replace("\r\n", "\n", StringComparison.Ordinal);

Contains(output, "PTY_READY\n");
Contains(output, "37 101\n");
Contains(output, "INPUT:hello from kterm\n");
Equal(7, status.ExitCode);
Equal<int?>(null, status.Signal);

Console.WriteLine("PASS Unix PTY spawn, TTY, input, output, resize, and exit status");

var stubbornProcess = await UnixPtyProcess.StartAsync(
    new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments =
        [
            "-c",
            "trap '' HUP TERM; printf 'STUBBORN_READY\\n'; while :; do sleep 1; done"
        ],
        WorkingDirectory = Environment.CurrentDirectory
    },
    timeout.Token);
var stubbornCompletion = stubbornProcess.Completion;
await ReadUntilAsync(stubbornProcess, "STUBBORN_READY", timeout.Token);
await stubbornProcess.DisposeAsync();
var stubbornStatus = await stubbornCompletion.WaitAsync(timeout.Token);
if (stubbornStatus.Signal is not (9 or 15 or 1))
{
    throw new InvalidOperationException(
        $"Expected PTY disposal to signal the child process, got '{stubbornStatus}'.");
}

Console.WriteLine("PASS Unix PTY disposal terminates a stubborn process group");

static async Task ReadUntilAsync(
    UnixPtyProcess process,
    string marker,
    CancellationToken cancellationToken)
{
    var received = new StringBuilder();
    var buffer = new byte[1024];
    while (!received.ToString().Contains(marker, StringComparison.Ordinal))
    {
        var count = await process.ReadAsync(buffer, cancellationToken);
        if (count == 0)
        {
            throw new EndOfStreamException($"PTY output ended before marker '{marker}'.");
        }
        received.Append(Encoding.UTF8.GetString(buffer, 0, count));
    }
}

static async Task<string> ReadAllOutputAsync(
    UnixPtyProcess process,
    CancellationToken cancellationToken)
{
    using var output = new MemoryStream();
    var buffer = new byte[4096];
    while (true)
    {
        var count = await process.ReadAsync(buffer, cancellationToken);
        if (count == 0)
        {
            break;
        }
        output.Write(buffer, 0, count);
    }
    return Encoding.UTF8.GetString(output.ToArray());
}

static void Contains(string actual, string expected)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Expected PTY output to contain '{expected}', got '{actual}'.");
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
