// Translations (Show translations; the Mac's LyricLayerView, Lunalyr's own lines): a line's translation from the lyric
// source sits under the line (and its reading) in the reading colour, or over it, or at the stage's foot or head (Translation
// sits, the Mac's 5783d638), arriving and leaving with the line. Line plans and stage lines set it with the line; at an
// edge it is drawn there, lines handing over sharing the place and cross-fading. A rolling stage keeps one size from line
// to line, so there it rests in a band the stage keeps clear (SpokenLayer) at its foot, or at its head when the
// translation sits over the words, up to two lines.

using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Motion;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Compositions;

internal abstract partial class LyricCompositionLayer
{
    private readonly Dictionary<int, Ink?> _rollingTranslations = [];
    private readonly Dictionary<int, Ink?> _edgeTranslations = [];
    private LyricSheet? _translatedSheet;
    private bool _sheetTranslated;

    private void InvalidateTranslations()
    {
        foreach (var ink in _rollingTranslations.Values) ink?.Dispose();
        _rollingTranslations.Clear();
        foreach (var ink in _edgeTranslations.Values) ink?.Dispose();
        _edgeTranslations.Clear();
        _translatedSheet = null;
        _sheetTranslated = false;
    }

    /// <summary>Line <paramref name="row"/>'s translation when translations are shown and it has one; null otherwise.</summary>
    protected string? TranslationOf(int row)
    {
        if (SceneShown is not { Style.Translations: true } || Sheet is not { } sheet || row < 0 || row >= sheet.Lines.Count) return null;
        return LineTranslations.Of(sheet.Lines[row]);
    }

    /// <summary>Where a shown translation sits (Translation sits): under its line unless the style says otherwise.</summary>
    protected TranslationPlacement TranslationPlace => SceneShown?.Style.TranslationPlacement ?? TranslationPlacement.Below;

    /// <summary>Line <paramref name="row"/>'s translation as set with its line (under or over it); null when it sits at an edge.</summary>
    protected string? LineTranslationOf(int row) => TranslationPlace.AtEdge() ? null : TranslationOf(row);

    /// <summary>
    /// The band a rolling stage keeps clear at its foot (the Mac's <c>bottomInset</c>): kept whenever translations are
    /// shown and the song has any, so the stage's words never change size from line to line. Zero otherwise.
    /// </summary>
    protected double TranslationBand
    {
        get
        {
            if (SceneShown is not { Style.Translations: true } || Sheet is not { } sheet) return 0;
            if (!ReferenceEquals(sheet, _translatedSheet))
            {
                _translatedSheet = sheet;
                _sheetTranslated = LineTranslations.Any(sheet.Lines);
            }
            return LineTranslations.RollingBand(BaseSize, Metrics.UserScale, _sheetTranslated);
        }
    }

    /// <summary>
    /// Line <paramref name="row"/>'s translation over a rolling stage's foot, centred, at most two lines (the Mac's
    /// overlay with <c>lineLimit(2)</c>), a little above the stage's bottom edge.
    /// </summary>
    private void DrawRollingTranslation(ID2D1DeviceContext dc, int row, double opacity)
    {
        if (!(opacity > 0.002) || TranslationBand <= 0) return;
        if (!_rollingTranslations.TryGetValue(row, out var ink))
        {
            ink = TranslationOf(row) is { } text ? BakeRollingTranslation(text, InkAt(Sheet!.Lines[row].Start).Base.Reading, LineTranslations.RollingSize) : null;
            _rollingTranslations[row] = ink;
            if (_rollingTranslations.Count > 6) TrimRollingTranslations(row);
        }
        if (ink is null) return;
        var origin = StageOrigin;
        // At the head when the translation sits over the words (the words move down by the band), else at the foot.
        var y = TranslationPlace.OverWords()
            ? origin.Y + BaseSize * LineTranslations.Gap + ink.Box.Y / 2
            : origin.Y + Stage.Height - BaseSize * LineTranslations.Gap - ink.Box.Y / 2;
        var world = Matrix3x2.CreateTranslation(origin.X + (float)(Stage.Width / 2), (float)y);
        ink.Draw(dc, world, 1, (float)opacity, 0, GlowSpec.None, 1);
    }

    /// <summary>
    /// Line <paramref name="row"/>'s translation at the stage's foot or head (Bottom or Top of the screen), at most two
    /// lines, at its line's opacity: lines overlapping in a hand-over share the place and cross-fade (the Mac's
    /// EdgeTranslations).
    /// </summary>
    protected void DrawEdgeTranslation(ID2D1DeviceContext dc, int row, double opacity)
    {
        if (!(opacity > 0.002) || !TranslationPlace.AtEdge() || Sheet is null) return;
        if (!_edgeTranslations.TryGetValue(row, out var ink))
        {
            ink = TranslationOf(row) is { } text
                ? BakeRollingTranslation(text, InkAt(Sheet.Lines[row].Start).Base.Reading, LineTranslations.LineSize)
                : null;
            _edgeTranslations[row] = ink;
            if (_edgeTranslations.Count > 6) TrimEdgeTranslations(row);
        }
        if (ink is null) return;
        var origin = StageOrigin;
        var inset = BaseSize * LineTranslations.EdgeInset;
        var y = TranslationPlace == TranslationPlacement.Top
            ? origin.Y + inset + ink.Box.Y / 2
            : origin.Y + Stage.Height - inset - ink.Box.Y / 2;
        var world = Matrix3x2.CreateTranslation(origin.X + (float)(Stage.Width / 2), (float)y);
        ink.Draw(dc, world, 1, (float)opacity, 0, GlowSpec.None, 1);
    }

    private void TrimEdgeTranslations(int row)
    {
        foreach (var old in _edgeTranslations.Keys.Where(key => Math.Abs(key - row) > 2).ToList())
        {
            _edgeTranslations[old]?.Dispose();
            _edgeTranslations.Remove(old);
        }
    }

    /// <summary>Keeps only the translations of the lines around <paramref name="row"/> (a method of its own, so the frames
    /// that draw one allocate nothing for this).</summary>
    private void TrimRollingTranslations(int row)
    {
        foreach (var old in _rollingTranslations.Keys.Where(key => Math.Abs(key - row) > 2).ToList())
        {
            _rollingTranslations[old]?.Dispose();
            _rollingTranslations.Remove(old);
        }
    }

    private Ink BakeRollingTranslation(string text, Lunalyr.Core.Colour.SrgbColour colour, double share)
    {
        Meter.Enter();
        try
        {
            var size = (float)(BaseSize * share * Metrics.UserScale);
            var width = (float)(Stage.Width * LineTranslations.Width);
            var rightToLeft = ScriptDirection.IsRightToLeft(text);
            var layout = Kit!.Layout(text, Face.Quiet, size, TextKit.LocaleFor(text), rightToLeft, width);
            if (layout.Metrics.LineCount > LineTranslations.RollingLines)
            {
                // Cut after the second line, as the Mac's two-line limit does, and say so with an ellipsis.
                var lines = layout.LineMetrics;
                var kept = 0;
                for (var i = 0; i < LineTranslations.RollingLines; i++) kept += (int)lines[i].Length;
                layout.Dispose();
                var shortened = text[..Math.Min(kept, text.Length)].TrimEnd() + "…";
                layout = Kit.Layout(shortened, Face.Quiet, size, TextKit.LocaleFor(shortened), rightToLeft, width);
            }
            using (layout)
                return Kit.Bake(layout, LyricInks.Vector(colour), 1, new ShadowSpec(0.3f, Points(6) * 0.5f, Points(2)));
        }
        finally
        {
            Meter.Exit();
        }
    }
}
