// Ported from Lunalyr for Mac's Sources/Visualizers/StarTrailsLyricWord.swift (Lunalyr's own code; no Verso lines).

using System.Text;
using Lunalyr.Core.Lyrics;

namespace Lunalyr.Core.Visualisers;

/// <summary>
/// The sung word that began most recently, as Star Trails lights stars by it: every word of a lyric line lights a fresh
/// handful of vocal stars, and where they sit follows the word's place in its line, so a sung line sketches a loose
/// constellation across the sky.
/// </summary>
/// <param name="Serial">Identifies the word: its line and its index in the line.</param>
/// <param name="Age">Seconds since it began (≥ 0).</param>
/// <param name="Fraction">Its place in its line, 0 (first) … 1 (last).</param>
/// <param name="Strength">0…1: longer words light brighter.</param>
public record struct StarTrailsLyricWord(uint Serial, double Age, double Fraction, double Strength)
{
    /// <summary>
    /// The newest word that began within <paramref name="window"/> seconds before <paramref name="time"/>. Credits,
    /// title headers and instrumental rows are not sung and light nothing. Allocation-free.
    /// </summary>
    public static StarTrailsLyricWord? Newest(IReadOnlyList<LyricLine> lines, double time, double window = 3)
    {
        ArgumentNullException.ThrowIfNull(lines);
        double bestStart = 0;
        StarTrailsLyricWord? best = null;
        var start = lines.Count;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].End >= time - window) { start = i; break; }
        }
        for (var row = start; row < lines.Count; row++)
        {
            var line = lines[row];
            if (line.Start > time) break;
            if (line.IsInstrumental || line.Credit == true) continue;
            var words = line.Words;
            var sungCount = 0;
            var firstSung = -1;
            for (var index = 0; index < words.Count; index++)
            {
                if (!IsSung(words[index].Text)) continue;
                if (firstSung < 0) firstSung = index;
                sungCount++;
            }
            if (sungCount == 0) continue;
            // A line sung as one has only its start to go by.
            var onsetCount = line.WordTimed ? sungCount : 1;
            var position = 0;
            for (var index = firstSung; index < words.Count && position < onsetCount; index++)
            {
                if (!IsSung(words[index].Text)) continue;
                double itemStart, itemEnd;
                if (line.WordTimed)
                {
                    itemStart = words[index].Start;
                    itemEnd = words[index].End;
                }
                else
                {
                    itemStart = line.Start;
                    itemEnd = line.End;
                }
                var current = position++;
                if (!(itemStart <= time && time - itemStart <= window)) continue;
                if (best is not null && !(itemStart >= bestStart)) continue;
                var fraction = onsetCount > 1 ? (double)current / (onsetCount - 1) : 0.5;
                var length = Math.Min(1, Math.Max(0, (itemEnd - itemStart) / 1.2));
                var serial = unchecked((uint)((long)(row + 1) * 997 + index + 1));
                bestStart = itemStart;
                best = new StarTrailsLyricWord(serial, time - itemStart, fraction, 0.5 + 0.5 * length);
            }
        }
        return best;
    }

    /// <summary>A word with at least one letter or number is sung; punctuation and symbols alone are not.</summary>
    private static bool IsSung(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune)) return true;
        }
        return false;
    }
}
