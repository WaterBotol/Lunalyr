// An action word's per-letter pass and its particles (the Mac's LivelyGlyphRenderer and LivelyTwister, 16 Nov 2026,
// Lunalyr's own lines, ported): each letter is its own slice of the word's baked text, drawn through its own transform
// (hops, shivers, ripples, bands, wedges, cells), and the particles (dust, embers, sparkles, crystals and rime, drips and
// runs of blood, shockwaves, bubbles, ink, the whirlwind) are drawn with the word, in its layout pixels.
//
// Windows differences, all in how a frame is drawn rather than what moves:
// - SwiftUI's colour matrices of the form (1 - k)·colour + k·c are drawn as the letter, then c laid over it at k.
// - A drip and a run of blood are filled in the word's own colour (the Mac stretches the letter's foot into them).
// - Dark's ink and the frost sheet are drawn without their soft blur.
// - Sink's water line is folded into each letter's fade below the line.

using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Motion;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Lunalyr.Render.Compositions;

/// <summary>What a word's letters need to draw an action (the Mac's <c>LivelyGlyphs</c>). Sizes in layout pixels.</summary>
internal readonly record struct LivelyState(WordAction Action, double T, double Duration, double Intensity, double Hold,
                                            double Size, Vector4 Glow, int Seed, double Lift)
{
    /// <summary>The Mac's seed for every word's particles.</summary>
    public const int DefaultSeed = 11;

    /// <summary>
    /// The state of <paramref name="accent"/>'s action <paramref name="elapsed"/> seconds after the onset, or null when
    /// there is none, under Reduce Motion (only a glow then), or once it is over. <paramref name="lift"/>: the pose's
    /// whole-word travel in ems, which particles that stay on the line undo.
    /// </summary>
    public static LivelyState? For(in RunAccent accent, double elapsed, double hold, double strength, bool reduceMotion,
                                   double size, Vector4 glow, double lift)
    {
        if (reduceMotion || accent.Action is not { } action) return null;
        if (!double.IsFinite(hold) || hold <= 0) hold = LivelyWord.UnknownHold;
        var duration = LivelyAction.Duration(action, hold);
        if (!(elapsed > 0) || !(elapsed < duration)) return null;
        var amount = (double.IsFinite(strength) ? strength : LivelyAction.DefaultAmount) * (accent.Gentle ? LivelyAction.GentleShare : 1);
        return new LivelyState(action, elapsed, duration, LivelyAction.Intensity(amount, hold), hold, size, glow, DefaultSeed, lift);
    }
}

internal sealed class LivelyLetters : IDisposable
{
    private ID2D1DeviceContext? _dc;
    private ID2D1SolidColorBrush? _brush;
    private ID2D1StrokeStyle? _round;
    private Glyph[] _glyphs = new Glyph[16];

    private struct Glyph
    {
        public int Index;
        public float Left, Right;
        /// <summary>Its slice of the baked text (neighbours meet halfway, the ends reach the bitmap's edges).</summary>
        public float SliceLeft, SliceRight;
        public double Along;
        public float MidX => (Left + Right) / 2;
        public float Width => Right - Left;
    }

    /// <summary>The colour the letters are drawn in, for what a colour matrix tints toward or a drip is filled with.</summary>
    private Vector4 _basis;
    /// <summary>The whole word's colour cue (multiplied colour, straight alpha; none at 0), laid over every letter.</summary>
    private Vector4 _cue;
    private float _opacity, _shadow;
    private Ink _ink = null!;
    private Matrix3x2 _world;
    private InkLetters _letters = null!;
    private bool _particles;
    private LivelyState _s;
    private float _clipLeft = float.NegativeInfinity, _clipRight = float.PositiveInfinity;

    /// <summary>
    /// Draws <paramref name="ink"/> (a one-line label with <see cref="Ink.Letters"/>) letter by letter for
    /// <paramref name="state"/>, through <paramref name="world"/> (layout pixels from the label's centre → device), with
    /// its particles when <paramref name="particles"/> (the first layer of a word drawn in several). Returns false when the
    /// label has no letters (the caller draws it whole).
    /// </summary>
    /// <param name="clipLeft">With <paramref name="clipRight"/>: only this stretch across the label (layout pixels from
    /// its centre, as it lies before its letters move) is drawn, so a word in two colours either side of an edge (a
    /// Cutout label's tile) draws each colour on its own side.</param>
    /// <param name="slotIndex">With <paramref name="slotCount"/>: this text is one letter of a word set letter by letter
    /// (Mosaic's), at that index (the Mac's <c>LivelyLetterSlot</c>, a161036): it moves as that letter of the word, the
    /// word taken as that many letters of its width, and the word's particles are drawn once, by its middle letter.</param>
    public bool Draw(ID2D1DeviceContext dc, Ink ink, in Matrix3x2 world, float worldScale, in LivelyState state, float opacity,
                     float shadow, Vector4 basis, Vector4 cue, GlowSpec glow, bool particles,
                     float clipLeft = float.NegativeInfinity, float clipRight = float.PositiveInfinity,
                     int slotIndex = -1, int slotCount = 0)
    {
        if (ink.Letters is not { Count: > 0 } letters) return false;
        if (!(opacity > 0.002f) || !(clipRight > clipLeft)) return true;
        _clipLeft = clipLeft;
        _clipRight = clipRight;
        Ensure(dc);
        _ink = ink;
        _world = world;
        _letters = letters;
        _s = state;
        _basis = basis;
        _cue = cue;
        _opacity = opacity;
        _shadow = shadow;
        var count = letters.Count;
        if (_glyphs.Length < count) _glyphs = new Glyph[count * 2];
        var minX = letters.MinX;
        var width = Math.Max(1e-3f, letters.MaxX - minX);
        var slotted = slotIndex >= 0 && slotCount > 0;
        for (var i = 0; i < count; i++)
        {
            var left = letters.Left[i];
            var right = letters.Right[i];
            _glyphs[i] = new Glyph
            {
                Index = slotted ? slotIndex + i : i,
                Left = left, Right = right,
                Along = slotted ? (slotCount > 1 ? Math.Min(1, (double)(slotIndex + i) / (slotCount - 1)) : 0.5)
                    : ((left + right) / 2 - minX) / width,
                SliceLeft = i == 0 ? float.NegativeInfinity : (letters.Right[i - 1] + left) / 2,
                SliceRight = i == count - 1 ? float.PositiveInfinity : (right + letters.Left[i + 1]) / 2,
            };
        }
        var letterCount = count;
        if (slotted)
        {
            // The word, estimated from this letter's width: that many letters, this one at its index.
            minX -= width * slotIndex;
            width *= Math.Max(1, slotCount);
            letterCount = slotCount;
            particles = particles && slotIndex == slotCount / 2;
        }
        _particles = particles;
        var word = new Rect(minX, letters.Top, width, letters.Bottom - letters.Top);
        if (glow.Visible) ink.DrawGlow(dc, world, worldScale, opacity, glow);
        if (particles) Behind(dc, word);
        for (var i = 0; i < count; i++) DrawGlyph(dc, ref _glyphs[i], letterCount, word);
        if (particles) Front(dc, count, word);
        return true;
    }

    private ID2D1Bitmap1? _scratch;
    private int _scratchWidth, _scratchHeight;

    /// <summary>
    /// As <see cref="Draw"/>, for a word drawn through a transform that turns in depth (Hinge's pages, a spin mid-turn):
    /// the word is drawn flat into a scratch image, its letters, tints and particles and what <paramref name="around"/>
    /// draws with it, and the image is put through <paramref name="world"/> (layout pixels from the label's centre →
    /// device), as SwiftUI turns a whole drawn view.
    /// </summary>
    /// <param name="around">Drawn into the same image behind (false) and in front (true) of the word, given the label's
    /// layout pixels → image.</param>
    public bool DrawProjected(ID2D1DeviceContext dc, Ink ink, in Matrix4x4 world, in LivelyState state, float opacity, float shadow,
                              Vector4 basis, Vector4 cue, GlowSpec glow, bool particles, Action<Matrix3x2, bool>? around = null)
    {
        if (ink.Letters is not { Count: > 0 } letters) return false;
        if (!(opacity > 0.002f)) return true;
        // The image's resolution: the projection's own scale at the word, so it is drawn about one to one.
        var s = Math.Clamp(LocalScale(world), 0.25f, 4f);
        var pad = (float)(state.Size * 2.2 + 20);
        float left = letters.MinX - pad, top = letters.Top - pad, right = letters.MaxX + pad, bottom = letters.Bottom + pad;
        s = MathF.Min(s, 4096 / MathF.Max(1, MathF.Max(right - left, bottom - top)));
        var w = Math.Max(1, (int)MathF.Ceiling((right - left) * s));
        var h = Math.Max(1, (int)MathF.Ceiling((bottom - top) * s));
        Ensure(dc);
        if (_scratch is null || _scratchWidth < w || _scratchHeight < h)
        {
            _scratch?.Dispose();
            _scratchWidth = Math.Max(w, _scratchWidth);
            _scratchHeight = Math.Max(h, _scratchHeight);
            _scratch = dc.CreateBitmap(new Vortice.Mathematics.SizeI(_scratchWidth, _scratchHeight), IntPtr.Zero, 0,
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                                      96, 96, BitmapOptions.Target));
        }
        var flat = Matrix3x2.CreateTranslation(-left, -top) * Matrix3x2.CreateScale(s);
        var previous = dc.Target;
        var previousTransform = dc.Transform;
        try
        {
            dc.Target = _scratch;
            dc.Transform = Matrix3x2.Identity;
            dc.Clear(new Color4(0, 0, 0, 0));
            around?.Invoke(flat, false);
            Draw(dc, ink, flat, s, state, 1, shadow, basis, cue,
                 glow.Visible ? glow with { Radius = glow.Radius * s } : GlowSpec.None, particles);
            around?.Invoke(flat, true);
        }
        finally
        {
            dc.Target = previous;
            previous?.Dispose();
            dc.Transform = previousTransform;
        }
        var project = Matrix4x4.CreateScale(1 / s, 1 / s, 1) * Matrix4x4.CreateTranslation(left, top, 0) * world;
        dc.Transform = Matrix3x2.Identity;
        var rect = new Vortice.RawRectF(0, 0, w, h);
        dc.DrawBitmap(_scratch, rect, opacity, InterpolationMode.Linear, rect, project);
        return true;
    }

    /// <summary>How many device pixels a layout pixel at the label's centre spans through <paramref name="world"/>.</summary>
    private static float LocalScale(in Matrix4x4 world)
    {
        static Vector2 Project(Vector2 p, in Matrix4x4 m)
        {
            var v = Vector4.Transform(new Vector4(p.X, p.Y, 0, 1), m);
            return Math.Abs(v.W) < 1e-6f ? new Vector2(v.X, v.Y) : new Vector2(v.X / v.W, v.Y / v.W);
        }
        var o = Project(Vector2.Zero, world);
        return MathF.Max(Vector2.Distance(Project(Vector2.UnitX, world), o), Vector2.Distance(Project(Vector2.UnitY, world), o));
    }

    private void Ensure(ID2D1DeviceContext dc)
    {
        if (ReferenceEquals(_dc, dc) && _brush is not null) return;
        DisposeResources();
        _dc = dc;
        _brush = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        using var factory = dc.Factory;
        _round = factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round,
        });
    }

    // ---- Letters ---------------------------------------------------------------------------------------------------

    /// <summary>The Mac's per-letter transform: about <paramref name="anchor"/>, rotate then scale, moved by (dx, dy).</summary>
    private static Matrix3x2 Xf(double dx, double dy, double sx, double sy, double deg, Vector2 anchor) =>
        Matrix3x2.CreateTranslation(-anchor)
        * Matrix3x2.CreateScale((float)sx, (float)sy)
        * Matrix3x2.CreateRotation((float)(deg * Math.PI / 180))
        * Matrix3x2.CreateTranslation(anchor.X + (float)dx, anchor.Y + (float)dy);

    /// <summary>One letter's slice through <paramref name="local"/> (in the label's space), with an extra tint laid over it.</summary>
    private void Slice(in Glyph g, in Matrix3x2 local, float alpha = 1, Vector4 tint = default, float top = float.NegativeInfinity,
                       float bottom = float.PositiveInfinity, float left = float.NaN, float right = float.NaN)
    {
        var a = _opacity * alpha;
        if (!(a > 0.002f)) return;
        var l = Math.Max(float.IsNaN(left) ? g.SliceLeft : left, _clipLeft);
        var r = Math.Min(float.IsNaN(right) ? g.SliceRight : right, _clipRight);
        if (!(r > l)) return;
        var m = local * _world;
        _ink.DrawRegion(_dc!, m, l, top, r, bottom, a, _shadow, _cue.W > 0.004f ? _cue with { W = _cue.W * a } : default);
        if (tint.W > 0.004f) _ink.DrawRegion(_dc!, m, l, top, r, bottom, 0, 0, tint with { W = tint.W * a });
    }

    private void DrawGlyph(ID2D1DeviceContext dc, ref Glyph g, int count, Rect word)
    {
        var s = _s;
        double t = s.T, d = s.Duration, a = s.Intensity, size = s.Size;
        double i = g.Index;
        var mid = new Vector2(g.MidX, (_letters.Top + _letters.Bottom) / 2);
        var foot = new Vector2(g.MidX, _letters.Baseline);
        switch (s.Action)
        {
            case WordAction.Spin:
            {
                // A twist through the word: the inner letters lead the turn, the outer ones trail it.
                var from = Math.Abs(g.Along - 0.5) * 2;
                var lag = 0.01 + 0.04 * from;
                var turn = LivelyAction.SpinAngle(t, a, s.Hold, d);
                var delta = LivelyAction.Soft(LivelyAction.SpinAngle(t - lag, a, s.Hold, d) - turn, 45) * Math.PI / 180;
                var speed = LivelyAction.SpinSpeed(t - lag, a, s.Hold, d);
                Slice(g, Xf(0, -0.07 * a * LivelyAction.Ss(speed) * (1 - from) * size, Math.Cos(delta), 1,
                            -6 * a * LivelyAction.Ss(speed) * (g.Along - 0.5) * 2, mid));
                break;
            }
            case WordAction.Hop:
            {
                var lag = 0.035 * g.Along;
                var now = LivelyAction.Hop(t, a, s.Hold, d);
                var late = LivelyAction.Hop(t - lag, a, s.Hold, d);
                var dy = LivelyAction.Soft(0.7 * (late.Dy - now.Dy), 0.09);
                Slice(g, Xf(0, dy * size, 1 - 0.3 * (late.Sy - now.Sy), 1 + 0.6 * (late.Sy - now.Sy), 0, foot));
                break;
            }
            case WordAction.Shake:
            {
                var ti = 0.12 + g.Along * 0.5 * Math.Min(d - 0.4, 1.0);
                var w = Math.Exp(-Math.Pow((t - ti) / 0.2, 2)) * LivelyAction.Rest(t, d);
                var dy = 0.022 * a * Math.Sin(2 * Math.PI * 8.5 * t + i * 1.7) * w;
                var deg = 4 * a * Math.Sin(2 * Math.PI * 6.1 * t + i) * w;
                Slice(g, Xf(0, dy * size, 1, 1, deg, mid));
                break;
            }
            case WordAction.Float:
            {
                var drift = LivelyAction.Ss5((t - 0.2 - g.Along * 0.15) / 0.75) * LivelyAction.Rest(t, d, last: 0.7 + 0.1 * g.Along);
                var dy = -0.06 * a * Math.Sin(2 * Math.PI * 1.2 * t - g.Along * 2.4) * drift;
                var deg = 5 * a * Math.Sin(2 * Math.PI * 0.9 * t - i * 0.9) * drift;
                Slice(g, Xf(0, dy * size, 1, 1, deg, mid));
                break;
            }
            case WordAction.Dip:
            {
                var lag = 0.07 * g.Along;
                var dy = LivelyAction.Soft(0.6 * (LivelyAction.Dip(t - lag, a, d) - LivelyAction.Dip(t, a, d)), 0.09);
                Slice(g, Xf(0, dy * size, 1, 1, 0, mid));
                break;
            }
            case WordAction.Shrink:
            {
                var squeeze = LivelyAction.Shrink(t - 0.03 * Math.Abs(g.Along - 0.5), a, d) / 0.46;
                Slice(g, Xf((word.Left + word.Width / 2 - g.MidX) * 0.14 * squeeze, 0, 1, 1, 0, mid));
                break;
            }
            case WordAction.Dissolve:
                Dust(dc, g);
                break;
            case WordAction.Flicker:
                Burn(g);
                break;
            case WordAction.Frost:
            {
                var fr = Frozen(g);
                var tint = fr > 0.01 ? Multiplied(1 - 0.4 * fr, 1 - 0.14 * fr, 1) : default;
                Slice(g, Matrix3x2.Identity, 1, tint);
                if (fr > 0.01)
                {
                    // A pale frosted coat, and a cold glint that sweeps across the word once as the frost sets.
                    var sweep = -0.3 + 1.6 * LivelyAction.Ss5((t - 0.8) / 0.65);
                    var glint = Math.Exp(-Math.Pow((g.Along - sweep) / 0.14, 2)) * (1 - LivelyAction.FrostThaw(t, d));
                    var alpha = Math.Min(0.85, (0.32 * fr + 0.5 * glint) * a);
                    Slice(g, Matrix3x2.Identity, 0, new Vector4(0.88f, 0.96f, 1, (float)alpha));
                }
                break;
            }
            case WordAction.Dark:
            {
                // Where the ink has reached, the letter shows pale against it.
                var inked = LivelyAction.Ss((LivelyAction.InkFront(t, d) - g.Along) / 0.18) * LivelyAction.Darkness(t, d);
                Slice(g, Matrix3x2.Identity, 1, inked > 0.01 ? new Vector4(0.8f, 0.82f, 0.92f, (float)(0.85 * inked * a)) : default);
                break;
            }
            case WordAction.Shatter:
                Shards(dc, g);
                break;
            case WordAction.Melt:
                Melt(dc, g);
                break;
            case WordAction.Bleed:
                Seep(g);
                break;
            case WordAction.Echo:
            {
                var e = LivelyAction.Envelope(t, attack: 0.2, release: 0.45, duration: d);
                var centre = new Vector2(word.Left + word.Width / 2, word.Top + word.Height / 2);
                for (var k = 3; k >= 1; k--)
                {
                    var alpha = (k == 1 ? 0.6 : k == 2 ? 0.38 : 0.2) * e;
                    var dx = (LivelyAction.EchoSway(t - k * 0.12, a) - LivelyAction.EchoSway(t, a) - k * 0.18 * a) * e;
                    Slice(g, Xf(dx * size, -k * 0.035 * a * e * size, 1 + 0.07 * k * e, 1 + 0.07 * k * e, 0, centre), (float)alpha);
                }
                Slice(g, Matrix3x2.Identity);
                break;
            }
            case WordAction.Wave:
            {
                var e = LivelyAction.Ss((t - 0.05 - g.Along * 0.2) / 0.35) * (1 - LivelyAction.Ss((t - (d - 0.55) - g.Along * 0.12) / 0.45));
                var phase = 2 * Math.PI * (g.Along * 1.1 - 1.5 * t);
                Slice(g, Xf(0, -0.17 * a * Math.Sin(phase) * e * size, 1, 1 + 0.06 * a * Math.Sin(phase) * e,
                            -10 * a * Math.Cos(phase) * e, mid));
                break;
            }
            case WordAction.Sink:
                SinkGlyph(g);
                break;
            default:
                Slice(g, Matrix3x2.Identity);
                break;
        }
    }

    /// <summary>The word's colour multiplied (the Mac's <c>colorMultiply</c>), laid over a letter in full.</summary>
    private Vector4 Multiplied(double r, double g, double b) =>
        new(_basis.X * (float)r, _basis.Y * (float)g, _basis.Z * (float)b, 1);

    /// <summary>Burn: the letter shimmering on the heat, its edge glowing orange and the heat climbing from its foot.</summary>
    private void Burn(in Glyph g)
    {
        var s = _s;
        double t = s.T, d = s.Duration, a = s.Intensity, size = s.Size, i = g.Index;
        var e = LivelyAction.Burn(t - 0.08 * g.Along, d);
        if (!(e > 0.005)) { Slice(g, Matrix3x2.Identity); return; }
        var shimmer = Matrix3x2.CreateTranslation((float)(0.008 * a * e * Math.Sin(2 * Math.PI * 3.1 * t + i * 1.9) * size),
                                                  (float)(-0.01 * a * e * Math.Sin(2 * Math.PI * 2.6 * t + i) * size));
        Slice(g, shimmer);
        // Heat from the foot: four overlapping layers, so it fades upward.
        var lit = size * (0.3 + 0.35 * e + 0.05 * Math.Sin(2 * Math.PI * 0.9 * t + i)) * e;
        var k = (float)(0.8 * a);
        for (var layer = 1; layer <= 4; layer++)
        {
            var h = (float)(lit * layer / 4);
            var foot = _letters.Baseline + (float)(size * 0.2);
            Slice(g, shimmer, (float)(0.24 * e), new Vector4(1, 0.42f, 0.08f, k), top: foot - h - (float)(size * 0.2), bottom: foot);
        }
    }

    /// <summary>Bleed: red soaks into the letter from the top down, a soft front.</summary>
    private void Seep(in Glyph g)
    {
        var s = _s;
        Slice(g, Matrix3x2.Identity);
        var soak = LivelyAction.Ss5((s.T - 0.05 - g.Along * 0.2) / 0.8) * (1 - LivelyAction.Ss5((s.T - (s.Duration - 0.7)) / 0.65));
        if (!(soak > 0.005)) return;
        var k = (float)(0.9 * s.Intensity);
        var height = _letters.Bottom - _letters.Top;
        for (var layer = 1; layer <= 4; layer++)
        {
            var h = height * (float)soak * (0.55f + 0.15f * layer);
            Slice(g, Matrix3x2.Identity, 0.3f, new Vector4(0.5f, 0, 0.05f, k), top: _letters.Top - 4, bottom: _letters.Top + h);
        }
    }

    /// <summary>How frozen a letter is: the frost front creeps across the word, then thaws.</summary>
    private double Frozen(in Glyph g) =>
        LivelyAction.Ss((LivelyAction.FrostFront(_s.T) - g.Along) / 0.22) * (1 - LivelyAction.FrostThaw(_s.T, _s.Duration));

    /// <summary>Dissolve: the letter breaks into drifting dust (cells of its own ink) and re-forms; a ghost stays.</summary>
    private void Dust(ID2D1DeviceContext dc, in Glyph g)
    {
        var s = _s;
        double t = s.T, d = s.Duration, a = s.Intensity, size = s.Size;
        var start = 0.1 + g.Along * 0.45;
        var back = d - 1.15 + g.Along * 0.3;
        var amount = LivelyAction.Ss5((t - start) / 0.8) * (1 - LivelyAction.Ss5((t - back) / 0.75));
        if (!(amount > 0.005)) { Slice(g, Matrix3x2.Identity); return; }
        Slice(g, Matrix3x2.Identity, (float)(1 - 0.62 * a * LivelyAction.Ss(amount / 0.6)));
        // The dust is drawn once, by the word's first layer.
        if (!_particles) return;
        var cell = (float)Math.Max(3, size * 0.12);
        var left = g.Left - 1;
        var top = _letters.Top - 1;
        var cols = (int)Math.Ceiling((g.Width + 2) / cell);
        var rows = (int)Math.Ceiling((_letters.Bottom - _letters.Top + 2) / cell);
        for (var row = 0; row < rows; row++)
            for (var col = 0; col < cols; col++)
            {
                var id = g.Index * 10_000 + row * 100 + col;
                if (!(LivelyAction.Rnd(s.Seed, id, 4) < 0.7)) continue;
                var r1 = LivelyAction.Rnd(s.Seed, id, 1);
                var r2 = LivelyAction.Rnd(s.Seed, id, 2);
                var r3 = LivelyAction.Rnd(s.Seed, id, 3);
                var dd = LivelyAction.Ss((amount - 0.35 * r1) / 0.65);
                var x0 = left + col * cell;
                var y0 = top + row * cell;
                var centre = new Vector2(x0 + cell / 2, y0 + cell / 2);
                var swirl = t * 1.3 + r3 * 6.3;
                var ox = ((0.3 + 1.0 * r2) * a + 0.12 * Math.Sin(swirl)) * size * dd;
                var oy = (-(0.2 + 0.9 * r3) * a + 0.1 * Math.Cos(swirl)) * size * dd;
                var scale = 1 - 0.35 * dd;
                var local = Xf(ox, oy, scale, scale, 0, centre);
                var tint = dd > 0.02 ? _s.Glow with { W = (float)(0.75 * dd) } : default;
                Slice(g, local, (float)(1 - 0.45 * dd), tint, top: y0, bottom: y0 + cell, left: x0, right: x0 + cell);
            }
    }

    /// <summary>Shatter: four wedges about a crack point fly apart and snap back.</summary>
    private void Shards(ID2D1DeviceContext dc, in Glyph g)
    {
        var s = _s;
        double t = s.T, d = s.Duration, a = s.Intensity, size = s.Size;
        var u = t - g.Along * 0.12;
        var split = LivelyAction.Spring(u - 0.05, peak: 0.13, overshoot: 0.06);
        var drift = 1 + 0.18 * LivelyAction.Ss((u - 0.12) / 0.4);
        var back = LivelyAction.Spring(t - 0.52 - g.Along * 0.06, peak: 0.15, overshoot: 0.25);
        var amount = Math.Max(-0.12, drift * (split - back)) * LivelyAction.Rest(t, d);
        if (!(Math.Abs(amount) > 0.003)) { Slice(g, Matrix3x2.Identity); return; }
        var crack = CrackPoint(g);
        var theta0 = LivelyAction.Rnd(s.Seed, g.Index, 3) * 2 * Math.PI;
        Span<double> angles = stackalloc double[5];
        for (var k = 0; k < 4; k++) angles[k] = theta0 + k * Math.PI / 2 + (LivelyAction.Rnd(s.Seed, g.Index, 10 + k) - 0.5) * 0.6;
        angles[4] = angles[0] + 2 * Math.PI;
        var far = (float)(size * 2);
        Vector2 Point(double angle) => new(crack.X + far * (float)Math.Cos(angle), crack.Y + far * (float)Math.Sin(angle));
        using var factory = dc.Factory;
        for (var k = 0; k < 4; k++)
        {
            double a0 = angles[k], a1 = angles[k + 1], am = (a0 + a1) / 2;
            using var wedge = factory.CreatePathGeometry();
            using (var sink = wedge.Open())
            {
                sink.BeginFigure(crack, FigureBegin.Filled);
                sink.AddLine(Point(a0));
                sink.AddLine(Point(am));
                sink.AddLine(Point(a1));
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            var push = size * 0.2 * a * amount;
            var local = Matrix3x2.CreateTranslation(-crack)
                * Matrix3x2.CreateRotation((float)((LivelyAction.Rnd(s.Seed, g.Index, 20 + k) - 0.5) * 18 * a * amount * Math.PI / 180))
                * Matrix3x2.CreateTranslation(crack)
                * Matrix3x2.CreateTranslation((float)(Math.Cos(am) * push), (float)(Math.Sin(am) * push + size * 0.06 * a * amount));
            // The wedge moves with its piece (the Mac clips after moving the context). The mask is given in device pixels,
            // so the device context's own transform must be the identity when the layer is pushed.
            dc.Transform = Matrix3x2.Identity;
            dc.PushLayer(new LayerParameters1
            {
                ContentBounds = new Vortice.RawRectF(float.NegativeInfinity, float.NegativeInfinity, float.PositiveInfinity, float.PositiveInfinity),
                GeometricMask = wedge, MaskTransform = local * _world, MaskAntialiasMode = AntialiasMode.PerPrimitive,
                Opacity = 1, LayerOptions = LayerOptions1.None,
            }, null!);
            try
            {
                Slice(g, local);
            }
            finally
            {
                dc.PopLayer();
            }
        }
    }

    private Vector2 CrackPoint(in Glyph g) =>
        new(g.MidX + (float)((LivelyAction.Rnd(_s.Seed, g.Index, 1) - 0.5) * 0.3) * g.Width,
            _letters.Baseline - (float)(_s.Size * (0.25 + 0.2 * LivelyAction.Rnd(_s.Seed, g.Index, 2))));

    /// <summary>Melt: the letter slumps in bands from its foot, then firms up with a wobble; drips of its ink fall.</summary>
    private void Melt(ID2D1DeviceContext dc, in Glyph g)
    {
        var s = _s;
        double t = s.T, d = s.Duration, a = s.Intensity, size = s.Size;
        const int bands = 10;
        var top = _letters.Top;
        var height = _letters.Bottom - _letters.Top;
        var bandH = height / bands;
        Span<double> melts = stackalloc double[bands];
        var any = false;
        for (var k = 0; k < bands; k++)
        {
            melts[k] = LivelyAction.Melt(t, g.Along * 0.25 + (1 - (k + 0.5) / bands) * 0.16, d);
            any |= Math.Abs(melts[k]) > 0.002;
        }
        if (!any) { Slice(g, Matrix3x2.Identity); return; }
        for (var k = 0; k < bands; k++)
        {
            var q = (k + 0.5) / bands;
            var m = melts[k];
            var dy = 0.24 * size * a * m * Math.Pow(1 - q, 1.3);
            var sx = 1 + 0.25 * a * m * q * q;
            var dx = 0.014 * size * a * Math.Max(0, m) * Math.Sin(2 * Math.PI * 0.6 * t + k * 0.55 + g.Index);
            var local = Matrix3x2.CreateTranslation(-g.MidX, 0) * Matrix3x2.CreateScale((float)sx, 1)
                * Matrix3x2.CreateTranslation(g.MidX + (float)dx, (float)dy);
            var y0 = top + k * bandH;
            // The top and foot bands reach the bitmap's edges, so nothing of the letter is lost.
            Slice(g, local, 1, default, top: k == 0 ? float.NegativeInfinity : y0 - 0.6f,
                  bottom: k == bands - 1 ? float.PositiveInfinity : y0 + bandH + 0.6f);
        }
        if (!_particles) return;
        // Drips of its own ink, each with weight.
        var fade = 1 - LivelyAction.Ss((t - (d - 0.5)) / 0.45);
        var dripCount = LivelyAction.Rnd(s.Seed, g.Index, 4) > 0.5 ? 2 : LivelyAction.Rnd(s.Seed, g.Index, 3) > 0.25 ? 1 : 0;
        var index = g.Index;
        for (var k = 0; k < dripCount; k++)
        {
            var drop = k;
            double R(int n) => LivelyAction.Rnd(s.Seed, index * 7 + drop, n);
            if (LivelyAction.DripAt(t, 0.15 + 0.4 * R(1) + k * 0.3, (0.7 + 0.6 * R(2)) * a, 0.4 + 0.25 * R(3), 0.35 + 0.25 * R(4)) is not { } drip)
                continue;
            var x0 = g.Left + g.Width * (float)(0.25 + 0.5 * R(5));
            var lip = _letters.Baseline - (float)(size * 0.05);
            FillDrip(dc, drip, x0, lip, (float)(size * 0.075 * (0.7 + 0.6 * R(2))), (float)size, (float)fade);
        }
    }

    private void FillDrip(ID2D1DeviceContext dc, in Drip drip, float x, float lip, float w, float size, float fade)
    {
        var ink = _basis;
        SetBrush(ink with { W = fade });
        dc.Transform = _world;
        dc.FillEllipse(new Ellipse(new Vector2(x, lip), w * 0.95f, w * 0.35f), _brush!);
        var length = (float)drip.Length * size;
        var bead = (float)drip.Bead * size;
        var neck = w * (float)drip.Neck;
        if (length > 0.3f)
        {
            using var factory = dc.Factory;
            using var trail = factory.CreatePathGeometry();
            using (var sink = trail.Open())
            {
                sink.BeginFigure(new Vector2(x - w / 2, lip), FigureBegin.Filled);
                sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = new Vector2(x - w / 2, lip + length * 0.55f), Point2 = new Vector2(x - neck / 2, lip + length) });
                sink.AddLine(new Vector2(x + neck / 2, lip + length));
                sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = new Vector2(x + w / 2, lip + length * 0.55f), Point2 = new Vector2(x + w / 2, lip) });
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            dc.FillGeometry(trail, _brush!);
        }
        if (bead > 0.3f) dc.FillEllipse(new Ellipse(new Vector2(x, lip + length - bead * 0.8f + bead * 1.05f), bead, bead * 1.05f), _brush!);
        if (drip.DropAlpha > 0.01)
        {
            SetBrush(ink with { W = fade * (float)drip.DropAlpha });
            var r = (float)drip.DropR * size;
            var y = lip + (float)drip.DropY * size;
            dc.FillEllipse(new Ellipse(new Vector2(x, y - r * 1.25f + r * 1.2f), r * 0.88f, r * 1.2f), _brush!);
        }
    }

    /// <summary>The water line: just under the word, where its label ends (fixed while the word goes down).</summary>
    private float Waterline() => _letters.Baseline + (float)(_s.Size * 0.12 - _s.Lift * _s.Size);

    /// <summary>Sink: the letters drop through the water line one by one; under it they are fainter and bluer.</summary>
    private void SinkGlyph(in Glyph g)
    {
        var s = _s;
        var depth = LivelyAction.Sink(s.T, g.Along * 0.3, s.Duration);
        if (!(Math.Abs(depth) > 0.005)) { Slice(g, Matrix3x2.Identity); return; }
        var a = s.Intensity;
        var own = 0.3 * a * (depth - LivelyAction.Sink(s.T, 0.15, s.Duration));
        var line = Waterline();
        var wobble = 5 * a * Math.Sin(2 * Math.PI * 0.9 * s.T + g.Index) * Math.Max(0, depth);
        var centre = new Vector2(g.MidX, (_letters.Top + _letters.Bottom) / 2);
        var local = Xf(0, own * s.Size, 1, 1, wobble, centre);
        // The line is fixed; in the letter's own slice it sits where the letter's travel has not yet carried it.
        var lineInLetter = line - (float)(own * s.Size);
        var under = LivelyAction.Clamp01(depth);
        Slice(g, local, 1, default, bottom: lineInLetter);
        Slice(g, local, (float)((1 - 0.3 * under) * (1 - 0.5 * under)), Multiplied(0.5, 0.78, 1), top: lineInLetter);
    }

    // ---- Particles ---------------------------------------------------------------------------------------------------

    private void SetBrush(Vector4 colour) => _brush!.Color = new Color4(colour.X, colour.Y, colour.Z, Math.Clamp(colour.W * _opacity, 0, 1));

    private void Behind(ID2D1DeviceContext dc, Rect word)
    {
        var s = _s;
        if (s.Action != WordAction.Dark) return;
        // Ink creeping over the word from the leading side, its edge uneven, then drawing back.
        var dark = LivelyAction.Darkness(s.T, s.Duration);
        if (!(dark > 0.005)) return;
        var size = (float)s.Size;
        var edge = word.Left + word.Width * (float)LivelyAction.InkFront(s.T, s.Duration);
        float top = word.Top - size * 0.35f, bottom = word.Bottom + size * 0.3f, left = word.Left - size * 0.9f;
        if (!(edge > left)) return;
        using var factory = dc.Factory;
        using var path = factory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            const int steps = 14;
            sink.BeginFigure(new Vector2(left, top), FigureBegin.Filled);
            for (var k = 1; k <= steps; k++)
            {
                var f = (float)k / steps;
                sink.AddLine(new Vector2(left + (edge - left) * f, top + size * 0.08f * (float)Math.Sin(f * 3 + s.T * 1.4) * f));
            }
            for (var k = 1; k <= steps; k++)
            {
                var f = (double)k / steps;
                var wobble = 0.16 * Math.Sin(f * 5.5 + s.T * 2.2) + 0.08 * Math.Sin(f * 11 - s.T * 3.1);
                sink.AddLine(new Vector2(edge + size * (float)wobble, top + (bottom - top) * (float)f));
            }
            sink.AddLine(new Vector2(left, bottom));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        dc.Transform = _world;
        // The Mac blurs the ink by a tenth of the type size: here its body, then a soft fringe of widening strokes.
        var ink = new Vector4(0.03f, 0.02f, 0.07f, (float)(0.82 * s.Intensity * dark));
        SetBrush(ink with { W = ink.W * 0.7f });
        dc.FillGeometry(path, _brush!);
        foreach (var (width, share) in (ReadOnlySpan<(float, float)>)[(0.2f, 0.12f), (0.12f, 0.16f), (0.05f, 0.2f)])
        {
            SetBrush(ink with { W = ink.W * share });
            dc.DrawGeometry(path, _brush!, size * width, _round);
        }
    }

    /// <summary>
    /// What an action draws round the whole word rather than on its letters (the Mac's LivelyTwister and LivelyVignette):
    /// spin's whirlwind (its far half behind the word, its near half in front) and dark's shadow closing in. In the
    /// word's frame <paramref name="frame"/> (unit layout pixels → device: the pose's tilt and travel, not its scale or
    /// turn), about the word's box (<paramref name="centre"/>, <paramref name="box"/>).
    /// </summary>
    public void DrawAround(ID2D1DeviceContext dc, in Matrix3x2 frame, Vector2 centre, Vector2 box, in LivelyPose pose, double t,
                           double size, Vector4 glow, float opacity, bool front)
    {
        if (!(opacity > 0.002f)) return;
        if (pose.Twister > 0.005) { Ensure(dc); _opacity = opacity; Twister(dc, frame, centre, box, pose, t, (float)size, glow, front); }
        if (!front && pose.Vignette > 0.005) { Ensure(dc); _opacity = opacity; Vignette(dc, frame, centre, box, pose.Vignette, (float)size); }
    }

    private void Twister(ID2D1DeviceContext dc, in Matrix3x2 frame, Vector2 centre, Vector2 box, in LivelyPose pose, double t,
                         float size, Vector4 glow, bool front)
    {
        var k = pose.Twister;
        float left = centre.X - box.X / 2, width = box.X;
        var bottom = centre.Y + box.Y / 2 + size * 0.18f;
        var top = centre.Y - box.Y / 2 - size * 0.6f;
        var spin = pose.Turn * Math.PI / 180;
        Vector2 Place(double h, double a)
        {
            var rx = (float)((width * 0.5 * (0.5 + 0.62 * h) + size * 0.14) * (0.72 + 0.28 * k));
            var ry = rx * 0.16f + size * 0.04f;
            return new Vector2(left + width / 2 + rx * (float)Math.Cos(a), bottom + (top - bottom) * (float)h + ry * (float)Math.Sin(a));
        }
        dc.Transform = frame;
        const int rings = 6, segments = 9;
        const double span = 1.25;
        for (var ring = 0; ring < rings; ring++)
        {
            var h = (double)ring / (rings - 1);
            for (var j = 0; j < 2; j++)
            {
                var a0 = spin * (1.25 - 0.5 * h) + t * (5.5 - 2.5 * h) + ring * 1.3 + j * Math.PI;
                for (var seg = 0; seg < segments; seg++)
                {
                    double f0 = (double)seg / segments, f1 = (double)(seg + 1) / segments;
                    double a = a0 + span * f0, b = a0 + span * f1;
                    // The near half is the lower half of each ring.
                    if (Math.Sin((a + b) / 2) > 0 != front) continue;
                    SetBrush(glow with { W = (float)(k * (front ? 0.6 : 0.28) * f1 * (0.55 + 0.45 * h)) });
                    dc.DrawLine(Place(h, a), Place(h, b), _brush!, Math.Max(1, size * (float)(0.022 + 0.018 * h * f1)), _round);
                }
            }
        }
        // Dust spiralling up the funnel.
        for (var n = 0; n < 16; n++)
        {
            var h = (t * 0.85 + LivelyAction.Rnd(5, n, 1)) % 1;
            var a = LivelyAction.Rnd(5, n, 2) * 2 * Math.PI + t * (7 - 3 * h) + spin * 0.6;
            if (Math.Sin(a) > 0 != front) continue;
            var r = size * (float)(0.022 + 0.022 * LivelyAction.Rnd(5, n, 3));
            SetBrush(glow with { W = (float)(k * (front ? 0.85 : 0.4) * Math.Sin(Math.PI * h)) });
            dc.FillEllipse(new Ellipse(Place(h, a), r, r), _brush!);
        }
    }

    private void Vignette(ID2D1DeviceContext dc, in Matrix3x2 frame, Vector2 centre, Vector2 box, double amount, float size)
    {
        var inner = Math.Max(box.X, box.Y) * 0.5f + size * (float)(1.6 - 1.1 * amount);
        var outer = inner + size * 1.8f;
        var squash = Math.Max(0.35f, (box.Y + size * 2.2f) / (box.X + size * 2.2f));
        var shade = new Color4(0.02f, 0.01f, 0.05f, 0);
        var stops = new[]
        {
            new GradientStop { Position = 0, Color = shade },
            new GradientStop { Position = inner / outer * 0.75f, Color = shade },
            new GradientStop { Position = inner / outer, Color = new Color4(0.02f, 0.01f, 0.05f, (float)(0.6 * amount) * _opacity) },
            new GradientStop { Position = 1, Color = shade },
        };
        using var collection = dc.CreateGradientStopCollection(stops);
        using var brush = dc.CreateRadialGradientBrush(new RadialGradientBrushProperties
        {
            Center = Vector2.Zero, GradientOriginOffset = Vector2.Zero, RadiusX = outer, RadiusY = outer,
        }, collection);
        // Squashed to the word's shape: wider than tall.
        dc.Transform = Matrix3x2.CreateScale(1, squash) * Matrix3x2.CreateTranslation(centre) * frame;
        dc.FillEllipse(new Ellipse(Vector2.Zero, outer, outer), brush);
    }

    private void Front(ID2D1DeviceContext dc, int count, Rect word)
    {
        var s = _s;
        double t = s.T, d = s.Duration, a = s.Intensity;
        var size = (float)s.Size;
        var seed = s.Seed;
        var liftPx = (float)(s.Lift * s.Size);
        var baseline = _letters.Baseline;
        dc.Transform = _world;
        switch (s.Action)
        {
            case WordAction.Hop:
            {
                // A puff of dust kicked up on the ground at each landing.
                var ground = baseline + size * 0.1f - liftPx;
                Span<(double At, double Strength)> landings = s.Hold >= 0.9 ? [(0.55, 1.0), (0.97, 0.6)] : [(0.55, 1.0)];
                foreach (var (at, strength) in landings)
                {
                    var p = (t - at + 0.02) / 0.45;
                    if (!(p > 0) || !(p < 1)) continue;
                    var outward = (float)LivelyAction.EaseOutCubic(p);
                    for (var k = 0; k < 8; k++)
                    {
                        var side = k < 4 ? -1f : 1f;
                        var n = (float)(k % 4);
                        var edge = side < 0 ? word.Left + word.Width * 0.15f : word.Right - word.Width * 0.15f;
                        var x = edge + side * size * (0.12f + 0.16f * n) * outward * (float)(0.6 + 0.4 * a);
                        var y = ground - size * (float)(0.06 + 0.05 * LivelyAction.Rnd(seed, k, 1)) * outward * (1 + n * 0.3f);
                        var r = size * (float)(0.035 + 0.03 * LivelyAction.Rnd(seed, k, 2)) * (0.5f + outward);
                        SetBrush(s.Glow with { W = (float)(0.5 * strength * a * (1 - p) * (1 - p) * LivelyAction.Ss(p / 0.08)) });
                        dc.FillEllipse(new Ellipse(new Vector2(x, y), r, r), _brush!);
                    }
                }
                break;
            }
            case WordAction.Float:
            {
                // Soft sparkles left on the line below the lifted word.
                var lift = LivelyAction.Ss(t / 0.42) * (1 - LivelyAction.Ss((t - (d - 0.5)) / 0.5));
                for (var k = 0; k < 12; k++)
                {
                    var born = 0.12 + LivelyAction.Rnd(seed, k, 2) * Math.Max(0.2, d - 0.75);
                    var p = (t - born) / 0.65;
                    if (!(p > 0) || !(p < 1)) continue;
                    var x = word.Left + word.Width * (float)LivelyAction.Rnd(seed, k, 1);
                    var y = baseline + size * (float)(0.1 + 0.4 * LivelyAction.Rnd(seed, k, 3)) + size * 0.12f * (float)p - liftPx;
                    var r = size * (float)((0.06 + 0.07 * LivelyAction.Rnd(seed, k, 4)) * Math.Sin(Math.PI * p) * (0.5 + 0.5 * a));
                    SetBrush(s.Glow with { W = (float)(0.9 * Math.Sin(Math.PI * p) * Math.Max(0.3, lift)) });
                    FillStar(dc, new Vector2(x, y), r);
                }
                break;
            }
            case WordAction.Flicker:
            {
                // Embers lifting off the word: each rises, slowing, drifts, shrinks, cools from yellow to red and fades.
                var e = LivelyAction.Burn(t, d);
                if (!(e > 0.005)) break;
                for (var k = 0; k < 18; k++)
                {
                    var life = 0.9 + 0.6 * LivelyAction.Rnd(seed, k, 5);
                    var born = 0.2 + LivelyAction.Rnd(seed, k, 2) * Math.Max(0.2, d - 0.5 - life * 0.6);
                    var p = (t - born) / life;
                    if (!(p > 0) || !(p < 1)) continue;
                    var rise = 1 - Math.Pow(1 - p, 1.7);
                    var x0 = word.Left + word.Width * (float)LivelyAction.Rnd(seed, k, 1);
                    var x = x0 + size * (float)(0.3 * (LivelyAction.Rnd(seed, k, 6) - 0.5) * p + 0.07 * Math.Sin(p * 5 + k) * p);
                    var y = word.Top + word.Height * (float)(0.25 + 0.3 * LivelyAction.Rnd(seed, k, 7))
                        - size * (float)((0.8 + 0.8 * LivelyAction.Rnd(seed, k, 8)) * rise * (0.6 + 0.4 * a));
                    var r = size * (float)((0.018 + 0.022 * LivelyAction.Rnd(seed, k, 3)) * (1 - 0.55 * p));
                    var colour = new Vector4(1, (float)(0.88 - 0.5 * p), (float)(0.35 - 0.3 * p), 1);
                    var alpha = Math.Pow(Math.Sin(Math.PI * p), 0.8) * e * (0.8 + 0.2 * Math.Sin(t * 7 + k));
                    SetBrush(colour with { W = (float)(0.18 * alpha) });
                    dc.FillEllipse(new Ellipse(new Vector2(x, y), r * 2.6f, r * 2.6f), _brush!);
                    SetBrush(colour with { W = (float)(0.95 * alpha) });
                    dc.FillEllipse(new Ellipse(new Vector2(x, y), r, r), _brush!);
                }
                break;
            }
            case WordAction.Frost:
                Frost(dc, count, word);
                break;
            case WordAction.Bleed:
                Blood(dc, count);
                break;
            case WordAction.Shatter:
            {
                // Crack lines flash as each letter breaks.
                for (var i = 0; i < count; i++)
                {
                    ref readonly var g = ref _glyphs[i];
                    var u = t - g.Along * 0.12;
                    if (!(u > 0) || !(u < 0.3)) continue;
                    var crack = CrackPoint(g);
                    var theta0 = LivelyAction.Rnd(seed, g.Index, 3) * 2 * Math.PI;
                    SetBrush(new Vector4(1, 1, 1, (float)(0.9 * (1 - u / 0.3))));
                    for (var k = 0; k < 4; k++)
                    {
                        var angle = theta0 + k * Math.PI / 2 + (LivelyAction.Rnd(seed, g.Index, 10 + k) - 0.5) * 0.6;
                        dc.DrawLine(crack, crack + new Vector2(size * 0.45f * (float)Math.Cos(angle), size * 0.45f * (float)Math.Sin(angle)),
                                    _brush!, Math.Max(1, size * 0.02f));
                    }
                }
                break;
            }
            case WordAction.Pulse:
            {
                // Each beat sends out a soft shockwave; the second beat's wave is smaller.
                var beat = 0.0;
                var last = Math.Max(0.01, s.Hold - 0.3);
                var n = 0;
                using var factory = dc.Factory;
                while (beat <= last)
                {
                    for (var k = 0; k < 2; k++)
                    {
                        var delay = k == 0 ? 0.0 : 0.24;
                        var strength = k == 0 ? 1.0 : 0.55;
                        var p = (t - beat - delay) / 0.95;
                        if (!(p > 0) || !(p < 1)) continue;
                        var grow = (float)LivelyAction.EaseOutCubic(p) * size * (float)(0.12 + 1.05 * a);
                        var alpha = Math.Pow(1 - p, 1.6) * strength * a * LivelyAction.Ss(p / 0.06);
                        var lineWidth = size * (float)(0.07 * (1 - p) + 0.008);
                        var p1 = LivelyAction.Rnd(seed, n * 2 + k, 1) * 6.3;
                        var p2 = LivelyAction.Rnd(seed, n * 2 + k, 2) * 6.3;
                        using var ring = factory.CreatePathGeometry();
                        using (var sink = ring.Open())
                        {
                            for (var step = 0; step <= 64; step++)
                            {
                                var th = step / 64.0 * 2 * Math.PI;
                                var wobble = 1 + 0.035 * Math.Sin(3 * th + p1 + p * 2) + 0.025 * Math.Sin(5 * th + p2 - p * 3);
                                var rx = (word.Width / 2 + grow) * (float)wobble;
                                var ry = (word.Height / 2 + grow * 0.75f) * (float)wobble;
                                var point = new Vector2(word.Left + word.Width / 2 + rx * (float)Math.Cos(th), word.Top + word.Height / 2 + ry * (float)Math.Sin(th));
                                if (step == 0) sink.BeginFigure(point, FigureBegin.Hollow);
                                else sink.AddLine(point);
                            }
                            sink.EndFigure(FigureEnd.Closed);
                            sink.Close();
                        }
                        SetBrush(s.Glow with { W = (float)(0.16 * alpha) });
                        dc.DrawGeometry(ring, _brush!, lineWidth * 3.5f);
                        SetBrush(s.Glow with { W = (float)(0.6 * alpha) });
                        dc.DrawGeometry(ring, _brush!, Math.Max(0.8f, lineWidth));
                    }
                    beat += 0.8;
                    n++;
                }
                break;
            }
            case WordAction.Sink:
            {
                var depth = LivelyAction.Clamp01(LivelyAction.Sink(t, 0.1, d) * 1.2);
                if (!(depth > 0.02)) break;
                var line = Waterline();
                SetBrush(new Vector4(1, 1, 1, (float)(0.35 * depth)));
                var from = word.Left - size * 0.4f;
                var to = word.Right + size * 0.4f;
                var previous = new Vector2(from, line);
                for (var x = from + 3; x < to + 3; x += 3)
                {
                    var next = new Vector2(x, line + size * 0.02f * (float)Math.Sin(x * 0.08 - t * 6));
                    dc.DrawLine(previous, next, _brush!, Math.Max(1, size * 0.015f), _round);
                    previous = next;
                }
                for (var k = 0; k < 10; k++)
                {
                    var born = 0.3 + LivelyAction.Rnd(seed, k, 2) * Math.Max(0.2, d - 1.0);
                    var p = (t - born) / 0.7;
                    if (!(p > 0) || !(p < 1)) continue;
                    var x0 = word.Left + word.Width * (float)LivelyAction.Rnd(seed, k, 1);
                    var startY = baseline + size * (float)(0.4 + 0.3 * a);
                    var y = startY + (line - size * 0.04f - startY) * (float)p;
                    var r = size * (float)(0.03 + 0.04 * LivelyAction.Rnd(seed, k, 3)) * (float)(p > 0.85 ? 1 + (p - 0.85) * 4 : 1);
                    var xx = x0 + size * 0.06f * (float)Math.Sin(p * 9 + k);
                    SetBrush(new Vector4(1, 1, 1, (float)(0.8 * (p > 0.85 ? (1 - p) / 0.15 : 1) * depth)));
                    dc.DrawEllipse(new Ellipse(new Vector2(xx, y), r, r), _brush!, Math.Max(1, size * 0.016f));
                }
                break;
            }
        }
    }

    private void Frost(ID2D1DeviceContext dc, int count, Rect word)
    {
        var s = _s;
        double t = s.T, d = s.Duration, a = s.Intensity;
        var size = (float)s.Size;
        var seed = s.Seed;
        var ice = new Vector4(0.86f, 0.95f, 1, 1);
        var thaw = LivelyAction.FrostThaw(t, d);
        var front = LivelyAction.FrostFront(t);
        if (!(front > -0.2) || !(thaw < 1)) return;
        // A pale sheet close round the letters, its edge soft, up to the frost's front.
        var sheet = new Rect(word.Left - size * 0.16f, word.Top - size * 0.06f, word.Width + size * 0.32f, word.Height + size * 0.12f);
        var reach = word.Left + word.Width * (float)front;
        var feather = size * 0.5f;
        if (reach + feather > sheet.Left)
        {
            // Strips of falling opacity stand in for the Mac's gradient, clipped to the sheet's rounded corners so it
            // reads as frost on the word rather than a box.
            using var factory = dc.Factory;
            using var rounded = factory.CreateRoundedRectangleGeometry(new RoundedRectangle(
                new Vortice.RawRectF(sheet.Left, sheet.Top, sheet.Right, sheet.Bottom), size * 0.12f, size * 0.12f));
            dc.Transform = Matrix3x2.Identity;
            dc.PushLayer(new LayerParameters1
            {
                ContentBounds = new Vortice.RawRectF(float.NegativeInfinity, float.NegativeInfinity, float.PositiveInfinity, float.PositiveInfinity),
                GeometricMask = rounded, MaskTransform = _world, MaskAntialiasMode = AntialiasMode.PerPrimitive,
                Opacity = 1, LayerOptions = LayerOptions1.None,
            }, null!);
            dc.Transform = _world;
            // The Mac's gradient across the sheet: 0.42 at its leading edge, 0.22 where the frost is solid, clear a feather
            // past the front.
            var stopAt = (float)Math.Min(1, Math.Max(0.001, (reach + feather - sheet.Left) / sheet.Width));
            var solid = (float)Math.Max(0, (reach - sheet.Left) / sheet.Width);
            var strength = (float)(a * (1 - thaw)) * _opacity;
            var stops = new[]
            {
                new GradientStop { Position = 0, Color = new Color4(ice.X, ice.Y, ice.Z, 0.42f * strength) },
                new GradientStop { Position = Math.Min(solid, stopAt * 0.99f), Color = new Color4(ice.X, ice.Y, ice.Z, 0.22f * strength) },
                new GradientStop { Position = stopAt, Color = new Color4(ice.X, ice.Y, ice.Z, 0) },
            };
            using (var collection = dc.CreateGradientStopCollection(stops))
            using (var gradient = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties
                   {
                       StartPoint = new Vector2(sheet.Left, sheet.Top + sheet.Height / 2),
                       EndPoint = new Vector2(sheet.Right, sheet.Top + sheet.Height / 2),
                   }, collection))
            {
                dc.FillRectangle(new Vortice.RawRectF(sheet.Left, sheet.Top, sheet.Right, sheet.Bottom), gradient);
            }
            dc.PopLayer();
        }
        // Rime: short spikes along the top and the foot, up to the front.
        SetBrush(ice with { W = (float)(0.7 * a) });
        var step = size * 0.07f;
        var x = sheet.Left;
        var n = 0;
        var stroke = Math.Max(1, size * 0.014f);
        while (x < Math.Min(sheet.Right, reach))
        {
            var grow = (float)(LivelyAction.Ss5((reach - x) / (size * 0.4)) * (1 - LivelyAction.Ss((thaw - 0.4 * LivelyAction.Rnd(seed, n, 90)) / 0.6)));
            var len = size * (float)(0.04 + 0.08 * LivelyAction.Rnd(seed, n, 91)) * grow;
            var lean = size * (float)(0.03 * (LivelyAction.Rnd(seed, n, 92) - 0.5));
            if (len > 0.05f)
            {
                dc.DrawLine(new Vector2(x, sheet.Top + size * 0.04f), new Vector2(x + lean, sheet.Top + size * 0.04f + len), _brush!, stroke);
                dc.DrawLine(new Vector2(x, sheet.Bottom - size * 0.04f), new Vector2(x - lean, sheet.Bottom - size * 0.04f - len), _brush!, stroke);
            }
            x += step * (float)(0.7 + 0.6 * LivelyAction.Rnd(seed, n, 93));
            n++;
        }
        // Crystals branching out on the letters as the front reaches them, shrinking one by one as it thaws.
        var crystalStroke = Math.Max(1, size * 0.02f);
        for (var i = 0; i < count; i++)
        {
            ref readonly var g = ref _glyphs[i];
            for (var k = 0; k < 6; k++)
            {
                var px = g.Left + g.Width * (float)(0.05 + 0.9 * LivelyAction.Rnd(seed, g.Index, 30 + k));
                var p = new Vector2(px, _letters.Baseline - size * (float)(0.7 * LivelyAction.Rnd(seed, g.Index, 40 + k)));
                var along = (px - word.Left) / Math.Max(1, word.Width);
                var grow = LivelyAction.Ss5((front - along - 0.1 * LivelyAction.Rnd(seed, g.Index, 50 + k)) / 0.3)
                    * (1 - LivelyAction.Ss5((thaw - 0.4 * LivelyAction.Rnd(seed, g.Index, 55 + k)) / 0.6));
                var r = size * (float)(0.07 + 0.12 * LivelyAction.Rnd(seed, g.Index, 60 + k)) * (float)grow * (float)(0.5 + 0.5 * a);
                if (!(r > 0.5f)) continue;
                SetBrush(ice with { W = (float)(0.95 * Math.Min(1, grow * 1.5)) });
                var angle = LivelyAction.Rnd(seed, g.Index, 70 + k) * Math.PI;
                for (var arm = 0; arm < 6; arm++)
                {
                    var b = angle + arm * Math.PI / 3;
                    var tip = p + new Vector2(r * (float)Math.Cos(b), r * (float)Math.Sin(b));
                    dc.DrawLine(p, tip, _brush!, crystalStroke);
                    var m = p + new Vector2(r * 0.55f * (float)Math.Cos(b), r * 0.55f * (float)Math.Sin(b));
                    foreach (var side in (ReadOnlySpan<double>)[-1.0, 1.0])
                    {
                        var c = b + side * Math.PI / 4;
                        dc.DrawLine(m, m + new Vector2(r * 0.3f * (float)Math.Cos(c), r * 0.3f * (float)Math.Sin(c)), _brush!, crystalStroke);
                    }
                }
            }
        }
    }

    private void Blood(ID2D1DeviceContext dc, int count)
    {
        // A hint of blood: two or three glossy runs from the letters' feet, each with a bead; the first lets its bead go.
        var s = _s;
        double t = s.T, d = s.Duration, a = s.Intensity;
        var size = (float)s.Size;
        var e = LivelyAction.Envelope(t, attack: 0.3, release: 0.65, duration: d);
        if (!(e > 0.005) || count == 0) return;
        var crimson = new Vector4(0.42f, 0, 0.05f, 1);
        var gloss = new Vector4(1, 0.8f, 0.82f, 1);
        var runs = Math.Min(3, Math.Max(2, count / 3 + 1));
        using var factory = dc.Factory;
        for (var k = 0; k < runs; k++)
        {
            double R(int n) => LivelyAction.Rnd(s.Seed, 300 + k, n);
            var slot = (k + 0.5 + 0.5 * (R(1) - 0.5)) / runs;
            ref readonly var g = ref _glyphs[Math.Min(count - 1, Math.Max(0, (int)(slot * count)))];
            var x = g.Left + g.Width * (float)(0.3 + 0.4 * R(2));
            var lip = _letters.Baseline - size * 0.06f;
            var start = 0.35 + 0.35 * R(3) + 0.15 * (k % 2);
            var pace = 1.0 + 0.7 * R(4);
            var reach = (0.22 + 0.3 * R(5)) * a;
            var flow = LivelyAction.Ss5((t - start) / pace);
            if (!(flow > 0.003)) continue;
            var w = size * (float)(0.06 + 0.025 * R(6));
            var releases = k == 0;
            var release = start + pace * 0.85;
            var length = size * (float)(reach * flow);
            var bead = w * (float)(0.45 + 0.25 * LivelyAction.Ss5((t - start) / (pace * 0.7)));
            var fallen = 0.0;
            if (releases && t > release)
            {
                fallen = t - release;
                bead = w * (float)(0.35 + 0.25 * LivelyAction.Ss5(fallen / 0.6));
                length *= (float)(1 - 0.12 * LivelyAction.Spring(fallen, peak: 0.15, overshoot: 0.3));
            }
            using (var run = factory.CreatePathGeometry())
            {
                using (var sink = run.Open())
                {
                    sink.BeginFigure(new Vector2(x - w * 0.7f, lip), FigureBegin.Filled);
                    sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = new Vector2(x - w * 0.5f, lip + length * 0.3f), Point2 = new Vector2(x - w * 0.42f, lip + length) });
                    sink.AddLine(new Vector2(x + w * 0.42f, lip + length));
                    sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = new Vector2(x + w * 0.5f, lip + length * 0.3f), Point2 = new Vector2(x + w * 0.7f, lip) });
                    sink.EndFigure(FigureEnd.Closed);
                    sink.Close();
                }
                SetBrush(crimson with { W = (float)(0.95 * e) });
                dc.FillGeometry(run, _brush!);
            }
            dc.FillEllipse(new Ellipse(new Vector2(x, lip), w * 0.9f, w * 0.3f), _brush!);
            var tip = new Vector2(x, lip + length);
            dc.FillEllipse(new Ellipse(new Vector2(tip.X, tip.Y - bead * 0.85f + bead * 1.025f), bead, bead * 1.025f), _brush!);
            // Gloss: a soft streak down the run and a glint on the bead.
            SetBrush(gloss with { W = (float)(0.22 * e) });
            dc.DrawLine(new Vector2(x - w * 0.22f, lip + w * 0.2f), new Vector2(x - w * 0.18f, lip + length - bead * 0.2f), _brush!,
                        Math.Max(1, w * 0.16f), _round);
            SetBrush(gloss with { W = (float)(0.4 * e) });
            dc.FillEllipse(new Ellipse(new Vector2(tip.X - bead * 0.275f, tip.Y - bead * 0.2f), bead * 0.225f, bead * 0.25f), _brush!);
            if (!(releases && fallen > 0)) continue;
            // The released bead falls with gravity, then splats.
            var big = w * 0.7f;
            var drop = size * (float)(0.4 * fallen + 0.5 * 6 * fallen * fallen);
            var floor = size * (float)(reach + 0.35);
            if (lip + length + drop < lip + floor)
            {
                SetBrush(crimson with { W = (float)(0.95 * e) });
                dc.FillEllipse(new Ellipse(new Vector2(x, tip.Y + drop - big * 1.1f + big * 1.15f), big * 0.85f, big * 1.15f), _brush!);
                continue;
            }
            var landAt = (-0.4 + Math.Sqrt(0.16 + 12 * ((floor - length) / size))) / 6;
            var since = Math.Max(0, fallen - landAt);
            var spread = (float)LivelyAction.EaseOutCubic(since / 0.18);
            var fade = 1 - LivelyAction.Ss5((since - 0.25) / 0.6);
            var y = lip + floor;
            float pw = big * (1.4f + 0.9f * spread), ph = big * (0.8f - 0.3f * spread);
            SetBrush(crimson with { W = (float)(0.7 * e * fade) });
            dc.FillEllipse(new Ellipse(new Vector2(x, y), pw / 2, ph / 2), _brush!);
            SetBrush(crimson with { W = (float)(0.85 * e * fade) });
            foreach (var side in (ReadOnlySpan<float>)[-1f, 1f])
            {
                var fx = x + side * big * (0.8f + 0.9f * spread);
                var fy = y - big * (float)(0.5 * Math.Sin(Math.PI * Math.Min(1, since / 0.3)));
                dc.FillEllipse(new Ellipse(new Vector2(fx, fy), big * 0.16f, big * 0.16f), _brush!);
            }
        }
    }

    private void FillStar(ID2D1DeviceContext dc, Vector2 centre, float r)
    {
        if (!(r > 0.1f)) return;
        using var factory = dc.Factory;
        using var star = factory.CreatePathGeometry();
        using (var sink = star.Open())
        {
            var inner = r * 0.28f;
            for (var k = 0; k < 8; k++)
            {
                var angle = k * Math.PI / 4 - Math.PI / 2;
                var rr = k % 2 == 0 ? r : inner;
                var p = centre + new Vector2(rr * (float)Math.Cos(angle), rr * (float)Math.Sin(angle));
                if (k == 0) sink.BeginFigure(p, FigureBegin.Filled);
                else sink.AddLine(p);
            }
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        dc.FillGeometry(star, _brush!);
    }

    private void DisposeResources()
    {
        _brush?.Dispose();
        _round?.Dispose();
        _scratch?.Dispose();
        _brush = null;
        _round = null;
        _scratch = null;
        _scratchWidth = _scratchHeight = 0;
        _dc = null;
    }

    public void Dispose() => DisposeResources();
}
