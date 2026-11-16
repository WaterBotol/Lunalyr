namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>A sung line's normalised text and start, for comparing timing between sources.</summary>
public readonly record struct TimedText(string Text, double Start);

/// <summary>
/// What the other sources say about a match. Sources copy one another, so agreement is not proof: it only breaks ties
/// among matches that rank level otherwise (Accuracy first), and takes away LRCLIB's lead when its listener-made
/// timing disagrees with two other services that agree with each other. Ported from the Mac's
/// <c>LyricAgreement</c> (Lunalyr's own code).
/// </summary>
public static class LyricAgreementRules
{
    /// <summary>Fills in <see cref="LyricCandidate.Agreement"/> on each candidate from the others.</summary>
    public static IReadOnlyList<LyricCandidate> Weigh(IReadOnlyList<LyricCandidate> candidates)
    {
        var texts = candidates.Select(Text).ToList();
        var timings = candidates.Select(Timing).ToList();
        var result = new LyricCandidate[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            // Apple Music's own lyrics and Lyrics+'s relay of them are one source (they rank as one), never two that
            // agree: peers and agreeing pairs are counted by the source each ranks as.
            var peers = new HashSet<LyricSourceId>();
            var sameWordsTimed = new List<int>();
            for (var j = 0; j < candidates.Count; j++)
            {
                if (j == i || candidates[j].RankingSource == candidates[i].RankingSource || !SameWords(texts[i], texts[j]))
                {
                    continue;
                }
                peers.Add(candidates[j].RankingSource);
                if (timings[i].Count > 0 && timings[j].Count > 0)
                {
                    sameWordsTimed.Add(j);
                }
            }
            var outlier = false;
            if (sameWordsTimed.Count >= 2 && !sameWordsTimed.Any(j => TimingAgrees(timings[i], timings[j])))
            {
                for (var a = 0; a < sameWordsTimed.Count && !outlier; a++)
                {
                    for (var b = a + 1; b < sameWordsTimed.Count; b++)
                    {
                        int x = sameWordsTimed[a], y = sameWordsTimed[b];
                        if (candidates[x].RankingSource != candidates[y].RankingSource && TimingAgrees(timings[x], timings[y]))
                        {
                            outlier = true;
                            break;
                        }
                    }
                }
            }
            var shift = TimelineShift(i, timings, texts, candidates);
            result[i] = candidates[i] with { Agreement = new LyricAgreement(peers.Count, sameWordsTimed.Count, outlier, shift) };
        }
        return result;
    }

    /// <summary>The least offset, in seconds, that counts as a timeline shifted to another recording or master: more than a lyric's sync slips.</summary>
    public const double TimelineShiftMinimum = 1.5;

    /// <summary>How closely a shifted timeline's lines keep to its own offset: at least <see cref="TimelineShiftShare"/> of them within this many seconds of it.</summary>
    public const double TimelineShiftSpread = 0.5;

    public const double TimelineShiftShare = 0.8;

    /// <summary>Lines paired by their words needed to judge an offset.</summary>
    public const int TimelineShiftMinPairs = 6;

    /// <summary>
    /// How <paramref name="b"/>'s sung lines run against <paramref name="a"/>'s: the median of their start differences (b
    /// minus a) over lines with the same words, paired in order, and whether nearly all of them keep to it
    /// (<see cref="TimelineShiftSpread"/>). Null with fewer than <see cref="TimelineShiftMinPairs"/> pairs. Ported from
    /// the Mac's 265d728.
    /// </summary>
    public static (double Median, bool Steady)? TimelineOffset(IReadOnlyList<TimedText> a, IReadOnlyList<TimedText> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var gaps = new List<double>();
        var from = 0;
        foreach (var line in a)
        {
            if (from >= b.Count) continue;
            var match = -1;
            for (var k = from; k < b.Count; k++)
            {
                if (b[k].Text == line.Text)
                {
                    match = k;
                    break;
                }
            }
            if (match < 0) continue;
            gaps.Add(b[match].Start - line.Start);
            from = match + 1;
        }
        if (gaps.Count < TimelineShiftMinPairs) return null;
        var median = gaps.Order().ElementAt(gaps.Count / 2);
        var near = gaps.Count(gap => Math.Abs(gap - median) <= TimelineShiftSpread);
        return (median, near >= TimelineShiftShare * gaps.Count);
    }

    /// <summary>The offset <paramref name="b"/> runs steadily <see cref="TimelineShiftMinimum"/> or more from <paramref name="a"/>, or null.</summary>
    public static double? IsShifted(IReadOnlyList<TimedText> a, IReadOnlyList<TimedText> b) =>
        TimelineOffset(a, b) is { Steady: true } offset && Math.Abs(offset.Median) >= TimelineShiftMinimum ? offset.Median : null;

    /// <summary>
    /// The offset of candidate <paramref name="i"/>'s timeline when it is timed to another recording or master, or null.
    /// Apple Music's own lyrics are the recording playing, so a timeline steadily off from their timing is shifted, and
    /// they never are themselves. Otherwise it takes two timed matches from other services that agree with each other,
    /// with the same words, both steadily off from it by about the same offset, while no other service's timing agrees
    /// with its own: of two that disagree there is no telling which is right.
    /// </summary>
    public static double? TimelineShift(int i, IReadOnlyList<IReadOnlyList<TimedText>> timings, IReadOnlyList<IReadOnlyList<string>> texts,
        IReadOnlyList<LyricCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(timings);
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates[i].Source == LyricSourceId.AppleMusicCache || timings[i].Count == 0) return null;
        var others = Enumerable.Range(0, candidates.Count).Where(j => j != i && candidates[j].RankingSource != candidates[i].RankingSource
                                                                       && timings[j].Count > 0 && SameWords(texts[i], texts[j])).ToList();
        foreach (var j in others)
        {
            if (candidates[j].Source == LyricSourceId.AppleMusicCache) return IsShifted(timings[j], timings[i]);
        }
        if (others.Any(j => TimingAgrees(timings[i], timings[j]))) return null;
        var shifted = others.Select(j => (Index: j, Offset: IsShifted(timings[j], timings[i]))).Where(s => s.Offset is not null).ToList();
        for (var a = 0; a < shifted.Count; a++)
        {
            for (var b = a + 1; b < shifted.Count; b++)
            {
                var (x, y) = (shifted[a], shifted[b]);
                if (candidates[x.Index].RankingSource != candidates[y.Index].RankingSource
                    && Math.Abs(x.Offset!.Value - y.Offset!.Value) <= TimelineShiftSpread && TimingAgrees(timings[x.Index], timings[y.Index]))
                {
                    return (x.Offset.Value + y.Offset.Value) / 2;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Sung lines, normalised for comparison: credits and headers are not sung, so they never count for or against
    /// agreement (the Mac's 1cdde21). Synced lines had them flagged when the match was built; plain text has them
    /// left out here.
    /// </summary>
    public static IReadOnlyList<string> Text(LyricCandidate candidate)
    {
        IEnumerable<string> raw = candidate.Result switch
        {
            LyricResult.Synced synced => synced.Lines.Where(line => !line.IsInstrumental).Select(line => line.Text),
            LyricResult.Plain plain => Parsing.LyricCredits.SungLines(plain.Lines, candidate.Title, candidate.Artist),
            _ => [],
        };
        return [.. raw.Select(LyricQuery.Normalize).Where(text => text.Length > 0)];
    }

    /// <summary>Sung lines with their start time; empty for plain text.</summary>
    public static IReadOnlyList<TimedText> Timing(LyricCandidate candidate)
    {
        if (candidate.Result is not LyricResult.Synced synced)
        {
            return [];
        }
        return [.. synced.Lines.Where(line => !line.IsInstrumental)
            .Select(line => new TimedText(LyricQuery.Normalize(line.Text), line.Start))
            .Where(line => line.Text.Length > 0)];
    }

    /// <summary>At least 60% of the lines of the longer text appear in the other, over at least four lines each.</summary>
    public static bool SameWords(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (Math.Min(a.Count, b.Count) < 4)
        {
            return false;
        }
        var pool = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in b)
        {
            pool[line] = pool.GetValueOrDefault(line) + 1;
        }
        var shared = 0;
        foreach (var line in a)
        {
            if (pool.GetValueOrDefault(line) > 0)
            {
                shared++;
                pool[line]--;
            }
        }
        return (double)shared / Math.Max(a.Count, b.Count) >= 0.6;
    }

    /// <summary>Lines with the same words, paired in order, start within 0.75 s of each other at the median, over at least four pairs.</summary>
    public static bool TimingAgrees(IReadOnlyList<TimedText> a, IReadOnlyList<TimedText> b)
    {
        var gaps = new List<double>();
        var from = 0;
        foreach (var line in a)
        {
            var match = -1;
            for (var k = from; k < b.Count; k++)
            {
                if (b[k].Text == line.Text)
                {
                    match = k;
                    break;
                }
            }
            if (match < 0)
            {
                continue;
            }
            gaps.Add(Math.Abs(b[match].Start - line.Start));
            from = match + 1;
            if (from >= b.Count)
            {
                break;
            }
        }
        if (gaps.Count < 4)
        {
            return false;
        }
        gaps.Sort();
        return gaps[gaps.Count / 2] <= 0.75;
    }
}
