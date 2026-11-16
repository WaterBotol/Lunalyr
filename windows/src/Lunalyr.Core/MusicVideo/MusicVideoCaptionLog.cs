// Ported from the Mac's CaptionSyncLog (Support/SyncLog.swift, 3c6c931 and 1384242; Lunalyr's own code).

using System.Globalization;

namespace Lunalyr.Core.MusicVideo;

/// <summary>
/// One log line per caption-sync decision: asked for or not, a reply ignored, empty or blocked (HTTP status and size),
/// fetched (the uploader's or automatic, cues, word timing), none within the wait, waiting for lyrics, lines matched and
/// agreeing, the offset, and applied, refused or skipped for the user's timing. Counts and offsets only: never lyric text
/// or the video's id.
/// </summary>
public static class MusicVideoCaptionLog
{
    public static string Seconds(double value) => value.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture) + " s";

    public static string AskedLine(string? language) =>
        language is not null ? $"captions: asked the player for them (language {language})" : "captions: not asked for (not the video showing)";

    public static string IgnoredLine(string why) => $"captions: reply ignored ({why})";

    /// <summary><paramref name="type"/>: how the player read the reply; one that can't be read arrives empty.</summary>
    public static string EmptyLine(int status, int bytes, string type = "text")
    {
        var readable = type is "fetch" or "text" or "json" or "";
        var why = !readable ? $"unreadable response type {type}"
            : status >= 400 ? "blocked or refused"
            : bytes == 0 ? "empty (no track, or refused)" : "not timed text";
        return $"captions: reply with no captions (HTTP {status}, {bytes} bytes): {why}";
    }

    public static string FetchedLine(bool automatic, string? language, int cues, bool wordTimed) =>
        $"captions: fetched {(automatic ? "YouTube's automatic (auto-only)" : "the uploader's")} track"
        + $", language {language ?? "?"}, {cues} cues, word timing {(wordTimed ? "yes" : "no")}";

    public static string NoneLine(double seconds) =>
        $"captions: none arrived within {(int)seconds} s (the video has none, or the player never loaded them)";

    public const string WaitingLine = "captions: waiting for the song's timed lyrics to measure";

    /// <summary>The measurement and what became of it. <paramref name="userOffset"/>: the user's own timing for the video, which won over a trusted offset.</summary>
    public static string MeasuredLine(MusicVideoCaptionSync.Result result, double? userOffset)
    {
        ArgumentNullException.ThrowIfNull(result);
        var line = $"captions: {result.Matches.Count} lines matched, {result.Agreeing} agree";
        if (result.Agreeing > 0) line += string.Create(CultureInfo.InvariantCulture, $" (spread {result.Spread:0.00} s)");
        if (result.Segments.Count > 0) line += $", {result.Segments.Count} segments";
        if (result.Offset is not { } offset)
        {
            if (result.Matches.Count > 0) line += $", best {Seconds(MusicVideoCaptionSync.Median(result.Matches.Select(m => m.Offset)))}";
            return line + ", " + result.Verdict.Summary;
        }
        line += $", offset {Seconds(offset)}";
        if (userOffset is { } user) return line + $", skipped: the user's timing {Seconds(user)} wins for this video";
        return line + ", applied";
    }

    public static string UserLine(double offset, double captions) =>
        $"captions: the user set {Seconds(offset)} over the captions' {Seconds(captions)}, kept for this video";
}
