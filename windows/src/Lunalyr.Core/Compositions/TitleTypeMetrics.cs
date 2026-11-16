// Ported from the Mac app's OpeningTitle.swift (OpeningTitle.TypeMetrics, 79088c3): Lunalyr's own code. The face is
// measured by the renderer (DirectWrite on Windows, Core Text on the Mac); this is what is made of the measures.

using System.Text;

namespace Lunalyr.Core.Compositions;

/// <summary>
/// How a line of the title card reads in the face it is set in, measured from the face itself, so a card in a pixel,
/// condensed, wide, serif, script or CJK face sets and lines up as it does in the face it was drawn for. The card was
/// tuned in Poppins: capitals 0.70 of the size, lower case 0.55, its first baseline 1.05 down. All per point of size.
/// </summary>
/// <param name="Scale">Multiplies the size the card gives the line, so its letters come out as tall as Poppins' would (0.85–1.3).</param>
/// <param name="InkTop">The ink's height above the baseline: the cover lines up there.</param>
/// <param name="Ascent">The first baseline's depth below the line's top.</param>
/// <param name="Descent">Below the baseline, to the line's foot.</param>
/// <param name="PadTop">Room added above each line, so a face with a tight line box spaces its lines as Poppins does.</param>
/// <param name="PadBottom">Room added below each line.</param>
public readonly record struct TitleTypeMetrics(double Scale, double InkTop, double Ascent, double Descent, double PadTop, double PadBottom)
{
    public const double ReferenceCap = 0.70;
    public const double ReferenceLower = 0.55;
    /// <summary>Poppins' average advance for a capital and a lower-case letter.</summary>
    public const double ReferenceCapAdvance = 0.67;
    public const double ReferenceLowerAdvance = 0.55;
    /// <summary>Kana, Han and Hangul in Poppins' fallback rise this far.</summary>
    public const double ReferenceOther = 0.84;

    /// <summary>The face the card was tuned in.</summary>
    public static TitleTypeMetrics Reference => new(1, 0.7, 0.95, 0.3, 0, 0);

    /// <summary>Where the line's ink starts, as a share of the first baseline's depth (padding included).</summary>
    public double CapShare => Math.Clamp(InkTop / (Ascent + PadTop), 0, 1);

    /// <summary>Space to add between a title's lines: the same room as above the first and below the last.</summary>
    public double LineSpacing(double size) => (PadTop + PadBottom) * size;

    /// <summary>
    /// The measures for <paramref name="text"/>: the face's <paramref name="ascent"/> and <paramref name="descent"/>, the
    /// ink heights of "H" and "x" (<paramref name="cap"/>, <paramref name="lower"/>), and how to measure a string's ink
    /// height and advance in it (all per point).
    /// </summary>
    public static TitleTypeMetrics Measure(string text, double ascent, double descent, double cap, double lower,
                                           Func<string, double?> inkHeight, Func<string, double> advance)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(inkHeight);
        ArgumentNullException.ThrowIfNull(advance);
        ascent = Math.Max(0.3, ascent);
        int letters = 0, latin = 0, upper = 0;
        var latinText = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            letters++;
            if (rune.Value < 0x80 || rune.Value is >= 0xC0 and <= 0x24F)
            {
                latin++;
                latinText.Append(rune.ToString());
                if (Rune.IsUpper(rune)) upper++;
            }
        }
        double scale, inkTop;
        if (letters > 0 && latin * 2 < letters)
        {
            // Mostly CJK, Hangul or another script: measured by its own ink, from whatever face draws it (a pixel face's
            // fallback for kana is larger than Poppins').
            inkTop = inkHeight(text) ?? cap;
            scale = Math.Clamp(ReferenceOther / Math.Max(0.05, inkTop), 0.85, 1.3);
        }
        else
        {
            var share = letters == 0 ? 1 : upper / (double)Math.Max(1, latin);
            var own = share * cap + (1 - share) * lower;
            var reference = share * ReferenceCap + (1 - share) * ReferenceLower;
            // A condensed face reads smaller than its height says, a wide one larger: a little of the width counts.
            var perLetter = advance(latinText.ToString()) / Math.Max(1, latin);
            var referenceAdvance = share * ReferenceCapAdvance + (1 - share) * ReferenceLowerAdvance;
            var width = perLetter > 0.05 ? Math.Pow(referenceAdvance / perLetter, 0.35) : 1;
            scale = own > 0.05 ? Math.Clamp(reference / own * width, 0.85, 1.3) : 1;
            inkTop = cap;
        }
        return new TitleTypeMetrics(scale, inkTop, ascent, descent, Math.Max(0, inkTop + 0.25 - ascent), Math.Max(0, 0.3 - descent));
    }
}
