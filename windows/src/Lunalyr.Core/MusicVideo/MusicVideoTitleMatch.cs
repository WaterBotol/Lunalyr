// Ported from the Mac's MusicVideoTitleMatch.swift (Lunalyr's own code; no line of the Verso import).

using System.Text;
using System.Text.RegularExpressions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.MusicVideo;

/// <summary>
/// Whether a video is <em>this song</em>, not just this artist. A video title is cut at its separators (- – — | : and
/// quotes); the artist's segments, bracketed and trailing qualifiers ("(Official Music Video)", "[4K]", "Remastered",
/// "ft. …") are dropped, and what is left must equal the song title under the same treatment (<see cref="Normal"/>).
/// "Love" is not "Cherry Flavored Love" or "Lovesick". Live, concert and fan uploads are refused outright: a date, a
/// "Venue, City" segment, or words such as live, concert, tour, 4K, fancam or pro-shot.
/// </summary>
public static partial class MusicVideoTitleMatch
{
    /// <summary>Words (whole, any case) that mark a live or fan upload. Allowed when the song's own title has them.</summary>
    public static readonly IReadOnlyList<string> LiveWords =
        ["live", "concert", "tour", "4k", "fancam", "fan cam", "pro shot", "proshot", "pro-shot"];

    /// <summary>Words a qualifier segment may be made of, alone or together.</summary>
    private static readonly HashSet<string> QualifierWords = new(StringComparer.Ordinal)
    {
        "official", "music", "video", "videos", "mv", "hd", "hq", "4k", "1080p", "720p", "uhd", "remastered", "remaster",
        "version", "explicit", "clean", "uncensored", "edit", "visual", "visualizer", "visualiser", "audio", "lyric",
        "lyrics", "new", "single", "premiere",
    };

    /// <summary>The song title as compared: brackets, "feat." credits and qualifier segments removed, then <see cref="Normal"/>.</summary>
    public static string SongKey(string title) => Core(title, []);

    /// <summary>The video title's song part, compared the same way, with the artist's own segments removed.</summary>
    public static string VideoKey(string title, string artist) => Core(title, ArtistParts(artist));

    /// <summary>
    /// The song and the video name the same song. Also tried with segments that are the artist beside qualifiers or the
    /// artist's name in its own script dropped ("米津玄師 MV「Lemon」", "IVE 아이브 'I AM' MV").
    /// </summary>
    public static bool Matches(string videoTitle, string songTitle, string artist)
    {
        var song = SongKey(songTitle);
        if (song.Length == 0) return false;
        var artists = ArtistParts(artist);
        return Core(videoTitle, artists) == song || Core(videoTitle, artists, loose: true) == song;
    }

    /// <summary>A live, concert or fan recording, unless the song's own title says so.</summary>
    public static bool IsLiveOrFanUpload(string title, string songTitle)
    {
        var name = " " + LyricQuery.Normalize(title) + " ";
        var song = " " + LyricQuery.Normalize(songTitle) + " ";
        foreach (var word in LiveWords)
        {
            var normal = " " + LyricQuery.Normalize(word) + " ";
            if (name.Contains(normal, StringComparison.Ordinal) && !song.Contains(normal, StringComparison.Ordinal)) return true;
        }
        if (DayMonthYear().IsMatch(title) || YearMonthDay().IsMatch(title)) return true;
        // " - Republik, Honolulu": a separate segment naming a place.
        var songCore = SongKey(songTitle);
        return Segments(StripBrackets(title)).Any(s => s.Contains(',') && SongKey(s) != songCore);
    }

    /// <summary>
    /// The artist credit split into its names: "A &amp; B", "A, B", "A x B", "A feat. B". Each is a channel or a title
    /// segment that may stand for the artist.
    /// </summary>
    public static IReadOnlyList<string> ArtistParts(string artist)
    {
        var parts = new List<string> { Normal(artist) };
        var split = ArtistSeparators().Replace(artist, "\u0001");
        parts.AddRange(split.Split('\u0001', StringSplitOptions.RemoveEmptyEntries).Select(Normal));
        return [.. parts.Where(p => p.Length >= 2).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Case, accents, spaces and punctuation ignored (the Mac's <c>CoverLookup.normal</c>): accents and case folded, then
    /// only letters, marks and numbers kept.
    /// </summary>
    public static string Normal(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var folded = LyricTextFolding.Fold(text);
        var builder = new StringBuilder(folded.Length);
        foreach (var rune in folded.EnumerateRunes())
        {
            if (LyricTextFolding.IsAlphanumeric(rune)) builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    // ---- Pieces ----

    private static string Core(string title, IReadOnlyList<string> artists, bool loose = false)
    {
        var kept = new StringBuilder();
        // "M/V", as K-pop titles write it, is the qualifier "MV".
        var text = SlashMv().Replace(title, "MV");
        foreach (var segment in Segments(StripBrackets(text)))
        {
            var cleaned = StripCredits(segment);
            var normal = Normal(cleaned);
            if (normal.Length == 0 || artists.Contains(normal) || IsQualifier(cleaned)) continue;
            if (loose && IsLooseArtistSegment(cleaned, artists)) continue;
            kept.Append(normal);
        }
        var key = kept.ToString();
        // "The Weeknd Blinding Lights": the artist written without a separator.
        foreach (var artist in artists.OrderByDescending(a => a.Length))
        {
            if (key.Length <= artist.Length) continue;
            if (key.StartsWith(artist, StringComparison.Ordinal)) key = key[artist.Length..];
            else if (key.EndsWith(artist, StringComparison.Ordinal)) key = key[..^artist.Length];
        }
        // A title that is only brackets ("(Interlude)") keeps them.
        return key.Length == 0 && artists.Count == 0 ? Normal(text) : key;
    }

    /// <summary>A segment that is the artist plus qualifier words ("米津玄師 MV"), or the artist plus their name in another script ("IVE 아이브").</summary>
    private static bool IsLooseArtistSegment(string segment, IReadOnlyList<string> artists)
    {
        var words = LyricQuery.Normalize(segment).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rest = string.Concat(words.Where(w => !QualifierWords.Contains(w)));
        if (rest.Length == 0) return false;
        foreach (var artist in artists)
        {
            if (rest == artist) return true;
            string other;
            if (rest.StartsWith(artist, StringComparison.Ordinal)) other = rest[artist.Length..];
            else if (rest.EndsWith(artist, StringComparison.Ordinal)) other = rest[..^artist.Length];
            else continue;
            if (other.Length > 0 && !other.Any(char.IsAscii)) return true;
        }
        return false;
    }

    /// <summary>
    /// Cut at separators: dashes, bars and colons with spaces, double quotes, Japanese corner brackets 「」『』, and single
    /// quotes that open or close a quotation ("BLACKPINK - 'Shut Down' MV"), not apostrophes.
    /// </summary>
    private static List<string> Segments(string text) =>
        [.. SegmentSeparators().Replace(text, "\u0001").Split('\u0001').Select(s => s.Trim()).Where(s => s.Length > 0)];

    private static string StripBrackets(string text)
    {
        var result = text;
        result = Parentheses().Replace(result, " ");
        result = SquareBrackets().Replace(result, " ");
        result = Braces().Replace(result, " ");
        result = LenticularBrackets().Replace(result, " ");
        return result;
    }

    /// <summary>"Song ft. Someone" → "Song".</summary>
    private static string StripCredits(string text) => Credits().Replace(text, "");

    private static bool IsQualifier(string segment)
    {
        var words = LyricQuery.Normalize(segment).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        return words.All(w => QualifierWords.Contains(w) || (w.Length == 4 && w.All(char.IsAsciiDigit)));
    }

    [GeneratedRegex(@"\b\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2,4}\b")]
    private static partial Regex DayMonthYear();

    [GeneratedRegex(@"\b\d{4}[/.\-]\d{1,2}[/.\-]\d{1,2}\b")]
    private static partial Regex YearMonthDay();

    [GeneratedRegex(@"\s*(?:,|&|\+|/|\bx\b|\band\b|\bfeat\.?|\bft\.?|\bfeaturing\b|\bwith\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparators();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[Mm]/[Vv](?![A-Za-z0-9])")]
    private static partial Regex SlashMv();

    [GeneratedRegex(@"\s+[-–—|:]\s+|\s*[|“”""«»「」『』]\s*|\s+[-–—]|[-–—]\s+|:\s+|(?:^|\s)['‘](?=\S)|(?<=\S)['’](?:\s|$)")]
    private static partial Regex SegmentSeparators();

    [GeneratedRegex(@"\([^()]*\)")]
    private static partial Regex Parentheses();

    [GeneratedRegex(@"\[[^\[\]]*\]")]
    private static partial Regex SquareBrackets();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Braces();

    [GeneratedRegex(@"【[^【】]*】")]
    private static partial Regex LenticularBrackets();

    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring|prod\.?(?:\s+by)?)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Credits();
}
