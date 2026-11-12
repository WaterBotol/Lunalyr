// Ported from the Mac app's LyricTypography.swift (LyricReadingInk: shares, shareOpacities) and StageLyricStyles.swift
// (StageLyricView.reading, revealedReading): Lunalyr's own code (commits 76130ae, 24b8b2e, b46b130, 2c91c86), no line
// from the Verso import.

using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>
/// A glued word's reading, token by token: each token's share of it (<see cref="LyricReadingPairs.Shares"/> or, on a
/// stage, <see cref="LyricReadingPairs.Parts"/>) and when that token is sung. Written together the shares are
/// <see cref="Text"/>, so the reading reads and measures exactly as the whole one did, and each share can show as its
/// own token is sung ("omo" under 思, then "i" under い, then "da" under 出).
/// </summary>
/// <param name="Text">The reading as drawn: the shares written together.</param>
/// <param name="Shares">One per token, in order (a token with no share has an empty one); empty for a reading of one
/// token, which shows whole.</param>
public sealed record ReadingShares(string Text, IReadOnlyList<ReadingShares.Share> Shares)
{
    /// <summary>One token's share of the reading, and the token's onset.</summary>
    public readonly record struct Share(string Text, double Start);

    /// <summary>
    /// The reading of a run of tokens, from each token's share in <paramref name="readings"/> (missing shares are
    /// empty). Null when no token has one. The first share's leading space is dropped, so the text is exactly the
    /// shares written together.
    /// </summary>
    public static ReadingShares? Of(IEnumerable<LyricWord> tokens, IReadOnlyDictionary<LyricWord, string> readings)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(readings);
        var shares = new List<Share>();
        var started = false;
        foreach (var token in tokens)
        {
            var share = readings.TryGetValue(token, out var found) ? found : "";
            if (!started) share = share.TrimStart();
            started |= share.Length > 0;
            shares.Add(new Share(share, token.Start));
        }
        return Make(shares);
    }

    /// <summary>
    /// A stage run's reading by part (the Mac's <c>StageLyricView.revealedReading</c>): each part's share keyed by the
    /// lyric word it was cut from, the first share's leading space dropped. Null when no part has one.
    /// </summary>
    public static ReadingShares? Of(StageRun run, IReadOnlyDictionary<LyricWord, string> readings)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(readings);
        if (readings.Count == 0) return null;
        var shares = new List<Share>(run.Parts.Count);
        var started = false;
        foreach (var part in run.Parts)
        {
            var share = part.Source is { } key && readings.TryGetValue(new LyricWord(key.Text, key.Start, key.End), out var found)
                ? found : "";
            if (!started) share = share.TrimStart();
            started |= share.Length > 0;
            shares.Add(new Share(share, part.Start));
        }
        return Make(shares);
    }

    private static ReadingShares? Make(List<Share> shares)
    {
        var text = string.Concat(shares.Select(share => share.Text));
        if (text.Length == 0) return null;
        return new ReadingShares(text, shares.Count > 1 ? shares : []);
    }

    /// <summary>Whether the reading fills in share by share (it has more than one).</summary>
    public bool Reveals => Shares.Count > 1;

    /// <summary>
    /// How far share <paramref name="index"/> shows at <paramref name="time"/>: from its token's onset it fades in over
    /// <see cref="StageMotion.SyllableFade"/> (as a stage word's syllables do), and never ahead of its own syllable,
    /// whose opacity relative to the word's is <paramref name="syllables"/>[index] where the layout reveals a word
    /// syllable by syllable (one per share; empty otherwise). The first share comes with the word. With Reduce Motion
    /// a share appears at its onset.
    /// </summary>
    public double Opacity(int index, double time, ReadOnlySpan<double> syllables, bool reduceMotion)
    {
        if (index <= 0 || index >= Shares.Count) return 1;
        var age = time - (Shares[index].Start - 0.03);
        if (reduceMotion) return age >= 0 ? 1 : 0;
        var own = LyricMotion.Settle(age, StageMotion.SyllableFade);
        if (syllables.Length != Shares.Count) return own;
        return Math.Min(own, Math.Max(0, syllables[index]));
    }

    /// <summary>
    /// Each share's opacity at <paramref name="time"/> (see <see cref="Opacity"/>) into <paramref name="into"/>. False
    /// for a reading of one token, which shows whole.
    /// </summary>
    public bool Opacities(double time, ReadOnlySpan<double> syllables, bool reduceMotion, Span<double> into)
    {
        if (!Reveals || into.Length < Shares.Count) return false;
        for (var i = 0; i < Shares.Count; i++) into[i] = Opacity(i, time, syllables, reduceMotion);
        return true;
    }

    /// <summary>
    /// Each token's opacity relative to the run's first, which the reading as a whole already takes (the Mac's
    /// <c>ScatteredRunFrame.tokenOpacities</c>): <paramref name="tokenOpacities"/> into <paramref name="into"/>.
    /// </summary>
    public static void RelativeToFirst(ReadOnlySpan<double> tokenOpacities, Span<double> into)
    {
        if (tokenOpacities.Length == 0) return;
        var first = Math.Max(tokenOpacities[0], 0.001);
        for (var i = 0; i < tokenOpacities.Length && i < into.Length; i++) into[i] = Math.Min(1, tokenOpacities[i] / first);
    }

    /// <summary>
    /// The part of the reading that shows given each share's opacity (<paramref name="opacities"/>, one per share), as
    /// the Mac draws it: a share at or below 0.001 is clear. Empty opacities show the whole reading.
    /// </summary>
    public string Shown(ReadOnlySpan<double> opacities)
    {
        if (opacities.Length != Shares.Count || !Reveals) return Text;
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < Shares.Count; i++)
            if (opacities[i] > 0.001) text.Append(Shares[i].Text);
        return text.ToString();
    }
}
