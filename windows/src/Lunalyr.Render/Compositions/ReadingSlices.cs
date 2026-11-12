using Lunalyr.Core.Lyrics.Readings;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Compositions;

/// <summary>
/// Where each share of a baked reading falls along it (<see cref="ReadingShares"/>), so the reading fills in share by
/// share as its tokens are sung (the Mac's <c>LyricReadingInk.revealing</c> and <c>StageLyricView.revealedReading</c>).
/// The reading is baked whole, once: a share not shown yet is simply not drawn, so the reading keeps its room and
/// never shifts as it fills in. Measured once per line; the frames use its scratch, so they allocate nothing.
/// </summary>
internal sealed class ReadingSlices
{
    private readonly double[] _values;
    private readonly double[] _syllables;

    private ReadingSlices(ReadingShares shares, double[] boundaries, int[] owners)
    {
        Shares = shares;
        Boundaries = boundaries;
        Owners = owners;
        _values = new double[owners.Length];
        _syllables = new double[owners.Length];
    }

    public ReadingShares Shares { get; }
    /// <summary>Where each share begins and ends across the reading's layout box, 0…1 (shares + 1 values).</summary>
    public double[] Boundaries { get; }
    /// <summary>Per share, the share whose opacity it is drawn at: its own, or for an empty share (no width, but the
    /// first and last slices reach past the box) its nearest neighbour with text.</summary>
    public int[] Owners { get; }

    /// <summary>Scratch for each token's opacity relative to the run's first (one per share).</summary>
    public Span<double> Syllables => _syllables;

    /// <summary>
    /// Measures where each share of <paramref name="shares"/> falls along <paramref name="layout"/>, the laid-out
    /// <paramref name="text"/>, by hit-testing where each share begins. Null when there is nothing to reveal by share,
    /// when the text is not the shares written together, or when the reading wraps (it then shows whole).
    /// </summary>
    public static ReadingSlices? Measure(IDWriteTextLayout layout, string? text, ReadingShares? shares)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (shares is not { Reveals: true } || text is null || shares.Text != text) return null;
        var metrics = layout.Metrics;
        if (metrics.LineCount != 1 || !(metrics.Width > 0)) return null;
        var count = shares.Shares.Count;
        var boundaries = new double[count + 1];
        var offset = 0;
        for (var k = 0; k < count; k++)
        {
            if (k > 0)
            {
                double fraction = 1;
                if (offset < text.Length)
                {
                    layout.HitTestTextPosition((uint)offset, false, out var x, out _, out _);
                    fraction = (x - metrics.Left) / metrics.Width;
                }
                boundaries[k] = Math.Clamp(Math.Max(fraction, boundaries[k - 1]), 0, 1);
            }
            offset += shares.Shares[k].Text.Length;
        }
        boundaries[count] = 1;
        var owners = new int[count];
        for (var k = 0; k < count; k++)
        {
            owners[k] = k;
            if (shares.Shares[k].Text.Length > 0) continue;
            var before = k - 1;
            while (before >= 0 && shares.Shares[before].Text.Length == 0) before--;
            var after = k + 1;
            while (after < count && shares.Shares[after].Text.Length == 0) after++;
            owners[k] = before >= 0 ? before : after < count ? after : k;
        }
        return new ReadingSlices(shares, boundaries, owners);
    }

    /// <summary>
    /// Each share's opacity at <paramref name="time"/> (<see cref="ReadingShares.Opacity"/>), or empty once every share
    /// shows (the reading is then drawn whole).
    /// </summary>
    public ReadOnlySpan<double> Opacities(double time, ReadOnlySpan<double> syllables, bool reduceMotion)
    {
        var whole = true;
        for (var k = 0; k < _values.Length; k++)
        {
            var value = Shares.Opacity(Owners[k], time, syllables, reduceMotion);
            _values[k] = value;
            whole &= value >= 0.999;
        }
        return whole ? default : _values;
    }

    /// <summary>
    /// Each share at its part's opacity (a stage run's <c>StageRun.PartOpacity</c>, one per part), or empty once every
    /// share shows.
    /// </summary>
    public ReadOnlySpan<double> FromParts(ReadOnlySpan<double> parts)
    {
        if (parts.Length < _values.Length) return default;
        var whole = true;
        for (var k = 0; k < _values.Length; k++)
        {
            var value = parts[Owners[k]];
            _values[k] = value;
            whole &= value >= 0.999;
        }
        return whole ? default : _values;
    }
}
