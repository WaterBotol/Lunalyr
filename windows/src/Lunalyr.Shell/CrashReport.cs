using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Lunalyr.Shell;

/// <summary>
/// Writes <c>crash-{local time}.txt</c> into the logs folder for an unhandled exception. Synchronous and direct (not
/// through the log queue, which may never get to run), and it never throws.
/// </summary>
public static class CrashReport
{
    public const string FilePrefix = "crash-";

    /// <summary>Writes the report and returns its path, or null if it couldn't be written.</summary>
    /// <param name="logsFolder">The logs folder (<c>{data}\Logs</c>).</param>
    /// <param name="exception">What went wrong; null when only a message is known.</param>
    /// <param name="context">Where it was caught, e.g. "AppDomain.UnhandledException".</param>
    /// <param name="version">The app's version.</param>
    /// <param name="now">The time to stamp; now when null.</param>
    public static string? Write(string logsFolder, Exception? exception, string context, string version, DateTimeOffset? now = null)
    {
        try
        {
            var time = now ?? DateTimeOffset.Now;
            Directory.CreateDirectory(logsFolder);
            var stem = FilePrefix + time.ToString("yyyy-MM-dd'T'HH-mm-ss-fff", CultureInfo.InvariantCulture);
            var path = Path.Combine(logsFolder, stem + ".txt");
            for (var n = 1; File.Exists(path); n++) path = Path.Combine(logsFolder, $"{stem}.{n}.txt");

            var text = new StringBuilder()
                .AppendLine("Lunalyr crashed.")
                .AppendLine(CultureInfo.InvariantCulture, $"Time: {time:O}")
                .AppendLine(CultureInfo.InvariantCulture, $"Version: {version}")
                .AppendLine(CultureInfo.InvariantCulture, $"Caught by: {context}")
                .AppendLine(CultureInfo.InvariantCulture, $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})")
                .AppendLine(CultureInfo.InvariantCulture, $"Runtime: {RuntimeInformation.FrameworkDescription}, process {Environment.ProcessId}")
                .AppendLine()
                .AppendLine(exception?.ToString() ?? "(no exception details)")
                .ToString();
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
