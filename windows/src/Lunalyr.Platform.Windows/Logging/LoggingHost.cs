using Lunalyr.Core.Logging;

namespace Lunalyr.Platform.Logging;

/// <summary>
/// The app's logging wiring: a <see cref="Logger"/> writing to a rolling file and the <c>Lunalyr</c> EventSource.
/// Dispose it at shutdown to flush the file.
/// </summary>
/// <example><code>
/// await using var logging = LoggingHost.Create(AppPaths.LogsFolder(AppPaths.DefaultDataRoot()));
/// logging.Logger.App.Info("Started");
/// </code></example>
public sealed class LoggingHost : IAsyncDisposable
{
    private LoggingHost(Logger logger, RollingFileLogSink file)
    {
        Logger = logger;
        File = file;
    }

    public Logger Logger { get; }

    public RollingFileLogSink File { get; }

    public static LoggingHost Create(string logsFolder, LogLevel minimumLevel = LogLevel.Info, TimeProvider? timeProvider = null) =>
        Create(new RollingFileLogOptions(logsFolder) { TimeProvider = timeProvider ?? TimeProvider.System }, minimumLevel);

    public static LoggingHost Create(RollingFileLogOptions fileOptions, LogLevel minimumLevel = LogLevel.Info)
    {
        var file = new RollingFileLogSink(fileOptions);
        var logger = new Logger(minimumLevel, fileOptions.TimeProvider, file, new EventSourceLogSink());
        return new LoggingHost(logger, file);
    }

    public Task FlushAsync() => File.FlushAsync();

    public ValueTask DisposeAsync() => File.DisposeAsync();
}
