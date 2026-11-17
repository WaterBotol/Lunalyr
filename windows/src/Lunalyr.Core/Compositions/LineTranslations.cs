// Translations under the lyrics: the Mac's LyricLayerView (currentLine, TranslationLine, LyricLayoutCache
// .hasTranslations), Lunalyr's own lines (blame-checked against a99fd65: the translation lines are clean; the one Verso
// line near them, the constellations' density scale, isn't used here). A translation that came with the lyrics (a
// bilingual LRC, a TTML's, NetEase's or QQ's, a .tr.lrc) sits under its line as a quieter second voice, in the reading
// colour, arriving and leaving with the line. A rolling stage keeps a band at its foot for it whenever the song has any,
// so its words never change size from line to line. Nothing here translates anything.

using Lunalyr.Core.Lyrics;

namespace Lunalyr.Core.Compositions;

/// <summary>
/// Where a line's translation sits (the Mac's <c>TranslationPlacement</c>, 5783d638): under or over its line, or at the
/// foot or head of the stage (the floating window's, in floating lyrics).
/// </summary>
public enum TranslationPlacement
{
    Below,
    Above,
    Bottom,
    Top,
}

public static class TranslationPlacements
{
    public static IReadOnlyList<TranslationPlacement> All { get; } =
        [TranslationPlacement.Below, TranslationPlacement.Above, TranslationPlacement.Bottom, TranslationPlacement.Top];

    public static string Raw(this TranslationPlacement place) => place switch
    {
        TranslationPlacement.Above => "above",
        TranslationPlacement.Bottom => "bottom",
        TranslationPlacement.Top => "top",
        _ => "below",
    };

    /// <summary>A stored value; anything unknown is <see cref="TranslationPlacement.Below"/>.</summary>
    public static TranslationPlacement Parse(string? raw) => raw switch
    {
        "above" => TranslationPlacement.Above,
        "bottom" => TranslationPlacement.Bottom,
        "top" => TranslationPlacement.Top,
        _ => TranslationPlacement.Below,
    };

    public static string Title(this TranslationPlacement place) => place switch
    {
        TranslationPlacement.Above => "Above the line",
        TranslationPlacement.Bottom => "Bottom of the screen",
        TranslationPlacement.Top => "Top of the screen",
        _ => "Under the line",
    };

    /// <summary>At an edge of the stage rather than with its line.</summary>
    public static bool AtEdge(this TranslationPlacement place) => place is TranslationPlacement.Bottom or TranslationPlacement.Top;

    /// <summary>Above the words, with its line or at the top.</summary>
    public static bool OverWords(this TranslationPlacement place) => place is TranslationPlacement.Above or TranslationPlacement.Top;
}

public static class LineTranslations
{
    /// <summary>A translation at the stage's edge sits this share of the base size in from it (the Mac's padding of 0.5).</summary>
    public const double EdgeInset = 0.5;

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
