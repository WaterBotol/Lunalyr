using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Lunalyr.Core.Logging;

namespace Lunalyr.Platform.Logging;

/// <summary>Options for <see cref="RollingFileLogSink"/>. Only <see cref="Folder"/> is required.</summary>
public sealed record RollingFileLogOptions(string Folder)
{
    /// <summary>File name prefix: files are <c>{prefix}-yyyy-MM-dd.log</c>, then <c>.1.log</c>, <c>.2.log</c>… when a day's file fills up.</summary>
    public string FilePrefix { get; init; } = "lunalyr";

    /// <summary>Size cap per file before rolling to the next index. Default 10 MB.</summary>
    public long MaxFileBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>How many days of files to keep, today included. Default 7.</summary>
    public int RetainDays { get; init; } = 7;

    /// <summary>Entries waiting to be written. When full, new entries are dropped and counted (callers never block).</summary>
    public int QueueCapacity { get; init; } = 8192;

    /// <summary>Clock used for the retention sweep at start-up. Entry dates come from the entries themselves.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Upper bound for <see cref="RollingFileLogSink.FlushAsync"/> and <see cref="RollingFileLogSink.DisposeAsync"/>.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Test hook: the writer task waits for this before draining the queue.</summary>
    internal Task? WriterStartGate { get; init; }
}

/// <summary>
/// Writes log entries to one UTF-8 file per local day under a folder, rolling over at a size cap and deleting
/// files older than the retention window. <see cref="Write"/> only enqueues onto a bounded channel; a single
/// background task does the IO. Nothing here throws into callers.
/// </summary>
public sealed partial class RollingFileLogSink : ILogSink, IAsyncDisposable
{
    private readonly record struct Item(LogEntry Entry, TaskCompletionSource? FlushSignal);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly RollingFileLogOptions _options;
    private readonly Channel<Item> _channel;
    private readonly Task _writerTask;

    private FileStream? _stream;
    private DateOnly _day;
    private int _index;
    private long _size;
    private long _droppedSinceNotice;
    private long _droppedTotal;
    private long _writeFailures;
    private volatile string? _currentPath;

    public RollingFileLogSink(RollingFileLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Folder);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFileBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RetainDays, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.QueueCapacity, 1);
        _options = options;
        _channel = Channel.CreateBounded<Item>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _writerTask = Task.Run(RunAsync);
    }

    public string Folder => _options.Folder;

    /// <summary>The file currently being written, or null before the first entry.</summary>
    public string? CurrentFilePath => _currentPath;

    /// <summary>Entries dropped because the queue was full or the sink was shut down.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedTotal);

    /// <summary>Entries that could not be written because of IO errors.</summary>
    public long WriteFailureCount => Interlocked.Read(ref _writeFailures);

    public void Write(in LogEntry entry)
    {
        if (!_channel.Writer.TryWrite(new Item(entry, null)))
        {
            Interlocked.Increment(ref _droppedSinceNotice);
            Interlocked.Increment(ref _droppedTotal);
        }
    }

    /// <summary>Waits until everything queued before this call is written and flushed. Bounded by the shutdown timeout.</summary>
    public async Task FlushAsync()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(_options.ShutdownTimeout);
            await _channel.Writer.WriteAsync(new Item(default, signal), cts.Token).ConfigureAwait(false);
            await signal.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Closed or timed out: nothing more we can do, and shutdown must not hang or throw.
        }
    }

    /// <summary>Stops accepting entries, writes what is queued (bounded by the shutdown timeout) and closes the file.</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _writerTask.WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timed out: the writer task closes the file when it gets there.
        }
    }

    private async Task RunAsync()
    {
        try
        {
            if (_options.WriterStartGate is { } gate)
            {
                await gate.ConfigureAwait(false);
            }
            SweepRetention(DateOnly.FromDateTime(_options.TimeProvider.GetLocalNow().DateTime));
            var reader = _channel.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    if (item.FlushSignal is { } signal)
                    {
                        WriteDroppedNotice();
                        FlushStream(toDisk: false);
                        signal.TrySetResult();
                        continue;
                    }
                    WriteEntry(item.Entry);
                }
                WriteDroppedNotice();
                FlushStream(toDisk: false);
            }
        }
        catch (Exception)
        {
            // Never let the writer task fault; fall through and close the file.
        }
        finally
        {
            FlushStream(toDisk: true);
            CloseStream();
        }
    }

    private void WriteDroppedNotice()
    {
        var dropped = Interlocked.Exchange(ref _droppedSinceNotice, 0);
        if (dropped > 0)
        {
            WriteEntry(new LogEntry(_options.TimeProvider.GetLocalNow(), LogLevel.Warn, LogCategory.App,
                $"{dropped} log entries were dropped because the log queue was full"));
        }
    }

    private void WriteEntry(in LogEntry entry)
    {
        try
        {
            var bytes = Utf8NoBom.GetBytes(LogLineFormat.Format(entry));
            var day = DateOnly.FromDateTime(entry.Timestamp.DateTime);
            if (_stream is null || day != _day)
            {
                OpenForDay(day, bytes.Length);
            }
            else if (_size > 0 && _size + bytes.Length > _options.MaxFileBytes)
            {
                CloseStream();
                OpenFile(day, _index + 1);
            }
            _stream!.Write(bytes);
            _size += bytes.Length;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _writeFailures);
            CloseStream(); // Reopen on the next entry.
        }
    }

    private void OpenForDay(DateOnly day, int nextEntryBytes)
    {
        CloseStream();
        if (day != _day)
        {
            SweepRetention(day);
        }
        Directory.CreateDirectory(_options.Folder);

        // Continue the newest file for this day if it has room, otherwise start the next index.
        var index = 0;
        foreach (var (fileDay, fileIndex, _) in EnumerateLogFiles())
        {
            if (fileDay == day && fileIndex > index)
            {
                index = fileIndex;
            }
        }
        var existing = new FileInfo(PathFor(day, index));
        if (existing.Exists && existing.Length > 0 && existing.Length + nextEntryBytes > _options.MaxFileBytes)
        {
            index++;
        }
        OpenFile(day, index);
    }

    private void OpenFile(DateOnly day, int index)
    {
        var path = PathFor(day, index);
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096);
        _size = _stream.Length;
        _day = day;
        _index = index;
        _currentPath = path;
    }

    private void FlushStream(bool toDisk)
    {
        try
        {
            _stream?.Flush(toDisk);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _writeFailures);
            CloseStream();
        }
    }

    private void CloseStream()
    {
        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
            // Ignore: the handle is gone either way.
        }
        _stream = null;
    }

    private void SweepRetention(DateOnly today)
    {
        try
        {
            if (!Directory.Exists(_options.Folder))
            {
                return;
            }
            var oldestKept = today.AddDays(-(_options.RetainDays - 1));
            foreach (var (fileDay, _, path) in EnumerateLogFiles())
            {
                if (fileDay < oldestKept)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception)
                    {
                        // In use or read-only: try again next sweep.
                    }
                }
            }
        }
        catch (Exception)
        {
            // Retention is best effort.
        }
    }

    private IEnumerable<(DateOnly Day, int Index, string Path)> EnumerateLogFiles()
    {
        if (!Directory.Exists(_options.Folder))
        {
            yield break;
        }
        foreach (var path in Directory.EnumerateFiles(_options.Folder, _options.FilePrefix + "-*.log"))
        {
            if (TryParseFileName(Path.GetFileName(path), _options.FilePrefix, out var day, out var index))
            {
                yield return (day, index, path);
            }
        }
    }

    private string PathFor(DateOnly day, int index) =>
        Path.Combine(_options.Folder, FileNameFor(_options.FilePrefix, day, index));

    internal static string FileNameFor(string prefix, DateOnly day, int index) =>
        index == 0
            ? $"{prefix}-{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log"
            : $"{prefix}-{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.{index.ToString(CultureInfo.InvariantCulture)}.log";

    internal static bool TryParseFileName(string fileName, string prefix, out DateOnly day, out int index)
    {
        day = default;
        index = 0;
        if (!fileName.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var match = FileSuffixPattern().Match(fileName, prefix.Length + 1);
        if (!match.Success
            || !DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
        {
            return false;
        }
        return !match.Groups[2].Success || int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    [GeneratedRegex(@"\G(\d{4}-\d{2}-\d{2})(?:\.(\d{1,6}))?\.log$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FileSuffixPattern();
}
