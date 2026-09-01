# KevinZonda.Terminal.UnixPty

`KevinZonda.Terminal.UnixPty` is a UI-independent, byte-oriented PTY process
library for macOS and Linux. It is intended for use by the KTerm Server and a
future desktop host such as Avalonia; it has no dependency on either one.

The managed library launches a small native `kterm-pty-helper`. The helper owns
`forkpty`, `exec`, terminal resize, and process-group shutdown. Keeping the
post-fork path native avoids executing managed .NET code in the child process.

```csharp
await using var process = await UnixPtyProcess.StartAsync(new PtyStartInfo
{
    FileName = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh",
    Arguments = ["-l"],
    WorkingDirectory = Environment.CurrentDirectory,
    Environment = new Dictionary<string, string?>
    {
        ["TERM"] = "xterm-256color",
        ["COLORTERM"] = "truecolor"
    },
    Columns = 80,
    Rows = 24
});

await process.WriteAsync("echo hello\n"u8.ToArray());
var buffer = new byte[16 * 1024];
var count = await process.ReadAsync(buffer);
```

The native helper is compiled with `clang` on macOS or `cc` on Linux and copied
next to the managed assembly. Windows can compile the managed project for
solution compatibility, but `UnixPtyProcess.StartAsync` throws
`PlatformNotSupportedException` there.
