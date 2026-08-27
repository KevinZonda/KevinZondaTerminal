namespace KevinZonda.Terminal.Server;

internal sealed record DashboardServerSnapshot(
    bool Enabled,
    string Version,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset GeneratedAtUtc,
    string StartingDirectory,
    double RuntimeRetentionMinutes,
    int RuntimeCount,
    int ConnectedRuntimeCount,
    int SessionCount,
    string? CsrfToken,
    IReadOnlyList<DashboardRuntimeSnapshot> Runtimes);

internal sealed record DashboardRuntimeSnapshot(
    string RuntimeId,
    DateTimeOffset CreatedAtUtc,
    bool Connected,
    DateTimeOffset? LastConnectedAtUtc,
    DateTimeOffset? LastDisconnectedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    long BufferedOutputBytes,
    IReadOnlyList<DashboardSessionSnapshot> Sessions);

internal sealed record DashboardSessionSnapshot(
    string SessionId,
    string ShellName,
    uint ProcessId,
    int Columns,
    int Rows,
    bool Exited,
    uint? ExitCode,
    string? Failure,
    long BufferedOutputBytes);
