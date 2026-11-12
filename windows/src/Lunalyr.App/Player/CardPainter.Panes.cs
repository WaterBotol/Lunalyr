using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Player;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.App.Player;

// The search and the queue, which take over the card's right-hand column (the Mac's SleeveParts.swift: PlayerPicker,
// PlayerSearchField, PlayerResultsList, PlayerQueueList, PaneCloseButton), and the compact song with the small
// transport under the cover while they are open (DesktopPlayerView.artColumn). The rows' rectangles are kept in
// Hits for the pointer and for UI Automation.
internal sealed partial class CardPainter
{
    // Segoe Fluent Icons.
    private const string GlyphSearch = "\uE721", GlyphQueue = "\uE8FD", GlyphClose = "\uE711", GlyphClear = "\uE894",
        GlyphChevron = "\uE70D", GlyphShuffle = "\uE8B1", GlyphReturn = "\uE751", GlyphNote = "\uE8D6", GlyphAlbum = "\uE93C",
        GlyphPlaying = "\uE995", GlyphPaused = "\uE769";

    private readonly Dictionary<CoverArt, ID2D1Bitmap1> _rowBitmaps = new(ReferenceEqualityComparer.Instance);
    private ID2D1RoundedRectangleGeometry? _rowClip36, _rowClip30;

    /// <summary>The parts drawn this frame that move or come and go (rows, the picker, buttons), with where they are.</summary>
    public List<(CardPart Part, RectF Rect)> Hits { get; } = [];

    /// <summary>How tall the results and the queue's rows are, for scrolling (DIPs, as last drawn).</summary>
    public float ResultsContent { get; private set; }

    public float QueueContent { get; private set; }

    /// <summary>Where a part is, in DIPs: the fixed ones from the layout, the rest as last drawn.</summary>
    public RectF PartRect(PlayerPane pane, CardPart part)
    {
        switch (part)
        {
            case CardPart.Cover: return CardLayout.CoverRect;
            case CardPart.Previous: return CardLayout.PreviousFor(pane);
            case CardPart.PlayPause: return CardLayout.PlayFor(pane);
            case CardPart.Next: return CardLayout.NextFor(pane);
            case CardPart.SeekBar when pane == PlayerPane.Song:
                return new RectF(CardLayout.ColumnLeft, CardLayout.SeekRowTop, CardLayout.ColumnWidth, CardLayout.BarBand);
            case CardPart.TimeLabel when pane == PlayerPane.Song:
                return new RectF(CardLayout.TimesRect.Right - 64, CardLayout.TimesRect.Y, 64, CardLayout.TimesHeight);
            case CardPart.SearchButton when pane == PlayerPane.Song: return CardLayout.SearchButtonRect;
            case CardPart.QueueButton when pane == PlayerPane.Song: return CardLayout.QueueButtonRect;
            case CardPart.SearchField when pane == PlayerPane.Search: return CardLayout.FieldRect;
        }

        foreach (var (hit, rect) in Hits)
            if (hit == part) return rect;
        return default;
    }

    /// <summary>The field's flat colour, behind the search box's text (the EDIT window over it paints the same colour).</summary>
    public static Color4 FieldColour(CardModel m)
    {
        var t = m.Theme;
        if (t.HighContrast) return new Color4(t.Base.R, t.Base.G, t.Base.B, 1);
        var toward = m.Look.Light ? new Color4(1, 1, 1, 1) : new Color4(0, 0, 0, 1);
        var amount = m.Look.Light ? 0.55f : 0.32f;
        return new Color4(t.Base.R + (toward.R - t.Base.R) * amount, t.Base.G + (toward.G - t.Base.G) * amount,
            t.Base.B + (toward.B - t.Base.B) * amount, 1);
    }

    /// <summary>A colour as a GDI COLORREF (0x00BBGGRR), for the search box's EDIT window.</summary>
    public static uint ColorRef(Color4 c) =>
        (uint)Math.Round(Math.Clamp(c.R, 0, 1) * 255) | ((uint)Math.Round(Math.Clamp(c.G, 0, 1) * 255) << 8) | ((uint)Math.Round(Math.Clamp(c.B, 0, 1) * 255) << 16);

    /// <summary><paramref name="a"/> over <paramref name="b"/> at <paramref name="alpha"/>, opaque.</summary>
    public static Color4 Mix(Color4 a, Color4 b, float alpha) =>
        new(b.R + (a.R - b.R) * alpha, b.G + (a.G - b.G) * alpha, b.B + (a.B - b.B) * alpha, 1);

    private void ReleaseRowCovers()
    {
        foreach (var bitmap in _rowBitmaps.Values) bitmap.Dispose();
        _rowBitmaps.Clear();
        _rowClip36?.Dispose(); _rowClip36 = null;
        _rowClip30?.Dispose(); _rowClip30 = null;
    }

    // ---- The art column, compact ----

    private void DrawCompact(ID2D1DeviceContext dc, CardModel m)
    {
        var t = m.Theme;
        var cover = CardLayout.CoverRect;
        var title = Layout(m.Title, TextStyle.CompactTitle, (int)cover.Width);
        DrawText(dc, title, cover.CentreX - title.Metrics.Width / 2, CardLayout.CompactTitleTop, t.Ink);
        if (m.Artist is { Length: > 0 } artist)
        {
            var a = Layout(artist, TextStyle.CompactArtist, (int)cover.Width);
            DrawText(dc, a, cover.CentreX - a.Metrics.Width / 2, CardLayout.CompactTitleTop + title.Metrics.Height + 2,
                WithAlpha(t.Ink, t.HighContrast ? 1 : 0.7f));
        }

        DrawTransport(dc, m);
    }

    /// <summary>The note about a play, over the top of the column while search or the queue is open.</summary>
    private void DrawPaneNote(ID2D1DeviceContext dc, CardModel m)
    {
        if (m.Note is not { } note || m.NoteAlpha <= 0.001f) return;
        var t = m.Theme;
        var layout = Layout(note, TextStyle.Note, (int)CardLayout.ColumnWidth - 20);
        var metrics = layout.Metrics;
        var box = new RectF(CardLayout.ColumnLeft, CardLayout.ColumnTop - 3, Math.Min(CardLayout.ColumnWidth, metrics.Width + 20), metrics.Height + 8);
        // Over the picker or the queue's header: solid enough to read.
        FillRounded(dc, box, 12, WithAlpha(FieldColour(m), m.NoteAlpha));
        Stroke(dc, Rect(box), 12, WithAlpha(t.CapsuleRim, t.CapsuleRim.A * m.NoteAlpha), 1);
        DrawText(dc, layout, box.X + 10, box.Y + 4, WithAlpha(t.Ink, m.NoteAlpha));
    }

    // ---- Search ----

    private void DrawSearch(ID2D1DeviceContext dc, CardModel m)
    {
        var t = m.Theme;
        var left = CardLayout.ColumnLeft;
        var width = CardLayout.ColumnWidth;
        DrawPicker(dc, m);
        IconButton(dc, m, CardLayout.PaneCloseRect, CardPart.PaneClose, on: false, glyph: GlyphClose, glyphSize: 10);

        if (m.Prompt is { } prompt)
        {
            var text = Layout(prompt.Text, TextStyle.Status, (int)width - 8);
            var y = CardLayout.FieldRect.Y + 4;
            DrawText(dc, text, left + 2, y, WithAlpha(t.Ink, t.HighContrast ? 1 : 0.85f));
            y += text.Metrics.Height + 14;
            if (prompt.ActionLabel is { } label)
            {
                var words = Layout(label, TextStyle.Button, (int)width);
                var box = new RectF(left, y, words.Metrics.Width + 32, 32);
                var hover = m.HoverPart == CardPart.PromptAction;
                var fill = t.HighContrast ? t.Highlight : m.Look.Light ? t.Ink : new Color4(1, 1, 1, 1);
                FillRounded(dc, box, 16, WithAlpha(fill, hover ? 0.88f : 1f));
                var ink = t.HighContrast ? t.HighlightText : m.Look.Light ? new Color4(1, 1, 1, 1) : new Color4(0, 0, 0, 1);
                DrawText(dc, words, box.X + 16, box.Y + (32 - words.Metrics.Height) / 2, ink);
                Hits.Add((CardPart.PromptAction, box));
            }

            DrawPaneNote(dc, m);
            return;
        }

        // The box: a flat well with a rim (white while it has the keyboard), the magnifier, and a clear button once
        // something is typed. The text itself is the EDIT window over it; the same text is drawn here underneath, so
        // the card reads right while the window comes and goes with the fade.
        var field = CardLayout.FieldRect;
        var well = FieldColour(m);
        FillRounded(dc, field, 10, well);
        var rim = m.FieldFocused ? (t.HighContrast ? t.Highlight : WithAlpha(t.Ink, 0.85f)) : WithAlpha(t.Ink, t.HighContrast ? 1 : 0.25f);
        Stroke(dc, Rect(field), 10, rim, m.FieldFocused ? 2 : 1);
        DrawGlyph(dc, GlyphSearch, new RectF(field.X + 8, field.Y, 22, field.Height), WithAlpha(t.Ink, 0.7f));
        var textRect = CardLayout.FieldTextRect;
        var shown = m.Query.Length > 0 ? m.Query : m.SearchPlaceholder;
        var typed = Layout(shown, TextStyle.Field, (int)textRect.Width);
        DrawText(dc, typed, textRect.X, textRect.Y + (textRect.Height - typed.Metrics.Height) / 2,
            m.Query.Length > 0 ? t.Ink : Mix(t.Ink, well, 0.5f));
        if (m.Query.Length > 0)
        {
            var clear = CardLayout.ClearRect;
            DrawGlyph(dc, GlyphClear, clear, WithAlpha(t.Ink, m.HoverPart == CardPart.ClearSearch ? 0.85f : 0.5f));
            Hits.Add((CardPart.ClearSearch, clear));
        }

        DrawResults(dc, m);
        DrawPaneNote(dc, m);
    }

    private void DrawPicker(ID2D1DeviceContext dc, CardModel m)
    {
        var t = m.Theme;
        if (m.Pickers.Count == 0) return;
        var x = CardLayout.ColumnLeft + 3;
        var y = CardLayout.ColumnTop + 3;
        var start = x - 3;
        var items = new (IDWriteTextLayout Text, float Width)[m.Pickers.Count];
        for (var i = 0; i < items.Length; i++)
        {
            var text = Layout(m.Pickers[i], TextStyle.Picker, 140);
            items[i] = (text, text.Metrics.Width + 20 + 11);
        }

        var total = items.Sum(i => i.Width) + 2 * (items.Length - 1) + 6;
        var capsule = new RectF(start, y - 3, total, 24 + 6);
        FillRounded(dc, capsule, 15, t.HighContrast ? t.Base : new Color4(0, 0, 0, m.Look.Light ? 0.08f : 0.25f));
        if (t.HighContrast) Stroke(dc, Rect(capsule), 15, t.Ink, 1);
        for (var i = 0; i < items.Length; i++)
        {
            var on = i == m.PickerSelected;
            var box = new RectF(x, y, items[i].Width, 24);
            var part = CardParts.Picker(i);
            if (on) FillRounded(dc, box, 12, t.HighContrast ? t.Highlight : m.Look.Light ? t.Ink : new Color4(1, 1, 1, 1));
            else if (m.HoverPart == part) FillRounded(dc, box, 12, WithAlpha(t.Ink, 0.1f));
            var ink = on ? (t.HighContrast ? t.HighlightText : m.Look.Light ? new Color4(1, 1, 1, 1) : new Color4(0, 0, 0, 1))
                : WithAlpha(t.Ink, t.HighContrast ? 1 : 0.72f);
            Fill(dc, new Ellipse(new Vector2(box.X + 13, box.CentreY), 3, 3), on || t.HighContrast ? ink : t.Accent);
            DrawText(dc, items[i].Text, box.X + 21, box.Y + (24 - items[i].Text.Metrics.Height) / 2, ink);
            Hits.Add((part, box));
            x += items[i].Width + 2;
        }
    }

    private void DrawResults(ID2D1DeviceContext dc, CardModel m)
    {
        var t = m.Theme;
        var left = CardLayout.ColumnLeft;
        var width = CardLayout.ColumnWidth;
        var top = CardLayout.ResultsTop;
        var bottom = CardLayout.ColumnBottom(PlayerPane.Search, 0);
        dc.PushAxisAlignedClip(new RawRectF(left - 4, top, left + width + 4, bottom), AntialiasMode.Aliased);
        var y = top - m.ResultsScroll;
        if (m.Status.Kind != PlayerSearchStatusKind.Idle)
        {
            var status = Layout(m.Status.Text, TextStyle.Status, (int)width - 16);
            DrawText(dc, status, left + 8, y + 8, WithAlpha(t.Ink, t.HighContrast ? 1 : m.Status.Kind == PlayerSearchStatusKind.Searching ? 0.65f : 0.78f));
            y += status.Metrics.Height + 16;
        }

        for (var i = 0; i < m.Results.Count; i++)
        {
            var row = new RectF(left, y, width, CardLayout.ResultRow);
            y += CardLayout.ResultRow + CardLayout.RowGap;
            if (row.Bottom < top || row.Y > bottom) continue;
            var result = m.Results[i];
            var part = CardParts.Result(i);
            var selected = i == m.Selection;
            if (selected) FillRounded(dc, row, 10, t.HighContrast ? t.Highlight : WithAlpha(t.Ink, 0.16f));
            else if (m.HoverPart == part || m.PressedPart == part) FillRounded(dc, row, 10, WithAlpha(t.Ink, m.PressedPart == part ? 0.14f : 0.08f));
            var ink = selected && t.HighContrast ? t.HighlightText : t.Ink;
            RowCover(dc, m, m.RowCover(result.ArtworkUri), new RectF(row.X + 6, row.Y + 6, 36, 36), 5,
                result.Kind == PlayerSearchResultKind.Album ? GlyphAlbum : GlyphNote);
            var right = row.Right - 8;
            if (selected)
            {
                var key = new RectF(right - 22, row.CentreY - 9, 22, 18);
                Stroke(dc, Rect(key), 4, WithAlpha(ink, 0.45f), 1);
                DrawGlyph(dc, GlyphReturn, key, WithAlpha(ink, 0.8f), size: 10);
                right = key.X - 8;
            }

            if (result.Kind == PlayerSearchResultKind.Album || result.InLibrary)
            {
                var tag = Layout(result.InLibrary ? "LIBRARY" : "ALBUM", TextStyle.RowTag, 80);
                DrawText(dc, tag, right - tag.Metrics.Width, row.CentreY - tag.Metrics.Height / 2, WithAlpha(ink, t.HighContrast ? 1 : 0.5f));
                right -= tag.Metrics.Width + 8;
            }

            var textLeft = row.X + 52;
            var title = Layout(result.Title, TextStyle.RowTitle, (int)(right - textLeft));
            DrawText(dc, title, textLeft, row.Y + 7, ink);
            var detail = Layout(Subtitle(result), TextStyle.RowDetail, (int)(right - textLeft));
            DrawText(dc, detail, textLeft, row.Y + 7 + title.Metrics.Height + 1, WithAlpha(ink, t.HighContrast ? 1 : 0.65f));
            Hits.Add((part, row));
        }

        dc.PopAxisAlignedClip();
        ResultsContent = y + m.ResultsScroll - top;
    }

    private static string Subtitle(PlayerSearchResult result) =>
        result.Kind == PlayerSearchResultKind.Album || result.Album.Length == 0 || result.Album == result.Title
            ? result.Artist
            : $"{result.Artist} · {result.Album}";

    // ---- Queue ----

    private void DrawQueue(ID2D1DeviceContext dc, CardModel m)
    {
        var t = m.Theme;
        var left = CardLayout.ColumnLeft;
        var width = CardLayout.ColumnWidth;
        var header = WithAlpha(t.Ink, t.HighContrast ? 1 : m.Look.Light ? 0.75f : 0.65f);
        DrawText(dc, Layout("QUEUE", TextStyle.Label, 100), left + 2, CardLayout.ColumnTop + 6, header);
        IconButton(dc, m, CardLayout.PaneCloseRect, CardPart.PaneClose, on: false, glyph: GlyphClose, glyphSize: 10);
        if (m.ShuffleOn && m.Queue.Count > 1)
        {
            var words = Layout("Playlist order, shuffle is on", TextStyle.RowDetail, 240);
            var x = CardLayout.PaneCloseRect.X - 6 - words.Metrics.Width;
            DrawText(dc, words, x, CardLayout.ColumnTop + 5, header);
            DrawGlyph(dc, GlyphShuffle, new RectF(x - 20, CardLayout.ColumnTop, 16, 24), header, size: 11);
        }

        var top = CardLayout.QueueTop;
        var bottom = CardLayout.ColumnBottom(PlayerPane.Queue, m.QueueRows);
        dc.PushAxisAlignedClip(new RawRectF(left - 4, top, left + width + 4, bottom), AntialiasMode.Aliased);
        var y = top - m.QueueScroll;
        var current = -1;
        for (var i = 0; i < m.Queue.Count; i++)
        {
            if (m.Queue[i].IsCurrent)
            {
                current = i;
                break;
            }
        }

        for (var i = 0; i < m.Queue.Count; i++)
        {
            if (i == 0 && current > 0) y = Section(dc, m, "Earlier", y);
            if (current >= 0 && i == current + 1) y = Section(dc, m, "Up next", y);
            var item = m.Queue[i];
            var row = new RectF(left, y, width, CardLayout.QueueRow);
            y += CardLayout.QueueRow + CardLayout.RowGap;
            if (row.Bottom < top || row.Y > bottom) continue;
            var part = CardParts.QueueRow(i);
            var now = i == current;
            var earlier = current >= 0 && i < current;
            var alpha = earlier ? (t.HighContrast ? 0.7f : 0.5f) : item.IsPlayable ? 1f : 0.6f;
            if (now) FillRounded(dc, row, 8, t.HighContrast ? t.Highlight : WithAlpha(t.Ink, 0.16f));
            else if (item.IsPlayable && (m.HoverPart == part || m.PressedPart == part)) FillRounded(dc, row, 8, WithAlpha(t.Ink, m.PressedPart == part ? 0.14f : 0.08f));
            var ink = now && t.HighContrast ? t.HighlightText : t.Ink;
            var art = m.RowCover(item.ArtworkUri) ?? (now ? m.Cover : null);
            RowCover(dc, m, art, new RectF(row.X + 6, row.Y + 4, 30, 30), 4, GlyphNote, alpha);
            var right = row.Right - 10;
            if (now)
            {
                DrawGlyph(dc, m.DisplayedPlaying ? GlyphPlaying : GlyphPaused, new RectF(right - 16, row.Y, 16, row.Height), ink, size: 11);
                right -= 24;
            }
            else if (item.Index > 0)
            {
                var number = Layout(item.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), TextStyle.RowDetail, 40);
                DrawText(dc, number, right - number.Metrics.Width, row.CentreY - number.Metrics.Height / 2, WithAlpha(ink, 0.42f * alpha));
                right -= number.Metrics.Width + 10;
            }

            var textLeft = row.X + 46;
            var title = Layout(item.Title, now ? TextStyle.RowTitleBold : TextStyle.RowTitle, (int)(right - textLeft));
            DrawText(dc, title, textLeft, row.Y + 4, WithAlpha(ink, alpha));
            if (item.Artist.Length > 0)
            {
                var artist = Layout(item.Artist, TextStyle.RowDetail, (int)(right - textLeft));
                DrawText(dc, artist, textLeft, row.Y + 4 + title.Metrics.Height, WithAlpha(ink, alpha * (t.HighContrast ? 1 : 0.62f)));
            }

            Hits.Add((part, row));
        }

        if (m.QueueMessage is { } message)
        {
            var note = Layout(message, TextStyle.Status, (int)width - 12);
            DrawText(dc, note, left + 6, y + 6, WithAlpha(t.Ink, t.HighContrast ? 1 : 0.6f));
            y += note.Metrics.Height + 12;
        }

        dc.PopAxisAlignedClip();
        QueueContent = y + m.QueueScroll - top;
        DrawPaneNote(dc, m);
    }

    private float Section(ID2D1DeviceContext dc, CardModel m, string title, float y)
    {
        var words = Layout(title, TextStyle.Section, 200);
        DrawText(dc, words, CardLayout.ColumnLeft + 2, y + 5, WithAlpha(m.Theme.Ink, m.Theme.HighContrast ? 0.8f : 0.5f));
        return y + CardLayout.QueueSection;
    }

    // ---- Pieces ----

    /// <summary>A round icon button (search, queue, close): a faint circle on hover, lit while what it opens is open.</summary>
    private void IconButton(ID2D1DeviceContext dc, CardModel m, RectF rect, CardPart part, bool on, string? glyph = null, float glyphSize = 13)
    {
        var t = m.Theme;
        var centre = new Vector2(rect.CentreX, rect.CentreY);
        var halo = on ? 0.2f : m.PressedPart == part ? 0.14f : m.HoverPart == part ? 0.1f : 0;
        if (halo > 0) Fill(dc, new Ellipse(centre, rect.Width / 2, rect.Width / 2), WithAlpha(t.Ink, halo));
        var size = m.PressedPart == part && !m.Look.ReduceMotion ? glyphSize * 0.88f : glyphSize;
        glyph ??= part == CardPart.SearchButton ? GlyphSearch : GlyphQueue;
        DrawGlyph(dc, glyph, rect, WithAlpha(t.Ink, on || t.HighContrast ? 1 : 0.75f), size);
        if (part is CardPart.SearchButton or CardPart.QueueButton or CardPart.PaneClose) Hits.Add((part, rect));
    }

    private void Chevron(ID2D1DeviceContext dc, Vector2 centre, Color4 colour) =>
        DrawGlyph(dc, GlyphChevron, new RectF(centre.X - 6, centre.Y - 8, 12, 16), colour, size: 8);

    /// <summary>A Segoe Fluent Icons glyph centred in <paramref name="box"/>.</summary>
    private void DrawGlyph(ID2D1DeviceContext dc, string glyph, RectF box, Color4 colour, float size = 13)
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

    /// <summary>A row's cover in rounded corners, or a plain tile with a glyph while it loads (or when there is none).</summary>
    private void RowCover(ID2D1DeviceContext dc, CardModel m, CoverArt? art, RectF box, float radius, string glyph, float alpha = 1)
    {
        var t = m.Theme;
        var bitmap = art is null ? null : RowBitmap(dc, art);
        if (bitmap is null)
        {
            FillRounded(dc, box, radius, WithAlpha(t.Ink, 0.1f * alpha));
            DrawGlyph(dc, glyph, box, WithAlpha(t.Ink, 0.4f * alpha), size: box.Width * 0.36f);
            return;
        }

        var clip = box.Width > 32 ? (_rowClip36 ??= _factory!.CreateRoundedRectangleGeometry(new RoundedRectangle(new RawRectF(0, 0, 36, 36), 5, 5)))
            : (_rowClip30 ??= _factory!.CreateRoundedRectangleGeometry(new RoundedRectangle(new RawRectF(0, 0, 30, 30), 4, 4)));
        var previous = dc.Transform;
        dc.Transform = Matrix3x2.CreateTranslation(box.X, box.Y) * previous;
        dc.PushLayer(new LayerParameters1
        {
            ContentBounds = new RawRectF(0, 0, box.Width, box.Height), GeometricMask = clip, MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity, Opacity = alpha,
        }, null!);
        dc.DrawBitmap(bitmap, new RawRectF(0, 0, box.Width, box.Height), 1, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, CoverSource(bitmap), null);
        dc.PopLayer();
        dc.Transform = previous;
    }

    private ID2D1Bitmap1? RowBitmap(ID2D1DeviceContext dc, CoverArt art)
    {
        if (_rowBitmaps.TryGetValue(art, out var bitmap)) return bitmap;
        if (_rowBitmaps.Count >= 64)
        {
            foreach (var old in _rowBitmaps.Values) old.Dispose();
            _rowBitmaps.Clear();
        }

        unsafe
        {
            fixed (byte* data = art.Pixels)
                bitmap = dc.CreateBitmap(new SizeI(art.Width, art.Height), (nint)data, (uint)(art.Width * 4),
                    new BitmapProperties1(new D2DPixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
        }

        _rowBitmaps[art] = bitmap;
        return bitmap;
    }
}
