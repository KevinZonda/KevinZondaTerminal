namespace KevinZonda.Terminal.UnixPty;

/// <summary>Describes a process to start inside a Unix pseudoterminal.</summary>
public sealed record PtyStartInfo
{
    /// <summary>An executable path or a name resolved through <c>PATH</c>.</summary>
    public required string FileName { get; init; }

    /// <summary>Arguments passed directly to the executable without shell parsing.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The initial working directory of the child process.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Environment overrides. A null value removes an inherited variable.
    /// The current process environment is inherited before these overrides are applied.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>Initial terminal width in character cells.</summary>
    public int Columns { get; init; } = 80;

    /// <summary>Initial terminal height in character cells.</summary>
    public int Rows { get; init; } = 24;

    /// <summary>
    /// Optional explicit path to the native helper. When omitted, the helper is
    /// resolved next to the managed application.
    /// </summary>
    public string? HelperPath { get; init; }
}
