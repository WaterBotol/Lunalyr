using System.Text;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Logging;

namespace Lunalyr.Platform.Windows.Tests.Logging;

public class RollingFileLogSinkTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(1));

    private static LogEntry Entry(DateTimeOffset at, string message, LogCategory category = LogCategory.Sync, LogLevel level = LogLevel.Info) =>
        new(at, level, category, message);

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static RollingFileLogOptions Options(TempFolder temp, DateTimeOffset? now = null) =>
        new(temp.Path) { TimeProvider = new ManualTimeProvider(now ?? Day1, (now ?? Day1).Offset) };

    [Fact]
    public async Task WritesOneUtf8FilePerDayThatCanBeReadWhileOpen()
    {
        using var temp = new TempFolder();
        await using var sink = new RollingFileLogSink(Options(temp));

        sink.Write(Entry(Day1, "歌词 matched"));
        sink.Write(Entry(Day1.AddMinutes(1), "second", LogCategory.Queue, LogLevel.Warn));
        await sink.FlushAsync();

        Assert.Equal(["lunalyr-2026-11-12.log"], temp.FileNames());
        var lines = ReadShared(temp.File("lunalyr-2026-11-12.log")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("2026-11-12T10:00:00.000+01:00\tINFO\tsync\t歌词 matched", lines[0]);
        Assert.Contains("\tWARN\tqueue\tsecond", lines[1]);
        using var raw = new FileStream(temp.File("lunalyr-2026-11-12.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Equal((byte)'2', (byte)raw.ReadByte()); // No BOM: the file starts with the timestamp.
    }

    [Fact]
    public async Task RollsToANewFileWhenTheLocalDayChanges()
    {
        using var temp = new TempFolder();
        await using var sink = new RollingFileLogSink(Options(temp));

        sink.Write(Entry(Day1, "today"));
        sink.Write(Entry(Day1.AddDays(1), "tomorrow"));
        await sink.FlushAsync();

        Assert.Equal(["lunalyr-2026-11-12.log", "lunalyr-2026-11-13.log"], temp.FileNames());
        Assert.Contains("tomorrow", ReadShared(temp.File("lunalyr-2026-11-13.log")));
        Assert.DoesNotContain("tomorrow", ReadShared(temp.File("lunalyr-2026-11-12.log")));
    }

    [Fact]
    public async Task RollsOverAtTheSizeCapKeepingOrder()
    {
        using var temp = new TempFolder();
        await using var sink = new RollingFileLogSink(Options(temp) with { MaxFileBytes = 200 });

        for (var i = 0; i < 12; i++)
        {
            sink.Write(Entry(Day1.AddSeconds(i), $"entry {i:D2} padding padding"));
        }
        await sink.FlushAsync();

        var files = temp.FileNames();
        Assert.True(files.Length >= 3, string.Join(", ", files));
        Assert.Contains("lunalyr-2026-11-12.log", files);
        Assert.Contains("lunalyr-2026-11-12.1.log", files);
        var ordered = files.OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal).ToList();
        var all = string.Concat(ordered.Select(f => ReadShared(temp.File(f))));
        for (var i = 0; i < 12; i++)
        {
            Assert.Contains($"entry {i:D2}", all);
        }
        Assert.True(all.IndexOf("entry 00", StringComparison.Ordinal) < all.IndexOf("entry 11", StringComparison.Ordinal));
        Assert.All(files, f => Assert.True(new FileInfo(temp.File(f)).Length <= 200, f));
    }

    [Fact]
    public async Task AppendsToTheExistingFileForTodayAfterARestart()
    {
        using var temp = new TempFolder();
        await using (var first = new RollingFileLogSink(Options(temp)))
        {
            first.Write(Entry(Day1, "before restart"));
        }
        await using (var second = new RollingFileLogSink(Options(temp)))
        {
            second.Write(Entry(Day1.AddMinutes(5), "after restart"));
        }

        var text = ReadShared(temp.File("lunalyr-2026-11-12.log"));
        Assert.Contains("before restart", text);
        Assert.Contains("after restart", text);
    }

    [Fact]
    public async Task KeepsTheLastSevenDaysAndLeavesOtherFilesAlone()
    {
        using var temp = new TempFolder();
        for (var daysAgo = 0; daysAgo <= 9; daysAgo++)
        {
            var day = DateOnly.FromDateTime(Day1.DateTime).AddDays(-daysAgo);
            File.WriteAllText(temp.File($"lunalyr-{day:yyyy-MM-dd}.log"), "old\n");
            File.WriteAllText(temp.File($"lunalyr-{day:yyyy-MM-dd}.1.log"), "old\n");
        }
        File.WriteAllText(temp.File("notes.txt"), "keep");
        File.WriteAllText(temp.File("other-2020-01-01.log"), "keep");
        File.WriteAllText(temp.File("lunalyr-not-a-date.log"), "keep");

        await using (var sink = new RollingFileLogSink(Options(temp)))
        {
            sink.Write(Entry(Day1, "now"));
            await sink.FlushAsync();
        }

        var files = temp.FileNames();
        Assert.Contains("lunalyr-2026-11-06.log", files);
        Assert.Contains("lunalyr-2026-11-06.1.log", files);
        Assert.DoesNotContain("lunalyr-2026-11-05.log", files);
        Assert.DoesNotContain("lunalyr-2026-11-05.1.log", files);
        Assert.DoesNotContain("lunalyr-2026-11-03.log", files);
        Assert.Contains("notes.txt", files);
        Assert.Contains("other-2020-01-01.log", files);
        Assert.Contains("lunalyr-not-a-date.log", files);
        Assert.Equal(7 * 2 + 3, files.Length);
    }

    [Fact]
    public async Task RetentionAlsoRunsWhenTheDayChangesWhileRunning()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("lunalyr-2026-11-06.log"), "old\n");
        await using var sink = new RollingFileLogSink(Options(temp));

        sink.Write(Entry(Day1, "today"));
        await sink.FlushAsync();
        Assert.Contains("lunalyr-2026-11-06.log", temp.FileNames());

        sink.Write(Entry(Day1.AddDays(1), "tomorrow"));
        await sink.FlushAsync();
        Assert.DoesNotContain("lunalyr-2026-11-06.log", temp.FileNames());
    }

    [Fact]
    public async Task WhenTheQueueIsFullEntriesAreDroppedCountedAndReportedWithoutBlocking()
    {
        using var temp = new TempFolder();
        var gate = new TaskCompletionSource();
        await using var sink = new RollingFileLogSink(Options(temp) with { QueueCapacity = 4, WriterStartGate = gate.Task });

        for (var i = 0; i < 10; i++)
        {
            sink.Write(Entry(Day1, $"entry {i}"));
        }
        Assert.Equal(6, sink.DroppedCount);

        gate.SetResult();
        await sink.FlushAsync();

        var text = ReadShared(temp.File("lunalyr-2026-11-12.log"));
        Assert.Contains("entry 3", text);
        Assert.DoesNotContain("entry 4", text);
        Assert.Contains("6 log entries were dropped", text);
    }

    [Fact]
    public async Task DisposeFlushesAndLaterWritesAreDroppedQuietly()
    {
        using var temp = new TempFolder();
        var sink = new RollingFileLogSink(Options(temp));
        sink.Write(Entry(Day1, "last words"));
        await sink.DisposeAsync();

        Assert.Contains("last words", ReadShared(temp.File("lunalyr-2026-11-12.log")));
        sink.Write(Entry(Day1, "after dispose"));
        await sink.FlushAsync();
        Assert.Equal(1, sink.DroppedCount);
    }

    [Fact]
    public async Task AnUnwritableFolderNeverThrowsIntoCallers()
    {
        using var temp = new TempFolder();
        var notAFolder = temp.File("blocker");
        File.WriteAllText(notAFolder, "a file where the log folder should be");
        await using var sink = new RollingFileLogSink(new RollingFileLogOptions(notAFolder));
        var log = new Logger(LogLevel.Debug, sink);

        log.App.Error("cannot be written");
        await sink.FlushAsync();

        Assert.Equal(1, sink.WriteFailureCount);
    }

    [Theory]
    [InlineData("lunalyr-2026-11-12.log", true, 0)]
    [InlineData("lunalyr-2026-11-12.3.log", true, 3)]
    [InlineData("LUNALYR-2026-11-12.LOG", true, 0)]
    [InlineData("lunalyr-2026-13-12.log", false, 0)]
    [InlineData("lunalyr-2026-11-12.log.bak", false, 0)]
    [InlineData("lunalyrx-2026-11-12.log", false, 0)]
    public void ParsesOnlyItsOwnFileNames(string name, bool ok, int index)
    {
        Assert.Equal(ok, RollingFileLogSink.TryParseFileName(name, "lunalyr", out var day, out var parsedIndex));
        if (ok)
        {
            Assert.Equal(new DateOnly(2026, 11, 12), day);
            Assert.Equal(index, parsedIndex);
            Assert.Equal(name.ToLowerInvariant(), RollingFileLogSink.FileNameFor("lunalyr", day, index));
        }
    }
}
