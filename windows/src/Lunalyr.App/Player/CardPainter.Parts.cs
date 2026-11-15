using System.Numerics;
using Vortice.Direct2D1;

namespace Lunalyr.App.Player;

// The card's parts as last drawn (for the pointer, the keyboard's focus and UI Automation) and its Segoe Fluent Icons
// glyphs. (The Mac's card also has search and the queue in its right-hand column; Windows has no player search: the
// owner removed it, 14 Nov 2026.)
internal sealed partial class CardPainter
{
    /// <summary>The parts drawn this frame that come and go (the film button, align mode's), with where they are.</summary>
    public List<(CardPart Part, RectF Rect)> Hits { get; } = [];

    /// <summary>Where a part is, in DIPs: the fixed ones from the layout, the rest as last drawn.</summary>
    public RectF PartRect(CardPart part)
    {
        switch (part)
        {
            case CardPart.Cover: return CardLayout.CoverRect;
            case CardPart.Previous: return CardLayout.PreviousRect;
            case CardPart.PlayPause: return CardLayout.PlayRect;
            case CardPart.Next: return CardLayout.NextRect;
            case CardPart.SeekBar:
                return new RectF(CardLayout.ColumnLeft, CardLayout.SeekRowTop, CardLayout.ColumnWidth, CardLayout.BarBand);
            case CardPart.TimeLabel:
                return new RectF(CardLayout.TimesRect.Right - 64, CardLayout.TimesRect.Y, 64, CardLayout.TimesHeight);
        }

        foreach (var (hit, rect) in Hits)
            if (hit == part) return rect;
        return default;
    }

    /// <summary>A Segoe Fluent Icons glyph centred in <paramref name="box"/>.</summary>
    private void DrawGlyph(ID2D1DeviceContext dc, string glyph, RectF box, Vortice.Mathematics.Color4 colour, float size = 13)
    {
        var layout = Layout(glyph, TextStyle.Glyph, 64);
        var scale = size / 13f;
        var metrics = layout.Metrics;
        var x = box.CentreX - metrics.Width * scale / 2;
        var y = box.CentreY - metrics.Height * scale / 2;
        if (Math.Abs(scale - 1) < 0.01f)
        {
            DrawText(dc, layout, x, y, colour);
            return;
        }

        var previous = dc.Transform;
        dc.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(x, y) * previous;
        DrawText(dc, layout, 0, 0, colour);
        dc.Transform = previous;
    }
}
