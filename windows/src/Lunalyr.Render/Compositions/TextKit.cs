using System.Numerics;
using Lunalyr.Core.Lyrics.Readings;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Lunalyr.Render.Compositions;

/// <summary>The faces a composition sets type in.</summary>
internal enum Face
{
    /// <summary>The lyric family (the style's), regular words.</summary>
    Regular,
    /// <summary>The lyric family, held words and Spoken (the Mac's heavy cut).</summary>
    Heavy,
    /// <summary>Editorial's contrasting face: a serif against the sans.</summary>
    Contrast,
    /// <summary>Readings and notices: the lyric family, lighter.</summary>
    Quiet,
}

/// <summary>
/// Fonts, text layouts and baked text for one surface. Text is rasterised once per line into bitmaps (grayscale
/// antialiasing, colour fonts on) on a device context of its own, never the engine's, so a frame only draws bitmaps.
/// <para>
/// With <see cref="Defers"/>, a bake only measures (what the layouts place by is known at once) and leaves the GPU work
/// queued: <see cref="RunPending"/> works through it a slice at a time (the text, then its shadow) within a frame's
/// allowance, and an ink drawn before its turn is baked there and then (<see cref="Ink.Bake"/>). The same calls make the
/// same bitmaps either way; only when they run changes.
/// </para>
/// </summary>
internal sealed class TextKit : IDisposable
{
    private readonly Queue<Ink> _pending = new();
    private readonly IDWriteFactory _dwrite;
    private readonly ID2D1DeviceContext _bake;
    private readonly ID2D1DeviceContext _draw;
    private readonly ID2D1SolidColorBrush _brush;
    private readonly IDWriteFontCollection _fonts;
    private readonly Dictionary<(string Family, FontWeight Weight, float Size, string Locale), IDWriteTextFormat> _formats = [];
    private readonly Vortice.Direct2D1.Effects.Shadow _shadow;
    private readonly ID2D1Image _shadowOut;
    private string _family = "";
    private string _resolvedFamily = "Segoe UI";
    private string _contrastFamily = "Georgia";
    private readonly string[] _mixFamilies = new string[MixFaces];

    /// <summary>How many typefaces "Randomise lyric fonts" picks from (the Mac's sans, serif, slab and soft faces).</summary>
    public const int MixFaces = 4;

    /// <summary>
    /// The typefaces "Randomise lyric fonts" picks from, each the first of its list installed: a sans, a serif, a slab
    /// and a soft face, as the Mac's four. Windows ships Georgia, Bahnschrift and Candara; Rockwell comes with Office.
    /// </summary>
    internal static readonly string[][] MixCandidates =
    [
        ["Segoe UI Variable Display", "Segoe UI"],
        ["Georgia", "Cambria", "Times New Roman"],
        ["Rockwell", "Roboto Slab", "Bahnschrift"],
        ["Candara", "Corbel", "Segoe UI"],
    ];

    public TextKit(ID2D1DeviceContext dc, IDWriteFactory dwrite)
    {
        _dwrite = dwrite;
        _draw = dc;
        using (var device = dc.Device) _bake = device.CreateDeviceContext(DeviceContextOptions.None);
        _bake.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _bake.SetDpi(96, 96);
        _brush = _bake.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _fonts = dwrite.GetSystemFontCollection(false);
        _shadow = new Vortice.Direct2D1.Effects.Shadow(_bake) { Optimization = ShadowOptimization.Quality };
        _shadowOut = _shadow.Output;
    }

    /// <summary>The device context words are drawn on (the engine's): their effects are created on it.</summary>
    public ID2D1DeviceContext Draw => _draw;

    /// <summary>How dark the lyrics being baked are (LyricInk.Dark), for their shadows; set with each line's ink.</summary>
    public float Dark { get; set; }

    /// <summary>Bakes are queued rather than made at once (see the class summary). Off by default.</summary>
    public bool Defers { get; set; }

    /// <summary>Where the time spent baking queued inks is counted (the layer's setup meter), if anywhere.</summary>
    public Diagnostics.SetupMeter? Meter { get; set; }

    /// <summary>True while queued bakes are waiting.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>Uses <paramref name="family"/> for lyrics when it is installed (Segoe UI otherwise).</summary>
    public void SetFamily(string family)
    {
        if (family == _family) return;
        _family = family;
        _resolvedFamily = Installed(family) ? family : Installed("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
        _contrastFamily = Installed("Georgia") ? "Georgia" : Installed("Cambria") ? "Cambria" : _resolvedFamily;
        for (var i = 0; i < MixFaces; i++)
            _mixFamilies[i] = MixCandidates[i].FirstOrDefault(Installed) ?? _resolvedFamily;
        foreach (var format in _formats.Values) format.Dispose();
        _formats.Clear();
    }

    private bool Installed(string family)
    {
        return !string.IsNullOrWhiteSpace(family) && _fonts.FindFamilyName(family, out _);
    }

    /// <summary>Families that set every Latin letter pinyin and Vietnamese use, in order of preference.</summary>
    private static readonly string[] LatinFallbacks = ["Segoe UI Variable Display", "Segoe UI Variable Text", "Segoe UI", "Arial"];

    private readonly Dictionary<(string Family, int Code), bool> _covers = [];

    /// <summary>Whether <paramref name="family"/>'s regular face has a glyph for <paramref name="code"/> (cached).</summary>
    private bool Covers(string family, int code)
    {
        if (_covers.TryGetValue((family, code), out var known)) return known;
        var covers = false;
        try
        {
            if (_fonts.FindFamilyName(family, out var index))
            {
                using var fontFamily = _fonts.GetFontFamily(index);
                using var font = fontFamily.GetFirstMatchingFont(FontWeight.Normal, FontStretch.Normal, Vortice.DirectWrite.FontStyle.Normal);
                covers = font.HasCharacter((uint)code);
            }
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            covers = true; // can't tell: leave it to DirectWrite
        }
        if (_covers.Count > 4096) _covers.Clear();
        _covers[(family, code)] = covers;
        return covers;
    }

    /// <summary>
    /// The family to set <paramref name="text"/> in whole when it is Latin only, has letters beyond Latin-1 (tone marks)
    /// and <paramref name="family"/> lacks one of them; null when the family sets it all, or the text is in another script.
    /// </summary>
    private string? WholeWordFallback(string text, string family)
    {
        var extended = false;
        foreach (var c in text)
        {
            if (c < 0x100) continue;
            // Latin Extended-A/B, combining marks, Latin Extended Additional: pinyin, Vietnamese.
            if (c is (>= 'Ā' and <= 'ɏ') or (>= '̀' and <= 'ͯ') or (>= 'Ḁ' and <= 'ỿ')) { extended = true; continue; }
            if (char.IsLetter(c)) return null; // another script: DirectWrite's own fallback is right for it
        }
        if (!extended) return null;
        var missing = false;
        foreach (var c in text)
            if (c >= 0x100 && !Covers(family, c)) { missing = true; break; }
        if (!missing) return null;
        foreach (var candidate in LatinFallbacks)
        {
            if (candidate == family || !Installed(candidate)) continue;
            var all = true;
            foreach (var c in text)
                if (c >= 0x80 && !Covers(candidate, c)) { all = false; break; }
            if (all) return candidate;
        }
        return null;
    }

    /// <summary>
    /// The face a word is set in, keeping its Hangul in the main face (the Mac's <c>LyricFace.draws</c>, dd7bc1e and
    /// 000abbf): a randomised face, or Editorial's contrasting one, that has no glyphs for the word's Hangul is passed
    /// over for the main face, so every Korean word falls back to the same face and weight. Kana and Han keep their
    /// contrast (their fallback matches the weight), so only Hangul is checked. A randomised face passed over goes to the
    /// main face, not the contrasting one, as on the Mac.
    /// </summary>
    internal (Face Face, int Mix) KeepHangulFace(Face face, int mix, string text)
    {
        if (mix < 0 && face != Face.Contrast) return (face, mix);
        var family = mix >= 0 ? MixFamily(mix) : _contrastFamily;
        if (Draws(family, text)) return (face, mix);
        return (face == Face.Contrast ? Face.Regular : face, -1);
    }

    /// <summary>Whether <paramref name="family"/> has its own glyph for every Hangul letter of <paramref name="text"/>.</summary>
    private bool Draws(string family, string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (LyricRuns.IsHangul(rune) && !Covers(family, rune.Value)) return false;
        return true;
    }

    /// <summary>The family "Randomise lyric fonts" face <paramref name="mix"/> resolves to on this PC.</summary>
    public string MixFamily(int mix) => _mixFamilies[(mix % MixFaces + MixFaces) % MixFaces] ?? _resolvedFamily;

    /// <param name="mix">One of the randomised faces (0–3), in <paramref name="face"/>'s weight; −1 for the face's own family.</param>
    public IDWriteTextFormat Format(Face face, float size, string locale, int mix = -1)
    {
        var (family, weight) = face switch
        {
            Face.Heavy => (_resolvedFamily, FontWeight.Bold),
            Face.Contrast => (_contrastFamily, FontWeight.Normal),
            Face.Quiet => (_resolvedFamily, FontWeight.Normal),
            _ => (_resolvedFamily, FontWeight.Medium),
        };
        if (mix >= 0) family = MixFamily(mix);
        size = MathF.Max(1, MathF.Round(size * 4) / 4);
        var key = (family, weight, size, locale);
        if (_formats.TryGetValue(key, out var found)) return found;
        if (_formats.Count > 96)
        {
            foreach (var format in _formats.Values) format.Dispose();
            _formats.Clear();
        }
        var made = _dwrite.CreateTextFormat(family, null, weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size, locale);
        made.WordWrapping = WordWrapping.NoWrap;
        _formats[key] = made;
        return made;
    }

    /// <summary>
    /// A laid-out label on one line (or wrapped and centred at <paramref name="wrapWidth"/>), right to left when its
    /// script reads that way. The caller disposes it.
    /// </summary>
    /// <param name="faceText">The word the face is chosen for, when it is not <paramref name="text"/> (a reading is set in
    /// its word's face).</param>
    public IDWriteTextLayout Layout(string text, Face face, float size, string locale, bool rightToLeft, float wrapWidth = 0,
                                    int mix = -1, string? faceText = null)
    {
        (face, mix) = KeepHangulFace(face, mix, faceText ?? text);
        var format = Format(face, size, locale, mix);
        var layout = _dwrite.CreateTextLayout(text, format, 100_000, 100_000);
        layout.ReadingDirection = rightToLeft ? ReadingDirection.RightToLeft : ReadingDirection.LeftToRight;
        // Pinyin's tone marks (ǎ ǐ ǒ ǔ ǖ ī ē…) are missing from many lyric typefaces, and DirectWrite then takes only
        // those letters from another font, mid-word (tester feedback on 0.9.0). A Latin word the family can't set whole is
        // set whole in one that can.
        if (WholeWordFallback(text, format.FontFamilyName) is { } whole)
            layout.SetFontFamilyName(whole, new TextRange(0, (uint)text.Length));
        if (wrapWidth > 0)
        {
            layout.WordWrapping = WordWrapping.Wrap;
            layout.TextAlignment = TextAlignment.Center;
            layout.MaxWidth = wrapWidth;
            var natural = layout.Metrics.WidthIncludingTrailingWhitespace;
            layout.MaxWidth = MathF.Min(wrapWidth, MathF.Ceiling(natural + 1));
        }
        else
        {
            layout.MaxWidth = MathF.Ceiling(layout.Metrics.WidthIncludingTrailingWhitespace + 1);
        }
        layout.MaxHeight = MathF.Ceiling(layout.Metrics.Height + 1);
        return layout;
    }

    /// <summary>
    /// Narrows a one-line <paramref name="layout"/> to <paramref name="maxWidth"/>, ending in "…" where it is cut (the
    /// Mac's <c>.lineLimit(1).truncationMode(.tail)</c>). A line that fits is left as it is.
    /// </summary>
    public void Truncate(IDWriteTextLayout layout, float maxWidth)
    {
        if (!(layout.Metrics.WidthIncludingTrailingWhitespace > maxWidth)) return;
        layout.MaxWidth = MathF.Max(1, maxWidth);
        using var sign = _dwrite.CreateEllipsisTrimmingSign(layout);
        layout.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, sign);
    }

    /// <summary>
    /// Bakes a layout into an <see cref="Ink"/>: the text in <paramref name="colour"/> at <paramref name="raster"/>
    /// bitmap pixels per layout pixel, and its soft shadow at half that resolution. With <see cref="Defers"/> the ink is
    /// measured now and baked later (the kit keeps its own reference to the layout, so the caller may dispose it).
    /// </summary>
    public Ink Bake(IDWriteTextLayout layout, Vector4 colour, float raster, ShadowSpec shadow)
    {
        // Dark lyrics: the shadow turns into a light halo with them.
        shadow = shadow.Darkened(Dark);
        var metrics = layout.Metrics;
        var overhang = layout.OverhangMetrics;
        var box = new Vector2(MathF.Max(1, metrics.Width), MathF.Max(1, metrics.Height));
        var reach = MathF.Max(MathF.Max(overhang.Left, overhang.Right), MathF.Max(overhang.Top, overhang.Bottom));
        var pad = MathF.Ceiling(MathF.Max(0, reach) + 2);
        raster = Math.Clamp(raster, 0.1f, 4f);
        var job = new PendingBake
        {
            Layout = Defers ? layout.QueryInterface<IDWriteTextLayout>() : layout,
            OwnsLayout = Defers,
            Colour = colour,
            Raster = raster,
            Shadow = shadow,
            Origin = new Vector2(pad - metrics.Left, pad - metrics.Top),
            SoftShift = new Vector2(-metrics.Left, -metrics.Top),
        };
        var ink = Queue(job, box, pad);
        ink.Letters = InkLetters.Measure(layout, metrics, box);
        return ink;
    }

    /// <summary>
    /// Bakes a label's tile into an <see cref="Ink"/>: a rounded rectangle of <paramref name="size"/> layout pixels
    /// (corners of <paramref name="radius"/>) in <paramref name="colour"/>, and its soft shadow at half resolution.
    /// </summary>
    public Ink BakeTile(Vector2 size, float radius, Vector4 colour, float raster, ShadowSpec shadow)
    {
        var box = new Vector2(MathF.Max(1, size.X), MathF.Max(1, size.Y));
        const float pad = 2;
        raster = Math.Clamp(raster, 0.1f, 4f);
        var job = new PendingBake
        {
            Colour = colour, Raster = raster, Shadow = shadow, Radius = radius, Box = box, Origin = new Vector2(pad, pad),
        };
        return Queue(job, box, pad);
    }

    /// <summary>Sizes the ink's bitmaps and either bakes it now or queues it.</summary>
    private Ink Queue(PendingBake job, Vector2 box, float pad)
    {
        var raster = job.Raster;
        job.Width = Math.Max(1, (int)MathF.Ceiling((box.X + 2 * pad) * raster));
        job.Height = Math.Max(1, (int)MathF.Ceiling((box.Y + 2 * pad) * raster));
        float softRaster = raster / 2, softPad = 0;
        var hasSoft = job.Shadow.Alpha > 0;
        if (hasSoft)
        {
            // The shadow is a blur: half resolution is plenty.
            softPad = MathF.Ceiling(pad + 3 * job.Shadow.Sigma + MathF.Abs(job.Shadow.OffsetY) + 2);
            job.SoftWidth = Math.Max(1, (int)MathF.Ceiling((box.X + 2 * softPad) * softRaster));
            job.SoftHeight = Math.Max(1, (int)MathF.Ceiling((box.Y + 2 * softPad) * softRaster));
            job.SoftPad = softPad;
        }
        var ink = new Ink(_draw, this, raster, new Vector2(pad + box.X / 2, pad + box.Y / 2) * raster, box, (job.Width, job.Height),
                          hasSoft, softRaster, new Vector2(softPad + box.X / 2, softPad + box.Y / 2) * softRaster,
                          (job.SoftWidth, job.SoftHeight))
        {
            Pending = job,
        };
        if (Defers)
        {
            _pending.Enqueue(ink);
        }
        else
        {
            while (ink.Pending is not null) Step(ink, job);
        }
        return ink;
    }

    /// <summary>
    /// Works through the queued bakes, a slice at a time, until <paramref name="deadline"/> (a
    /// <see cref="System.Diagnostics.Stopwatch"/> timestamp) has passed; at least one slice when any is waiting.
    /// </summary>
    public void RunPending(long deadline)
    {
        if (_pending.Count == 0) return;
        Meter?.Enter();
        try
        {
            while (_pending.TryPeek(out var ink))
            {
                if (ink.Pending is not PendingBake job)
                {
                    _pending.Dequeue();
                    continue;
                }
                Step(ink, job);
                if (ink.Pending is null) _pending.Dequeue();
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) break;
            }
        }
        finally
        {
            Meter?.Exit();
        }
    }

    /// <summary>
    /// Bakes <paramref name="ink"/> now, if it is still waiting (it is about to be drawn), with every bake queued before
    /// it: bakes always run in the order they were asked for, as they did when each was made at once. A precaution:
    /// Direct2D keeps state across bakes on a device (its glyph cache among it), and two layers baking into one device
    /// were seen to move each other's pixels by a step; on a device of its own, a layer baking out of order was not.
    /// </summary>
    internal void Complete(Ink ink)
    {
        if (ink.Pending is not PendingBake) return;
        Meter?.Enter();
        try
        {
            while (ink.Pending is not null && _pending.TryPeek(out var head))
            {
                if (head.Pending is not PendingBake job)
                {
                    _pending.Dequeue();
                    continue;
                }
                Step(head, job);
                if (head.Pending is null) _pending.Dequeue();
            }
            // Not queued (made while the kit was not deferring): baked on its own.
            while (ink.Pending is PendingBake own) Step(ink, own);
        }
        finally
        {
            Meter?.Exit();
        }
    }

    /// <summary>Lets go of a bake still waiting (its ink was disposed).</summary>
    internal void Cancel(Ink ink)
    {
        if (ink.Pending is PendingBake job) job.Release();
        ink.Pending = null;
    }

    /// <summary>
    /// One slice of a bake: first the text (or tile) bitmap, then its soft shadow, after which the ink takes its
    /// bitmaps. The GPU calls are the ones an immediate bake makes, in the same order.
    /// </summary>
    private void Step(Ink ink, PendingBake job)
    {
        var properties = new BitmapProperties1(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target);
        if (job.Text is null)
        {
            job.Text = _bake.CreateBitmap(new SizeI(job.Width, job.Height), IntPtr.Zero, 0, properties);
            _brush.Color = new Color4(job.Colour.X, job.Colour.Y, job.Colour.Z, job.Colour.W);
            if (job.Layout is { } layout) Render(job.Text, Matrix3x2.CreateScale(job.Raster), layout, job.Origin);
            else FillRounded(job.Text, job.Raster, job.Origin, job.Box, job.Radius);
            // A deferred bake leaves its shadow for the next slice; an immediate one goes straight on.
            if (job.Shadow.Alpha > 0 && Defers) return;
        }
        ID2D1Bitmap1? soft = null;
        var shadow = job.Shadow;
        if (shadow.Alpha > 0)
        {
            var softRaster = job.Raster / 2;
            var softPad = job.SoftPad;
            var size = new SizeI(job.SoftWidth, job.SoftHeight);
            using var source = _bake.CreateBitmap(size, IntPtr.Zero, 0, properties);
            _brush.Color = new Color4(0, 0, 0, 1);
            if (job.Layout is { } layout)
            {
                Render(source, Matrix3x2.CreateScale(softRaster), layout,
                       new Vector2(softPad + job.SoftShift.X, softPad + job.SoftShift.Y + shadow.OffsetY));
            }
            else
            {
                FillRounded(source, softRaster, new Vector2(softPad, softPad + shadow.OffsetY), job.Box, job.Radius);
            }
            soft = _bake.CreateBitmap(size, IntPtr.Zero, 0, properties);
            _shadow.SetInput(0, source, true);
            _shadow.BlurStandardDeviation = MathF.Max(0.01f, shadow.Sigma * softRaster);
            _shadow.Color = new Vector4(shadow.Colour, shadow.Alpha);
            _bake.Target = soft;
            _bake.BeginDraw();
            _bake.Clear(new Color4(0, 0, 0, 0));
            _bake.Transform = Matrix3x2.Identity;
            _bake.DrawImage(_shadowOut, InterpolationMode.Linear, CompositeMode.SourceOver);
            _bake.EndDraw();
            _bake.Target = null;
            _shadow.SetInput(0, null!, true);
        }
        var text = job.Text;
        job.Text = null;
        job.Release();
        ink.Finish(text, soft);
    }

    /// <summary>A bake waiting in the queue, or part done: what it draws and where.</summary>
    private sealed class PendingBake
    {
        /// <summary>The text to render (null for a tile).</summary>
        public IDWriteTextLayout? Layout;
        /// <summary>The layout is the kit's own reference (released when the bake is done or let go).</summary>
        public bool OwnsLayout;
        public Vector4 Colour;
        public float Raster;
        public ShadowSpec Shadow;
        /// <summary>Where the text's (or tile's) origin goes in the bitmap, in layout pixels.</summary>
        public Vector2 Origin;
        /// <summary>The layout's own offset (its metrics' left and top, negated), for the shadow's origin.</summary>
        public Vector2 SoftShift;
        public float SoftPad;
        public float Radius;
        public Vector2 Box;
        public int Width, Height, SoftWidth, SoftHeight;
        /// <summary>The text bitmap, once its slice is done.</summary>
        public ID2D1Bitmap1? Text;

        public void Release()
        {
            Text?.Dispose();
            Text = null;
            if (OwnsLayout) Layout?.Dispose();
            Layout = null;
        }
    }

    private void FillRounded(ID2D1Bitmap1 target, float raster, Vector2 origin, Vector2 box, float radius)
    {
        _bake.Target = target;
        _bake.BeginDraw();
        _bake.Clear(new Color4(0, 0, 0, 0));
        _bake.Transform = Matrix3x2.CreateScale(raster);
        _bake.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(origin.X, origin.Y, box.X, box.Y), radius, radius), _brush);
        _bake.Transform = Matrix3x2.Identity;
        _bake.EndDraw();
        _bake.Target = null;
    }

    private void Render(ID2D1Bitmap1 target, Matrix3x2 transform, IDWriteTextLayout layout, Vector2 origin)
    {
        _bake.Target = target;
        _bake.BeginDraw();
        _bake.Clear(new Color4(0, 0, 0, 0));
        _bake.Transform = transform;
        _bake.DrawTextLayout(origin, layout, _brush, DrawTextOptions.EnableColorFont);
        _bake.Transform = Matrix3x2.Identity;
        _bake.EndDraw();
        _bake.Target = null;
    }

    /// <summary>
    /// A locale for text, so CJK fallback picks the right forms: Japanese when there is kana, Korean for Hangul, Chinese
    /// for Han alone; otherwise English.
    /// </summary>
    public static string LocaleFor(string text)
    {
        bool han = false, hangul = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F) return "ja-jp";
            if (v is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F) hangul = true;
            else if (v is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF) han = true;
        }
        return hangul ? "ko-kr" : han ? "zh-cn" : "en-us";
    }

    private readonly Dictionary<float, IDWriteTextFormat> _symbolFormats = [];
    private string? _symbolFamily;

    /// <summary>
    /// A laid-out icon glyph from Segoe Fluent Icons (Segoe MDL2 Assets where it is missing): the stage layouts' word
    /// icons (<see cref="Core.Compositions.SegoeIcons"/>). A glyph the font lacks falls back to the system's text faces.
    /// The caller disposes it.
    /// </summary>
    public IDWriteTextLayout SymbolLayout(string glyph, float size)
    {
        _symbolFamily ??= Installed(Core.Compositions.SegoeIcons.Family) ? Core.Compositions.SegoeIcons.Family
            : Installed(Core.Compositions.SegoeIcons.FallbackFamily) ? Core.Compositions.SegoeIcons.FallbackFamily : _resolvedFamily;
        size = MathF.Max(1, MathF.Round(size * 4) / 4);
        if (!_symbolFormats.TryGetValue(size, out var format))
        {
            if (_symbolFormats.Count > 32)
            {
                foreach (var old in _symbolFormats.Values) old.Dispose();
                _symbolFormats.Clear();
            }
            format = _dwrite.CreateTextFormat(_symbolFamily, null, FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size, "en-us");
            format.WordWrapping = WordWrapping.NoWrap;
            _symbolFormats[size] = format;
        }
        var layout = _dwrite.CreateTextLayout(glyph, format, 100_000, 100_000);
        layout.MaxWidth = MathF.Ceiling(layout.Metrics.WidthIncludingTrailingWhitespace + 1);
        layout.MaxHeight = MathF.Ceiling(layout.Metrics.Height + 1);
        return layout;
    }

    /// <summary>Whether the installed icon font draws <paramref name="glyph"/> itself (not by fallback).</summary>
    public bool SymbolFontHas(string glyph)
    {
        var family = Installed(Core.Compositions.SegoeIcons.Family) ? Core.Compositions.SegoeIcons.Family : Core.Compositions.SegoeIcons.FallbackFamily;
        if (!_fonts.FindFamilyName(family, out var index)) return false;
        using var fonts = _fonts.GetFontFamily(index);
        using var font = fonts.GetFirstMatchingFont(FontWeight.Normal, FontStretch.Normal, Vortice.DirectWrite.FontStyle.Normal);
        foreach (var rune in glyph.EnumerateRunes())
            if (!font.HasCharacter((uint)rune.Value)) return false;
        return true;
    }

    public void Dispose()
    {
        foreach (var format in _symbolFormats.Values) format.Dispose();
        _symbolFormats.Clear();
        // Bakes still waiting are let go; their inks are never drawn once the kit is gone.
        while (_pending.TryDequeue(out var ink)) Cancel(ink);
        foreach (var format in _formats.Values) format.Dispose();
        _formats.Clear();
        _shadowOut.Dispose();
        _shadow.Dispose();
        _fonts.Dispose();
        _brush.Dispose();
        _bake.Dispose();
    }
}

/// <summary>A soft drop shadow baked under text: opacity, blur (standard deviation, pixels), drop (pixels) and colour (black by default).</summary>
internal readonly record struct ShadowSpec(float Alpha, float Sigma, float OffsetY, Vector3 Colour = default)
{
    public static ShadowSpec None => default;

    /// <summary>
    /// The shadow under dark lyrics (the Mac's next/tester-fixes 8a52096): grey at lightness <paramref name="dark"/>, its
    /// opacity ×(1 + 0.6·dark) and its drop ×(1 − dark), so dark words sit in a soft light halo instead of a dark shadow.
    /// </summary>
    public ShadowSpec Darkened(float dark)
    {
        if (!(dark > 0.0005f) || Alpha <= 0) return this;
        var d = MathF.Min(1, dark);
        var grey = Lunalyr.Core.Colour.Perceptual.ToColour(new Lunalyr.Core.Colour.OkLab(d, 0, 0));
        var mixed = Vector3.Lerp(Colour, new Vector3((float)grey.R, (float)grey.G, (float)grey.B), d);
        return this with { Colour = mixed, Alpha = MathF.Min(1, Alpha * (1 + 0.6f * d)), OffsetY = OffsetY * (1 - d) };
    }
}
