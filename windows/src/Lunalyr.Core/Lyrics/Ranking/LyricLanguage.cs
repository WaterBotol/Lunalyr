using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>The script sung lines are written in, as far as <see cref="LyricLanguage"/> tells them apart.</summary>
public enum SungScript
{
    /// <summary>Latin script only (English, or a romanisation).</summary>
    Latin,

    /// <summary>Mostly Chinese, Japanese or Korean script.</summary>
    Native,

    /// <summary>Both, or too little to tell.</summary>
    Mixed,
}

/// <summary>
/// Which language a song is sung in, as far as the lyrics found for it say, so lyrics in another language rank as
/// another version. Ported from the Mac's <c>LyricLanguage</c> (Lunalyr's own code).
/// </summary>
/// <remarks>
/// Apple Music lists a Japanese song that also has an English recording under its English title in many storefronts
/// ("Racing into the Night" for 夜に駆ける), while the English recording carries "English Version" or similar
/// (<see cref="VersionTags.Languages"/>). A title without such a tag is taken to be the original, so lyrics in English
/// only, for a song whose own lyrics are Japanese, are the English version's. Only Chinese, Japanese and Korean script
/// is told apart from Latin script: romaji is Latin, and lyrics mixing both (as K-pop often does) are never held
/// against a song.
/// </remarks>
public static class LyricLanguage
{
    /// <summary>Kana, Han or Hangul.</summary>
    public static bool IsNative(Rune rune) => rune.Value is
        (>= 0x3040 and <= 0x30FF) or (>= 0x31F0 and <= 0x31FF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF)
        or (>= 0xF900 and <= 0xFAFF) or (>= 0xAC00 and <= 0xD7AF) or (>= 0x1100 and <= 0x11FF) or (>= 0x3130 and <= 0x318F)
        or (>= 0x20000 and <= 0x2FA1F);

    /// <summary>Any kana, Han or Hangul in <paramref name="text"/>.</summary>
    public static bool HasNative(string? text)
    {
        foreach (var rune in (text ?? "").EnumerateRunes())
        {
            if (IsNative(rune))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The script of sung lines: native when at least four lines and 30% of them carry native script, Latin when at
    /// least four lines have letters and at most one in twenty carries native script, else mixed. Null when there are
    /// fewer than four lines to judge.
    /// </summary>
    public static SungScript? ScriptOf(IEnumerable<string> lines)
    {
        var sung = lines.Where(HasLetters).ToList();
        if (sung.Count < 4)
        {
            return null;
        }
        var native = sung.Count(HasNative);
        if (native >= 4 && native * 10 >= sung.Count * 3)
        {
            return SungScript.Native;
        }
        return native * 20 <= sung.Count ? SungScript.Latin : SungScript.Mixed;
    }

    /// <summary>The script of a match's sung lines (instrumental breaks and credits left out).</summary>
    public static SungScript? ScriptOf(LyricCandidate candidate) => candidate.Result switch
    {
        LyricResult.Synced synced => ScriptOf(synced.Lines.Where(line => !line.IsInstrumental && line.Credit != true).Select(line => line.Text)),
        LyricResult.Plain plain => ScriptOf(plain.Lines),
        _ => null,
    };

    private static readonly string[] EastAsianGenreWords =
    [
        "j-pop", "jpop", "j-rock", "jrock", "anime", "japan", "kayokyoku", "enka", "vocaloid",
        "k-pop", "kpop", "korea", "c-pop", "cpop", "mandopop", "cantopop", "chinese", "visual kei",
    ];

    /// <summary>
    /// Genres whose songs are mostly sung in Chinese, Japanese or Korean. Only a hint: it decides a tie between the
    /// lyrics found, and whether the original title is worth looking up, never on its own.
    /// </summary>
    public static bool IsEastAsianGenre(string? genre)
    {
        if (string.IsNullOrEmpty(genre))
        {
            return false;
        }
        var lowered = genre.ToLowerInvariant();
        return EastAsianGenreWords.Any(word => lowered.Contains(word, StringComparison.Ordinal)) || HasNative(lowered);
    }

    /// <summary>
    /// The script the playing song's lyrics are written in, when the song or the lyrics found say so; null when
    /// nothing says. In order: a language tag on the title, or the album ("English Version"); Apple Music's own
    /// lyrics, which belong to the playing track; a title or album in native script; lyrics found under the song's
    /// original title (<see cref="OriginalTitles"/>); otherwise the other lyrics found, more native than Latin, or as
    /// many with an East Asian genre (<see cref="IsEastAsianGenre"/>). Lyrics whose title names a language version do
    /// not count: their tag explains them.
    /// </summary>
    public static SungScript? Expected(LyricQuery query, IReadOnlyList<LyricCandidate> candidates)
    {
        var declared = Languages(query.Title);
        var tags = declared.Count == 0 ? Languages(query.Album ?? "") : declared;
        if (tags.Count > 0)
        {
            return tags.SetEquals(["english"]) ? SungScript.Latin : tags.Contains("english") ? null : SungScript.Native;
        }
        if (candidates.FirstOrDefault(c => c.Source == LyricSourceId.AppleMusicCache) is { } music && ScriptOf(music) is { } own)
        {
            return own == SungScript.Mixed ? null : own;
        }
        if (HasNative(query.Title) || HasNative(query.Album))
        {
            return SungScript.Native;
        }
        int native = 0, latin = 0;
        foreach (var candidate in candidates)
        {
            if (Languages(candidate.Title).Count > 0)
            {
                continue;
            }
            switch (ScriptOf(candidate))
            {
                case SungScript.Native:
                    if (candidate.ViaOriginalTitle)
                    {
                        return SungScript.Native;
                    }
                    native++;
                    break;
                case SungScript.Latin:
                    latin++;
                    break;
            }
        }
        if (native > latin || (native > 0 && native == latin && IsEastAsianGenre(query.Genre)))
        {
            return SungScript.Native;
        }
        return null;
    }

    /// <summary>
    /// Each candidate with <see cref="LyricCandidate.LanguageMismatch"/> set afresh: its lyrics are Latin only where
    /// the song's are native, or native where they are Latin. Apple Music's own lyrics and local files (chosen for the
    /// track) are never marked.
    /// </summary>
    public static List<LyricCandidate> Weigh(IReadOnlyList<LyricCandidate> candidates, LyricQuery query)
    {
        var expected = Expected(query, candidates);
        var weighed = new List<LyricCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var mismatch = expected is { } wanted && !candidate.Source.IsLocal() && ScriptOf(candidate) is { } script
                && (wanted == SungScript.Native ? script == SungScript.Latin : script == SungScript.Native);
            weighed.Add(candidate.LanguageMismatch == mismatch ? candidate : candidate with { LanguageMismatch = mismatch });
        }
        return weighed;
    }

    private static HashSet<string> Languages(string name)
    {
        var tags = new HashSet<string>(VersionTags.Tags(name), StringComparer.Ordinal);
        tags.IntersectWith(VersionTags.Languages);
        return tags;
    }

    private static bool HasLetters(string line)
    {
        foreach (var rune in line.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                return true;
            }
        }
        return false;
    }
}
