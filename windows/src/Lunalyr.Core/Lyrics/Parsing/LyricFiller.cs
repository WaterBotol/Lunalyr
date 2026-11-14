// Chinese catalogues' filler for songs without words (tester feedback on 0.9.0, 14 Nov 2026; the Mac's working set from
// the Mac session the same day): "纯音乐，请欣赏" ("instrumental, please enjoy") timed over an intro, before the real
// lyrics. Such a line is dropped; every other line stays. A whole line must match, with full- and half-width punctuation
// and spaces allowed, so a sung line that merely mentions 纯音乐 is kept. Lyrics that are only filler are no lyrics.

using System.Text;

namespace Lunalyr.Core.Lyrics.Parsing;

public static class LyricFiller
{
    /// <summary>The filler lines, without punctuation or spaces, in simplified and traditional script.</summary>
    private static readonly HashSet<string> Lines = new(StringComparer.Ordinal)
    {
        "纯音乐请欣赏", "纯音乐请您欣赏", "此歌曲为没有填词的纯音乐", "此歌曲为没有填词的纯音乐请您欣赏", "此歌曲为没有填词的纯音乐请欣赏",
        "純音樂請欣賞", "純音樂請您欣賞", "此歌曲為沒有填詞的純音樂", "此歌曲為沒有填詞的純音樂請您欣賞", "此歌曲為沒有填詞的純音樂請欣賞",
    };

    /// <summary>Whether <paramref name="text"/> is, as a whole, the filler for a song without words.</summary>
    public static bool IsFiller(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return false;
        var kept = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
            if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c)) kept.Append(c);
        return Lines.Contains(kept.ToString());
    }

    /// <summary><paramref name="lines"/> without filler lines (the same list when there are none).</summary>
    public static IReadOnlyList<LyricLine> Without(IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Any(l => IsFiller(l.Text)) ? [.. lines.Where(l => !IsFiller(l.Text))] : lines;
    }

    /// <summary><paramref name="lines"/> without filler lines (the same list when there are none).</summary>
    public static IReadOnlyList<string> Without(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Any(IsFiller) ? [.. lines.Where(l => !IsFiller(l))] : lines;
    }
}
