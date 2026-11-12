// The line being sung, for the tray flyout: ported from the Mac's LyricNow (Sources/UI/Design/LunalyrModels.swift)
// and MenuBarLyricQuote.content (Sources/UI/MenuBar/MenuBarNowPlaying.swift), Lunalyr's own code.

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Shell.Flyout;

/// <summary>The lyric line being sung, as a value, so the flyout can be drawn from a fixed one.</summary>
public sealed record LyricNow
{
    /// <summary>The line being sung; null between lines, in instrumental breaks, and without timed lyrics.</summary>
    public string? Line { get; init; }

    /// <summary>The index of the line being sung, so a line repeated word for word still reads as a new line.</summary>
    public int? Index { get; init; }

    /// <summary>The next line, when there is one.</summary>
    public string? Next { get; init; }

    /// <summary>"LRCLIB", "Apple Music"…, when lyrics are showing.</summary>
    public string? Source { get; init; }

    /// <summary>"Word-by-word", "Line-synced"…, when lyrics are showing.</summary>
    public string? Precision { get; init; }

    /// <summary>Lyrics are being looked up.</summary>
    public bool IsLoading { get; init; }

    /// <summary>The lookup finished without lyrics.</summary>
    public bool NotFound { get; init; }

    public static LyricNow None { get; } = new();

    /// <summary>After the voice stops, the next line reads better than a stale one once this long has passed.</summary>
    public const double StaleAfter = 1.5;

    /// <summary>
    /// The answer for <paramref name="lyrics"/> at <paramref name="time"/> (the lyric clock: position plus the global and
    /// song offsets). <paramref name="replacements"/> are readings shown in place of the lines ("Replace lyrics"), by
    /// line index. Cheap enough for a few times a second.
    /// </summary>
    public static LyricNow At(LyricResult? lyrics, double time, bool isLoading, bool notFound,
        IReadOnlyDictionary<int, string>? replacements = null)
    {
        var now = new LyricNow
        {
            Source = lyrics?.Provenance.Source,
            Precision = lyrics?.Provenance.Precision,
            IsLoading = isLoading,
            NotFound = notFound,
        };
        if (lyrics is not LyricResult.Synced { Lines: { Count: > 0 } lines } || !double.IsFinite(time)) return now;
        if (new LyricLineIndex(lines).LineAt(time) is not { } index || index < 0 || index >= lines.Count) return now;

        string? Text(int i)
        {
            if (i < 0 || i >= lines.Count || lines[i].IsInstrumental) return null;
            var text = replacements is not null && replacements.TryGetValue(i, out var reading) && reading.Length > 0 ? reading : lines[i].Text;
            var trimmed = text.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        var finished = time > lines[index].End + StaleAfter && index + 1 < lines.Count;
        var line = finished ? null : Text(index);
        return now with { Line = line, Index = line is null ? null : index, Next = Text(index + 1) };
    }
}

/// <summary>What the flyout's lyric block shows (MenuBarLyricQuote.Content).</summary>
public abstract record LyricContent
{
    /// <summary>A line, with the next one beneath it when there is one.</summary>
    public sealed record Line(string Text, string? Next) : LyricContent;

    /// <summary>An instrumental break or a pause in the voice, with the line to come.</summary>
    public sealed record Between(string Next) : LyricContent;

    /// <summary>"Looking for lyrics…", "No synced lyrics for this song.", "♪".</summary>
    public sealed record Placeholder : LyricContent;

    public static LyricContent Of(LyricNow now)
    {
        ArgumentNullException.ThrowIfNull(now);
        if (now.Line is { } line) return new Line(line, now.Next);
        if (now.Source is not null && now.Next is { } next) return new Between(next);
        return new Placeholder();
    }
}
