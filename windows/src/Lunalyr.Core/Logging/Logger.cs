namespace Lunalyr.Core.Logging;

/// <summary>Receives log entries. Implementations should be fast and non-blocking; the logger swallows their exceptions.</summary>
public interface ILogSink
{
    void Write(in LogEntry entry);
}

/// <summary>
/// Category-aware logger that fans entries out to sinks (rolling file, ETW, test sinks). It never throws into
/// callers: a failing sink is skipped. Thread-safe.
/// </summary>
/// <example><code>log.Sync.Info("Matched lyrics at offset 120 ms");</code></example>
public sealed class Logger
{
    private readonly ILogSink[] _sinks;
    private readonly TimeProvider _time;
    private volatile LogLevel _minimumLevel;

    public Logger(LogLevel minimumLevel, TimeProvider? timeProvider, params ILogSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _minimumLevel = minimumLevel;
        _time = timeProvider ?? TimeProvider.System;
        _sinks = [.. sinks];
    }

    public Logger(LogLevel minimumLevel, params ILogSink[] sinks)
        : this(minimumLevel, null, sinks)
    {
    }

    /// <summary>A logger that drops everything.</summary>
    public static Logger Null { get; } = new(LogLevel.Error);

    public LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set => _minimumLevel = value;
    }

    public CategoryLogger App => new(this, LogCategory.App);
    public CategoryLogger Queue => new(this, LogCategory.Queue);
    public CategoryLogger Sync => new(this, LogCategory.Sync);
    public CategoryLogger Lyrics => new(this, LogCategory.Lyrics);
    public CategoryLogger Player => new(this, LogCategory.Player);
    public CategoryLogger Render => new(this, LogCategory.Render);
    public CategoryLogger Settings => new(this, LogCategory.Settings);

    public CategoryLogger For(LogCategory category) => new(this, category);

    public bool IsEnabled(LogLevel level) => _sinks.Length > 0 && level >= _minimumLevel;

    public void Write(LogCategory category, LogLevel level, string? message, Exception? exception = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }
        LogEntry entry;
        try
        {
            entry = new LogEntry(_time.GetLocalNow(), level, category, message ?? string.Empty, exception?.ToString());
        }
        catch
        {
            return;
        }
        foreach (var sink in _sinks)
        {
            try
            {
                sink.Write(entry);
            }
            catch
            {
                // Logging must never break the caller.
            }
        }
    }
}

/// <summary>A logger bound to one category. Cheap to create; pass it around or cache it.</summary>
public readonly struct CategoryLogger(Logger logger, LogCategory category)
{
    public LogCategory Category => category;

    public bool IsEnabled(LogLevel level) => logger?.IsEnabled(level) == true;

    public void Debug(string message) => logger?.Write(category, LogLevel.Debug, message);
    public void Info(string message) => logger?.Write(category, LogLevel.Info, message);
    public void Warn(string message, Exception? exception = null) => logger?.Write(category, LogLevel.Warn, message, exception);
    public void Error(string message, Exception? exception = null) => logger?.Write(category, LogLevel.Error, message, exception);
}
