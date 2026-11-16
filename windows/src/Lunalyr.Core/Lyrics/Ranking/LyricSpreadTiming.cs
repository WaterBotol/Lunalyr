// Ported from the Mac app's LyricSpreadTiming.swift (4c0d3ce; Lunalyr's own code).

namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>
/// Word timing that a tool spread over each line rather than timing to the singing: each word's share of the line
/// equal, or in proportion to its letters (or syllables). It looks word-timed but carries no more than the line's start
/// and end, so it ranks as line timing (<see cref="LyricTimingRules.RankedTiming"/>).
/// </summary>
/// <remarks>
/// Thresholds from real files (16 Nov 2026, on the Mac: Apple TTML via Lyrics+, QQ QRC, Kugou KRC, NetEase YRC, Kuwo,
/// AMLL, LRCLIB, PetitLyrics, Deezer, Migu and Soda for eight songs in English, Chinese, Japanese and Korean): a line
/// counts as spread when the gaps between its word starts fit one of those models within 25 ms (or 3% of the average
/// gap), stamps being centiseconds at best. Genuine files had at most 32% of lines that fit by chance (PetitLyrics,
/// which times short lines; 0–18% for the rest), while the same files with words spread evenly or by length had
/// 46–100%. So a file is spread when at least 60% of at least six judged lines are.
/// </remarks>
public static class LyricSpreadTiming
{
    public const double Tolerance = 0.025;
    public const double RelativeTolerance = 0.03;
    public const double LineShare = 0.6;
    public const int MinLines = 6;

    private static readonly double[] Pads = [0, 0.5, 1, 1.5, 2, 3];

    /// <summary>
    /// Whether a line's word starts were spread by a model rather than timed, or null when the line cannot tell (line
    /// timing, or fewer than four words). The last word is left out: its end is where the line ends.
    /// </summary>
    public static bool? IsEvenlySpread(LyricLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.WordTimed || line.Words.Count < 4 || !LyricTimingRules.ValidWordTiming(line)) return null;
        var words = line.Words;
        var gaps = new double[words.Count - 1];
        double total = 0;
        for (var i = 0; i < gaps.Length; i++)
        {
            gaps[i] = words[i + 1].Start - words[i].Start;
            if (gaps[i] < 0) return null;
            total += gaps[i];
        }
        if (!(total > 0)) return null;
        var mean = total / gaps.Length;
        var tolerance = Math.Max(Tolerance, RelativeTolerance * mean);
        // Equal shares.
        if (Rms(gaps.Select(g => g - mean)) <= tolerance) return true;
        var letters = words.Take(words.Count - 1).Select(w => (double)Math.Max(1, w.Text.EnumerateRunes().Count(r => System.Text.Rune.IsLetterOrDigit(r)))).ToArray();
        // In proportion to letters plus a constant, as tools weigh words.
        foreach (var pad in Pads)
        {
            var weights = letters.Select(l => l + pad).ToArray();
            var scale = total / weights.Sum();
            if (Rms(gaps.Zip(weights, (g, w) => g - scale * w)) <= tolerance) return true;
        }
        // A straight-line fit to letters or syllables needs a fifth word to mean anything.
        if (gaps.Length < 4) return false;
        var syllables = words.Take(words.Count - 1).Select(w => (double)Syllables(w.Text)).ToArray();
        foreach (var measure in new[] { letters, syllables })
        {
            if (LinearResidual(measure, gaps) is { } residual && residual <= tolerance) return true;
        }
        return false;
    }

    /// <summary>Whether most of a file's judged lines are spread (<see cref="IsEvenlySpread"/>).</summary>
    public static bool HasEvenlySpreadWordTiming(IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        int judged = 0, spread = 0;
        foreach (var line in lines)
        {
            if (line.IsInstrumental || IsEvenlySpread(line) is not { } verdict) continue;
            judged++;
            if (verdict) spread++;
        }
        return judged >= MinLines && spread >= LineShare * judged;
    }

    public static bool HasEvenlySpreadWordTiming(LyricResult result) =>
        result is LyricResult.Synced synced && HasEvenlySpreadWordTiming(synced.Lines);

    private static double Rms(IEnumerable<double> residuals)
    {
        double sum = 0;
        var count = 0;
        foreach (var r in residuals)
        {
            sum += r * r;
            count++;
        }
        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    /// <summary>Vowel groups in a Latin word; one a character in other scripts.</summary>
    private static int Syllables(string word)
    {
        var letters = word.ToLowerInvariant().EnumerateRunes().Where(System.Text.Rune.IsLetterOrDigit).ToList();
        if (!letters.All(r => r.IsAscii)) return Math.Max(1, letters.Count);
        int groups = 0;
        var inVowel = false;
        foreach (var rune in letters)
        {
            var vowel = "aeiouy".Contains((char)rune.Value, StringComparison.Ordinal);
            if (vowel && !inVowel) groups++;
            inVowel = vowel;
        }
        return Math.Max(1, groups);
    }

    /// <summary>The root-mean-square residual of a least-squares line through (x, y), or null when x does not vary.</summary>
    private static double? LinearResidual(double[] x, double[] y)
    {
        var n = (double)x.Length;
        double meanX = x.Average(), meanY = y.Average();
        var variance = x.Sum(v => (v - meanX) * (v - meanX));
        if (!(variance > 0)) return null;
        var slope = x.Zip(y, (a, b) => (a - meanX) * (b - meanY)).Sum() / variance;
        var intercept = meanY - slope * meanX;
        return Math.Sqrt(x.Zip(y, (a, b) => Math.Pow(b - (slope * a + intercept), 2)).Sum() / n);
    }
}
