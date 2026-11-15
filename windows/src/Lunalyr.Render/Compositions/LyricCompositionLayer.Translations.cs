// Translations under the lyrics (Show translations under lyrics; the Mac's LyricLayerView, Lunalyr's own lines): a
// line's translation from the lyric source sits under the line (and its reading) in the reading colour, arriving and
// leaving with it. Line plans and stage lines set it with the line; a rolling stage keeps one size from line to line,
// so there it rests over the stage's foot, in a band the stage keeps clear (SpokenLayer), up to two lines.

using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Motion;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Compositions;

internal abstract partial class LyricCompositionLayer
{
    private readonly Dictionary<int, Ink?> _rollingTranslations = [];
    private LyricSheet? _translatedSheet;
    private bool _sheetTranslated;

    private void InvalidateTranslations()
    {
        foreach (var ink in _rollingTranslations.Values) ink?.Dispose();
        _rollingTranslations.Clear();
        _translatedSheet = null;
        _sheetTranslated = false;
    }

    /// <summary>Line <paramref name="row"/>'s translation when translations are shown and it has one; null otherwise.</summary>
    protected string? TranslationOf(int row)
    {
        if (SceneShown is not { Style.Translations: true } || Sheet is not { } sheet || row < 0 || row >= sheet.Lines.Count) return null;
        return LineTranslations.Of(sheet.Lines[row]);
    }

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
            ink = TranslationOf(row) is { } text ? BakeRollingTranslation(text, InkAt(Sheet!.Lines[row].Start).Base.Reading) : null;
            _rollingTranslations[row] = ink;
            if (_rollingTranslations.Count > 6) TrimRollingTranslations(row);
        }
        if (ink is null) return;
        var origin = StageOrigin;
        var bottom = origin.Y + Stage.Height - BaseSize * LineTranslations.Gap;
        var world = Matrix3x2.CreateTranslation(origin.X + (float)(Stage.Width / 2), (float)(bottom - ink.Box.Y / 2));
        ink.Draw(dc, world, 1, (float)opacity, 0, GlowSpec.None, 1);
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

    private Ink BakeRollingTranslation(string text, Lunalyr.Core.Colour.SrgbColour colour)
    {
        Meter.Enter();
        try
        {
            var size = (float)(BaseSize * LineTranslations.RollingSize * Metrics.UserScale);
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
