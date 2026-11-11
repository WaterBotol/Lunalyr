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
            var peers = 0;
            var sameWordsTimed = new List<int>();
            for (var j = 0; j < candidates.Count; j++)
            {
                if (j == i || candidates[j].Source == candidates[i].Source || !SameWords(texts[i], texts[j]))
                {
                    continue;
                }
                peers++;
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
                        if (TimingAgrees(timings[sameWordsTimed[a]], timings[sameWordsTimed[b]]))
                        {
                            outlier = true;
                            break;
                        }
                    }
                }
            }
            result[i] = candidates[i] with { Agreement = new LyricAgreement(peers, sameWordsTimed.Count, outlier) };
        }
        return result;
    }

    /// <summary>Sung lines, normalised for comparison.</summary>
    public static IReadOnlyList<string> Text(LyricCandidate candidate)
    {
        IEnumerable<string> raw = candidate.Result switch
        {
            LyricResult.Synced synced => synced.Lines.Where(line => !line.IsInstrumental).Select(line => line.Text),
            LyricResult.Plain plain => plain.Lines,
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
