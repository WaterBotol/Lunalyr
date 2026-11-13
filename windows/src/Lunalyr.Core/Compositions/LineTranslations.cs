// Translations under the lyrics: the Mac's LyricLayerView (currentLine, TranslationLine, LyricLayoutCache
// .hasTranslations), Lunalyr's own lines (blame-checked against a99fd65: the translation lines are clean; the one Verso
// line near them, the constellations' density scale, isn't used here). A translation that came with the lyrics (a
// bilingual LRC, a TTML's, NetEase's or QQ's, a .tr.lrc) sits under its line as a quieter second voice, in the reading
// colour, arriving and leaving with the line. A rolling stage keeps a band at its foot for it whenever the song has any,
// so its words never change size from line to line. Nothing here translates anything.

using Lunalyr.Core.Lyrics;

namespace Lunalyr.Core.Compositions;

public static class LineTranslations
{
    /// <summary>A translation under its line: this share of the base size, times the size slider.</summary>
    public const double LineSize = 0.34;

    /// <summary>A translation over a rolling stage's foot: this share of the base size, times the size slider.</summary>
    public const double RollingSize = 0.30;

    /// <summary>How wide a translation may run, as a share of the stage's width (it wraps beyond).</summary>
    public const double Width = 0.8;

    /// <summary>How many lines a translation may take on a rolling stage (the band is kept for two).</summary>
    public const int RollingLines = 2;

    /// <summary>The gap between a line (or its reading) and its translation, as a share of the base size.</summary>
    public const double Gap = 0.2;

    /// <summary>Line <paramref name="line"/>'s translation as shown (trimmed), or null when it has none.</summary>
    public static string? Of(LyricLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.Translation?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Whether any line has a translation to show (the Mac's <c>hasTranslations</c>).</summary>
    public static bool Any(IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines)
            if (Of(line) is not null) return true;
        return false;
    }

    /// <summary>
    /// The band a rolling stage keeps clear at its foot for translations, in the units of <paramref name="baseSize"/>
    /// (the stage's base, before the rolling stages' 0.75): room for <see cref="RollingLines"/> lines of translation
    /// and a little air. Zero when translations are off or the song has none.
    /// </summary>
    public static double RollingBand(double baseSize, double userScale, bool shown) =>
        shown ? baseSize * RollingSize * userScale * 2.7 + baseSize * 0.3 : 0;
}
