using System.Numerics;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Compositions;

/// <summary>
/// Where each letter (DirectWrite cluster) of a one-line text sits, in layout pixels from the centre of its layout box,
/// left to right: the Mac's glyph typographic bounds (advance by line height) for an action word's per-letter pass.
/// Whitespace takes no letter.
/// </summary>
internal sealed class InkLetters(float[] left, float[] right, float top, float baseline, float bottom)
{
    public float[] Left { get; } = left;
    public float[] Right { get; } = right;
    /// <summary>The line's top, baseline and foot.</summary>
    public float Top { get; } = top;
    public float Baseline { get; } = baseline;
    public float Bottom { get; } = bottom;
    public int Count => Left.Length;

    /// <summary>
    /// Every letter is one character, with no spaces or ligatures between them: letter k starts where character k does,
    /// so a share counted by character can be placed by letter (<see cref="Ink.Measured"/>).
    /// </summary>
    public bool OneCharacterEach { get; init; }
    /// <summary>The letters' span across.</summary>
    public float MinX => Left.Length > 0 ? Left[0] : 0;
    public float MaxX => Right.Length > 0 ? Right[^1] : 0;

    /// <summary>The letters of <paramref name="layout"/> (null unless it is one line with at least one letter).</summary>
    public static InkLetters? Measure(IDWriteTextLayout layout, TextMetrics metrics, Vector2 box)
    {
        if (metrics.LineCount != 1 || !(metrics.Width > 0)) return null;
        var lines = new LineMetrics[1];
        layout.GetLineMetrics(lines, out var lineCount);
        if (lineCount < 1) return null;
        // Asked once with no room for the count, then for the clusters themselves.
        layout.GetClusterMetrics([], out var clusterCount);
        if (clusterCount == 0) return null;
        var clusters = new ClusterMetrics[clusterCount];
        if (layout.GetClusterMetrics(clusters, out clusterCount).Failure) return null;
        var cx = metrics.Left + box.X / 2;
        var cy = metrics.Top + box.Y / 2;
        var spans = new List<(float Left, float Right)>(clusters.Length);
        uint position = 0;
        var single = true;
        foreach (var cluster in clusters)
        {
            if (!cluster.IsWhitespace && !cluster.IsNewline && cluster.Width > 0)
            {
                layout.HitTestTextPosition(position, false, out _, out _, out var hit);
                spans.Add((hit.Left - cx, hit.Left + hit.Width - cx));
                single &= cluster.Length == 1;
            }
            else
            {
                single = false;
            }
            position += cluster.Length;
        }
        if (spans.Count == 0) return null;
        spans.Sort((a, b) => a.Left.CompareTo(b.Left));
        return new InkLetters([.. spans.Select(s => s.Left)], [.. spans.Select(s => s.Right)],
                              metrics.Top - cy, metrics.Top + lines[0].Baseline - cy, metrics.Top + lines[0].Height - cy)
        {
            OneCharacterEach = single,
        };
    }
}
