using Lunalyr.Core.Logging;

namespace Lunalyr.Core.Tests.Logging;

public class RecentProblemsSinkTests
{
    [Fact]
    public void KeepsOnlyTheLastWarningsAndErrors()
    {
        var sink = new RecentProblemsSink(capacity: 2);
        var heard = new List<string>();
        sink.Logged += entry => heard.Add(entry.Message);
        var logger = new Logger(LogLevel.Debug, sink);
        logger.Render.Info("fine");
        logger.Render.Warn("one");
        logger.Render.Error("two");
        logger.App.Warn("three");
        Assert.Equal(["two", "three"], sink.Entries.Select(e => e.Message));
        Assert.Equal(["one", "two", "three"], heard);
    }

    [Fact]
    public void AListenerThatThrowsNeverBreaksLogging()
    {
        var sink = new RecentProblemsSink();
        sink.Logged += _ => throw new InvalidOperationException();
        new Logger(LogLevel.Debug, sink).App.Warn("still kept");
        Assert.Single(sink.Entries);
    }
}
