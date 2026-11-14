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
    private LoggingHost(Logger logger, RollingFileLogSink file, RecentProblemsSink problems)
    {
        Logger = logger;
        File = file;
        Problems = problems;
    }

    public Logger Logger { get; }

    public RollingFileLogSink File { get; }

    /// <summary>The last warnings and errors, for Copy diagnostics.</summary>
    public RecentProblemsSink Problems { get; }

    public static LoggingHost Create(string logsFolder, LogLevel minimumLevel = LogLevel.Info, TimeProvider? timeProvider = null) =>
        Create(new RollingFileLogOptions(logsFolder) { TimeProvider = timeProvider ?? TimeProvider.System }, minimumLevel);

    public static LoggingHost Create(RollingFileLogOptions fileOptions, LogLevel minimumLevel = LogLevel.Info)
    {
        var file = new RollingFileLogSink(fileOptions);
        var problems = new RecentProblemsSink();
        var logger = new Logger(minimumLevel, fileOptions.TimeProvider, file, new EventSourceLogSink(), problems);
        return new LoggingHost(logger, file, problems);
    }

    public Task FlushAsync() => File.FlushAsync();

    public ValueTask DisposeAsync() => File.DisposeAsync();
}
