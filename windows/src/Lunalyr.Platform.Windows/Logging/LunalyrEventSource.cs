using System.Diagnostics.Tracing;
using Lunalyr.Core.Logging;

namespace Lunalyr.Platform.Logging;

/// <summary>
/// The <c>Lunalyr</c> ETW / EventPipe provider. One event per level; the payload is (category, message, exception).
/// Enable it with <c>dotnet-trace collect --providers Lunalyr</c> or PerfView <c>/Providers=*Lunalyr</c>.
/// Native AOT apps must set <c>&lt;EventSourceSupport&gt;true&lt;/EventSourceSupport&gt;</c> or the provider is a no-op.
/// </summary>
[EventSource(Name = ProviderName)]
public sealed class LunalyrEventSource : EventSource
{
    public const string ProviderName = "Lunalyr";

    public static readonly LunalyrEventSource Log = new();

    private LunalyrEventSource()
    {
    }

    [Event(1, Level = EventLevel.Verbose, Message = "[{0}] {1}")]
    public void Debug(string category, string message, string exception) => WriteEvent(1, category, message, exception);

    [Event(2, Level = EventLevel.Informational, Message = "[{0}] {1}")]
    public void Info(string category, string message, string exception) => WriteEvent(2, category, message, exception);

    [Event(3, Level = EventLevel.Warning, Message = "[{0}] {1}")]
    public void Warn(string category, string message, string exception) => WriteEvent(3, category, message, exception);

    [Event(4, Level = EventLevel.Error, Message = "[{0}] {1}")]
    public void Error(string category, string message, string exception) => WriteEvent(4, category, message, exception);
}

/// <summary>Forwards log entries to <see cref="LunalyrEventSource"/>. Costs one check when no ETW session listens.</summary>
public sealed class EventSourceLogSink : ILogSink
{
    public void Write(in LogEntry entry)
    {
        var source = LunalyrEventSource.Log;
        var level = entry.Level switch
        {
            LogLevel.Debug => EventLevel.Verbose,
            LogLevel.Info => EventLevel.Informational,
            LogLevel.Warn => EventLevel.Warning,
            _ => EventLevel.Error,
        };
        if (!source.IsEnabled(level, EventKeywords.None))
        {
            return;
        }
        var category = LogNames.Of(entry.Category);
        var exception = entry.Exception ?? string.Empty;
        switch (entry.Level)
        {
            case LogLevel.Debug:
                source.Debug(category, entry.Message, exception);
                break;
            case LogLevel.Info:
                source.Info(category, entry.Message, exception);
                break;
            case LogLevel.Warn:
                source.Warn(category, entry.Message, exception);
                break;
            default:
                source.Error(category, entry.Message, exception);
                break;
        }
    }
}
