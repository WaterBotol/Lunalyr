using System.Numerics;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Core.Player;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Lunalyr.App.Player;

// Align mode on the card, drawn as the Mac's VideoAlignRow and VideoAlignControls (Lunalyr's own code, re-drawn for
// Direct2D): the video's whole length as the track, the song's span over it in the album's colour with a tick at each
// end, the knob where the video is now, the offset in a capsule over the knob ("Video +3.4 s") with its detail under it;
// under the bar the video's time, the keys and its length; and Reset, Cancel and Done in place of the transport.
internal sealed partial class CardPainter
{
    private const string GlyphFilm = "", GlyphReset = "";

    /// <summary>The bar's thickness at rest (the Mac's 7 pt), two more while dragged.</summary>
    private const float AlignThickness = 7;

    private void DrawAlign(ID2D1DeviceContext dc, CardModel m)
    {
        if (m.Align is not { } display) return;
        var t = m.Theme;
        var left = CardLayout.ColumnLeft;
        var width = CardLayout.ColumnWidth;
        var centre = CardLayout.BarCentre;
        var layout = display.Layout(width);
        var knob = left + (float)layout.Knob;
        var spanStart = left + (float)layout.SpanStart;
        var spanEnd = left + (float)layout.SpanEnd;
        var thickness = display.Dragging ? AlignThickness + 2 : AlignThickness;
        var alpha = display.Available ? 1f : 0.6f;
        var y = centre - thickness / 2;

        // The whole video, the video so far lightly, then the song inside it.
        FillRounded(dc, new RectF(left, y, width, thickness), thickness / 2,
            WithAlpha(t.HighContrast ? t.Disabled : t.Ink, (t.HighContrast ? 1 : m.Look.Light ? 0.3f : 0.22f) * alpha));
        if (knob > left) FillRounded(dc, new RectF(left, y, knob - left, thickness), thickness / 2, WithAlpha(t.Ink, 0.18f * alpha));
        var span = t.HighContrast ? t.Highlight : t.Accent;
        FillRounded(dc, new RectF(spanStart, y, Math.Max(2, spanEnd - spanStart), thickness), thickness / 2,
            WithAlpha(span, (display.Dragging ? 0.95f : 0.75f) * alpha));
        foreach (var edge in new[] { spanStart, spanEnd })
            Fill(dc, new Vortice.RawRectF(edge - 0.5f, centre - 8, edge + 0.5f, centre + 8), WithAlpha(t.Ink, 0.7f * alpha));
        var knobRadius = display.Dragging ? 7.5f : 6.5f;
        if (!t.HighContrast) Fill(dc, new Ellipse(new Vector2(knob, centre + 1), knobRadius + 1.5f, knobRadius + 1.5f), new Color4(0, 0, 0, 0.25f));
        Fill(dc, new Ellipse(new Vector2(knob, centre), knobRadius, knobRadius), t.HighContrast ? t.Ink : m.Look.Light ? t.Ink : new Color4(1, 1, 1, 1));
        if (display.Dragging) StrokeEllipse(dc, new Ellipse(new Vector2(knob, centre), knobRadius - 1, knobRadius - 1), span, 2);
        Hits.Add((CardPart.AlignBar, new RectF(left, CardLayout.SeekRowTop, width, CardLayout.BarBand)));

        // Under the bar: where the video is, the keys, its length.
        var times = CardLayout.TimesRect;
        var timeColour = Ink(m, PlayerInk.Secondary);
        DrawText(dc, Layout(ScrubMath.Timestamp(display.VideoTime), TextStyle.Time, 100), times.X, times.Y, timeColour);
        var length = Layout(ScrubMath.Timestamp(display.VideoLength), TextStyle.Time, 100);
        DrawText(dc, length, times.Right - length.Metrics.Width, times.Y, timeColour);
        var hint = Layout(MusicVideoAlignDisplay.Hint, TextStyle.RowDetail, (int)width - 90);
        DrawText(dc, hint, times.CentreX - hint.Metrics.Width / 2, times.Y, Ink(m, PlayerInk.Tertiary));

        // Over the knob: the offset the release keeps, and what it means (12 by 8 inside its capsule).
        var title = Layout(display.Title, TextStyle.AlignTitle, 200);
        var detail = Layout(display.Detail, TextStyle.Rate, 300);
        var boxWidth = Math.Max(title.Metrics.Width, detail.Metrics.Width) + 24;
        var boxHeight = title.Metrics.Height + 1 + detail.Metrics.Height + 16;
        var box = Clamp(new RectF(knob - boxWidth / 2, CardLayout.SeekRowTop - 4 - boxHeight, boxWidth, boxHeight), left, width);
        Capsule(dc, m, box, 1);
        DrawText(dc, title, box.CentreX - title.Metrics.Width / 2, box.Y + 8, Ink(m, PlayerInk.Primary));
        DrawText(dc, detail, box.CentreX - detail.Metrics.Width / 2, box.Y + 8 + title.Metrics.Height + 1, Ink(m, PlayerInk.Secondary));

        // The row under it: Reset (label type; quaternary while there is nothing to reset), Cancel, Done.
        var canReset = display.Origin == MusicVideoOffsetOrigin.Manual;
        var reset = CardLayout.AlignResetRect;
        var resetInk = Ink(m, !canReset ? PlayerInk.Quaternary : m.HoverPart == CardPart.AlignReset ? PlayerInk.Primary : PlayerInk.Secondary);
        var resetText = Layout("Reset", TextStyle.Label, 80);
        DrawGlyph(dc, GlyphReset, new RectF(reset.X, reset.Y, 18, reset.Height), resetInk, 12);
        DrawText(dc, resetText, reset.X + 22, reset.CentreY - resetText.Metrics.Height / 2, resetInk);
        if (canReset) Hits.Add((CardPart.AlignReset, reset));
        var cancel = CardLayout.AlignCancelRect;
        var cancelText = Layout("Cancel", TextStyle.ButtonMedium, 80);
        if (m.HoverPart == CardPart.AlignCancel && !t.HighContrast) FillRounded(dc, cancel, 16, WithAlpha(t.Ink, F(PlayerTokens.Fill.Hover)));
        DrawText(dc, cancelText, cancel.CentreX - cancelText.Metrics.Width / 2, cancel.CentreY - cancelText.Metrics.Height / 2,
            Ink(m, m.HoverPart == CardPart.AlignCancel ? PlayerInk.Primary : PlayerInk.Secondary));
        Hits.Add((CardPart.AlignCancel, cancel));
        var done = CardLayout.AlignDoneRect;
        var doneFill = t.Ink;
        if (m.HoverPart == CardPart.AlignDone && !t.HighContrast) doneFill = Darken(doneFill, m.Look.Light ? -0.12f : 0.06f);
        FillRounded(dc, done, 16, doneFill);
        var doneText = Layout("Done", TextStyle.Button, 80);
        DrawText(dc, doneText, done.CentreX - doneText.Metrics.Width / 2, done.CentreY - doneText.Metrics.Height / 2, t.InkInverse);
        Hits.Add((CardPart.AlignDone, done));
    }

    /// <summary>The film button beside search, lit in align mode, dimmed while the video can't be lined up.</summary>
    private void DrawAlignButton(ID2D1DeviceContext dc, CardModel m)
    {
        var rect = CardLayout.AlignButtonRect;
        var t = m.Theme;
        var on = m.Align is not null;
        var hovered = m.HoverPart == CardPart.AlignButton;
        var halo = on || m.PressedPart == CardPart.AlignButton ? F(PlayerTokens.Fill.Selected) : hovered ? F(PlayerTokens.Fill.Hover) : 0;
        if (halo > 0) Fill(dc, new Ellipse(new Vector2(rect.CentreX, rect.CentreY), ButtonSize / 2, ButtonSize / 2), WithAlpha(t.Ink, halo));
        // Lit in the album's accent (a state); dimmed to quaternary while the video can't be lined up.
        var ink = Ink(m, m.AlignReason is not null ? PlayerInk.Quaternary : hovered ? PlayerInk.Primary : PlayerInk.Secondary);
        DrawGlyph(dc, GlyphFilm, rect, on ? (t.HighContrast ? t.Highlight : t.Accent) : ink, F(PlayerTokens.Glyph.Icon));
        Hits.Add((CardPart.AlignButton, rect));
    }
}
