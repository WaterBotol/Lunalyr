using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Player;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.App.Player;

/// <summary>
/// Draws the "Sleeve" card from a <see cref="CardModel"/> with Direct2D and DirectWrite, in DIPs, from the player's
/// tokens (<see cref="PlayerTokens"/>, docs/DESIGN.md): a flat album tint, four ink levels, three type weights, edges
/// only in a contrast theme. Device resources (brushes, the cover bitmap) are made once and kept until the device
/// changes; text layouts are kept per string. A frame allocates only when a string it shows changes (the times, once a
/// second).
/// </summary>
internal sealed partial class CardPainter : IDisposable
{
    private const string Display = "Segoe UI Variable Display", Text = "Segoe UI Variable Text";

    private readonly IDWriteFactory _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
    private readonly Dictionary<(string Text, TextStyle Style, int MaxWidth), IDWriteTextLayout> _layouts = [];
    private readonly Dictionary<TextStyle, IDWriteTextFormat> _formats = [];
    private IDWriteTypography? _tabular;

    // Device resources.
    private ID2D1DeviceContext? _dc;
    private ID2D1Factory1? _factory;
    private ID2D1SolidColorBrush? _brush;
    private ID2D1RadialGradientBrush? _vinyl, _soft;
    private ID2D1GradientStopCollection? _vinylStops, _softStops;
    private ID2D1Bitmap1? _cover;
    private CoverArt? _coverSource;
    private ID2D1RoundedRectangleGeometry? _coverClip;
    private ID2D1PathGeometry? _playGlyph, _skipGlyph;

    private enum TextStyle
    {
        Label, Title, TitleSmall, Artist, Album, Time, CapsuleLyric, CapsuleReading, CapsuleTime, Rate, Note, Live,
        RowDetail, RowTag, Glyph, Button, ButtonMedium, AlignTitle,
    }

    /// <summary>Lets go of everything made on the old device (after a device loss or a new card surface).</summary>
    public void ReleaseDevice()
    {
        _brush?.Dispose(); _brush = null;
        _vinyl?.Dispose(); _vinyl = null; _vinylStops?.Dispose(); _vinylStops = null;
        _soft?.Dispose(); _soft = null; _softStops?.Dispose(); _softStops = null;
        _cover?.Dispose(); _cover = null; _coverSource = null;
        _coverClip?.Dispose(); _coverClip = null;
        _playGlyph?.Dispose(); _playGlyph = null;
        _skipGlyph?.Dispose(); _skipGlyph = null;
        _wedge?.Dispose(); _wedge = null;
        _dc = null;
    }

    public void Dispose()
    {
        ReleaseDevice();
        foreach (var layout in _layouts.Values) layout.Dispose();
        _layouts.Clear();
        foreach (var format in _formats.Values) format.Dispose();
        _formats.Clear();
        _tabular?.Dispose();
        _dwrite.Dispose();
    }

    /// <summary>The card's height in DIPs at rest (the Song pane).</summary>
    public static float Height => (float)PlayerCard.RestHeight;

    // ---- Frame ----

    public void Paint(ID2D1DeviceContext dc, ID2D1Factory1 factory, CardModel m, float width, float height)
    {
        if (!ReferenceEquals(dc, _dc))
        {
            ReleaseDevice();
            _dc = dc;
            _factory = factory;
        }

        Prepare(dc, m);
        dc.Transform = Matrix3x2.Identity;
        dc.Clear(new Color4(0, 0, 0, 0));
        var t = m.Theme;
        var whole = new RawRectF(0, 0, width, height);
        var reveal = Math.Clamp(m.Reveal, 0, 1);
        var layered = reveal < 0.999f;
        if (layered) dc.PushLayer(new LayerParameters1 { ContentBounds = whole, MaskTransform = Matrix3x2.Identity, Opacity = reveal }, null!);

        Hits.Clear();
        DrawBackground(dc, m, width, height);
        DrawArt(dc, m);
        DrawColumn(dc, m);
        if (m.FocusVisible) DrawFocus(dc, m);
        // A contrast theme's firm edge, inside DWM's 8 px corners (no edge in any other look).
        if (t.HighContrast) Stroke(dc, new RawRectF(1, 1, width - 1, height - 1), 6, t.Hairline, 2);

        if (layered) dc.PopLayer();
    }

    private void Prepare(ID2D1DeviceContext dc, CardModel m)
    {
        _brush ??= dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        if (_vinyl is null)
        {
            _vinylStops = dc.CreateGradientStopCollection([new GradientStop(0, Grey(0.17f)), new GradientStop(1, Grey(0.03f))]);
            _vinyl = dc.CreateRadialGradientBrush(new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1, 1), _vinylStops);
            _softStops = dc.CreateGradientStopCollection([new GradientStop(0, new Color4(0, 0, 0, 1)), new GradientStop(0.55f, new Color4(0, 0, 0, 0.45f)), new GradientStop(1, new Color4(0, 0, 0, 0))]);
            _soft = dc.CreateRadialGradientBrush(new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1, 1), _softStops);
        }

        if (!ReferenceEquals(_coverSource, m.Cover))
        {
            _cover?.Dispose();
            _cover = null;
            _coverSource = m.Cover;
            if (m.Cover is { } art)
            {
                unsafe
                {
                    fixed (byte* data = art.Pixels)
                        _cover = dc.CreateBitmap(new SizeI(art.Width, art.Height), (nint)data, (uint)(art.Width * 4),
                            new BitmapProperties1(new D2DPixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
                }
            }
        }

        _coverClip ??= _factory!.CreateRoundedRectangleGeometry(new RoundedRectangle(Rect(CardLayout.CoverRect), CoverRadius, CoverRadius));
        _playGlyph ??= Triangle(_factory!);
        _skipGlyph ??= DoubleTriangle(_factory!);
    }

    // ---- Background ----

    private void DrawBackground(ID2D1DeviceContext dc, CardModel m, float width, float height)
    {
        // The veil, so the ink always reads, then the album's colour as one flat tint: no gradient in chrome.
        var whole = new RawRectF(0, 0, width, height);
        Fill(dc, whole, m.Theme.Veil);
        Fill(dc, whole, m.Theme.Tint);
    }

    // ---- Art column ----

    private void DrawArt(ID2D1DeviceContext dc, CardModel m)
    {
        var t = m.Theme;
        var cover = CardLayout.CoverRect;
        if (m.HasSong)
        {
            // The record peeks out of the sleeve, sliding back in on pause.
            var size = CardLayout.RecordSize;
            var left = cover.X + CardLayout.RecordIn + (CardLayout.RecordOut - CardLayout.RecordIn) * m.RecordOut;
            var centre = new Vector2(left + size / 2, cover.Y + cover.Height / 2);
            DrawRecord(dc, m, centre, size / 2);
        }

        // The cover's one soft shadow, then the cover in its rounded corners.
        if (!t.HighContrast)
            SoftShadow(dc, new RectF(cover.X, cover.Y + F(PlayerTokens.Shadow.CoverY), cover.Width, cover.Height),
                F(PlayerTokens.Shadow.CoverBlur), F(PlayerTokens.Shadow.CoverOpacity));
        if (_cover is not null && m.HasSong)
        {
            dc.PushLayer(new LayerParameters1
            {
                ContentBounds = Rect(cover), GeometricMask = _coverClip, MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity, Opacity = 1,
            }, null!);
            var source = CoverSource(_cover);
            dc.DrawBitmap(_cover, Rect(cover), 1, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, source, null);
            dc.PopLayer();
        }
        else
        {
            FillRounded(dc, cover, CoverRadius, t.HighContrast ? t.Veil : WithAlpha(t.Ink, F(PlayerTokens.Fill.Placeholder)));
            DrawNote(dc, new Vector2(cover.CentreX, cover.CentreY), 20, Ink(m, PlayerInk.Quaternary));
        }

        // An edge only in a contrast theme.
        Stroke(dc, Rect(cover), CoverRadius, t.Hairline, 1);
    }

    private static RawRectF CoverSource(ID2D1Bitmap1 bitmap)
    {
        // Fill the square: a wide or tall cover is cropped to its middle.
        var size = bitmap.PixelSize;
        float w = size.Width, h = size.Height;
        var side = Math.Min(w, h);
        return new RawRectF((w - side) / 2, (h - side) / 2, (w + side) / 2, (h + side) / 2);
    }

    private void DrawRecord(ID2D1DeviceContext dc, CardModel m, Vector2 centre, float radius)
    {
        var t = m.Theme;
        if (t.HighContrast)
        {
            Fill(dc, new Ellipse(centre, radius, radius), t.Veil);
            StrokeEllipse(dc, new Ellipse(centre, radius, radius), t.Ink, 1.5f);
            StrokeEllipse(dc, new Ellipse(centre, radius * 0.33f, radius * 0.33f), t.Ink, 1.5f);
            return;
        }

        // Its shadow, offset to the right, as dark as the cover's.
        SoftShadow(dc, new Ellipse(new Vector2(centre.X + 3, centre.Y), radius + 6, radius + 6), F(PlayerTokens.Shadow.CoverOpacity));
        _vinyl!.Center = centre;
        _vinyl.RadiusX = radius;
        _vinyl.RadiusY = radius;
        dc.FillEllipse(new Ellipse(centre, radius, radius), _vinyl);
        // The grooves.
        for (var r = radius * 0.4f; r < radius * 0.98f; r += 3.5f)
            StrokeEllipse(dc, new Ellipse(centre, r, r), new Color4(1, 1, 1, 0.05f), 0.6f);
        // A sheen across it, as light catches the vinyl.
        _brush!.Color = new Color4(1, 1, 1, 0.07f);
        dc.FillGeometry(SheenWedge(centre, radius), _brush);
        // The label in the album's accent, flat, and the hole.
        var label = radius * 0.33f;
        Fill(dc, new Ellipse(centre, label, label), t.Accent);
        Fill(dc, new Ellipse(centre, 3, 3), Grey(0.05f));
    }

    private ID2D1PathGeometry? _wedge;
    private Vector2 _wedgeCentre;
    private float _wedgeRadius;

    // Two opposite slivers from the centre: the Mac's angular gradient, approximated.
    private ID2D1PathGeometry SheenWedge(Vector2 centre, float radius)
    {
        if (_wedge is not null && _wedgeCentre == centre && _wedgeRadius == radius) return _wedge;
        _wedge?.Dispose();
        _wedge = _factory!.CreatePathGeometry();
        using (var sink = _wedge.Open())
        {
            foreach (var angle in new[] { -0.6f, MathF.PI - 0.6f })
            {
                sink.BeginFigure(centre, FigureBegin.Filled);
                sink.AddLine(centre + radius * 0.96f * new Vector2(MathF.Cos(angle), MathF.Sin(angle)));
                sink.AddArc(new ArcSegment(centre + radius * 0.96f * new Vector2(MathF.Cos(angle + 0.5f), MathF.Sin(angle + 0.5f)),
                    new Size(radius * 0.96f, radius * 0.96f), 0, SweepDirection.Clockwise, ArcSize.Small));
                sink.EndFigure(FigureEnd.Closed);
            }

            sink.Close();
        }

        _wedgeCentre = centre;
        _wedgeRadius = radius;
        return _wedge;
    }

    // ---- Song column ----

    private void DrawColumn(ID2D1DeviceContext dc, CardModel m)
    {
        var t = m.Theme;
        var left = CardLayout.ColumnLeft;
        var width = CardLayout.ColumnWidth;

        // The player, small, over the song (or the note about a command, while it shows): a dot in the album's
        // colour and the name in secondary ink. Plain text: the Mac's chevron opens its search, which Windows hasn't.
        if (m.PlayerName.Length > 0 && m.NoteAlpha < 0.999f)
        {
            // As the player names itself, not tracked capitals (the design pass's "no ALL CAPS subtitles").
            var label = Layout(m.PlayerName, TextStyle.Label, (int)width - 34);
            var alpha = 1 - m.NoteAlpha;
            var centreY = CardLayout.ColumnTop + label.Metrics.Height / 2;
            Fill(dc, new Ellipse(new Vector2(left + 3, centreY), 3, 3), WithAlpha(t.HighContrast ? t.Ink : t.Accent, alpha));
            DrawText(dc, label, left + 12, CardLayout.ColumnTop, Ink(m, PlayerInk.Secondary, alpha));
        }

        if (m.Note is { } note && m.NoteAlpha > 0.001f)
        {
            var layout = Layout(note, TextStyle.Note, (int)width - 24);
            var metrics = layout.Metrics;
            var box = new RectF(left - 2, CardLayout.ColumnTop - 4, Math.Min(width, metrics.Width + 24), metrics.Height + 12);
            Capsule(dc, m, box, m.NoteAlpha);
            DrawText(dc, layout, box.X + 12, box.Y + 6, Ink(m, PlayerInk.Primary, m.NoteAlpha));
        }

        // The song, stepping aside while the seek bar's capsules are up over it.
        // A note over the column (two lines at most) reaches into the title: the song steps back while it shows.
        var titleAlpha = m.TitleAlpha * (1 - 0.8f * m.NoteAlpha);
        if (titleAlpha > 0.001f)
        {
            var y = CardLayout.TitleTop;
            var title = TitleLayout(m.Title, (int)width);
            DrawText(dc, title, left, y, Ink(m, PlayerInk.Primary, titleAlpha));
            y += title.Metrics.Height + CardLayout.TitleGap;
            if (m.Artist is { Length: > 0 } artist)
            {
                var a = Layout(artist, TextStyle.Artist, (int)width);
                DrawText(dc, a, left, y, Ink(m, PlayerInk.Secondary, titleAlpha));
                y += a.Metrics.Height + CardLayout.TitleGap;
            }

            // The album, when it fits above the seek row (the bar's band is empty above the hairline).
            var b = m.Album is { Length: > 0 } album ? Layout(album, TextStyle.Album, (int)width) : null;
            if (b is not null && y + b.Metrics.Height <= CardLayout.SeekRowTop + 2)
                DrawText(dc, b, left, y, Ink(m, PlayerInk.Tertiary, titleAlpha));
        }

        // Align mode (the music video): the video's bar and its row take the seek row's and the transport's place.
        if (m.Align is not null)
        {
            DrawAlign(dc, m);
            return;
        }

        DrawSeekRow(dc, m);
        DrawTransport(dc, m);
    }

    private IDWriteTextLayout TitleLayout(string title, int width)
    {
        // One line at the title's size, set smaller down to its minimum scale before it is cut short.
        var full = Layout(title, TextStyle.Title, width);
        if (full.Metrics.WidthIncludingTrailingWhitespace <= width + 0.5f) return full;
        return Layout(title, TextStyle.TitleSmall, width);
    }

    // ---- Seek row ----

    private void DrawSeekRow(ID2D1DeviceContext dc, CardModel m)
    {
        var t = m.Theme;
        var left = CardLayout.ColumnLeft;
        var width = CardLayout.ColumnWidth;
        // The times: tertiary at rest, secondary while the bar is active.
        var tertiary = Level(PlayerInk.Tertiary);
        var timeAlpha = t.HighContrast ? 1f : tertiary + (Level(PlayerInk.Secondary) - tertiary) * m.Active;
        switch (m.RowKind)
        {
            case ScrubRowKind.Hidden:
                return;
            case ScrubRowKind.Live:
            {
                // "Live" in sentence case beside a red dot, in a capsule.
                var live = Layout("Live", TextStyle.Live, 80);
                var box = new RectF(left, CardLayout.SeekRowTop + 6, 8 + 6 + 6 + live.Metrics.Width + 8, live.Metrics.Height + 8);
                Capsule(dc, m, box, 1);
                Fill(dc, new Ellipse(new Vector2(box.X + 11, box.CentreY), 3, 3), t.HighContrast ? t.Ink : new Color4(1, 0.23f, 0.19f, 1));
                DrawText(dc, live, box.X + 20, box.Y + 4, Ink(m, PlayerInk.Primary));
                return;
            }
            case ScrubRowKind.ElapsedOnly:
            {
                var elapsed = Layout(Elapsed(m.Elapsed), TextStyle.Time, 80);
                DrawText(dc, elapsed, left, CardLayout.SeekRowTop + CardLayout.BarBand, Ink(m, PlayerInk.Tertiary));
                var unknown = Layout("Length unknown", TextStyle.RowDetail, 200);
                DrawText(dc, unknown, left + elapsed.Metrics.Width + 8, CardLayout.SeekRowTop + CardLayout.BarBand, Ink(m, PlayerInk.Tertiary));
                return;
            }
        }

        var s = m.Scrub;
        var duration = m.Duration;
        var session = m.Session;
        var dragging = session is not null;
        var playX = (float)(session?.DisplayX ?? ScrubMath.X(m.Elapsed, duration));
        var overshoot = (float)(session?.Overshoot ?? 0);
        var thickness = m.Thickness;
        var centre = CardLayout.BarCentre;
        var structure = m.Active * (s.HasLyrics ? 1 : 0);

        // The rubber band stretches the whole bar a little past the end being pulled.
        var stretch = 1 + Math.Abs(overshoot) / width;
        var anchor = overshoot < 0 ? left + width : left;
        var transform = Matrix3x2.CreateScale(stretch, 1, new Vector2(anchor, centre));
        dc.Transform = transform;

        var unplayed = t.HighContrast ? t.Disabled : WithAlpha(t.Ink, m.Look.Light ? 0.18f : 0.22f);
        Track(dc, m, left, width, centre, thickness, unplayed, float.PositiveInfinity, structure);
        if (structure > 0.001f && s.Repeated.Length > 0 && !t.HighContrast)
        {
            // Lines that come back (most often the chorus), a shade lighter.
            foreach (var (start, end) in s.Repeated)
            {
                var from = left + (float)ScrubMath.X(start, duration);
                var to = left + (float)ScrubMath.X(end, duration);
                dc.PushAxisAlignedClip(new RawRectF(from, centre - thickness, Math.Max(from + 1, to), centre + thickness), AntialiasMode.Aliased);
                Track(dc, m, left, width, centre, thickness, WithAlpha(t.Ink, 0.10f * structure), float.PositiveInfinity, structure);
                dc.PopAxisAlignedClip();
            }
        }

        var fillOpacity = m.RowKind == ScrubRowKind.Advert ? 0.5f : m.DisplayedPlaying || dragging ? 1f : 0.6f;
        var fill = t.HighContrast ? t.Highlight : dragging ? WithAlpha(t.Accent, 0.9f) : WithAlpha(t.Ink, m.Look.Light ? 0.85f : 0.92f);
        Track(dc, m, left, width, centre, thickness, WithAlpha(fill, fill.A * fillOpacity), left + playX + Math.Max(0, overshoot), structure);

        if (structure > 0.001f)
        {
            // Ticks at each line start; the one the knob is held by is wider and bright.
            var top = centre + thickness / 2 + 2;
            var tickColour = WithAlpha(t.Ink, (t.HighContrast ? 1 : 0.30f) * structure);
            foreach (var x in s.Ticks) Fill(dc, new RawRectF(left + (float)x - 0.5f, top, left + (float)x + 0.5f, top + 3), tickColour);
            double? snapped = session?.SnappedTime ?? (m.Hover is { Snapped: { } i } && i < s.Targets.Length ? s.Targets[i] : null);
            if (snapped is { } st)
            {
                var x = left + (float)ScrubMath.X(st, duration);
                Fill(dc, new RawRectF(x - 1, top, x + 1, top + 3), WithAlpha(t.Ink, structure));
            }
        }

        dc.Transform = Matrix3x2.Identity;
        if (m.RowKind == ScrubRowKind.Seekable)
        {
            if (m.Hover is { } hover && !dragging && m.Active > 0.01f)
                Fill(dc, new RawRectF(left + (float)hover.X - 0.5f, centre - 8, left + (float)hover.X + 0.5f, centre + 8), WithAlpha(t.Ink, 0.5f * m.Active));
            if (m.Active > 0.01f)
            {
                var knob = dragging ? 7f : 6f;
                if (!t.HighContrast) Fill(dc, new Ellipse(new Vector2(left + playX, centre + 1), knob + 1.5f, knob + 1.5f), new Color4(0, 0, 0, 0.18f * m.Active));
                Fill(dc, new Ellipse(new Vector2(left + playX, centre), knob, knob), WithAlpha(t.HighContrast ? t.Ink : m.Look.Light ? t.Ink : new Color4(1, 1, 1, 1), m.Active));
            }
        }

        // The times under the bar: elapsed on the left, time left (or the length) on the right.
        var times = CardLayout.TimesRect;
        var timeColour = WithAlpha(t.Ink, timeAlpha);
        if (m.RowKind == ScrubRowKind.Advert)
        {
            DrawText(dc, Layout("Advertisement", TextStyle.RowTag, 200), times.X, times.Y, timeColour);
        }
        else
        {
            DrawText(dc, Layout(Elapsed(m.Elapsed), TextStyle.Time, 100), times.X, times.Y, timeColour);
            var trailing = Layout(Trailing(m.Elapsed, duration, m.ShowsRemaining), TextStyle.Time, 100);
            DrawText(dc, trailing, times.Right - trailing.Metrics.Width, times.Y, m.HoverPart == CardPart.TimeLabel ? Ink(m, PlayerInk.Primary) : timeColour);
        }

        if (m.RowKind == ScrubRowKind.Seekable && m.Capsules > 0.001f) DrawCapsules(dc, m);
    }

    private void Track(ID2D1DeviceContext dc, CardModel m, float left, float width, float centre, float thickness, Color4 colour,
        float clipRight, float structure)
    {
        // A capsule, broken by 2 DIP gaps where sections start (only while the structure shows).
        var clipped = clipRight < left + width + 50;
        if (clipped) dc.PushAxisAlignedClip(new RawRectF(left - 50, centre - 10, Math.Max(left - 50, clipRight), centre + 10), AntialiasMode.Aliased);
        var radius = thickness / 2;
        var y = centre - thickness / 2;
        var start = left;
        if (structure > 0.5f)
        {
            foreach (var gapTime in m.Scrub.Breaks)
            {
                var gap = (float)ScrubMath.X(gapTime, m.Duration);
                if (gap <= 2 || gap >= width - 2) continue;
                var end = left + gap - 1;
                if (end > start) FillRounded(dc, new RectF(start, y, end - start, thickness), radius, colour);
                start = left + gap + 1;
            }
        }

        if (left + width > start) FillRounded(dc, new RectF(start, y, left + width - start, thickness), radius, colour);
        if (clipped) dc.PopAxisAlignedClip();
    }

    private void DrawCapsules(ID2D1DeviceContext dc, CardModel m)
    {
        var t = m.Theme;
        var alpha = m.Capsules;
        var rise = m.Look.ReduceMotion ? 0 : (1 - alpha) * 4;
        var bottom = CardLayout.SeekRowTop - 2 + rise;
        var left = CardLayout.ColumnLeft;
        var width = CardLayout.ColumnWidth;
        var time = m.CapsuleTime;

        // The time, with the rate under a slow hand (8 by 4 inside its capsule).
        var timeLayout = Layout(CapsuleTime(time), TextStyle.CapsuleTime, 100);
        var rate = m.Session?.RateLabel is { } r ? Layout(r, TextStyle.Rate, 120) : null;
        var timeWidth = timeLayout.Metrics.Width + (rate is null ? 0 : rate.Metrics.Width + 6) + 16;
        var timeHeight = timeLayout.Metrics.Height + 8;
        var timeBox = Clamp(new RectF(left + (float)m.CapsuleX - timeWidth / 2, bottom - timeHeight, timeWidth, timeHeight), left, width);

        // The lyric at that moment, above it.
        RectF? lyricBox = null;
        IDWriteTextLayout? lyric = null, reading = null;
        var instrumental = false;
        if (m.Scrub.HasLyrics)
        {
            var at = ScrubMath.Lyric(time, m.Scrub.Lines);
            if (at.Kind == ScrubLyricKind.Line)
            {
                var line = m.Scrub.Lines[at.Index];
                var replaced = m.Romanization == Lunalyr.Core.Lyrics.Readings.RomanizationDisplay.Replace ? line.Romanized : null;
                lyric = Layout(replaced ?? line.Text, TextStyle.CapsuleLyric, 330);
                if (m.Romanization == Lunalyr.Core.Lyrics.Readings.RomanizationDisplay.Below && line.Romanized is { } roman)
                    reading = Layout(roman, TextStyle.CapsuleReading, 330);
            }
            else if (at.Kind == ScrubLyricKind.Instrumental)
            {
                lyric = Layout("♪ Instrumental", TextStyle.CapsuleLyric, 330);
                instrumental = true;
            }

            if (lyric is not null)
            {
                var w = Math.Max(lyric.Metrics.Width, reading?.Metrics.Width ?? 0) + 24;
                var h = lyric.Metrics.Height + (reading is null ? 0 : reading.Metrics.Height + 1) + 16;
                lyricBox = Clamp(new RectF(left + (float)m.CapsuleX - w / 2, timeBox.Y - 4 - h, w, h), left, width);
            }
        }

        if (lyricBox is { } box && lyric is not null)
        {
            Capsule(dc, m, box, alpha);
            var x = box.CentreX - lyric.Metrics.Width / 2;
            DrawText(dc, lyric, x, box.Y + 8, Ink(m, instrumental ? PlayerInk.Secondary : PlayerInk.Primary, alpha));
            if (reading is not null)
                DrawText(dc, reading, box.CentreX - reading.Metrics.Width / 2, box.Y + 8 + lyric.Metrics.Height + 1, Ink(m, PlayerInk.Secondary, alpha));
        }

        Capsule(dc, m, timeBox, alpha);
        DrawText(dc, timeLayout, timeBox.X + 8, timeBox.Y + 4, Ink(m, PlayerInk.Primary, alpha));
        if (rate is not null)
            DrawText(dc, rate, timeBox.X + 8 + timeLayout.Metrics.Width + 6, timeBox.Y + 4, Ink(m, PlayerInk.Secondary, alpha));
    }

    private static RectF Clamp(RectF box, float left, float width)
    {
        var x = Math.Min(Math.Max(box.X, left), Math.Max(left, left + width - box.Width));
        return box with { X = x };
    }

    /// <summary>
    /// A capsule over the card (the time and lyric over the bar, a note): the card's own deep shade lifted on a soft
    /// shadow, not a second accent; an edge only in a contrast theme.
    /// </summary>
    private void Capsule(ID2D1DeviceContext dc, CardModel m, RectF box, float alpha)
    {
        var t = m.Theme;
        if (!t.HighContrast && !m.Look.ReduceTransparency)
            SoftShadow(dc, new RectF(box.X, box.Y + CapsuleShadowY, box.Width, box.Height), CapsuleShadowBlur, CapsuleShadowOpacity * alpha);
        FillRounded(dc, box, ControlRadius, WithAlpha(t.Capsule, t.Capsule.A * alpha));
        Stroke(dc, Rect(box), ControlRadius, WithAlpha(t.Hairline, t.Hairline.A * alpha), F(PlayerTokens.Line.Width));
    }

    /// <summary>The keyboard's focus, ringed after Tab (Windows' focus visual: two strokes, so it shows on any colour).</summary>
    private void DrawFocus(ID2D1DeviceContext dc, CardModel m)
    {
        var r = PartRect(m.FocusPart);
        if (r.Width <= 0) return;
        var round = CardParts.IsRound(m.FocusPart) ? r.Width / 2 + 4 : 10;
        var outer = m.Theme.HighContrast ? m.Theme.Highlight : m.Theme.Ink;
        Stroke(dc, Rect(r.Inflate(4)), round, outer, 2);
        Stroke(dc, Rect(r.Inflate(2)), round - 2, m.Theme.InkInverse, 1);
    }

    // ---- Transport ----

    private void DrawTransport(ID2D1DeviceContext dc, CardModel m)
    {
        var t = m.Theme;
        // Commands off for this run (--no-playback, --demo): still there to press (the card says why), but quieter, at
        // the tertiary level. Without a song, the skips are disabled (quaternary).
        var enabled = m.CommandsOff && !m.Theme.HighContrast ? Level(PlayerInk.Tertiary) : 1f;
        var skipAlpha = (m.HasSong ? 1f : Level(PlayerInk.Quaternary)) * enabled;
        DrawSkip(dc, m, CardLayout.PreviousRect, forward: false, skipAlpha, CardPart.Previous);
        DrawSkip(dc, m, CardLayout.NextRect, forward: true, skipAlpha, CardPart.Next);
        // The film button, at the row's right-hand end, while Music video is the wallpaper.
        if (m.MusicVideoOffered) DrawAlignButton(dc, m);

        var disc = CardLayout.PlayRect;
        // The play disc: the ink as a flat disc (no shadow), its glyph in the colour behind the ink.
        var pressed = m.PressedPart == CardPart.PlayPause && !m.Look.ReduceMotion ? PressScale : 1f;
        var radius = disc.Width / 2 * pressed;
        var centre = new Vector2(disc.CentreX, disc.CentreY);
        var discColour = t.Ink;
        var glyphColour = t.InkInverse;
        if (m.HoverPart == CardPart.PlayPause && !t.HighContrast) discColour = Darken(discColour, m.Look.Light ? -0.12f : 0.06f);
        Fill(dc, new Ellipse(centre, radius, radius), WithAlpha(discColour, enabled));
        var glyph = radius * 0.76f;
        if (m.DisplayedPlaying)
        {
            var bar = glyph * 0.26f;
            var h = glyph * 0.9f;
            FillRounded(dc, new RectF(centre.X - glyph * 0.36f, centre.Y - h / 2, bar, h), 1.5f, WithAlpha(glyphColour, enabled));
            FillRounded(dc, new RectF(centre.X + glyph * 0.36f - bar, centre.Y - h / 2, bar, h), 1.5f, WithAlpha(glyphColour, enabled));
        }
        else
        {
            DrawGlyph(dc, _playGlyph!, new Vector2(centre.X + radius * 0.06f, centre.Y), glyph * 0.95f, WithAlpha(glyphColour, enabled), mirror: false);
        }
    }

    private void DrawSkip(ID2D1DeviceContext dc, CardModel m, RectF cell, bool forward, float alpha, CardPart part)
    {
        var t = m.Theme;
        var centre = new Vector2(cell.CentreX, cell.CentreY);
        var halo = m.PressedPart == part ? F(PlayerTokens.Fill.Selected) : m.HoverPart == part ? F(PlayerTokens.Fill.Hover) : 0;
        if (halo > 0 && m.HasSong) Fill(dc, new Ellipse(centre, ButtonSize / 2, ButtonSize / 2), WithAlpha(t.Ink, halo));
        var scale = m.PressedPart == part && !m.Look.ReduceMotion ? PressScale : 1f;
        DrawGlyph(dc, _skipGlyph!, centre, 17 * scale, WithAlpha(t.Ink, alpha), mirror: !forward);
    }

    private void DrawGlyph(ID2D1DeviceContext dc, ID2D1PathGeometry glyph, Vector2 centre, float size, Color4 colour, bool mirror)
    {
        var transform = Matrix3x2.CreateScale(mirror ? -size : size, size) * Matrix3x2.CreateTranslation(centre);
        dc.Transform = transform;
        _brush!.Color = colour;
        dc.FillGeometry(glyph, _brush);
        dc.Transform = Matrix3x2.Identity;
    }

    private void DrawNote(ID2D1DeviceContext dc, Vector2 centre, float size, Color4 colour)
    {
        // A plain music note for an empty cover: a head and a stem.
        Fill(dc, new Ellipse(new Vector2(centre.X - size * 0.25f, centre.Y + size * 0.45f), size * 0.32f, size * 0.25f), colour);
        Fill(dc, new RawRectF(centre.X + size * 0.02f, centre.Y - size * 0.6f, centre.X + size * 0.12f, centre.Y + size * 0.45f), colour);
        Fill(dc, new RawRectF(centre.X + size * 0.02f, centre.Y - size * 0.6f, centre.X + size * 0.45f, centre.Y - size * 0.45f), colour);
    }

    // Glyphs in a unit box centred on the origin.
    private static ID2D1PathGeometry Triangle(ID2D1Factory1 factory)
    {
        var path = factory.CreatePathGeometry();
        using var sink = path.Open();
        sink.BeginFigure(new Vector2(-0.38f, -0.5f), FigureBegin.Filled);
        sink.AddLine(new Vector2(0.5f, 0));
        sink.AddLine(new Vector2(-0.38f, 0.5f));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return path;
    }

    private static ID2D1PathGeometry DoubleTriangle(ID2D1Factory1 factory)
    {
        var path = factory.CreatePathGeometry();
        using var sink = path.Open();
        foreach (var x in new[] { -0.5f, 0f })
        {
            sink.BeginFigure(new Vector2(x, -0.32f), FigureBegin.Filled);
            sink.AddLine(new Vector2(x + 0.5f, 0));
            sink.AddLine(new Vector2(x, 0.32f));
            sink.EndFigure(FigureEnd.Closed);
        }

        sink.Close();
        return path;
    }

    // ---- Text ----

    // The strings that change with the clock, made again only when the second (or the label) changes.
    private (long Key, string Text) _elapsed = (long.MinValue, ""), _capsuleTime = (long.MinValue, "");
    private ((long, long, bool) Key, string Text) _trailing = ((long.MinValue, 0, false), "");

    private string Elapsed(double seconds)
    {
        var key = double.IsFinite(seconds) ? (long)Math.Floor(Math.Max(-1, seconds)) : -1;
        if (key != _elapsed.Key) _elapsed = (key, ScrubMath.Timestamp(seconds));
        return _elapsed.Text;
    }

    private string CapsuleTime(double seconds)
    {
        var key = double.IsFinite(seconds) ? (long)Math.Floor(Math.Max(-1, seconds)) : -1;
        if (key != _capsuleTime.Key) _capsuleTime = (key, ScrubMath.Timestamp(seconds));
        return _capsuleTime.Text;
    }

    private string Trailing(double elapsed, double duration, bool remaining)
    {
        var left = Math.Ceiling(Math.Max(0, duration - Math.Min(Math.Max(double.IsFinite(elapsed) ? elapsed : 0, 0), Math.Max(duration, 0))));
        var key = ((long)(remaining ? left : -1), (long)Math.Floor(double.IsFinite(duration) ? duration : 0), remaining);
        if (key != _trailing.Key) _trailing = (key, ScrubMath.TrailingLabel(elapsed, duration, remaining));
        return _trailing.Text;
    }


    private IDWriteTextLayout Layout(string text, TextStyle style, int maxWidth)
    {
        if (_layouts.TryGetValue((text, style, maxWidth), out var cached)) return cached;
        if (_layouts.Count > 320)
        {
            foreach (var old in _layouts.Values) old.Dispose();
            _layouts.Clear();
        }

        var layout = _dwrite.CreateTextLayout(text, Format(style), maxWidth, 200);
        if (style is TextStyle.Note)
        {
            // A note wraps (two lines at most for the card's own sentences), so it is read whole.
            layout.WordWrapping = WordWrapping.Wrap;
            layout.MaxWidth = MathF.Min(maxWidth, MathF.Ceiling(layout.Metrics.WidthIncludingTrailingWhitespace + 1));
        }
        else
        {
            layout.WordWrapping = WordWrapping.NoWrap;
            // One line, cut short with an ellipsis rather than past the column.
            using var sign = _dwrite.CreateEllipsisTrimmingSign(layout);
            layout.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, sign);
        }
        if (TypeOf(style).Type.TabularDigits)
        {
            _tabular ??= MakeTabular();
            layout.SetTypography(_tabular, new TextRange(0, (uint)text.Length));
        }

        _layouts[(text, style, maxWidth)] = layout;
        return layout;
    }

    private IDWriteTypography MakeTabular()
    {
        var typography = _dwrite.CreateTypography();
        typography.AddFontFeature(new FontFeature { NameTag = FontFeatureTag.TabularFigures, Parameter = 1 });
        return typography;
    }

    private IDWriteTextFormat Format(TextStyle style)
    {
        if (_formats.TryGetValue(style, out var format)) return format;
        var (family, type) = TypeOf(style);
        var weight = type.Weight switch { >= 600 => FontWeight.SemiBold, >= 500 => FontWeight.Medium, _ => FontWeight.Normal };
        format = _dwrite.CreateTextFormat(family, null, weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, (float)type.Size, "");
        _formats[style] = format;
        return format;
    }

    /// <summary>Each text style's token (<see cref="PlayerTokens.TypeScale"/>): Segoe UI Variable for SF Pro, Display for the title.</summary>
    private static (string Family, PlayerType Type) TypeOf(TextStyle style)
    {
        var scale = PlayerTokens.TypeScale.Title;
        return style switch
        {
            TextStyle.Title => (Display, scale),
            TextStyle.TitleSmall => (Display, scale with { Size = scale.Size * PlayerTokens.TypeScale.TitleMinimumScale }),
            TextStyle.Label => (Text, PlayerTokens.TypeScale.Label),
            TextStyle.Artist => (Text, PlayerTokens.TypeScale.Artist),
            TextStyle.Album => (Text, PlayerTokens.TypeScale.Meta),
            TextStyle.Time => (Text, PlayerTokens.TypeScale.Time),
            TextStyle.CapsuleTime => (Text, PlayerTokens.TypeScale.TimeStrong),
            TextStyle.CapsuleLyric => (Text, PlayerTokens.TypeScale.CapsuleLyric),
            TextStyle.AlignTitle => (Text, PlayerTokens.TypeScale.CapsuleLyric with { TabularDigits = true }),
            TextStyle.CapsuleReading or TextStyle.RowDetail => (Text, PlayerTokens.TypeScale.RowSubtitle),
            TextStyle.Rate or TextStyle.Live or TextStyle.RowTag => (Text, PlayerTokens.TypeScale.Tag),
            TextStyle.Note => (Text, PlayerTokens.TypeScale.Note),
            TextStyle.Button => (Text, PlayerTokens.TypeScale.RowTitle),
            TextStyle.ButtonMedium => (Text, PlayerTokens.TypeScale.RowTitle with { Weight = 500 }),
            TextStyle.Glyph => ("Segoe Fluent Icons", new PlayerType(PlayerTokens.Glyph.Icon, 400)),
            _ => (Text, PlayerTokens.TypeScale.Meta),
        };
    }

    private void DrawText(ID2D1DeviceContext dc, IDWriteTextLayout layout, float x, float y, Color4 colour)
    {
        if (colour.A <= 0.001f) return;
        _brush!.Color = colour;
        dc.DrawTextLayout(new Vector2(x, y), layout, _brush, DrawTextOptions.EnableColorFont | DrawTextOptions.Clip);
    }

    // ---- Tokens ----

    private static readonly float CoverRadius = F(PlayerTokens.Radius.Cover), ControlRadius = F(PlayerTokens.Radius.Control),
        ButtonSize = F(PlayerTokens.Glyph.Button),
        PressScale = F(PlayerTokens.Motion.PressScale), CapsuleShadowOpacity = F(PlayerTokens.Shadow.CapsuleOpacity),
        CapsuleShadowBlur = F(PlayerTokens.Shadow.CapsuleBlur), CapsuleShadowY = F(PlayerTokens.Shadow.CapsuleY);

    private static float F(double value) => (float)value;

    /// <summary>An ink level's opacity (the Mac's normal levels; a contrast theme never fades its colours).</summary>
    private static float Level(PlayerInk level) => F(PlayerTokens.Ink.Opacity(level));

    /// <summary>
    /// The ink at <paramref name="level"/>, faded by <paramref name="alpha"/> (a fade in or out). In a contrast theme,
    /// the theme's text colour as it is, and its grey text for the quaternary level (disabled and decoration).
    /// </summary>
    private static Color4 Ink(CardModel m, PlayerInk level, float alpha = 1)
    {
        var t = m.Theme;
        if (t.HighContrast) return WithAlpha(level == PlayerInk.Quaternary ? t.Disabled : t.Ink, alpha);
        return WithAlpha(t.Ink, Level(level) * alpha);
    }

    // ---- Shapes ----

    private void Fill(ID2D1DeviceContext dc, RawRectF rect, Color4 colour)
    {
        if (colour.A <= 0.001f) return;
        _brush!.Color = colour;
        dc.FillRectangle(rect, _brush);
    }

    private void Fill(ID2D1DeviceContext dc, Ellipse ellipse, Color4 colour)
    {
        if (colour.A <= 0.001f) return;
        _brush!.Color = colour;
        dc.FillEllipse(ellipse, _brush);
    }

    private void FillRounded(ID2D1DeviceContext dc, RectF rect, float radius, Color4 colour)
    {
        if (colour.A <= 0.001f || rect.Width <= 0 || rect.Height <= 0) return;
        _brush!.Color = colour;
        radius = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        dc.FillRoundedRectangle(new RoundedRectangle(Rect(rect), radius, radius), _brush);
    }

    private void Stroke(ID2D1DeviceContext dc, RawRectF rect, float radius, Color4 colour, float width)
    {
        if (colour.A <= 0.001f) return;
        _brush!.Color = colour;
        var inset = width / 2;
        dc.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(rect.Left + inset, rect.Top + inset, rect.Right - inset, rect.Bottom - inset), radius, radius), _brush, width);
    }

    private void StrokeEllipse(ID2D1DeviceContext dc, Ellipse ellipse, Color4 colour, float width)
    {
        _brush!.Color = colour;
        dc.DrawEllipse(ellipse, _brush, width);
    }

    /// <summary>
    /// A soft shadow under a rounded box: widening, fading rounded rectangles, about one per DIP of blur (five, 2.4 DIPs
    /// apart for the cover's 12, showed as bands on the light card).
    /// </summary>
    private void SoftShadow(ID2D1DeviceContext dc, RectF box, float blur, float strength)
    {
        if (strength <= 0.001f) return;
        var steps = Math.Max(5, (int)MathF.Ceiling(blur));
        for (var i = steps; i >= 1; i--)
        {
            var spread = blur * i / steps;
            FillRounded(dc, box.Inflate(spread - blur * 0.35f), 8 + spread, new Color4(0, 0, 0, strength / steps * 0.9f));
        }
    }

    private void SoftShadow(ID2D1DeviceContext dc, Ellipse ellipse, float strength)
    {
        if (strength <= 0.001f) return;
        _soft!.Center = ellipse.Point;
        _soft.RadiusX = ellipse.RadiusX;
        _soft.RadiusY = ellipse.RadiusY;
        _soft.Opacity = strength;
        dc.FillEllipse(ellipse, _soft);
    }

    private static RawRectF Rect(RectF r) => new(r.X, r.Y, r.Right, r.Bottom);

    private static Color4 Grey(float v) => new(v, v, v, 1);

    private static Color4 WithAlpha(Color4 c, float alpha) => new(c.R, c.G, c.B, Math.Clamp(alpha, 0, 1));

    private static Color4 Darken(Color4 c, float amount) =>
        new(Math.Clamp(c.R - amount, 0, 1), Math.Clamp(c.G - amount, 0, 1), Math.Clamp(c.B - amount, 0, 1), c.A);
}
