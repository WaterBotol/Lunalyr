// Shared lyric data types for the whole lyric core. Every milestone 2 workstream builds on these.
// Times are seconds from the start of the song, as doubles (the Mac app's TimeInterval).
// Types are partial where a workstream is expected to add behaviour (ranking, parsing) in its own file.

namespace Lunalyr.Core.Lyrics;

/// <summary>One timed word (or syllable group) of a sung line.</summary>
public sealed record LyricWord(string Text, double Start, double End);

/// <summary>
/// One line of synced lyrics. Equality covers every field, so "is this the same result" checks see each one,
/// including fields added later.
/// </summary>
public sealed partial record LyricLine
{
    public required double Start { get; init; }
    public required double End { get; init; }

    /// <summary>
    /// True when the source timed only the line's start (LRC), so <see cref="End"/> is just where the next line or
    /// the track begins, not where singing stops. Null when the source supplied the end.
    /// </summary>
    public bool? EndFromNextLine { get; init; }

    public required string Text { get; init; }

    /// <summary>Word timing, when the source has it. Empty for line-timed lines.</summary>
    public IReadOnlyList<LyricWord> Words { get; init; } = [];

    public bool IsInstrumental { get; init; }

    /// <summary>True when <see cref="Words"/> came from the source's own word or syllable timing.</summary>
    public bool WordTimed { get; init; }

    /// <summary>A reading supplied with the lyrics (or generated later), shown below or instead of the line.</summary>
    public string? Romanization { get; init; }

    public string? Translation { get; init; }

    /// <summary>
    /// True for a production credit ("Produced by: …"). Such a line is also <see cref="IsInstrumental"/> with no
    /// words, so the lead layer skips it; the credits layer draws its text instead.
    /// </summary>
    public bool? Credit { get; init; }

    public bool Equals(LyricLine? other) =>
        other is not null && Start == other.Start && End == other.End && EndFromNextLine == other.EndFromNextLine
        && Text == other.Text && Words.SequenceEqual(other.Words) && IsInstrumental == other.IsInstrumental
        && WordTimed == other.WordTimed && Romanization == other.Romanization && Translation == other.Translation
        && Credit == other.Credit;

    public override int GetHashCode() => HashCode.Combine(Start, End, Text, Words.Count, IsInstrumental, WordTimed);
}

/// <summary>Where a result came from and how precisely it is timed, e.g. "Word-by-word · LRCLIB".</summary>
public sealed record LyricProvenance(string Precision, string Source)
{
    public string Label => $"{Precision} · {Source}";
}

/// <summary>The lyrics for a recording: synced lines, or plain text lines when nothing is timed.</summary>
public abstract record LyricResult(LyricProvenance Provenance)
{
    public sealed record Synced(IReadOnlyList<LyricLine> Lines, LyricProvenance Provenance) : LyricResult(Provenance)
    {
        public bool Equals(Synced? other) => other is not null && Provenance == other.Provenance && Lines.SequenceEqual(other.Lines);
        public override int GetHashCode() => HashCode.Combine(Provenance, Lines.Count);
    }

    public sealed record Plain(IReadOnlyList<string> Lines, LyricProvenance Provenance) : LyricResult(Provenance)
    {
        public bool Equals(Plain? other) => other is not null && Provenance == other.Provenance && Lines.SequenceEqual(other.Lines);
        public override int GetHashCode() => HashCode.Combine(Provenance, Lines.Count);
    }
}

/// <summary>
/// The lyric sources, in the Mac app's identifiers (kept so saved choices and settings can be compared across
/// apps). <see cref="AppleMusicCache"/> is Music's own lyrics on the Mac; on Windows it stands for Apple Music lyrics
/// wherever they turn out to be reachable.
/// </summary>
public enum LyricSourceId
{
    AppleMusicCache,
    LyricsPlus,
    NetEase,
    QQMusic,
    Kugou,
    Lrclib,
    Lyrimuse,
    Kuwo,
    LyricsOvh,
    Amll,
}

/// <summary>What a lyric lookup is for. Helpers (normalising, similarity, recording key) live in the ranking code.</summary>
public sealed partial record LyricQuery
{
    public LyricQuery(string title, string artist, string? album, double duration)
    {
        Title = title;
        Artist = artist;
        Album = album;
        // Lengths beyond ten days come from broken replies; non-finite ones mean "unknown".
        Duration = double.IsFinite(duration) ? Math.Clamp(duration, 0, 864000) : 0;
    }

    public string Title { get; }
    public string Artist { get; }
    public string? Album { get; }

    /// <summary>Seconds; 0 when unknown (streams, radio).</summary>
    public double Duration { get; }
}

/// <summary>The wire formats lyric sources deliver, before parsing.</summary>
public enum LyricFormat
{
    /// <summary>LRC, including enhanced LRC with inline word times.</summary>
    Lrc,
    /// <summary>LRCLIB's Lyricsfile (YAML with word tokens).</summary>
    Lyricsfile,
    /// <summary>TTML (Apple Music, AMLL, local exports).</summary>
    Ttml,
    /// <summary>NetEase's word-timed YRC.</summary>
    Yrc,
    /// <summary>Kugou's word-timed KRC (already decrypted to text).</summary>
    Krc,
    /// <summary>Lyrics+ / KPoe JSON with syllables.</summary>
    KPoeJson,
    /// <summary>Untimed text, one line per line.</summary>
    Plain,
}

/// <summary>
/// What a source returned, before parsing: the payload in its format plus what the service said it matched.
/// Providers produce this; the parsers turn it into a <see cref="LyricResult"/>.
/// </summary>
public sealed record RawLyrics(
    LyricSourceId Source,
    LyricFormat Format,
    string Payload,
    string? ReportedTitle = null,
    string? ReportedArtist = null,
    double? ReportedDuration = null,
    // Extra payloads some sources send alongside (e.g. a separate translation or reading LRC), keyed by kind.
    IReadOnlyDictionary<string, string>? Extras = null);
