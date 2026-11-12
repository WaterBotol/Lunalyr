// Ported from the Mac app's CreditsLayer.swift (LyricCreditCues) and LayerExit.swift: Lunalyr's own code (git blame
// puts none of either in the squashed Verso import).

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>
/// One production credit shown on the wallpaper: its words (estimated over its first moments, so it is spelled out
/// word by word like a lyric), and when it arrives and leaves.
/// </summary>
public sealed record CreditCue(int Id, string Text, double Start, double End, IReadOnlyList<LyricWord> Words)
{
    /// <summary>Even cues sit at the top of the stage, odd ones at its foot, so one arriving as the last leaves never sits on it.</summary>
    public bool Top => Id % 2 == 0;

    public bool Equals(CreditCue? other) =>
        other is not null && Id == other.Id && Text == other.Text && Start == other.Start && End == other.End
        && Words.SequenceEqual(other.Words);

    public override int GetHashCode() => HashCode.Combine(Id, Text, Start, End, Words.Count);
}

/// <summary>
/// Production credits as their own background layer. Lyric services publish credits ("Produced by: …", "作词 : …") as
/// timed rows ahead of the lyric; they are not sung, so the lead layer skips them (<see cref="LyricLine.Credit"/>),
/// but each one is introduced in the background while the song plays on, animated word by word like a lyric, at the
/// lead's own brightness, in a thin band along the top or foot of the stage. The song's own title and artist are the
/// opening title's (<see cref="OpeningTitle"/>), not a credit.
/// </summary>
public static class CreditCues
{
    /// <summary>Time between one credit arriving and the next.</summary>
    public const double Stagger = 8.0;
    public const double MinimumHold = 11.0;
    public const double FadeIn = 0.5;
    public const double FadeOut = 1.5;

    /// <summary>
    /// One cue per credit line of <paramref name="lines"/>. Credits that share a time (or have none to speak of) arrive
    /// one after another instead of piling up, and none before <paramref name="notBefore"/> (the song's opening title
    /// has the screen until then). The lyric file's own title and artist headings, bare names and repeated credits are
    /// dropped: the opening title names the song.
    /// </summary>
    /// <param name="distribute">Spreads a credit's words over its first moments (the plain estimate when null).</param>
    public static List<CreditCue> Cues(IReadOnlyList<LyricLine> lines, string? title = "", string? artist = "",
                                       WordDistributor? distribute = null, double notBefore = 0)
    {
        ArgumentNullException.ThrowIfNull(lines);
        distribute ??= EstimatedWords.Distribute;
        var cues = new List<CreditCue>();
        var cursor = double.IsFinite(notBefore) ? notBefore : 0;
        var names = new LyricCredits.TrackNames(title ?? "", artist ?? "");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Credit != true || names.IsHeading(line.Text) || names.IsLoneName(line.Text)) continue;
            if (!seen.Add(LyricQuery.Normalize(line.Text))) continue;
            var text = line.Text.Trim();
            if (text.Length == 0 || !double.IsFinite(line.Start)) continue;
            var start = Math.Max(line.Start, cursor);
            cursor = start + Stagger;
            var count = TextElements.Count(text);
            var spell = Math.Min(3.5, 0.2 * count);
            var spelled = Math.Max(0.4, spell);
            // From its last word arriving to leaving takes half what it used to.
            var hold = 0.2 + spelled + (MinimumHold + Math.Min(5.0, count * 0.12) - 0.2 - spelled) / 2;
            var words = distribute(text, start + 0.2, start + 0.2 + spelled);
            if (words.Count == 0) continue;
            cues.Add(new CreditCue(cues.Count, text, start, start + hold, words));
        }
        return cues;
    }

    /// <summary>0…1: eases in, holds, then fades out slowly.</summary>
    public static double Level(CreditCue cue, double time)
    {
        ArgumentNullException.ThrowIfNull(cue);
        if (time < cue.Start) return 0;
        if (time < cue.Start + FadeIn) return Easing.Smoothstep((time - cue.Start) / FadeIn);
        if (time <= cue.End) return 1;
        return 1 - Easing.Smoothstep((time - cue.End) / FadeOut);
    }

    /// <summary>0 while the cue is up, then 0…1 as it leaves.</summary>
    public static double ExitProgress(CreditCue cue, double time)
    {
        ArgumentNullException.ThrowIfNull(cue);
        return time <= cue.End ? 0 : Easing.Smoothstep((time - cue.End) / FadeOut);
    }

    /// <summary>The edge bands the cues hold, and when: backing vocals keep to the other band meanwhile.</summary>
    public static CreditBand[] Bands(IReadOnlyList<CreditCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var bands = new CreditBand[cues.Count];
        for (var i = 0; i < cues.Count; i++) bands[i] = new CreditBand(cues[i].Start, cues[i].End + FadeOut, cues[i].Top);
        return bands;
    }
}

/// <summary>
/// How a background layer leaves. Each kind always leaves the same way, so what is going (a credit, backing vocals) can
/// be told by how it goes as well as by how it arrived.
/// </summary>
public enum LayerExitKind
{
    /// <summary>Production credits lift away toward the edge they sit on, softening.</summary>
    Credit,
    /// <summary>Backing vocals sink: a small settle downward and shrink, no blur.</summary>
    Backing,
}

public static class LayerExit
{
    /// <summary>What <paramref name="progress"/> (0 while held … 1 gone) does to a layer <paramref name="size"/> tall.</summary>
    public static (double Offset, double Scale, double Blur) Pose(LayerExitKind kind, double progress, double size, bool towardTop = false)
    {
        var p = Math.Min(1, Math.Max(0, progress));
        return kind switch
        {
            LayerExitKind.Credit => ((towardTop ? -1 : 1) * p * size * 0.45, 1, p * size * 0.09),
            _ => (p * size * 0.3, 1 - 0.06 * p, 0),
        };
    }

    /// <summary>
    /// As <see cref="Pose(LayerExitKind, double, double, bool)"/>, except that under Reduce Motion a layer only fades as it
    /// leaves: it neither moves, swells nor blurs (the Mac's <c>LayerExitModifier</c>).
    /// </summary>
    public static (double Offset, double Scale, double Blur) Pose(LayerExitKind kind, double progress, double size, bool towardTop,
                                                                 bool reduceMotion) =>
        reduceMotion ? (0, 1, 0) : Pose(kind, progress, size, towardTop);
}
