using Lunalyr.Core.Logging;

namespace Lunalyr.Core.Tests.Logging;

public class LoggerTests
{
    private sealed class ListSink : ILogSink
    {
        public List<LogEntry> Entries { get; } = [];
        public void Write(in LogEntry entry)
        {
            lock (Entries)
            {
                Entries.Add(entry);
            }
        }
    }

    private sealed class ThrowingSink : ILogSink
    {
        public void Write(in LogEntry entry) => throw new InvalidOperationException("sink is broken");
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test", "test");
    }

    [Fact]
    public void RoutesCategoryLevelAndLocalTimestamp()
    {
        var sink = new ListSink();
        var time = new FixedTime(new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero));
        var log = new Logger(LogLevel.Debug, time, sink);

        log.Sync.Info("offset 120 ms");
        log.Queue.Warn("skipped", new InvalidOperationException("boom"));

        Assert.Equal(2, sink.Entries.Count);
        Assert.Equal(LogCategory.Sync, sink.Entries[0].Category);
        Assert.Equal(LogLevel.Info, sink.Entries[0].Level);
        Assert.Equal("offset 120 ms", sink.Entries[0].Message);
        Assert.Equal(TimeSpan.FromHours(2), sink.Entries[0].Timestamp.Offset);
        Assert.Equal(10, sink.Entries[0].Timestamp.Hour);
        Assert.Contains("boom", sink.Entries[1].Exception);
    }

    [Fact]
    public void FiltersBelowMinimumLevel()
    {
        var sink = new ListSink();
        var log = new Logger(LogLevel.Warn, sink);

        log.Render.Debug("frame");
        log.Render.Info("frame");
        log.Render.Error("device lost");

        Assert.Single(sink.Entries);
        Assert.False(log.Render.IsEnabled(LogLevel.Info));
        log.MinimumLevel = LogLevel.Debug;
        Assert.True(log.Render.IsEnabled(LogLevel.Debug));
    }

    [Fact]
    public void FailingSinkNeverThrowsAndOtherSinksStillReceive()
    {
        var sink = new ListSink();
        var log = new Logger(LogLevel.Debug, new ThrowingSink(), sink);

        log.App.Error("still logged");

        Assert.Single(sink.Entries);
    }

    [Fact]
    public void NullLoggerAndDefaultCategoryLoggerAreSafe()
    {
        Logger.Null.Player.Error("ignored");
        default(CategoryLogger).Error("ignored");
        Assert.False(Logger.Null.IsEnabled(LogLevel.Error));
    }

    [Theory]
    [InlineData(LogCategory.App, "app")]
    [InlineData(LogCategory.Queue, "queue")]
    [InlineData(LogCategory.Sync, "sync")]
    [InlineData(LogCategory.Lyrics, "lyrics")]
    [InlineData(LogCategory.Player, "player")]
    [InlineData(LogCategory.Render, "render")]
    [InlineData(LogCategory.Settings, "settings")]
    public void CategoryNamesRoundTrip(LogCategory category, string name)
    {
        Assert.Equal(name, LogNames.Of(category));
        Assert.True(LogNames.TryParseCategory(name.ToUpperInvariant(), out var parsed));
        Assert.Equal(category, parsed);
    }
}
