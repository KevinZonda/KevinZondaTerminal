namespace KevinZonda.Terminal.Server.Launcher;

internal enum LauncherLogSource
{
    System,
    StandardOutput,
    StandardError
}

internal sealed record LauncherLogEntry(
    DateTimeOffset Timestamp,
    LauncherLogSource Source,
    string Message);

internal sealed class LauncherLogBuffer
{
    private const int MaximumEntries = 10_000;
    private readonly Lock _gate = new();
    private readonly List<LauncherLogEntry> _entries = [];
    private Action<LauncherLogEntry>? _entryAdded;
    private Action? _cleared;

    internal void Add(LauncherLogSource source, string message)
    {
        var entry = new LauncherLogEntry(DateTimeOffset.Now, source, message);
        Action<LauncherLogEntry>? listeners;
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > MaximumEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaximumEntries);
            }
            listeners = _entryAdded;
        }
        listeners?.Invoke(entry);
    }

    internal IReadOnlyList<LauncherLogEntry> Subscribe(
        Action<LauncherLogEntry> entryAdded,
        Action cleared)
    {
        lock (_gate)
        {
            _entryAdded += entryAdded;
            _cleared += cleared;
            return _entries.ToArray();
        }
    }

    internal void Unsubscribe(
        Action<LauncherLogEntry> entryAdded,
        Action cleared)
    {
        lock (_gate)
        {
            _entryAdded -= entryAdded;
            _cleared -= cleared;
        }
    }

    internal void Clear()
    {
        Action? listeners;
        lock (_gate)
        {
            _entries.Clear();
            listeners = _cleared;
        }
        listeners?.Invoke();
    }
}
