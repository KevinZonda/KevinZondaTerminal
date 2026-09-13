using System.Collections.Concurrent;
using System.Text;
using KevinZonda.Terminal.Terminal;

// Reproduce the old bridge's empty-check/removal interleaving without relying
// on scheduler timing. The producer's new chunk becomes unreachable.
var oldQueues = new ConcurrentDictionary<string, ConcurrentQueue<string>>();
var oldQueue = oldQueues.GetOrAdd("session", _ => new());
oldQueue.Enqueue("\x1b[");
oldQueue.TryDequeue(out _);
Require(oldQueue.IsEmpty, "Expected the UI to observe an empty queue.");
oldQueues.GetOrAdd("session", _ => new()).Enqueue("38;2;93;64;84m");
oldQueues.TryRemove(new KeyValuePair<string, ConcurrentQueue<string>>("session", oldQueue));
Require(oldQueues.IsEmpty && !oldQueue.IsEmpty, "Old output-loss interleaving was not reproduced.");
Console.WriteLine("PASS reproduced the old queue removal race");

var queues = new TerminalOutputQueue();
queues.Enqueue("a", "\x1b[");
queues.Enqueue("a", "38;2;93;64;84;48;2;72;39;62m中文🙂\x1b[0m");
queues.Enqueue("b", "other session");
Require(queues.DequeueBatch("a", 1) == "\x1b[", "A batch split an output chunk.");
Require(queues.DequeueBatch("a", 1) == "38;2;93;64;84;48;2;72;39;62m中文🙂\x1b[0m", "Color/Unicode output changed.");
Require(queues.DequeueBatch("b", 64) == "other session", "Session output was mixed.");
Require(queues.SessionIds.Length == 0, "Drained queues were not reclaimed.");
queues.Enqueue("a", "new output");
Require(queues.DequeueBatch("a", 64) == "new output", "Output after queue removal was lost.");

const int count = 100_000;
const int sessions = 4;
var received = Enumerable.Range(0, sessions).Select(_ => new StringBuilder()).ToArray();
string Chunk(int index) => $"\x1b[38;2;93;64;84;48;2;72;39;62m{index}:中文🙂\x1b[0m\r\n";
using var start = new ManualResetEventSlim();
var producers = Enumerable.Range(0, sessions).Select(session => Task.Run(() =>
{
    start.Wait();
    for (var i = 0; i < count; i++)
    {
        // Deliberately split the escape introducer from its parameters.
        queues.Enqueue(session.ToString(), "\x1b[");
        queues.Enqueue(session.ToString(), Chunk(i)[2..]);
        if (i % 32 == 0) Thread.Yield();
    }
})).ToArray();
var done = Task.WhenAll(producers);
start.Set();
while (!done.IsCompleted || queues.SessionIds.Length > 0)
{
    foreach (var session in queues.SessionIds)
    {
        received[int.Parse(session)].Append(queues.DequeueBatch(session, 1024));
    }
    Thread.Yield();
}
await done;
var expected = string.Concat(Enumerable.Range(0, count).Select(Chunk));
foreach (var output in received)
{
    Require(output.ToString() == expected, "Concurrent output was lost, duplicated, or reordered.");
}
queues.Enqueue("last", "pending");
queues.Clear();
Require(queues.SessionIds.Length == 0, "Clear left pending output.");
Console.WriteLine("PASS batching, Unicode, session isolation, and 800,000 concurrent chunks");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
