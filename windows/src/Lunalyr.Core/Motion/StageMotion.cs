using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Motion;

/// <summary>
/// One glued run of a line in the stage layouts: "beau" "ti" "ful" is a single run, shown syllable by syllable as each
/// is sung.
/// </summary>
public sealed record StageRun(int Id, string Text, double Start, double End, IReadOnlyList<StageRun.Part> Parts)
{
    /// <summary>One sung piece of a run, with the lyric word it was cut from (for its reading).</summary>
    public readonly record struct Part(string Text, double Start, TimedWordKey? Source = null);

    /// <summary>
    /// Glued syllables join into one run, but never into a run too long to read as a word: an unspaced Chinese,
    /// Japanese or Korean line, or a romanised one, is cut into short runs that arrive one by one.
    /// </summary>
    public static List<StageRun> Make(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue)
    {
        var runs = new List<StageRun>();
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            var text = word.Text.Trim();
            if (text.Length == 0) continue;
            var part = new Part(text, word.Start, new TimedWordKey(word));
            if (Joins(text, index, glue, runs.Count > 0 ? runs[^1].Text : null))
            {
                var last = runs[^1];
                runs[^1] = last with
                {
                    Text = last.Text + text,
                    End = Math.Max(last.End, word.End),
                    Parts = [.. last.Parts, part],
                };
            }
            else
            {
                runs.Add(new StageRun(runs.Count, text, word.Start, Math.Max(word.End, word.Start), [part]));
            }
        }
        return runs;
    }

    // Whether the word at `index` joins the run before it: glued to it, and the two together still short enough to read
    // as one word.
    private static bool Joins(string text, int index, IReadOnlyList<bool> glue, string? previous)
    {
        if (index <= 0 || index >= glue.Count || !glue[index] || previous is null) return false;
        return TextElements.Count(previous) + TextElements.Count(text) <= (LyricScript.KeepsSyllables(text) ? 3 : 12);
    }

    /// <summary>
    /// The glue of the runs <see cref="Make"/> builds: true where a word joins the run before it (a blank word belongs to
    /// no run of its own). Readings are paired by this, so each run carries its own reading.
    /// </summary>
    public static bool[] Glue(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue)
    {
        var result = new bool[words.Count];
        string? last = null;
        for (var index = 0; index < words.Count; index++)
        {
            var text = words[index].Text.Trim();
            if (text.Length == 0) { result[index] = index > 0; continue; }
            if (Joins(text, index, glue, last))
            {
                result[index] = true;
                last = (last ?? "") + text;
            }
            else
            {
                last = text;
            }
        }
        return result;
    }

    /// <summary>
    /// The reading drawn under <paramref name="run"/>: its parts' shares written together (each carries the space
    /// before it where a new phrase starts, see <c>LyricReadingPairs.Parts</c>), so the syllables of one word join; or
    /// null when it has none (or "Below lyrics" is off, so there are none). <paramref name="readings"/> is keyed by the
    /// lyric words the run was cut from.
    /// </summary>
    public static string? Reading(StageRun run, IReadOnlyDictionary<LyricWord, string> readings)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(readings);
        if (readings.Count == 0) return null;
        var text = string.Concat(run.Parts
            .Select(part => part.Source is { } key && readings.TryGetValue(new LyricWord(key.Text, key.Start, key.End), out var reading) ? reading : null)
            .Where(reading => reading is not null)).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>The runs that have arrived (<see cref="StageMotion.ShownCount"/>): always the first ones, in reading order.</summary>
    public static List<StageRun> Shown(IReadOnlyList<StageRun> runs, double time) =>
        [.. runs.Take(StageMotion.ShownCount(runs, time))];

    /// <summary>
    /// How visible one part is: the first arrives with its word; each later syllable fades in over a short settle as it
    /// is sung, never cut in.
    /// </summary>
    public static double PartOpacity(int index, IReadOnlyList<Part> parts, double time)
    {
        if (index < 0 || index >= parts.Count) return 0;
        var age = time - (parts[index].Start - 0.03);
        if (index == 0) return age >= 0 ? 1 : 0;
        return LyricMotion.Settle(age, StageMotion.SyllableFade);
    }
}

/// <summary>
/// The timing every stage layout shares (Hinge, Broadside, Portal, Mosaic, Torque, Fable). Each function is a pure
/// function of playback time, so a seek, a pause or a dropped frame never changes what is drawn.
/// </summary>
public static class StageMotion
{
    /// <summary>A word starts to arrive this far ahead of its onset.</summary>
    public const double Lead = 0.04;
    /// <summary>A sung syllable of a glued word fading in (curve: Settle).</summary>
    public const double SyllableFade = 0.16;
    /// <summary>
    /// Rise of the arrival spring at an ordinary pace; a fast line divides it by its speed. Damping 0.62 overshoots
    /// about 8%, but the spring starts from rest, so a sum of arrivals never steps in speed at an onset.
    /// </summary>
    public const double ArrivalRise = 0.18;
    public const double ArrivalDamping = 0.62;
    /// <summary>The cadence a rank or camera hand-off is tuned to at an ordinary pace.</summary>
    public const double HandoffCadence = 0.5;
    /// <summary>Hinge's faces swinging shut, and the extra delay of its last word's fold.</summary>
    public const double HingeClose = 0.6;
    public const double HingeFoldStagger = 0.24;

    /// <summary>A word's pose arrival, 0…1 with a little overshoot. Under Reduce Motion a step at the onset.</summary>
    public static double Arrival(double start, double time, double speed = 1, bool reduceMotion = false)
    {
        var age = time - (start - Lead);
        if (reduceMotion) return age >= 0 ? 1 : 0;
        var rise = ArrivalRise / Math.Min(3, Math.Max(1, speed));
        return LyricMotion.SpringStep(age, new Spring(3.36 / rise, ArrivalDamping));
    }

    /// <summary>One onset's share of a rank or a camera move: the rolling hand-off spring, so velocity carries on.</summary>
    public static double Handoff(double start, double time, ulong seed, double speed = 1, bool reduceMotion = false)
    {
        var age = time - (start - Lead);
        if (reduceMotion) return age >= 0 ? 1 : 0;
        return LyricMotion.RollingHandoff(age, HandoffCadence / Math.Min(3, Math.Max(1, speed)), seed, 0);
    }

    /// <summary>The whole line leaving: the same settle as every other line exit.</summary>
    public static double Leave(double time, double exitStart) => LyricMotion.Settle(time - exitStart, WallpaperMotion.LineExit);

    /// <summary>
    /// How much a word is still the one being sung: 1 until the next word starts to take over, 0 once it has. Every
    /// live-only property is blended by this, so nothing restyles in a single frame.
    /// </summary>
    public static double Liveness(double rank) => 1 - Easing.Smoothstep((rank - 0.15) / 0.7);

    /// <summary>How much room a word takes in a stack: none until it starts to arrive, all of it once it has.</summary>
    public static double Weight(double arrival) => Math.Min(1, Math.Max(0, arrival));

    /// <summary>Torque: how visible a word is from its rank. Old words fade out above the fourth.</summary>
    public static double TorqueVisibility(double rank) => Math.Max(0, 1 - Math.Max(0, rank - 3) * 0.8);

    /// <summary>Hinge: the column shrinks by 15% as its seventh word arrives, not at once.</summary>
    public static double HingeColumnShrink(double arrived) => 1 - 0.15 * Easing.Smoothstep(arrived - 6);

    /// <summary>Hinge: how far one word has folded shut. Oldest first; all shut by the end of the exit span.</summary>
    public static double HingeFold(double order, double time, double exitStart) =>
        LyricMotion.Settle(time - exitStart - HingeFoldStagger * Math.Min(1, Math.Max(0, order)), WallpaperMotion.LineExit);

    /// <summary>Reduce Motion: how far the stage has dissolved from its previous step (before the onset) to this one.</summary>
    public static double ReducedStep(double onset, double time) =>
        Easing.Smoothstep((time - (onset - Lead)) / WallpaperMotion.ReducedDissolve);

    /// <summary>
    /// Mosaic: the big setting shrinks to the small one's size by settle 0.7, then the two cross-fade between 0.7 and
    /// 0.95, so the two copies are never seen at different sizes.
    /// </summary>
    public static (double BigScale, double BigOpacity, double SmallOpacity) MosaicMorph(double settle, double smallRatio)
    {
        var shrink = Easing.Smoothstep(settle / 0.7);
        var swap = Easing.Smoothstep((settle - 0.7) / 0.25);
        return (1 - (1 - smallRatio) * shrink, 1 - swap, swap);
    }

    /// <summary>
    /// How many runs have arrived: always the first ones, in reading order. (A source whose word times run backwards
    /// must not make a later run show before an earlier one.)
    /// </summary>
    public static int ShownCount(IReadOnlyList<StageRun> runs, double time)
    {
        var count = 0;
        while (count < runs.Count && time >= runs[count].Start - Lead) count++;
        return count;
    }

    /// <summary>A run's hand-off seed from the stage's seed and the run's id.</summary>
    public static ulong RunSeed(long stageSeed, int runId)
    {
        var mixed = unchecked(stageSeed + runId * 7919L);
        return mixed == long.MinValue ? unchecked((ulong)mixed) : (ulong)Math.Abs(mixed);
    }

    /// <summary>
    /// How far each shown run has been pushed back by the runs after it: the sum of their hand-offs, so it carries its
    /// speed from one onset to the next. Writes one rank per run into <paramref name="ranks"/>; allocation-free.
    /// </summary>
    public static void Ranks(IReadOnlyList<StageRun> shown, int count, double time, double speed, long seed,
                             bool reduceMotion, Span<double> ranks)
    {
        double after = 0;
        for (var index = count - 1; index >= 0; index--)
        {
            ranks[index] = after;
            after += Handoff(shown[index].Start, time, RunSeed(seed, shown[index].Id), speed, reduceMotion);
        }
    }

    /// <summary>
    /// Under Reduce Motion, how far the stage has dissolved from the step before the newest shown run to this one; null
    /// when there is nothing to dissolve. A layout that never moves its words (Fable) needs none.
    /// </summary>
    public static double? ReducedDissolve(IReadOnlyList<StageRun> runs, int shownCount, bool layoutMovesWords,
                                          double time, bool reduceMotion)
    {
        if (!reduceMotion || !layoutMovesWords || shownCount == 0) return null;
        var step = ReducedStep(runs[shownCount - 1].Start, time);
        return step < 1 ? step : null;
    }
}
