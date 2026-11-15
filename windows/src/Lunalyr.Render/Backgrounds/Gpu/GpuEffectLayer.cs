// The GPU effects background (the Mac's "GPU effects", ShaderStyle): one pixel shader per effect, drawn at a bounded
// pixel budget, tinted with the album gradient and colour richness in the same pass, stretched over the surface, then
// the shared scrim, text backing and grain. The plumbing follows Lunalyr's own Mac renderer (ShaderBackgroundRenderer,
// aa22c91); every effect's shader is written fresh for Windows from a description (docs/GPU-EFFECTS.md).

using System.Numerics;
using System.Runtime.InteropServices;
using Lunalyr.Core.Backgrounds;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Logging;
using Lunalyr.Render.Scene;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Backgrounds.Gpu;

/// <summary>Creates <see cref="GpuEffectLayer"/>s for one effect; the id is <c>shader.&lt;effect id&gt;</c>.</summary>
public sealed class GpuEffectLayerFactory : IRenderLayerFactory
{
    private readonly GpuEffect _effect;
    private readonly BackgroundOptions _options;
    private readonly Logger _log;
    private readonly double? _pixelBudget;

    /// <param name="pixelBudget">Pixels to shade per frame instead of the effect's own budget (measurements only).</param>
    public GpuEffectLayerFactory(string effectId, BackgroundOptions? options = null, Logger? log = null, double? pixelBudget = null)
    {
        _pixelBudget = pixelBudget;
        _effect = GpuEffectCatalog.Find(effectId) ?? throw new ArgumentException($"There is no GPU effect \"{effectId}\".", nameof(effectId));
        _options = options ?? BackgroundOptions.Default;
        _log = log ?? Logger.Null;
    }

    public string Id => _effect.LayerId;

    public GpuEffect Effect => _effect;

    public IRenderLayer Create() => new GpuEffectLayer(_effect, _options, _log, _pixelBudget);

    /// <summary>A factory for every effect in <see cref="GpuEffectCatalog"/>.</summary>
    public static IReadOnlyList<GpuEffectLayerFactory> All(BackgroundOptions? options = null, Logger? log = null) =>
        [.. GpuEffectCatalog.All.Select(e => new GpuEffectLayerFactory(e.Id, options, log))];
}

/// <summary>The constants every effect and pattern shader reads (Common.hlsli's <c>Frame</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuFrameConstants
{
    public Vector2 Size;
    public Vector2 PixelScale;
    public float Time;
    public float Flow;
    public float Vividness;
    public float Strength;
    public Vector4 TintA;
    public Vector4 TintB;
    public Vector4 Stop0, Stop1, Stop2, Stop3, Stop4;
    public Vector4 Extra;

    public const int Bytes = 160;
}

/// <summary>
/// One GPU effect on one surface. Each frame: one full-screen triangle into the budgeted canvas, the canvas stretched
/// over the surface, then the scrim, text backing and grain. Nothing is allocated per frame.
/// </summary>
public sealed class GpuEffectLayer : IRenderLayer
{
    /// <summary>The album's five stops (fewer are padded with the last, as on the Mac).</summary>
    internal const int StopCount = 5;

    private readonly GpuEffect _effect;
    private readonly BackgroundOptions _options;
    private readonly Logger _log;
    private readonly WallpaperShadeDrawer _shade;
    private readonly GpuCanvas _canvas = new();
    private readonly ColourFade _fade = new(StopCount);
    private readonly Vector4[] _colours = new Vector4[StopCount];
    private readonly Vector4[] _current = new Vector4[StopCount];
    private ID3D11PixelShader? _shader;
    private ID2D1DeviceContext? _dc;
    private Palette? _palette;
    private bool _hasPalette;
    private Vector2 _size;
    private float _scale = 1;
    private double _lastNow;
    private bool _failed;
    private EffectClock _clock;
    private ShaderTimeBase _timeBase;

    private readonly double? _pixelBudget;

    internal GpuEffectLayer(GpuEffect effect, BackgroundOptions options, Logger log, double? pixelBudget = null)
    {
        _pixelBudget = pixelBudget;
        _effect = effect;
        _options = options;
        _log = log;
        _shade = new WallpaperShadeDrawer(options);
    }

    public GpuEffect Effect => _effect;

    /// <summary>The canvas the effect is drawn at (for tests and measurements).</summary>
    public (int Width, int Height) CanvasSize => (_canvas.Width, _canvas.Height);

    /// <summary>The effect runs at 15% under Reduce Motion but never stops, as on the Mac.</summary>
    public bool IsAnimating => _options.Speed > 0 || _fade.IsFading(_lastNow) || _shade.IsEasing(_lastNow);

    public void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        ReleaseResources();
        _dc = dc;
        _size = size;
        _scale = scale > 0 && float.IsFinite(scale) ? scale : 1;
        try
        {
            _canvas.Create(dc, GpuFrameConstants.Bytes, premultiplied: false);
            _shader = _canvas.PixelShader("Effects." + _effect.Id);
            if (_shader is null)
            {
                _log.Render.Warn($"GPU effect \"{_effect.Id}\" has no shader in this build; drawing {GpuEffectCatalog.Fallback} instead.");
                _shader = _canvas.PixelShader("Effects." + GpuEffectCatalog.Fallback);
            }
            SizeCanvas();
            _shade.CreateResources(dc, size, scale);
            _failed = _shader is null;
        }
        catch (SharpGenException ex)
        {
            _failed = true;
            _log.Render.Warn($"GPU effect \"{_effect.Id}\": the shader could not be set up.", ex);
        }
    }

    public void Resize(Vector2 size, float scale)
    {
        _size = size;
        _scale = scale > 0 && float.IsFinite(scale) ? scale : 1;
        _shade.Resize(size, scale);
        try { SizeCanvas(); }
        catch (SharpGenException ex)
        {
            _failed = true;
            _log.Render.Warn($"GPU effect \"{_effect.Id}\": resizing failed; waiting for new resources.", ex);
        }
    }

    /// <summary>
    /// The pixel budget for this surface: the Mac's for the effect's cost (smaller when economical). Not
    /// <see cref="BackgroundOptions.PixelBudget"/>, which is the colour field's.
    /// </summary>
    internal double Budget => _pixelBudget ?? ShaderRenderBudget.Pixels(_effect.Cost, preview: false, _options.Economical);

    private void SizeCanvas()
    {
        if (_dc is null) return;
        // The surface's pixels are the Mac's points × backing scale: fit those to the budget.
        var (width, height) = ShaderRenderBudget.DrawableSize(_size.X, _size.Y, 1, Budget);
        _canvas.Resize(_dc, width, height);
    }

    public void Render(in FrameContext frame)
    {
        if (_failed || _shader is null || _canvas.Bitmap is null) return;
        var now = frame.WallSeconds;
        _lastNow = now;
        try
        {
            Observe(frame.Scene.Palette, now);
            _fade.Current(now, _current);
            var (time, flow) = _clock.At(now, SceneAdjust.Speed(_options, frame.Scene), _options.ReduceMotion || frame.Scene.ReduceMotion);
            var (shaderTime, shaderFlow) = _timeBase.Uniforms(time, flow);
            var constants = Constants(_current, shaderTime, shaderFlow, SceneAdjust.Vividness(_options, frame.Scene));
            _canvas.Draw(_shader, constants);
            frame.Dc.DrawBitmap(_canvas.Bitmap, new Vortice.RawRectF(0, 0, _size.X, _size.Y), 1, InterpolationMode.Linear, null, null);
            _shade.Draw(frame, Luminance(_current));
        }
        catch (SharpGenException ex)
        {
            // A lost device comes back through ReleaseResources/CreateResources; until then draw nothing.
            _failed = true;
            _log.Render.Warn($"GPU effect \"{_effect.Id}\": drawing failed; waiting for new resources.", ex);
        }
    }

    private GpuFrameConstants Constants(ReadOnlySpan<Vector4> stops, float time, float flow, double vividness)
    {
        var dip = new Vector2(_size.X / _scale, _size.Y / _scale);
        return new GpuFrameConstants
        {
            Size = dip,
            PixelScale = new Vector2(_canvas.Width / Math.Max(1e-3f, dip.X), _canvas.Height / Math.Max(1e-3f, dip.Y)),
            Time = time,
            Flow = flow,
            Vividness = (float)Math.Clamp(vividness, 0, 1.8),
            TintA = Opaque(stops[0]),
            TintB = Opaque(stops[2]),
            Stop0 = OkLab(stops[0]), Stop1 = OkLab(stops[1]), Stop2 = OkLab(stops[2]), Stop3 = OkLab(stops[3]), Stop4 = OkLab(stops[4]),
        };
    }

    private static Vector4 Opaque(Vector4 c) => new(c.X, c.Y, c.Z, 1);

    /// <summary>
    /// Gamma-encoded sRGB to OKLab, the space the album gradient's stops are blended in (ported from Lunalyr's
    /// <c>ShaderBackgroundRenderer.oklab</c>).
    /// </summary>
    public static Vector4 OkLab(Vector4 rgb)
    {
        static float Linear(float c)
        {
            c = Math.Clamp(c, 0, 1);
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        float r = Linear(rgb.X), g = Linear(rgb.Y), b = Linear(rgb.Z);
        var l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
        var m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
        var s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
        return new Vector4(0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
                           1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
                           0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s, 0);
    }

    /// <summary>The effects take their light from the first and third stops and can glow a little past them.</summary>
    internal static double Luminance(ReadOnlySpan<Vector4> stops)
    {
        var best = Math.Max(LyricLegibility.Luminance(Double3.From(new Vector3(stops[0].X, stops[0].Y, stops[0].Z))),
                            LyricLegibility.Luminance(Double3.From(new Vector3(stops[2].X, stops[2].Y, stops[2].Z))));
        return Math.Min(1, best * 1.1);
    }

    /// <summary>Takes up a new palette: at once the first time, faded after that.</summary>
    private void Observe(Palette palette, double now)
    {
        if (_hasPalette && ReferenceEquals(palette, _palette)) return;
        Collect(palette, _colours);
        _palette = palette;
        if (_hasPalette && _fade.Target.SequenceEqual(_colours)) return;
        if (_hasPalette) _fade.Change(_colours, now); else _fade.Reset(_colours);
        _hasPalette = true;
    }

    /// <summary>The palette's five stops in the cover's order; fewer are padded with the last (the base when none).</summary>
    internal static void Collect(Palette palette, Span<Vector4> into)
    {
        var accents = palette.Accents;
        for (var i = 0; i < into.Length; i++)
            into[i] = accents.Count == 0 ? palette.Base : accents[Math.Min(i, accents.Count - 1)];
    }

    public void ReleaseResources()
    {
        _shader?.Dispose();
        _shader = null;
        _canvas.Release();
        _shade.ReleaseResources();
        _dc = null;
    }

    public void Dispose()
    {
        ReleaseResources();
        _shade.Dispose();
    }
}
