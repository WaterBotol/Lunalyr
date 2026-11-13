// Draws a lyric card offscreen: the Mac's LyricCardView and LyricCardFrame (LyricCard.swift, Lunalyr's own code; the
// blame check found only imports from the Verso commit), re-drawn with Direct2D. The backdrop and the card's chrome
// (cover, title, artist, the Lunalyr mark) follow the Mac's layout in card points; a timed line is drawn by the
// wallpaper's own composition layer into the lyric stage, so a card looks like the wallpaper; a still (the PNG, the
// preview, a line without timing) is the Mac's one shaped paragraph, each word in its own colour.

using System.Numerics;
using Lunalyr.Core.Cards;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;
using Lunalyr.Render.Compositions;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.Render.Cards;

/// <summary>How a card is coloured: the card's palette (see <see cref="ForCard"/>), the song's cover and the lyric style.</summary>
public sealed record CardLook(Palette Palette, CoverArt? Cover, LyricStyle Style)
{
    /// <summary>
    /// The look of a card from the wallpaper's: the palette's text backdrop is the card's own (its album gradient under
    /// the lightest corner of its scrim, the Mac's <c>inkInputs</c>), and the style draws one line on its own, with no
    /// opening title, credits or interlude dots.
    /// </summary>
    public static CardLook ForCard(Palette palette, CoverArt? cover, LyricStyle style)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(style);
        var album = AlbumOf(palette);
        var backdrop = Math.Max(0.06, LyricLegibility.Darkened(album.GradientLuminance(1), LyricCardLayout.LightestScrim));
        return new CardLook(palette with { TextBackdrop = backdrop }, cover,
            style with { OpeningTitle = false, Credits = false, Interludes = false, FloatingTextSize = null });
    }

    /// <summary>The album palette behind a scene palette (its nine cover colours), or the fallback colours.</summary>
    internal static AlbumWallpaperPalette AlbumOf(Palette palette)
    {
        if (palette.ArtworkStops.Count < 9) return AlbumWallpaperPalette.Fallback;
        var grid = palette.ArtworkStops.Select(stop => AlbumColourGrid.Hsb(new Lunalyr.Core.Colour.Double3(stop.X, stop.Y, stop.Z))).ToList();
        return new AlbumWallpaperPalette(grid);
    }

    /// <summary>The palette a picture behind the card gives (its own colours, not the album's), from its pixels.</summary>
    public static Palette PaletteFor(ArtworkPixels picture) =>
        PaletteMapper.FromAlbum(new AlbumWallpaperPalette(AlbumColourGrid.Extract(picture)));
}

/// <summary>
/// One timed page as the composition draws it: a scene holding only its line (and its readings), built once per page so
/// the layer sets it once.
/// </summary>
public sealed class CardPageScene
{
    private CardPageScene(SceneSnapshot scene, string compositionId)
    {
        Scene = scene;
        CompositionId = compositionId;
    }

    public SceneSnapshot Scene { get; }

    public string CompositionId { get; }

    /// <summary>
    /// The scene for <paramref name="animation"/> on a card of <paramref name="content"/>'s shape with
    /// <paramref name="look"/>. <paramref name="wordReadings"/> and <paramref name="reading"/> are the line's readings as
    /// the wallpaper has them (word by word, and the whole line), when shown.
    /// </summary>
    public static CardPageScene For(CardAnimation animation, LyricCardContent content, CardLook look,
                                    IReadOnlyList<LyricWord>? wordReadings = null, string? reading = null)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(look);
        var stage = content.LyricStage;
        // The Mac's LyricCardFrame: rolling layouts set a bigger word, no taller than the stage allows.
        var size = animation.Rolls
            ? Math.Min(Math.Max(290, animation.CardTypeSize * 1.62), stage.Height / LyricCardLayout.RollingHeightInTypeSizes)
            : animation.CardTypeSize;
        var line = animation.Line;
        var scene = new SceneSnapshot
        {
            Title = content.Title,
            Artist = content.Artist,
            Lyrics = new LyricResult.Synced([line], new LyricProvenance("Card", "Card")),
            WordReadings = wordReadings is { Count: > 0 } ? new Dictionary<int, IReadOnlyList<LyricWord>> { [0] = wordReadings } : new Dictionary<int, IReadOnlyList<LyricWord>>(),
            Readings = reading is { Length: > 0 } ? new Dictionary<int, string> { [0] = reading } : new Dictionary<int, string>(),
            Palette = look.Palette,
            Style = look.Style with { FloatingTextSize = size },
            ReduceMotion = content.ReduceMotion,
            TrackKey = "card",
        };
        return new CardPageScene(scene, Lunalyr.Core.Compositions.CompositionTraits.Id(animation.Composition));
    }
}

/// <summary>What the lyric stage shows in a frame.</summary>
public abstract record CardLyrics
{
    /// <summary>Nothing (the backdrop and chrome alone: the Mac's <c>hideLyrics</c>).</summary>
    public sealed record None : CardLyrics;

    /// <summary>A page set as one still paragraph (the PNG, the preview, a line without timing).</summary>
    public sealed record Paragraph(CardPage Page, double Opacity = 1) : CardLyrics;

    /// <summary>A timed page drawn by its composition at <paramref name="SongTime"/>.</summary>
    public sealed record Timed(CardPageScene Scene, double SongTime, double CardTime, double Opacity = 1, double Delta = 1 / 30.0) : CardLyrics;
}

/// <summary>
/// Draws lyric cards of one shape at one scale (pixels per card point) on a Direct2D surface with no window. Not
/// thread-safe: one thread draws. Dispose it to let its device go.
/// </summary>
public sealed class LyricCardPainter : IDisposable
{
    private readonly OffscreenSurface _surface;
    private readonly Dictionary<string, IRenderLayerFactory> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IRenderLayer Layer, Vector2 Size)> _layers = new(StringComparer.Ordinal);
    private readonly ID2D1Bitmap1 _stage;
    private readonly ID2D1Bitmap1 _backdrop;
    private readonly ID2D1SolidColorBrush _brush;
    private readonly Vortice.Direct2D1.Effects.Shadow _shadow;
    private readonly Vortice.Direct2D1.Effects.GaussianBlur _blur;
    private readonly CardRect _stageRect;
    private (object? Content, object? Look, object? Background) _backdropKey;
    private (CoverArt? Art, ID2D1Bitmap1? Bitmap) _cover;
    private (CoverArt? Art, ID2D1Bitmap1? Bitmap) _picture;

    /// <param name="aspect">The card's shape.</param>
    /// <param name="scale">Pixels per card point: 2 for the PNG, 2/3 for the MP4, 1 for the GIF.</param>
    /// <param name="factories">The composition layers (<c>WallpaperLayers.Factories</c>).</param>
    /// <param name="hasTrackDetails">The card shows a cover, title or artist (it moves the lyric stage).</param>
    /// <param name="hardware">Draw on the GPU (falls back to WARP when there is none).</param>
    public LyricCardPainter(CardAspect aspect, double scale, IEnumerable<IRenderLayerFactory> factories, bool hasTrackDetails = true,
                            bool hardware = true)
    {
        ArgumentNullException.ThrowIfNull(factories);
        Aspect = aspect;
        Scale = scale > 0 && double.IsFinite(scale) ? scale : 1;
        var size = aspect.Size();
        Width = Math.Max(16, (int)Math.Round(size.Width * Scale));
        Height = Math.Max(16, (int)Math.Round(size.Height * Scale));
        try
        {
            _surface = new OffscreenSurface(Width, Height, (float)Scale, hardware);
        }
        catch (SharpGen.Runtime.SharpGenException) when (hardware)
        {
            _surface = new OffscreenSurface(Width, Height, (float)Scale, hardware: false);
        }
        foreach (var factory in factories) _factories[factory.Id] = factory;
        _stageRect = LyricCardLayout.LyricStage(aspect, hasTrackDetails);
        var dc = _surface.Context;
        var target = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target);
        _stage = dc.CreateBitmap(new SizeI(Px(_stageRect.Width), Px(_stageRect.Height)), IntPtr.Zero, 0, target);
        _backdrop = dc.CreateBitmap(new SizeI(Width, Height), IntPtr.Zero, 0, target);
        _brush = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _shadow = new Vortice.Direct2D1.Effects.Shadow(dc) { Optimization = ShadowOptimization.Quality };
        _blur = new Vortice.Direct2D1.Effects.GaussianBlur(dc) { BorderMode = BorderMode.Hard, Optimization = GaussianBlurOptimization.Quality };
    }

    public CardAspect Aspect { get; }

    public double Scale { get; }

    public int Width { get; }

    public int Height { get; }

    private int Px(double points) => Math.Max(1, (int)Math.Round(points * Scale));

    private float F(double points) => (float)(points * Scale);

    /// <summary>
    /// Draws one frame: the backdrop (<paramref name="background"/>'s frame, a picture as premultiplied BGRA, or the album
    /// look) with the card's chrome, then <paramref name="lyrics"/> in the lyric stage.
    /// </summary>
    public void Draw(LyricCardContent content, CardLook look, CoverArt? background, CardLyrics lyrics)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(look);
        ArgumentNullException.ThrowIfNull(lyrics);
        var dc = _surface.Context;
        var main = dc.Target;
        try
        {
            var key = ((object?)content, (object?)look, (object?)background);
            if (_backdropKey != key)
            {
                dc.Target = _backdrop;
                dc.BeginDraw();
                dc.Transform = Matrix3x2.Identity;
                dc.Clear(new Color4(0, 0, 0, 1));
                DrawBackdrop(dc, content, look, background);
                DrawChrome(dc, content, look);
                dc.EndDraw().CheckError();
                _backdropKey = key;
            }

            var opacity = 0.0;
            var shadowed = false;
            if (lyrics is CardLyrics.Paragraph paragraph && paragraph.Page.Words.Count > 0)
            {
                opacity = paragraph.Opacity;
                shadowed = true;
                dc.Target = _stage;
                dc.BeginDraw();
                dc.Transform = Matrix3x2.Identity;
                dc.Clear(new Color4(0, 0, 0, 0));
                DrawParagraph(dc, content, look, paragraph.Page);
                dc.EndDraw().CheckError();
            }
            else if (lyrics is CardLyrics.Timed timed)
            {
                opacity = timed.Opacity;
                DrawComposition(dc, content, timed);
            }

            dc.Target = main;
            dc.BeginDraw();
            dc.Transform = Matrix3x2.Identity;
            dc.DrawImage(_backdrop, Vector2.Zero, InterpolationMode.NearestNeighbor, CompositeMode.SourceCopy);
            if (opacity > 0.001)
            {
                var at = new Vector2(F(_stageRect.X), F(_stageRect.Y));
                if (shadowed)
                {
                    // The Mac's .shadow(color: .black.opacity(0.35), radius: 12, y: 3).
                    _shadow.SetInput(0, _stage, true);
                    _shadow.BlurStandardDeviation = F(12) / 2;
                    _shadow.Color = new Vector4(0, 0, 0, (float)(0.35 * opacity));
                    dc.DrawImage(_shadow.Output, at + new Vector2(0, F(3)), InterpolationMode.Linear, CompositeMode.SourceOver);
                    _shadow.SetInput(0, null!, true);
                }
                // A timed line takes the text size as a scale of the whole stage about its centre (the Mac fits the line
                // to that share of the stage); a paragraph was set at its size already.
                var share = shadowed ? 1f : (float)Math.Clamp(content.TextScale, 0.3, 1.5);
                float w = _stage.PixelSize.Width, h = _stage.PixelSize.Height;
                var rect = new Vortice.RawRectF(at.X + w * (1 - share) / 2, at.Y + h * (1 - share) / 2, at.X + w * (1 + share) / 2, at.Y + h * (1 + share) / 2);
                dc.DrawBitmap(_stage, rect, (float)Math.Clamp(opacity, 0, 1),
                              share == 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityCubic, null, null);
            }
            dc.EndDraw().CheckError();
        }
        finally
        {
            dc.Target = main;
            main?.Dispose();
        }
    }

    /// <summary>The frame's pixels: premultiplied BGRA (opaque), top row first, <see cref="Width"/> × 4 bytes a row.</summary>
    public void ReadPixels(byte[] pixels) => _surface.ReadPixels(pixels);

    public byte[] ReadPixels() => _surface.ReadPixels();

    // ---- The backdrop ----

    private void DrawBackdrop(ID2D1DeviceContext dc, LyricCardContent content, CardLook look, CoverArt? background)
    {
        var whole = new Vortice.RawRectF(0, 0, Width, Height);
        if (background is not null)
        {
            // The Mac: black, the picture filling the card, an even veil, then a deeper one under the credits.
            DrawFilled(dc, Bitmap(dc, ref _picture, background), background.Width, background.Height, 1, 1);
            _brush.Color = new Color4(0, 0, 0, 0.42f);
            dc.FillRectangle(whole, _brush);
            using var veil = Gradient(dc, [(0, new Color4(0, 0, 0, 0)), (1, new Color4(0, 0, 0, 0.5f))],
                                      new Vector2(Width / 2f, Height / 2f), new Vector2(Width / 2f, Height));
            dc.FillRectangle(whole, veil.Brush);
            return;
        }
        // The album colours, the cover blurred over them, and a scrim darkest at the bottom right.
        var album = CardLook.AlbumOf(look.Palette);
        var stops = album.WallpaperStops;
        var colours = new (float, Color4)[stops.Length];
        for (var i = 0; i < stops.Length; i++) colours[i] = (i / (float)Math.Max(1, stops.Length - 1), new Color4(stops[i].X, stops[i].Y, stops[i].Z, 1));
        using (var field = Gradient(dc, colours, Vector2.Zero, new Vector2(Width, Height))) dc.FillRectangle(whole, field.Brush);
        if (look.Cover is { } cover && Bitmap(dc, ref _cover, cover) is { } art)
        {
            _blur.SetInput(0, art, true);
            var fill = Math.Max(Width / (double)cover.Width, Height / (double)cover.Height) * 1.22;
            // SwiftUI's blur(radius: 108) on the card; the radius read as two standard deviations.
            _blur.StandardDeviation = (float)(F(108) / 2 / fill);
            var before = dc.Transform;
            dc.Transform = Matrix3x2.CreateTranslation(-cover.Width / 2f, -cover.Height / 2f) * Matrix3x2.CreateScale((float)fill)
                           * Matrix3x2.CreateTranslation(Width / 2f, Height / 2f);
            using (var layer = dc.CreateLayer(null))
            {
                dc.PushLayer(new LayerParameters1 { ContentBounds = new Vortice.RawRectF(float.MinValue / 4, float.MinValue / 4, float.MaxValue / 4, float.MaxValue / 4), Opacity = 0.62f }, layer);
                dc.DrawImage(_blur.Output, InterpolationMode.Linear, CompositeMode.SourceOver);
                dc.PopLayer();
            }
            dc.Transform = before;
            _blur.SetInput(0, null!, true);
        }
        using var scrim = Gradient(dc,
            [(0, new Color4(0, 0, 0, (float)LyricCardLayout.LightestScrim)), (0.5f, new Color4(0, 0, 0, 0.64f)), (1, new Color4(0, 0, 0, 0.86f))],
            Vector2.Zero, new Vector2(Width, Height));
        dc.FillRectangle(whole, scrim.Brush);
    }

    /// <summary>A bitmap scaled to fill the card (cropping), at <paramref name="opacity"/>.</summary>
    private void DrawFilled(ID2D1DeviceContext dc, ID2D1Bitmap1? bitmap, int width, int height, double extraScale, float opacity)
    {
        if (bitmap is null) return;
        var fill = Math.Max(Width / (double)width, Height / (double)height) * extraScale;
        float w = (float)(width * fill), h = (float)(height * fill);
        var rect = new Vortice.RawRectF((Width - w) / 2, (Height - h) / 2, (Width + w) / 2, (Height + h) / 2);
        dc.DrawBitmap(bitmap, rect, opacity, InterpolationMode.HighQualityCubic, null, null);
    }

    private static unsafe ID2D1Bitmap1? Bitmap(ID2D1DeviceContext dc, ref (CoverArt? Art, ID2D1Bitmap1? Bitmap) cache, CoverArt art)
    {
        if (ReferenceEquals(cache.Art, art) && cache.Bitmap is not null) return cache.Bitmap;
        cache.Bitmap?.Dispose();
        fixed (byte* pixels = art.Pixels)
        {
            cache = (art, dc.CreateBitmap(new SizeI(art.Width, art.Height), (IntPtr)pixels, (uint)(art.Width * 4),
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None)));
        }
        return cache.Bitmap;
    }

    private sealed class GradientBrush(ID2D1GradientStopCollection stops, ID2D1LinearGradientBrush brush) : IDisposable
    {
        public ID2D1LinearGradientBrush Brush => brush;

        public void Dispose()
        {
            brush.Dispose();
            stops.Dispose();
        }
    }

    private static GradientBrush Gradient(ID2D1DeviceContext dc, (float At, Color4 Colour)[] stops, Vector2 from, Vector2 to)
    {
        var collection = dc.CreateGradientStopCollection(stops.Select(s => new GradientStop(s.At, s.Colour)).ToArray(), Gamma.StandardRgb, ExtendMode.Clamp);
        var brush = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(from, to), collection);
        return new GradientBrush(collection, brush);
    }

    // ---- The chrome: cover, credits and the mark ----

    private void DrawChrome(ID2D1DeviceContext dc, LyricCardContent content, CardLook look)
    {
        var size = content.Size;
        var cover = content.HasArtwork ? look.Cover : null;
        var hasCover = cover is not null;
        var inset = LyricCardLayout.Inset;
        if (content.Aspect.HasSidebar() && content.HasLyrics && content.HasTrackDetails)
        {
            // A column beside the words: (space) cover, credits, (space), the mark; 36 points between each.
            var width = LyricCardLayout.SidebarWidth;
            var credits = MeasureCredits(content, 56, 36, width);
            var used = (hasCover ? width + 36 : 0) + credits + 36 + LyricCardLayout.SignatureHeight + 36;
            var free = Math.Max(0, size.Height - 2 * inset - used);
            var y = inset + free / 2;
            if (hasCover)
            {
                DrawCover(dc, cover!, inset, y, width);
                y += width + 36;
            }
            DrawCredits(dc, content, inset, y, width, 56, 36, centred: false);
            DrawSignature(dc, inset, size.Height - inset - LyricCardLayout.SignatureHeight);
            return;
        }
        if (!content.HasLyrics)
        {
            // The song alone: a big cover and its credits in the middle, the mark in the corner.
            var credits = MeasureCredits(content, 74, 42, size.Width - 2 * inset);
            var block = (hasCover ? 420 + 38 : 0) + credits;
            var room = size.Height - 2 * inset - LyricCardLayout.SignatureHeight;
            var y = inset + Math.Max(0, (room - block) / 2);
            if (hasCover)
            {
                DrawCover(dc, cover!, (size.Width - 420) / 2, y, 420);
                y += 420 + 38;
            }
            DrawCredits(dc, content, inset, y, size.Width - 2 * inset, 74, 42, centred: true);
            DrawSignature(dc, size.Width - inset - SignatureWidth(), size.Height - inset - LyricCardLayout.SignatureHeight);
            return;
        }
        if (content.HasTrackDetails)
        {
            // The footer: cover, credits, and the mark at its foot.
            var top = size.Height - inset - LyricCardLayout.TrackFooterHeight;
            var x = inset;
            if (hasCover)
            {
                DrawCover(dc, cover!, x, top + (LyricCardLayout.TrackFooterHeight - 190) / 2, 190);
                x += 190 + 28;
            }
            var signature = SignatureWidth();
            var width = size.Width - inset - x - 28 - signature;
            var credits = MeasureCredits(content, 54, 36, width);
            DrawCredits(dc, content, x, top + (LyricCardLayout.TrackFooterHeight - credits) / 2, width, 54, 36, centred: false);
            DrawSignature(dc, size.Width - inset - signature, size.Height - inset - LyricCardLayout.SignatureHeight);
            return;
        }
        DrawSignature(dc, size.Width - inset - SignatureWidth(), size.Height - inset - LyricCardLayout.SignatureHeight);
    }

    /// <summary>The cover in a rounded tile with a hairline and a soft shadow (the Mac's <c>cover(_:size:)</c>).</summary>
    private void DrawCover(ID2D1DeviceContext dc, CoverArt cover, double x, double y, double side)
    {
        var bitmap = Bitmap(dc, ref _cover, cover);
        if (bitmap is null) return;
        var rect = new Vortice.RawRectF(F(x), F(y), F(x + side), F(y + side));
        var rounded = new RoundedRectangle(rect, F(side * 0.15), F(side * 0.15));
        // Shadow: black 36%, radius 22, 9 points down. The tile's shape is recorded as a command list for the effect.
        using (var shape = dc.CreateCommandList())
        {
            var previous = dc.Target;
            dc.Target = shape;
            _brush.Color = new Color4(0, 0, 0, 1);
            dc.FillRoundedRectangle(rounded, _brush);
            dc.Target = previous;
            previous?.Dispose();
            shape.Close();
            _shadow.SetInput(0, shape, true);
            _shadow.BlurStandardDeviation = F(22) / 2;
            _shadow.Color = new Vector4(0, 0, 0, 0.36f);
            var before = dc.Transform;
            dc.Transform = Matrix3x2.CreateTranslation(0, F(9)) * before;
            dc.DrawImage(_shadow.Output, InterpolationMode.Linear, CompositeMode.SourceOver);
            dc.Transform = before;
            _shadow.SetInput(0, null!, true);
        }
        _brush.Color = new Color4(0, 0, 0, 0.40f);
        dc.FillRoundedRectangle(rounded, _brush);
        using (var geometry = _surface.Factory.CreateRoundedRectangleGeometry(rounded))
        using (var layer = dc.CreateLayer(null))
        {
            dc.PushLayer(new LayerParameters1 { ContentBounds = rect, GeometricMask = geometry, MaskAntialiasMode = AntialiasMode.PerPrimitive, Opacity = 1 }, layer);
            // Fitted whole (scaledToFit), centred.
            var fit = Math.Min(side / cover.Width, side / cover.Height);
            double w = cover.Width * fit, h = cover.Height * fit;
            dc.DrawBitmap(bitmap, new Vortice.RawRectF(F(x + (side - w) / 2), F(y + (side - h) / 2), F(x + (side + w) / 2), F(y + (side + h) / 2)),
                          1, InterpolationMode.HighQualityCubic, null, null);
            dc.PopLayer();
        }
        _brush.Color = new Color4(1, 1, 1, 0.18f);
        var inner = new RoundedRectangle(new Vortice.RawRectF(rect.Left + F(1), rect.Top + F(1), rect.Right - F(1), rect.Bottom - F(1)),
                                         F(side * 0.15) - F(1), F(side * 0.15) - F(1));
        dc.DrawRoundedRectangle(inner, _brush, F(2));
    }

    private IDWriteTextLayout CreditLayout(string text, double size, FontWeight weight, double width, bool centred)
    {
        var dwrite = _surface.DWrite;
        var locale = TextKit.LocaleFor(text);
        // Two lines at most, shrunk to 68% before an ellipsis cuts it (the Mac's lineLimit(2), minimumScaleFactor(0.68)).
        for (var shrink = 1.0; ; shrink -= 0.04)
        {
            using var format = dwrite.CreateTextFormat("Segoe UI Variable Display", null, weight, Vortice.DirectWrite.FontStyle.Normal,
                FontStretch.Normal, F(size * shrink), locale);
            var layout = dwrite.CreateTextLayout(text, format, F(width), F(size * 4));
            layout.WordWrapping = WordWrapping.Wrap;
            layout.TextAlignment = centred ? TextAlignment.Center : TextAlignment.Leading;
            layout.ReadingDirection = ScriptDirection.IsRightToLeft(text) ? ReadingDirection.RightToLeft : ReadingDirection.LeftToRight;
            if (weight == FontWeight.SemiBold)
            {
                using var spaced = layout.QueryInterface<IDWriteTextLayout1>();
                spaced.SetCharacterSpacing(0, -F(size * shrink * 0.025), 0, new TextRange(0, (uint)text.Length));
            }
            if (layout.Metrics.LineCount <= 2 || shrink <= 0.68 + 1e-6)
            {
                if (layout.Metrics.LineCount > 2)
                {
                    var lines = layout.LineMetrics;
                    layout.MaxHeight = lines[0].Height + lines[1].Height + 1;
                    using var sign = dwrite.CreateEllipsisTrimmingSign(layout);
                    layout.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Word }, sign);
                }
                return layout;
            }
            layout.Dispose();
        }
    }

    private static double TwoLineHeight(IDWriteTextLayout layout)
    {
        var lines = layout.LineMetrics;
        double total = 0;
        for (var i = 0; i < Math.Min(2, lines.Length); i++) total += lines[i].Height;
        return total;
    }

    /// <summary>The credits' height in card points.</summary>
    private double MeasureCredits(LyricCardContent content, double titleSize, double artistSize, double width)
    {
        double height = 0;
        if (content.Title.Length > 0)
        {
            using var title = CreditLayout(content.Title, titleSize, FontWeight.SemiBold, width, false);
            height += TwoLineHeight(title) / Scale;
        }
        if (content.Artist.Length > 0)
        {
            using var artist = CreditLayout(content.Artist, artistSize, FontWeight.Medium, width, false);
            if (height > 0) height += 9;
            height += TwoLineHeight(artist) / Scale;
        }
        return height;
    }

    private void DrawCredits(ID2D1DeviceContext dc, LyricCardContent content, double x, double y, double width, double titleSize,
                             double artistSize, bool centred)
    {
        if (content.Title.Length > 0)
        {
            using var title = CreditLayout(content.Title, titleSize, FontWeight.SemiBold, width, centred);
            _brush.Color = new Color4(1, 1, 1, 1);
            dc.DrawTextLayout(new Vector2(F(x), F(y)), title, _brush, DrawTextOptions.EnableColorFont);
            y += TwoLineHeight(title) / Scale + 9;
        }
        if (content.Artist.Length > 0)
        {
            using var artist = CreditLayout(content.Artist, artistSize, FontWeight.Medium, width, centred);
            _brush.Color = new Color4(1, 1, 1, 0.80f);
            dc.DrawTextLayout(new Vector2(F(x), F(y)), artist, _brush, DrawTextOptions.EnableColorFont);
        }
    }

    private IDWriteTextLayout SignatureLayout()
    {
        using var format = _surface.DWrite.CreateTextFormat("Segoe UI Variable Display", null, FontWeight.SemiBold,
            Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, F(28), "en-us");
        var layout = _surface.DWrite.CreateTextLayout("Lunalyr", format, 100_000, F(44));
        layout.WordWrapping = WordWrapping.NoWrap;
        return layout;
    }

    /// <summary>The mark and its name, in card points.</summary>
    private double SignatureWidth()
    {
        using var layout = SignatureLayout();
        return 40 + 10 + layout.Metrics.WidthIncludingTrailingWhitespace / Scale;
    }

    /// <summary>The Lunalyr mark (40 points) and "Lunalyr" in white at 72%, <see cref="LyricCardLayout.SignatureHeight"/> tall.</summary>
    private void DrawSignature(ID2D1DeviceContext dc, double x, double y)
    {
        DrawMark(dc, x, y + (LyricCardLayout.SignatureHeight - 40) / 2, 40);
        using var layout = SignatureLayout();
        _brush.Color = new Color4(1, 1, 1, 0.72f);
        var height = layout.Metrics.Height;
        dc.DrawTextLayout(new Vector2(F(x + 50), F(y) + (F(LyricCardLayout.SignatureHeight) - height) / 2), layout, _brush, DrawTextOptions.None);
    }

    /// <summary>
    /// The Lunalyr mark (the Mac's <c>LunalyrMark</c>, Lunalyr's own): a crescent moon, a coral tile with a word on it,
    /// a fainter line under it and a four-pointed star, drawn in its 1024-unit artwork square.
    /// </summary>
    private void DrawMark(ID2D1DeviceContext dc, double x, double y, double size)
    {
        var factory = _surface.Factory;
        var unit = (float)(size * Scale / 760);
        var before = dc.Transform;
        dc.Transform = Matrix3x2.CreateTranslation(-140, -110) * Matrix3x2.CreateScale(unit) * Matrix3x2.CreateTranslation(F(x), F(y)) * before;
        var ink = new Color4(0.97f, 0.95f, 1, 1);
        using (var outer = factory.CreateEllipseGeometry(new Ellipse(new Vector2(168 + 300, 232 + 300), 300, 300)))
        using (var bite = factory.CreateEllipseGeometry(new Ellipse(new Vector2(340 + 280, 150 + 280), 280, 280)))
        using (var moon = factory.CreatePathGeometry())
        {
            using (var sink = moon.Open())
            {
                outer.CombineWithGeometry(bite, CombineMode.Exclude, null, 0.25f, sink);
                sink.Close();
            }
            _brush.Color = ink;
            dc.FillGeometry(moon, _brush);
        }
        _brush.Color = new Color4(1, 0.5f, 0.42f, 1);
        dc.FillRoundedRectangle(new RoundedRectangle(new Vortice.RawRectF(430, 470, 840, 574), 30, 30), _brush);
        _brush.Color = new Color4(0.17f, 0.08f, 0.30f, 1);
        dc.FillRoundedRectangle(new RoundedRectangle(new Vortice.RawRectF(470, 505, 700, 539), 17, 17), _brush);
        _brush.Color = new Color4(0.97f, 0.95f, 1, 0.55f);
        dc.FillRoundedRectangle(new RoundedRectangle(new Vortice.RawRectF(470, 640, 760, 678), 19, 19), _brush);
        using (var star = factory.CreatePathGeometry())
        {
            using (var sink = star.Open())
            {
                const float cx = 790, cy = 290, outerRadius = 78, innerRadius = outerRadius * 0.28f;
                for (var i = 0; i < 8; i++)
                {
                    var angle = i * MathF.PI / 4 - MathF.PI / 2;
                    var r = i % 2 == 0 ? outerRadius : innerRadius;
                    var p = new Vector2(cx + MathF.Cos(angle) * r, cy + MathF.Sin(angle) * r);
                    if (i == 0) sink.BeginFigure(p, FigureBegin.Filled);
                    else sink.AddLine(p);
                }
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            _brush.Color = ink;
            dc.FillGeometry(star, _brush);
        }
        dc.Transform = before;
    }

    // ---- The lyrics ----

    /// <summary>
    /// The page as one shaped paragraph in the lyric stage (the Mac's <c>LyricCardView.lyrics</c>): each word in its own
    /// colour along the line, sized by how much the page holds, wrapped to the stage, fitted with air above and below,
    /// set from the left (the right for a right-to-left line) and centred in height.
    /// </summary>
    private void DrawParagraph(ID2D1DeviceContext dc, LyricCardContent content, CardLook look, CardPage page)
    {
        var words = page.Words;
        var stage = _stageRect;
        var count = Math.Max(1, LyricCardLayout.Weight(words.Where(w => w != LyricCardLayout.LineBreak)));
        var nominal = 146 + 24 * Math.Max(0, 1 - count / 8);
        var point = nominal * Math.Min(1, Math.Sqrt(9 / count) * 1.15);
        var rightToLeft = page.RightToLeft;
        var text = new System.Text.StringBuilder();
        var ranges = new List<(int Start, int Length, int Index)>();
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            if (word == LyricCardLayout.LineBreak)
            {
                text.Append('\n');
                continue;
            }
            if (index > 0 && words[index - 1] != LyricCardLayout.LineBreak) text.Append(' ');
            ranges.Add((text.Length, word.Length, index));
            text.Append(word);
        }
        var shown = text.ToString();
        var dwrite = _surface.DWrite;
        var family = InstalledOr(look.Style.FontFamily);
        using var format = dwrite.CreateTextFormat(family, null, FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal, F(point), TextKit.LocaleFor(shown));
        using var layout = dwrite.CreateTextLayout(shown, format, F(stage.Width), F(stage.Height * 8));
        layout.WordWrapping = WordWrapping.Wrap;
        layout.ReadingDirection = rightToLeft ? ReadingDirection.RightToLeft : ReadingDirection.LeftToRight;
        layout.TextAlignment = TextAlignment.Leading;
        // SwiftUI's lineSpacing adds 12% of the size between lines.
        var first = layout.LineMetrics;
        if (first.Length > 0)
            layout.SetLineSpacing(LineSpacingMethod.Uniform, first[0].Height + F(point * 0.12), first[0].Baseline + F(point * 0.06));
        var ink = LyricInks.Settled(look.Palette, look.Style);
        var brushes = new List<ID2D1SolidColorBrush>();
        try
        {
            foreach (var (start, length, index) in ranges)
            {
                var slot = words.Count > 1 ? index / (double)(words.Count - 1) : 0.5;
                var colour = ink.Sample(slot, (ulong)index).Sung;
                var brush = dc.CreateSolidColorBrush(new Color4((float)colour.R, (float)colour.G, (float)colour.B, (float)colour.A));
                brushes.Add(brush);
                layout.SetDrawingEffect(brush, new TextRange((uint)start, (uint)length));
            }
            var metrics = layout.Metrics;
            double width = metrics.Width / Scale, height = metrics.Height / Scale;
            // Long lines are fitted with air above and below, so they sit in the middle of the stage instead of crowding
            // the credits underneath.
            var air = Math.Min(stage.Height * 0.16, 96);
            var share = Math.Clamp(content.TextScale, 0.3, 1.5);
            var available = new Lunalyr.Core.Compositions.SizeD(stage.Width * (count < 4 ? 0.82 : 0.9) * share, (stage.Height - 2 * air) * share);
            var fit = Lunalyr.Core.Compositions.LyricStageFit.Scale(new(width, height), available, count < 4 ? 2.6 : 1);
            // In the stage bitmap: the block's leading edge on the stage's, centred in height, scaled about that point.
            var anchor = new Vector2(rightToLeft ? F(stage.Width) : 0, F(stage.Height) / 2);
            var origin = new Vector2(0, anchor.Y - metrics.Height / 2 - metrics.Top);
            dc.Transform = Matrix3x2.CreateTranslation(-anchor) * Matrix3x2.CreateScale((float)fit) * Matrix3x2.CreateTranslation(anchor);
            _brush.Color = new Color4(1, 1, 1, 1);
            dc.DrawTextLayout(origin, layout, _brush, DrawTextOptions.EnableColorFont);
            dc.Transform = Matrix3x2.Identity;
        }
        finally
        {
            foreach (var brush in brushes) brush.Dispose();
        }
    }

    private string InstalledOr(string family)
    {
        using var fonts = _surface.DWrite.GetSystemFontCollection(false);
        if (!string.IsNullOrWhiteSpace(family) && fonts.FindFamilyName(family, out _)) return family;
        return fonts.FindFamilyName("Segoe UI Variable Display", out _) ? "Segoe UI Variable Display" : "Segoe UI";
    }

    /// <summary>A timed page drawn by its composition into the stage bitmap (the Mac's <c>LyricCardFrame</c>).</summary>
    private void DrawComposition(ID2D1DeviceContext dc, LyricCardContent content, CardLyrics.Timed timed)
    {
        var id = _factories.ContainsKey(timed.Scene.CompositionId) ? timed.Scene.CompositionId : "editorial";
        var size = new Vector2(_stage.PixelSize.Width, _stage.PixelSize.Height);
        if (!_layers.TryGetValue(id, out var made))
        {
            if (!_factories.TryGetValue(id, out var factory)) return;
            var layer = factory.Create();
            layer.CreateResources(dc, _surface.DWrite, size, (float)Scale);
            _layers[id] = made = (layer, size);
        }
        dc.Target = _stage;
        dc.BeginDraw();
        dc.Transform = Matrix3x2.Identity;
        dc.Clear(new Color4(0, 0, 0, 0));
        var frame = new FrameContext
        {
            Dc = dc, DWrite = _surface.DWrite, D2DFactory = _surface.Factory, Size = size, Scale = (float)Scale,
            WallSeconds = 6 + timed.CardTime, SongSeconds = timed.SongTime, IsPlaying = true, Scene = timed.Scene.Scene,
            DeltaSeconds = Math.Clamp(timed.Delta, 0, 0.25),
        };
        made.Layer.Render(frame);
        dc.Transform = Matrix3x2.Identity;
        dc.EndDraw().CheckError();
    }

    public void Dispose()
    {
        foreach (var (layer, _) in _layers.Values) layer.Dispose();
        _layers.Clear();
        _cover.Bitmap?.Dispose();
        _picture.Bitmap?.Dispose();
        _blur.Dispose();
        _shadow.Dispose();
        _brush.Dispose();
        _backdrop.Dispose();
        _stage.Dispose();
        _surface.Dispose();
    }
}
