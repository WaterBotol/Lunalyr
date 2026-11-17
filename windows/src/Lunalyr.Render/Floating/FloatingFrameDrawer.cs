// The floating lyrics' surroundings (the Mac's FloatingLyricsFrame, FloatingLyricsBackdrop and FloatingEditChrome in
// FloatingLyricsView.swift, Lunalyr's own lines; its three Verso-attributed lines are property-wrapper declarations
// with no counterpart here): a soft dark shade fading out towards every edge, one soft shadow for the whole lyric
// stage, and edit mode's outline, hint, Done and grip. The engine and tools/FloatingPreview both draw through this
// class, so the previews show what ships.

using System.Numerics;
using Lunalyr.Core.Floating;
using Lunalyr.Render.Scene;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.Render.Floating;

/// <summary>Draws the floating window's frame around its lyric stage. One per surface; all calls on the render thread.</summary>
public sealed class FloatingFrameDrawer : IDisposable
{
    /// <summary>How faint the window gets while the pointer is over it (the Mac's <c>pointerAlpha</c>).</summary>
    public const double PointerAlpha = 0.18;

    /// <summary>The window's fade in and out, seconds (the Mac's 0.25).</summary>
    public const double FadeSeconds = 0.25;

    private const int ShadeColumns = 256, ShadeRows = 128;

    private ID2D1DeviceContext? _dc;
    private ID2D1Bitmap1? _shade;
    private ID2D1Bitmap1? _stage;
    private Vortice.Direct2D1.Effects.Shadow? _shadow;
    private ID2D1Image? _shadowOut;
    private ID2D1SolidColorBrush? _brush;
    private ID2D1StrokeStyle? _dash;
    private IDWriteTextFormat? _hintFormat, _doneFormat, _placeholderFormat;
    private Vector2 _size;
    // The box inside the window (the shade, the chrome), and the see-through margin round it (the Mac's 7e2b22b9).
    private Vector2 _box;
    private float _inset;
    private float _scale = 1;

    /// <summary>The lyric stage, drawn first, then shadowed and laid over the shade. Null before <see cref="BeginStage"/>.</summary>
    public ID2D1Bitmap1? Stage => _stage;

    /// <summary>
    /// Makes (or keeps) the resources for a surface of <paramref name="size"/> pixels at <paramref name="scale"/>, its box
    /// <paramref name="inset"/> pixels in from every edge (the margin the lyrics may spill into).
    /// </summary>
    public void Prepare(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale, float inset = 0)
    {
        _inset = Math.Max(0, Math.Min(inset, Math.Min(size.X, size.Y) / 2));
        _box = new Vector2(Math.Max(0, size.X - 2 * _inset), Math.Max(0, size.Y - 2 * _inset));
        if (!ReferenceEquals(dc, _dc)) ReleaseResources();
        if (_dc is null)
        {
            _dc = dc;
            _shade = ShadeBitmap(dc);
            _brush = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
            _shadow = new Vortice.Direct2D1.Effects.Shadow(dc) { Optimization = ShadowOptimization.Balanced };
            _shadowOut = _shadow.Output;
        }
        if (size != _size || scale != _scale || _stage is null)
        {
            _size = size;
            _scale = scale > 0 && float.IsFinite(scale) ? scale : 1;
            _stage?.Dispose();
            _stage = dc.CreateBitmap(new SizeI(Math.Max(1, (int)size.X), Math.Max(1, (int)size.Y)), IntPtr.Zero, 0,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
            _shadow!.SetInput(0, _stage, true);
            _dash?.Dispose();
            using (var factory = dc.Factory)
                _dash = factory.CreateStrokeStyle(new StrokeStyleProperties { DashStyle = DashStyle.Custom, DashCap = CapStyle.Flat },
                    [(float)(FloatingEditChrome.DashOn / FloatingEditChrome.OutlineWidth), (float)(FloatingEditChrome.DashOff / FloatingEditChrome.OutlineWidth)]);
            DropText();
            _hintFormat = TextFormat(dwrite, (float)(FloatingEditChrome.HintTextSize * _scale), FontWeight.Medium);
            _doneFormat = TextFormat(dwrite, (float)(13 * _scale), FontWeight.SemiBold);
            _placeholderFormat = TextFormat(dwrite, (float)(FloatingEditChrome.PlaceholderTextSize * _scale), FontWeight.SemiBold);
        }
    }

    private static IDWriteTextFormat TextFormat(IDWriteFactory dwrite, float size, FontWeight weight)
    {
        var format = dwrite.CreateTextFormat("Segoe UI Variable Text", null, weight, FontStyle.Normal, FontStretch.Normal, size, "en-us");
        format.WordWrapping = WordWrapping.NoWrap;
        return format;
    }

    /// <summary>Points the context at the stage bitmap and clears it.</summary>
    public void BeginStage(ID2D1DeviceContext dc)
    {
        dc.Target = _stage;
        dc.Transform = Matrix3x2.Identity;
        dc.Clear(new Color4(0, 0, 0, 0));
    }

    /// <summary>Points the context back at <paramref name="target"/>.</summary>
    public static void EndStage(ID2D1DeviceContext dc, ID2D1Image target)
    {
        dc.Target = target;
        dc.Transform = Matrix3x2.Identity;
    }

    /// <summary>
    /// The window's content over a cleared (transparent) target: the shade, the stage with its shadow, and in edit mode
    /// the chrome, all at <paramref name="opacity"/> (the window's fade).
    /// </summary>
    /// <param name="hasStage">A stage was drawn this frame (<see cref="BeginStage"/>…<see cref="EndStage"/>).</param>
    public void Compose(ID2D1DeviceContext dc, IDWriteFactory dwrite, double opacity, double backdrop, bool reduceTransparency,
                        bool hasStage, bool editing, bool placeholder, Vector4 accent)
    {
        if (_dc is null || opacity < 0.002) return;
        var whole = new RawRectF(0, 0, _size.X, _size.Y);
        bool pushed = opacity < 0.999;
        if (pushed)
            dc.PushLayer(new LayerParameters1
            {
                ContentBounds = whole, MaskTransform = Matrix3x2.Identity, MaskAntialiasMode = AntialiasMode.PerPrimitive,
                Opacity = (float)opacity, LayerOptions = LayerOptions1.None,
            }, null!);
        try
        {
            // The shade covers the box only; the stage (laid out in the box) may draw into the margin round it.
            dc.Transform = Matrix3x2.CreateTranslation(_inset, _inset);
            DrawBackdrop(dc, backdrop, reduceTransparency);
            dc.Transform = Matrix3x2.Identity;
            if (hasStage && _stage is not null)
            {
                // One soft shadow for the whole stage, so thin type still reads where the shade fades out.
                var strength = Math.Clamp(backdrop, 0, 1);
                _shadow!.BlurStandardDeviation = (float)(3 * _scale);
                _shadow.Color = new Vector4(0, 0, 0, (float)(0.35 + 0.35 * strength));
                dc.DrawImage(_shadowOut!, new Vector2(0, _scale), null, InterpolationMode.Linear, CompositeMode.SourceOver);
                dc.DrawBitmap(_stage, whole, 1, InterpolationMode.NearestNeighbor, null, null);
            }
            if (editing)
            {
                dc.Transform = Matrix3x2.CreateTranslation(_inset, _inset);
                DrawEditChrome(dc, dwrite, accent, placeholder);
                dc.Transform = Matrix3x2.Identity;
            }
        }
        finally
        {
            if (pushed) dc.PopLayer();
        }
    }

    /// <summary>
    /// A soft dark shade fading out towards every edge, so there is no box; with transparency effects off, a plain panel
    /// as dark as the shade's strength. Shade at 0% draws neither.
    /// </summary>
    private void DrawBackdrop(ID2D1DeviceContext dc, double strength, bool reduceTransparency)
    {
        if (reduceTransparency)
        {
            var panel = FloatingBackdrop.Panel(strength);
            if (panel <= 0) return;
            var inset = 4 * _scale;
            var corner = (float)Math.Min(22 * _scale, (_box.Y - 2 * inset) * 0.2);
            _brush!.Color = new Color4(0, 0, 0, (float)panel);
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(inset, inset, _box.X - inset, _box.Y - inset), corner, corner), _brush);
            return;
        }
        if (strength <= FloatingBackdrop.Off) return;
        dc.DrawBitmap(_shade!, new RawRectF(0, 0, _box.X, _box.Y), (float)(FloatingBackdrop.MaximumShade * Math.Min(1, strength)),
            InterpolationMode.Linear, null, null);
    }

    /// <summary>Black, clear at both ends and full from <paramref name="edge"/> in from each (the Mac's <c>fade(edge:)</c>).</summary>
    public static double Fade(double t, double edge) =>
        t <= 0 || t >= 1 ? 0 : t < edge ? t / edge : t > 1 - edge ? (1 - t) / edge : 1;

    private static unsafe ID2D1Bitmap1 ShadeBitmap(ID2D1DeviceContext dc)
    {
        // The two gradient masks multiplied: 22% in from the sides, 30% in from the top and bottom.
        var pixels = new byte[ShadeColumns * ShadeRows * 4];
        for (var y = 0; y < ShadeRows; y++)
        {
            var v = Fade((y + 0.5) / ShadeRows, 0.3);
            for (var x = 0; x < ShadeColumns; x++)
            {
                var a = (byte)Math.Round(255 * v * Fade((x + 0.5) / ShadeColumns, 0.22));
                pixels[(y * ShadeColumns + x) * 4 + 3] = a; // premultiplied black: colour stays 0
            }
        }
        fixed (byte* data = pixels)
            return dc.CreateBitmap(new SizeI(ShadeColumns, ShadeRows), (IntPtr)data, ShadeColumns * 4,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
    }

    /// <summary>Edit position: an outline, a hint, Done, a grip in the corner, and "Lyrics appear here" with nothing playing.</summary>
    private void DrawEditChrome(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector4 accent, bool placeholder)
    {
        var s = _scale;
        var brush = _brush!;
        var accentColour = new Color4(accent.X, accent.Y, accent.Z, 1);
        var radius = (float)(FloatingEditChrome.CornerRadius * s);
        var line = (float)(FloatingEditChrome.OutlineWidth * s);
        var outline = new RoundedRectangle(new RawRectF(line / 2, line / 2, _box.X - line / 2, _box.Y - line / 2), radius, radius);
        brush.Color = new Color4(0, 0, 0, 0.18f);
        dc.FillRoundedRectangle(outline, brush);
        brush.Color = accentColour;
        dc.DrawRoundedRectangle(outline, brush, line, _dash!);

        if (placeholder)
        {
            using var layout = dwrite.CreateTextLayout(FloatingEditChrome.Placeholder, _placeholderFormat!, _box.X, _box.Y);
            layout.TextAlignment = TextAlignment.Center;
            layout.ParagraphAlignment = ParagraphAlignment.Center;
            brush.Color = new Color4(0, 0, 0, 0.6f);
            dc.DrawTextLayout(new Vector2(0, 1.5f * s), layout, brush, DrawTextOptions.None);
            brush.Color = new Color4(1, 1, 1, 0.85f);
            dc.DrawTextLayout(Vector2.Zero, layout, brush, DrawTextOptions.None);
        }

        // The hint, top left.
        using (var hint = dwrite.CreateTextLayout(FloatingEditChrome.Hint, _hintFormat!, _box.X, 100))
        {
            var box = FloatingEditChrome.HintBox(hint.Metrics.WidthIncludingTrailingWhitespace, s);
            var done = FloatingEditChrome.Done(_box.X, _box.Y, s);
            // A narrow window keeps the hint clear of Done.
            if (box.X + box.Width <= done.X - 6 * s)
            {
                var r = (float)(box.Height / 2);
                brush.Color = new Color4(0, 0, 0, 0.6f);
                dc.FillRoundedRectangle(new RoundedRectangle(Rect(box), r, r), brush);
                brush.Color = new Color4(1, 1, 1, 1);
                dc.DrawTextLayout(new Vector2((float)(box.X + 10 * s), (float)(box.Y + (box.Height - hint.Metrics.Height) / 2)), hint, brush, DrawTextOptions.None);
            }
        }

        // Done, top right.
        {
            var done = FloatingEditChrome.Done(_box.X, _box.Y, s);
            var r = (float)(6 * s);
            brush.Color = accentColour;
            dc.FillRoundedRectangle(new RoundedRectangle(Rect(done), r, r), brush);
            using var label = dwrite.CreateTextLayout(FloatingEditChrome.DoneTitle, _doneFormat!, (float)done.Width, (float)done.Height);
            label.TextAlignment = TextAlignment.Center;
            label.ParagraphAlignment = ParagraphAlignment.Center;
            brush.Color = OnAccent(accent);
            dc.DrawTextLayout(new Vector2((float)done.X, (float)done.Y), label, brush, DrawTextOptions.None);
        }

        // The grip: a circle with a two-way diagonal arrow.
        {
            var grip = FloatingEditChrome.Grip(_box.X, _box.Y, s);
            var centre = new Vector2((float)(grip.X + grip.Width / 2), (float)(grip.Y + grip.Height / 2));
            brush.Color = accentColour;
            dc.FillEllipse(new Ellipse(centre, (float)(grip.Width / 2), (float)(grip.Height / 2)), brush);
            brush.Color = OnAccent(accent);
            float a = (float)(5 * s), head = (float)(3 * s), w = (float)(1.8 * s);
            var from = centre - new Vector2(a, a);
            var to = centre + new Vector2(a, a);
            dc.DrawLine(from, to, brush, w);
            dc.DrawLine(from, from + new Vector2(head, 0), brush, w);
            dc.DrawLine(from, from + new Vector2(0, head), brush, w);
            dc.DrawLine(to, to - new Vector2(head, 0), brush, w);
            dc.DrawLine(to, to - new Vector2(0, head), brush, w);
        }
    }

    /// <summary>White on the accent, or near-black on a pale one.</summary>
    private static Color4 OnAccent(Vector4 accent) =>
        0.2126 * accent.X + 0.7152 * accent.Y + 0.0722 * accent.Z > 0.6 ? new Color4(0.08f, 0.08f, 0.08f, 1) : new Color4(1, 1, 1, 1);

    private static RawRectF Rect(Core.Motion.Box box) => new((float)box.X, (float)box.Y, (float)(box.X + box.Width), (float)(box.Y + box.Height));

    private void DropText()
    {
        _hintFormat?.Dispose(); _hintFormat = null;
        _doneFormat?.Dispose(); _doneFormat = null;
        _placeholderFormat?.Dispose(); _placeholderFormat = null;
    }

    public void ReleaseResources()
    {
        DropText();
        _shadowOut?.Dispose(); _shadowOut = null;
        _shadow?.Dispose(); _shadow = null;
        _stage?.Dispose(); _stage = null;
        _shade?.Dispose(); _shade = null;
        _brush?.Dispose(); _brush = null;
        _dash?.Dispose(); _dash = null;
        _dc = null;
        _size = default;
    }

    public void Dispose() => ReleaseResources();

    /// <summary>
    /// The scene the floating stage draws: the shared scene with the floating text size (the stage then fills nearly the
    /// whole window, and the opening title takes the middle) and the lyric colours judged against the floating shade
    /// (the Mac's <c>lyricBackdropOverride</c>). Only the floating surface sees it.
    /// </summary>
    /// <param name="behind">What the shade lies over (<see cref="FloatingBackdrop.Behind"/>): an unknown app, or a music
    /// video wallpaper as its own lyrics take it.</param>
    public static SceneSnapshot SceneFor(SceneSnapshot scene, double textSize, double backdrop, bool reduceTransparency,
                                         double behind = Core.Colour.LyricLegibility.UnknownBackdropLuminance, double inset = 0,
                                         bool titleCard = false) =>
        scene with
        {
            // The song's title has a window of its own (the Mac's b11a1ab3): the lyrics' window draws none and keeps no row
            // for it; the card's window draws only the card.
            Style = scene.Style with
            {
                FloatingTextSize = textSize, FloatingInset = inset, FloatingTitleOnly = titleCard,
                OpeningTitle = titleCard && scene.Style.OpeningTitle,
            },
            Palette = scene.Palette with { TextBackdrop = FloatingBackdrop.Luminance(backdrop, reduceTransparency, behind), TextUnbacked = null },
        };
}
