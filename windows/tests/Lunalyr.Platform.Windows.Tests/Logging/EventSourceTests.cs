using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Logging;

namespace Lunalyr.Platform.Windows.Tests.Logging;

public class EventSourceTests
{
    private sealed class Listener : EventListener
    {
        // Field initialisers run before the base constructor, which may already call OnEventSourceCreated.
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public IReadOnlyCollection<EventWrittenEventArgs> Events => _events;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == LunalyrEventSource.ProviderName)
            {
                EnableEvents(eventSource, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData) => _events.Enqueue(eventData);
    }

    [Fact]
    public void ProviderNameAndGuidMatchTheDocs()
    {
        Assert.Equal("Lunalyr", LunalyrEventSource.Log.Name);
        // Documented in docs/LOGGING.md for WPR / logman, which need the GUID.
        Assert.Equal(new Guid("aabe7a04-cf7b-5b6d-7a70-4b7caec9dc02"), LunalyrEventSource.Log.Guid);
        Assert.Null(LunalyrEventSource.Log.ConstructionException);
    }

    [Fact]
    public void LoggerEntriesReachEtwListenersWithCategoryAndLevel()
    {
        _ = LunalyrEventSource.Log;
        using var listener = new Listener();
        var log = new Logger(LogLevel.Debug, new EventSourceLogSink());
        var marker = Guid.NewGuid().ToString("N");

        log.Sync.Debug($"debug {marker}");
        log.Queue.Warn($"warn {marker}", new InvalidOperationException("why"));

        var mine = listener.Events.Where(e => e.Payload?.Count == 3 && e.Payload[1] is string m && m.Contains(marker, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, mine.Count);
        Assert.Equal(EventLevel.Verbose, mine[0].Level);
        Assert.Equal("Debug", mine[0].EventName);
        Assert.Equal("sync", mine[0].Payload![0]);
        Assert.Equal(EventLevel.Warning, mine[1].Level);
        Assert.Equal("queue", mine[1].Payload![0]);
        Assert.Contains("why", (string)mine[1].Payload![2]!);
        Assert.Equal(["category", "message", "exception"], mine[1].PayloadNames);
    }
}
