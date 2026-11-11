using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Logging;

/// <summary>One log event. <paramref name="Exception"/> is the exception's text (type, message, stack), if any.</summary>
public readonly record struct LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    LogCategory Category,
    string Message,
    string? Exception = null);

/// <summary>
/// The on-disk line format, shared by the file sink and <c>tools/lunalyr-log.ps1</c>:
/// <code>2026-11-12T10:15:30.123+01:00&lt;TAB&gt;INFO&lt;TAB&gt;sync&lt;TAB&gt;message</code>
/// Extra message lines and exception text follow as continuation lines that start with a TAB, so every entry
/// starts with a digit and filters never split a stack trace from its entry.
/// </summary>
public static class LogLineFormat
{
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    public static string Format(in LogEntry entry)
    {
        var sb = new StringBuilder(64 + entry.Message.Length);
        sb.Append(entry.Timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture))
          .Append('\t').Append(LogNames.Of(entry.Level))
          .Append('\t').Append(LogNames.Of(entry.Category))
          .Append('\t');
        AppendLines(sb, entry.Message.TrimEnd('\r', '\n'), first: true);
        if (!string.IsNullOrEmpty(entry.Exception))
        {
            AppendLines(sb, entry.Exception.TrimEnd('\r', '\n'), first: false);
        }
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Parses the first line of an entry (continuation lines are not included).</summary>
    public static bool TryParseHeader(string line, out LogEntry entry)
    {
        entry = default;
        var parts = line.Split('\t', 4);
        if (parts.Length < 4
            || !DateTimeOffset.TryParseExact(parts[0], TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)
            || !LogNames.TryParseLevel(parts[1], out var level)
            || !LogNames.TryParseCategory(parts[2], out var category))
        {
            return false;
        }
        entry = new LogEntry(ts, level, category, parts[3]);
        return true;
    }

    private static void AppendLines(StringBuilder sb, string text, bool first)
    {
        var lineStart = 0;
        var needSeparator = !first;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n' && text[i] != '\r')
            {
                continue;
            }
            if (needSeparator)
            {
                sb.Append("\n\t");
            }
            sb.Append(text, lineStart, i - lineStart);
            needSeparator = true;
            if (i < text.Length - 1 && text[i] == '\r' && text[i + 1] == '\n')
            {
                i++;
            }
            lineStart = i + 1;
        }
    }
}
