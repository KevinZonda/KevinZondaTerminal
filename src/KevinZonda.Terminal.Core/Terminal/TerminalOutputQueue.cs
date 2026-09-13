using System.Text;

namespace KevinZonda.Terminal.Terminal;

// One UI consumer, with output arriving from background session readers.
internal sealed class TerminalOutputQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<string>> _queues = new();

    internal string[] SessionIds
    {
        get
        {
            lock (_gate)
            {
                return _queues.Keys.ToArray();
            }
        }
    }

    internal void Enqueue(string sessionId, string data)
    {
        lock (_gate)
        {
            if (!_queues.TryGetValue(sessionId, out var queue))
            {
                _queues.Add(sessionId, queue = new Queue<string>());
            }
            queue.Enqueue(data);
        }
    }

    internal string DequeueBatch(string sessionId, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);
        lock (_gate)
        {
            if (!_queues.TryGetValue(sessionId, out var queue))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            while (builder.Length < maxChars && queue.TryDequeue(out var chunk))
            {
                builder.Append(chunk);
            }

            // Removal and enqueue must share the lock. Otherwise a producer can
            // append to a queue after it was observed empty, just before removal,
            // silently losing output (possibly half of a VT escape sequence).
            if (queue.Count == 0)
            {
                _queues.Remove(sessionId);
            }
            return builder.ToString();
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _queues.Clear();
        }
    }
}
