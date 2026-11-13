// "Sung sweep": how far the sung colour has filled a word. Ported from the Mac's SpokenTypographyView.swift
// (RollingTokenState.sweep and its progress(_:), aa22c91: a word fills over its own time, a run of parts part by part by
// each part's share of the label) and ScatteredLyricText.swift's SungText tint (aa22c91: the tint is that progress when
// the ink sweeps). Scattered's own progress expression (ScatteredLyricText.swift line 704) is on an a99fd65 line and was
// not used; the same formula comes from the Spoken lines above. A line sung as one uses LyricPhraseTiming.Sweep, which
// Windows already had.

using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;

namespace Lunalyr.Core.Colour;

public static class SungSweep
{
    /// <summary>How far through <paramref name="word"/> the singing is, 0…1.</summary>
    public static double Progress(LyricWord word, double time)
    {
        ArgumentNullException.ThrowIfNull(word);
        return Math.Min(Math.Max((time - word.Start) / Math.Max(word.End - word.Start, 0.001), 0), 1);
    }

    /// <summary>
    /// How far the sung colour has filled a run: each token fills its share of the label (<see cref="EditorialRun.Boundaries"/>)
    /// as it is sung, so a glued run fills part by part.
    /// </summary>
    public static double Progress(EditorialRun run, IReadOnlyList<EditorialToken> tokens, double time)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(tokens);
        if (!run.IsGlued || run.Boundaries.Length != run.TokenCount + 1) return Progress(run.Source, time);
        var total = 0.0;
        for (var offset = 0; offset < run.TokenCount; offset++)
            total += (run.Boundaries[offset + 1] - run.Boundaries[offset]) * Progress(tokens[run.TokenStart + offset].Source, time);
        return Math.Min(Math.Max(total, 0), 1);
    }

    /// <summary>How far a rolling word has filled: part by part, by each part's share, when it has parts.</summary>
    public static double Progress(RollingWord token, double time)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Parts.Count <= 1 || token.Boundaries.Count != token.Parts.Count + 1) return Progress(token.Source, time);
        var total = 0.0;
        for (var offset = 0; offset < token.Parts.Count; offset++)
            total += (token.Boundaries[offset + 1] - token.Boundaries[offset]) * Progress(token.Parts[offset], time);
        return Math.Min(Math.Max(total, 0), 1);
    }

    /// <summary>How far a line has filled: to its end when word-timed, else over its estimated words' time.</summary>
    public static double Progress(LyricLine line, double time) => Motion.LyricPhraseTiming.Sweep(line, time);

    /// <summary>
    /// The span of a label <paramref name="width"/> wide (centred on 0) the sung colour covers at <paramref name="tint"/>,
    /// from the leading edge in the reading direction.
    /// </summary>
    public static (double From, double To) Span(double width, double tint, bool rightToLeft)
    {
        var t = Math.Min(Math.Max(tint, 0), 1);
        if (t >= 1) return (double.NegativeInfinity, double.PositiveInfinity);
        return rightToLeft ? (width / 2 - width * t, double.PositiveInfinity) : (double.NegativeInfinity, -width / 2 + width * t);
    }
}
