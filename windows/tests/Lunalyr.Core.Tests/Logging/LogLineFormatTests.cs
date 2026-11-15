using Lunalyr.Core.Logging;

namespace Lunalyr.Core.Tests.Logging;

public class LogLineFormatTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 11, 12, 10, 15, 30, 123, TimeSpan.FromHours(1));

    [Fact]
    public void FormatsSingleLineEntry()
    {
        var line = LogLineFormat.Format(new LogEntry(Stamp, LogLevel.Info, LogCategory.Sync, "matched"));

        Assert.Equal("2026-11-12T10:15:30.123+01:00\tINFO\tsync\tmatched\n", line);
    }

    [Fact]
    public void MultiLineMessageAndExceptionBecomeTabContinuationLines()
    {
        var line = LogLineFormat.Format(new LogEntry(Stamp, LogLevel.Error, LogCategory.Lyrics, "first\r\nsecond\rthird", "Ex: bad\n   at Foo()"));

        var lines = line.TrimEnd('\n').Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.EndsWith("\tfirst", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("\t", l));
        Assert.Equal("\tsecond", lines[1]);
        Assert.Equal("\tthird", lines[2]);
        Assert.Equal("\tEx: bad", lines[3]);
        Assert.Equal("\t   at Foo()", lines[4]);
        Assert.DoesNotContain('\r', line);
    }

    [Fact]
    public void HeaderParsesBack()
    {
        var formatted = LogLineFormat.Format(new LogEntry(Stamp, LogLevel.Warn, LogCategory.Queue, "tab\tinside"));

        Assert.True(LogLineFormat.TryParseHeader(formatted.TrimEnd('\n'), out var entry));
        Assert.Equal(Stamp, entry.Timestamp);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Equal(LogCategory.Queue, entry.Category);
        Assert.Equal("tab\tinside", entry.Message);
        Assert.False(LogLineFormat.TryParseHeader("\tcontinuation", out _));
    }
}
