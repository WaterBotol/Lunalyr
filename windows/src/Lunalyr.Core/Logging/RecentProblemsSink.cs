namespace Lunalyr.Core.Logging;

/// <summary>
/// Keeps the last warnings and errors in memory, for Settings › General › Copy diagnostics (tester feedback on 0.9.0: a
/// tester's log can be weeks away, a paste is not) and for the notice that a display's wallpaper could not start.
/// Thread-safe.
/// </summary>
public sealed class RecentProblemsSink(int capacity = 40) : ILogSink
{
    private readonly Lock _gate = new();
    private readonly Queue<LogEntry> _entries = new();

    /// <summary>Raised for each warning or error, on the thread that logged it.</summary>
    public event Action<LogEntry>? Logged;

    public void Write(in LogEntry entry)
    {
        if (entry.Level < LogLevel.Warn) return;
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > capacity) _entries.Dequeue();
        }
        try
        {
            Logged?.Invoke(entry);
        }
        catch (Exception)
        {
            // A listener must never break logging.
        }
    }

    /// <summary>The warnings and errors kept, oldest first.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_gate) return [.. _entries]; }
    }
}
