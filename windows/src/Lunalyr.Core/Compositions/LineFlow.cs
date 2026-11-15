// Ported from the Mac app's LyricLayerView.swift (LyricLayer's static stage rules and LyricLineStack.offsets) and
// LyricHighlightTile.swift (LyricHighlightTiming.lineEnd): Lunalyr's own code. Which line the stage draws, when an
// earlier one is still leaving, when lines hand over, the next line's preview, and where a held duet line goes.

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>The settings the stage rules read: how the composition treats word-timed lines and the word options.</summary>
/// <param name="RollsWordTimed">Word-timed lines roll one word at a time (Spoken, Cascade, Cutout).</param>
/// <param name="RevealsWords">Words arrive one by one as they are sung.</param>
/// <param name="PreviewsNextWord">The next word shows faintly just before it is sung.</param>
/// <param name="PreviewLead">How many seconds ahead the preview begins.</param>
/// <param name="FillsStage">The composition sets the whole stage itself (Hinge, Broadside, Portal, Mosaic, Torque, Fable): it
/// draws nothing for a line-timed preview, so none is made.</param>
public readonly record struct StageRules(bool RollsWordTimed, bool RevealsWords = true, bool PreviewsNextWord = true,
                                         double PreviewLead = 0.45, bool FillsStage = false)
{
    /// <summary>Whether line <paramref name="row"/> rolls.</summary>
    public bool Rolls(IReadOnlyList<LyricLine> lines, int row) =>
        RollsWordTimed && row >= 0 && row < lines.Count && lines[row].WordTimed;
}

public static class LineFlow
{
    /// <summary>A line-only phrase easing away before a word-timed line takes the stage (and the other way round).</summary>
    public const double TimingHandoffDuration = 0.18;

    /// <summary>A held line moving aside, from the next line's start.</summary>
    public const double HeldShiftDuration = 0.45;

    /// <summary>The next line's preview ramping in from when it was allowed.</summary>
    public const double PreviewRamp = 0.16;

    /// <summary>The fade at the end of a line (<c>LyricHighlightTiming.fade</c>).</summary>
    public const double HighlightFade = 0.42;

    /// <summary>Whether two neighbouring lines switch between word timing and line timing (instrumental breaks aside).</summary>
    public static bool ChangesTiming(LyricLine from, LyricLine to) =>
        !from.IsInstrumental && !to.IsInstrumental && from.WordTimed != to.WordTimed;

    /// <summary>When line <paramref name="row"/> hands the stage on. A rolling stage carries its words on into the next line by itself, so it hands over as the next line starts.</summary>
    public static double HandOver(int row, IReadOnlyList<LyricLine> lines, StageRules rules)
    {
        if (row + 1 < lines.Count && row >= 0 && rules.Rolls(lines, row)) return lines[row + 1].Start;
        return LyricPhraseTiming.HandOver(row, lines);
    }

    /// <summary>
    /// Whether an earlier line (<paramref name="line"/> before <paramref name="current"/>) is still being sung or
    /// finishing its exit. An earlier renderer never reappears after a change of timing, and a rolling line followed by
    /// another rolling line has no exit of its own: its words carry on in that line's stage.
    /// </summary>
    public static bool IsLeaving(int line, int current, IReadOnlyList<LyricLine> lines, double time, StageRules rules,
                                 double span = LyricPhraseTiming.ExitSpan)
    {
        if (line < 0 || line >= current || current >= lines.Count) return false;
        if (ChangesTiming(lines[line], lines[current])) return false;
        for (var k = line; k < current; k++)
            if (ChangesTiming(lines[k], lines[k + 1])) return false;
        var handOver = HandOver(line, lines, rules);
        if (!LyricPhraseTiming.StillLeaving(lines[line], handOver, time, span)) return false;
        if (rules.Rolls(lines, line) && rules.Rolls(lines, line + 1)) return false;
        return !(rules.Rolls(lines, current) && rules.Rolls(lines, line));
    }

    /// <summary>The whole outgoing stage easing away before a line of the other timing starts; 1 otherwise.</summary>
    public static double TimingHandoffPresence(int row, IReadOnlyList<LyricLine> lines, double time)
    {
        if (row < 0 || row + 1 >= lines.Count || !ChangesTiming(lines[row], lines[row + 1])) return 1;
        return 1 - Easing.Smoothstep((time - (lines[row + 1].Start - TimingHandoffDuration)) / TimingHandoffDuration);
    }

    /// <summary>A rolling stage carries on at full strength into the next rolling line; otherwise it fades on its own exit.</summary>
    public static double RollingPresence(bool nextRolls, bool outgoing, double exit) => nextRolls && !outgoing ? 1 : 1 - exit;

    /// <summary>When a stage-filling layout's line has been sung and held long enough (<see cref="LyricPhraseTiming.StageHeldUntil"/>).</summary>
    public static double StageExitStart(LyricLine line, LyricPhraseTiming pose) =>
        Math.Min(pose.ExitStart, LyricPhraseTiming.StageHeldUntil(line));

    /// <summary>
    /// The exit the renderer draws: word-timed lines that do not roll (and line-timed lines in stage layouts) leave a
    /// moment after their last word; everything else at the phrase's own exit.
    /// </summary>
    public static double RenderedExitStart(int row, IReadOnlyList<LyricLine> lines, double handOver, bool rolls, bool fillsStage)
    {
        var line = lines[row];
        var phrase = LyricPhraseTiming.ExitStartOf(line, handOver);
        if (rolls || !(line.WordTimed || fillsStage)) return phrase;
        return Math.Min(phrase, LyricPhraseTiming.StageHeldUntil(line));
    }

    /// <summary>0…1: how far the line after <paramref name="row"/> has arrived (its spring); 0 before it starts or when <paramref name="row"/> is held.</summary>
    public static double IncomingArrival(int row, IReadOnlyList<LyricLine> lines, double time, StageRules rules)
    {
        if (row + 1 >= lines.Count || row < 0 || time < lines[row + 1].Start || LyricPhraseTiming.IsHeld(row, lines)) return 0;
        var pose = new LyricPhraseTiming(lines[row + 1], HandOver(row + 1, lines, rules), time);
        return Math.Min(1, Math.Max(0, pose.Arrival));
    }

    /// <summary>
    /// 0…1: how far a held line (a duet part or ad-lib still sung after the next line started) has moved aside, from
    /// that line's start; null for a line that is not held. Not for rolling or stage-filling layouts.
    /// </summary>
    public static double? HeldShift(int row, IReadOnlyList<LyricLine> lines, double time)
    {
        if (!LyricPhraseTiming.IsHeld(row, lines)) return null;
        return LyricMotion.Settle(time - lines[row + 1].Start, HeldShiftDuration);
    }

    /// <summary>The previewed first word of a line-timed line as the line arrives: never fainter than the preview it takes over from.</summary>
    public static double HeldFirstRunOpacity(double presence) => Math.Max(0.25, presence);

    /// <summary>0…1: how far a line's preview has ramped in.</summary>
    public static double PreviewLevel(LyricLine line, double time, double since, double lead)
    {
        if (LyricPhraseTiming.FirstWordOnset(line) is not { } onset) return 0;
        return WordReveal.Visibility(time, onset, onset + 1, true, true, lead) / 0.25
            * Easing.Smoothstep((time - since) / PreviewRamp);
    }

    /// <summary>
    /// The line whose first word previews over line <paramref name="index"/> at <paramref name="time"/>, or null: the
    /// next line, when it is sung with the same timing, is not a stage-rolling continuation, starts within the preview
    /// lead, its first word is a single word, and nothing of the current line is still to be sung.
    /// </summary>
    public static int? NextLinePreview(int index, LyricSheet sheet, double time, StageRules rules)
    {
        var lines = sheet.Lines;
        var next = index + 1;
        if (index < 0 || next >= lines.Count || lines[next].IsInstrumental || ChangesTiming(lines[index], lines[next])
            || !(time < lines[next].Start)) return null;
        if (!rules.PreviewsNextWord || (lines[next].WordTimed && !rules.RevealsWords)) return null;
        if (lines[index].WordTimed && lines[next].WordTimed && rules.RollsWordTimed) return null;
        if (!lines[next].WordTimed && rules.FillsStage) return null;
        // One preview at a time, even if two lines overlap in the source.
        if (lines[index].WordTimed)
        {
            var words = lines[index].Words;
            var shown = sheet.Visible[index];
            for (var i = 0; i < words.Count; i++)
                if (words[i].Start > time && shown[i]) return null;
        }
        if (LyricPhraseTiming.FirstWordOnset(lines[next]) is not { } onset || !(onset > time)
            || onset - time > rules.PreviewLead) return null;
        return sheet.FirstPreviewable[next] ? next : null;
    }

    /// <summary>
    /// The dimming a line-timed line had reached when the next line's preview handed over to that line, kept while
    /// it leaves (dropped at the onset, the leaving line brightened just as the new one appeared).
    /// </summary>
    public static double CarriedYield(int row, LyricSheet sheet, double time, StageRules rules)
    {
        var lines = sheet.Lines;
        if (row < 0 || row + 1 >= lines.Count || lines[row].WordTimed) return 0;
        var handOver = Math.BitDecrement(lines[row + 1].Start);
        if (!(time > handOver) || NextLinePreview(row, sheet, handOver, rules) != row + 1) return 0;
        return PreviewLevel(lines[row + 1], handOver, sheet.PreviewSince[row], rules.PreviewLead);
    }

    /// <summary>Where a line's highlight releases: its end, or its last word's end if later.</summary>
    public static double HighlightLineEnd(double? end, IReadOnlyList<LyricWord> words)
    {
        var result = end ?? double.NegativeInfinity;
        for (var i = 0; i < words.Count; i++) result = Math.Max(result, words[i].End);
        return result;
    }
}

/// <summary>How a line is fitted to the stage (<see cref="LyricStageFit.PresentationScale"/>).</summary>
public readonly record struct LineFit(SizeD Available, double UserScale, double Neutral)
{
    public double ScaleFor(SizeD size) => LyricStageFit.PresentationScale(size, Available, UserScale, Neutral);
}

/// <summary>One drawn line as the stack places it.</summary>
/// <param name="Size">The line's size before it is fitted.</param>
/// <param name="IsLine">False for something that is not a line (it stays centred and is ignored).</param>
/// <param name="Fit">How it is fitted.</param>
/// <param name="Presence">How present it is in the middle of the stage (entrance and exit, 0…1); null for a preview, which never pushes a held line about.</param>
/// <param name="Held">How far a held line has moved aside, 0…1; null for any other.</param>
public readonly record struct StackedLine(SizeD Size, bool IsLine, LineFit Fit, double? Presence, double? Held);

/// <summary>
/// The lines on a stage, each centred, except a held line, which rises clear above the lines in the middle by their
/// measured, fitted height, as far as the top of the stage allows, at 70% of its size.
/// </summary>
public static class LineStack
{
    /// <summary>How much smaller a held line is once it has moved aside.</summary>
    public const double HeldShrink = 0.3;

    /// <summary>
    /// Each line's vertical offset from the middle of the stage into <paramref name="offsets"/>: zero for all but held
    /// lines, which stack upwards (the latest nearest the middle) above the tallest line there, weighted by how present
    /// it is, and never past the top of the stage. Allocation-free.
    /// </summary>
    public static void Offsets(ReadOnlySpan<StackedLine> items, double stageHeight, double gap, Span<double> offsets)
    {
        static double Extent(in StackedLine item) => item.IsLine ? item.Size.Height * item.Fit.ScaleFor(item.Size) : 0;
        double middle = 0;
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].Held is not null || items[i].Presence is not { } presence) continue;
            middle = Math.Max(middle, Extent(items[i]) / 2 * Math.Min(1, Math.Max(0, presence)));
        }
        var top = -middle;
        for (var i = items.Length - 1; i >= 0; i--)
        {
            offsets[i] = 0;
            if (items[i].Held is not { } travel || !(travel > 0) || !items[i].IsLine) continue;
            var amount = Math.Min(1, travel);
            var half = Extent(items[i]) * (1 - HeldShrink * amount) / 2;
            var target = Math.Max(-stageHeight / 2 + half, top - gap - half);
            offsets[i] = Math.Min(0, target) * amount;
            top = offsets[i] - half;
        }
    }
}
