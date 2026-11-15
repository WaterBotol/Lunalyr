namespace Lunalyr.Core.Logging;

/// <summary>Severity of a log entry, lowest first.</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// Log categories. <see cref="Queue"/> and <see cref="Sync"/> match the Mac app's categories so logs from both
/// apps read the same way.
/// </summary>
public enum LogCategory
{
    App = 0,
    Queue = 1,
    Sync = 2,
    Lyrics = 3,
    Player = 4,
    Render = 5,
    Settings = 6,
}

/// <summary>Stable lowercase names used in log files, ETW payloads and <c>tools/lunalyr-log.ps1</c>.</summary>
public static class LogNames
{
    public static string Of(LogCategory category) => category switch
    {
        LogCategory.App => "app",
        LogCategory.Queue => "queue",
        LogCategory.Sync => "sync",
        LogCategory.Lyrics => "lyrics",
        LogCategory.Player => "player",
        LogCategory.Render => "render",
        LogCategory.Settings => "settings",
        _ => "app",
    };

    public static string Of(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO",
    };

    public static bool TryParseCategory(string? text, out LogCategory category)
    {
        foreach (var candidate in Enum.GetValues<LogCategory>())
        {
            if (string.Equals(Of(candidate), text, StringComparison.OrdinalIgnoreCase))
            {
                category = candidate;
                return true;
            }
        }
        category = default;
        return false;
    }

    public static bool TryParseLevel(string? text, out LogLevel level)
    {
        foreach (var candidate in Enum.GetValues<LogLevel>())
        {
            if (string.Equals(Of(candidate), text, StringComparison.OrdinalIgnoreCase))
            {
                level = candidate;
                return true;
            }
        }
        level = default;
        return false;
    }
}
