using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Compositions;

/// <summary>
/// One synced lyric result, prepared once for drawing: the line index, the vocal windows, and per line the facts the
/// stage asks every frame (glue, which words are drawn, whether the first word may be previewed). Built when the
/// lyrics change, so nothing per frame has to allocate.
/// </summary>
public sealed class LyricSheet
{
    private LyricSheet(IReadOnlyList<LyricLine> lines, VocalWindow[] windows, bool[][] glue, bool[][] visible,
                       double[] previewSince, bool[] previewable)
    {
        Lines = lines;
        Index = new LyricLineIndex(lines);
        VocalWindows = windows;
        Glue = glue;
        Visible = visible;
        PreviewSince = previewSince;
        FirstPreviewable = previewable;
    }

    public IReadOnlyList<LyricLine> Lines { get; }
    public LyricLineIndex Index { get; }

    /// <summary>When singing is heard, for the instrumental-break cue.</summary>
    public VocalWindow[] VocalWindows { get; }

    /// <summary>Per line, per word: continues the previous word's run (<see cref="LyricRuns.Glue(LyricLine, IReadOnlyList{LyricWord})"/>).</summary>
    public bool[][] Glue { get; }

    /// <summary>Per line, per word: drawn at all (<see cref="LyricRuns.IsVisible"/>).</summary>
    public bool[][] Visible { get; }

    /// <summary>
    /// Per line: when the next line may first be previewed — once the line's last drawn word has begun (word-timed),
    /// or from the line's start.
    /// </summary>
    public double[] PreviewSince { get; }

    /// <summary>
    /// Per line: whether its first word may be previewed — a single word, not a phrase sung as one (word-timed: the
    /// first drawn word; line-timed: the first run, when the line has more than one).
    /// </summary>
    public bool[] FirstPreviewable { get; }

    public int Count => Lines.Count;

    /// <param name="lines">The synced lines, as the lyrics model presents them.</param>
    /// <param name="infersPauses">"Treat long pauses as instrumental" for line-only lyrics (not QQ Music's, which are split instead).</param>
    public static LyricSheet Build(IReadOnlyList<LyricLine> lines, bool infersPauses = true)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var count = lines.Count;
        var glue = new bool[count][];
        var visible = new bool[count][];
        var since = new double[count];
        var previewable = new bool[count];
        for (var row = 0; row < count; row++)
        {
            var line = lines[row];
            var words = line.Words;
            glue[row] = [.. LyricRuns.Glue(line, words)];
            var shown = new bool[words.Count];
            for (var i = 0; i < words.Count; i++) shown[i] = LyricRuns.IsVisible(words[i].Text);
            visible[row] = shown;
            since[row] = line.Start;
            if (line.WordTimed)
            {
                for (var i = words.Count - 1; i >= 0; i--)
                {
                    if (shown[i]) { since[row] = words[i].Start; break; }
                }
                var first = Array.IndexOf(shown, true);
                previewable[row] = first >= 0 && PreviewWords.IsPreviewWord(words[first].Text);
            }
            else
            {
                previewable[row] = LyricRunParts.CanPreviewFirstRun(words, glue[row]);
            }
        }
        VocalWindow[] windows;
        try { windows = LyricGapTiming.Windows(lines, infersPauses); }
        catch (ArgumentException) { windows = []; }
        return new LyricSheet(lines, windows, glue, visible, since, previewable);
    }
}
