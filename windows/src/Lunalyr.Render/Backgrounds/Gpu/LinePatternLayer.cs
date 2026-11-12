// The line patterns background (the Mac's "Line patterns", BackgroundStyle): the album colour field ("gradient") with a
// pattern of faint white lines over it, added as light (never darkening), under the scrim and text backing. Each
// pattern is drawn either by a Direct2D painter (sparse strokes: rings, trees, traces…) or by a pixel shader at native
// resolution (dense line fields). The plumbing is Windows' own; every pattern is written fresh from a description
// (docs/GPU-EFFECTS.md), nothing of the Mac's FractalBackgroundView is translated.

using System.Numerics;
using Lunalyr.Core.Backgrounds;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Render.Scene;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Lunalyr.Render.Backgrounds.Gpu;

/// <summary>Constants for the line patterns.</summary>
public static class LinePatternLayer
{
    /// <summary>The Mac's default "Pattern opacity" (<c>patternOpacity</c>).</summary>
    public const double DefaultOpacity = 0.85;

    /// <summary>The patterns' own opacity on the Mac, under the setting's.</summary>
    public const double InkOpacity = 0.85;

    /// <summary>The pattern clock runs at half the flow clock.</summary>
    public const double PhaseRate = 0.5;
}

/// <summary>Creates line-pattern layers for one pattern; the id is <c>pattern:&lt;pattern id&gt;</c>.</summary>
public sealed class LinePatternLayerFactory : IRenderLayerFactory
{
    private readonly LinePattern _pattern;
    private readonly BackgroundOptions _options;
    private readonly double _opacity;
    private readonly Logger _log;

    public LinePatternLayerFactory(string patternId, BackgroundOptions? options = null, double opacity = LinePatternLayer.DefaultOpacity,
                                   Logger? log = null)
    {
        _pattern = LinePatternCatalog.Find(patternId) ?? throw new ArgumentException($"There is no line pattern \"{patternId}\".", nameof(patternId));
        _options = options ?? BackgroundOptions.Default;
        _opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : LinePatternLayer.DefaultOpacity;
        _log = log ?? Logger.Null;
    }

    public string Id => _pattern.LayerId;

    public LinePattern Pattern => _pattern;

    public IRenderLayer Create() => new GradientLayer(_options, _log, new LinePatternOverlay(_pattern, _options, _opacity, _log));

    public static IReadOnlyList<LinePatternLayerFactory> All(BackgroundOptions? options = null, double opacity = LinePatternLayer.DefaultOpacity,
                                                            Logger? log = null) =>
        [.. LinePatternCatalog.All.Select(p => new LinePatternLayerFactory(p.Id, options, opacity, log))];
}

/// <summary>What a line painter needs each frame. Valid only during <see cref="ILinePainter.Draw"/>.</summary>
internal readonly ref struct LinePatternFrame
{
    /// <summary>The surface in physical pixels.</summary>
    public required Vector2 Size { get; init; }

    /// <summary>The monitor's DPI scale: the Mac's sizes are in points, so multiply them by this.</summary>
    public required float Scale { get; init; }

    /// <summary>The pattern clock in seconds (half the flow clock; slowed by Motion speed and Reduce Motion).</summary>
    public required double Phase { get; init; }

    /// <summary>The song's seed (title and artist), for the arrangement's random details.</summary>
    public required int Seed { get; init; }

    /// <summary>The white brush, drawn as added light. Set its opacity with <see cref="Ink"/>.</summary>
    public required ID2D1SolidColorBrush Brush { get; init; }

    /// <summary>The overall line opacity (0.85 × Pattern opacity).</summary>
    public required float Strength { get; init; }

    /// <summary>The brush at <paramref name="opacity"/> (0...1, the stroke's own), times the pattern's strength.</summary>
    public ID2D1SolidColorBrush Ink(float opacity)
    {
        Brush.Opacity = Math.Clamp(opacity, 0, 1) * Strength;
        return Brush;
    }
}

/// <summary>
/// Draws one line pattern with Direct2D primitives. Rules: never allocate in <see cref="Draw"/> (no geometry, no
/// arrays, no closures); build anything fixed (path geometries that only move or turn) in <see cref="Prepare"/>, which
/// is called when resources are made, when the surface changes size and when the song's seed changes. Leave the
/// device context's transform as it was found.
/// </summary>
internal interface ILinePainter : IDisposable
{
    void Prepare(ID2D1DeviceContext dc, ID2D1Factory1 factory, Vector2 size, float scale, int seed);

    void Draw(ID2D1DeviceContext dc, in LinePatternFrame frame);

    /// <summary>Lets go of device resources (the device was lost or the surface is going away).</summary>
    void Release();
}

/// <summary>The line pattern drawn over a colour field: by its painter when it has one, otherwise by its shader.</summary>
internal sealed class LinePatternOverlay : IFieldOverlay
{
    private readonly LinePattern _pattern;
    private readonly BackgroundOptions _options;
    private readonly double _opacity;
    private readonly Logger _log;
    private readonly ILinePainter? _painter;
    private readonly GpuCanvas _canvas = new();
    private ID3D11PixelShader? _shader;
    private ID2D1SolidColorBrush? _ink;
    private ID2D1Factory1? _factory;
    private ID2D1DeviceContext? _dc;
    private Vector2 _size;
    private float _scale = 1;
    private int _seed;
    private bool _seeded;
    private string? _title, _artist;
    private bool _failed;
    private EffectClock _clock;
    private ShaderTimeBase _timeBase;

    public LinePatternOverlay(LinePattern pattern, BackgroundOptions options, double opacity, Logger log)
    {
        _pattern = pattern;
        _options = options;
        _opacity = opacity;
        _log = log;
        _painter = LinePainters.For(pattern.Id);
    }

    public double? PatternOpacity => _opacity;

    private float Strength => (float)(LinePatternLayer.InkOpacity * _opacity);

    public void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        ReleaseResources();
        _dc = dc;
        _size = size;
        _scale = scale > 0 && float.IsFinite(scale) ? scale : 1;
        try
        {
            _ink = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
            using (var factory = dc.Factory) _factory = factory.QueryInterface<ID2D1Factory1>();
            if (_painter is not null)
            {
                _painter.Prepare(dc, _factory, _size, _scale, _seed);
            }
            else
            {
                _canvas.Create(dc, GpuFrameConstants.Bytes, premultiplied: true);
                _shader = _canvas.PixelShader("Patterns." + _pattern.Id);
                if (_shader is null) _log.Render.Warn($"Line pattern \"{_pattern.Id}\" has no painter or shader in this build; drawing none.");
                _canvas.Resize(dc, (int)MathF.Ceiling(size.X), (int)MathF.Ceiling(size.Y));
            }
            _failed = false;
        }
        catch (SharpGenException ex)
        {
            _failed = true;
            _log.Render.Warn($"Line pattern \"{_pattern.Id}\": setting up failed.", ex);
        }
    }

    public void Resize(Vector2 size, float scale)
    {
        _size = size;
        _scale = scale > 0 && float.IsFinite(scale) ? scale : 1;
        if (_dc is null || _failed) return;
        try
        {
            if (_painter is not null && _factory is not null) _painter.Prepare(_dc, _factory, _size, _scale, _seed);
            else _canvas.Resize(_dc, (int)MathF.Ceiling(size.X), (int)MathF.Ceiling(size.Y));
        }
        catch (SharpGenException ex)
        {
            _failed = true;
            _log.Render.Warn($"Line pattern \"{_pattern.Id}\": resizing failed.", ex);
        }
    }

    public void Draw(in FrameContext frame)
    {
        if (_failed || _dc is null || _ink is null) return;
        var dc = frame.Dc;
        try
        {
            Reseed(frame.Scene);
            var (time, flow) = _clock.At(frame.WallSeconds, _options.Speed, _options.ReduceMotion || frame.Scene.ReduceMotion);
            var phase = flow * LinePatternLayer.PhaseRate;
            var blend = dc.PrimitiveBlend;
            var transform = dc.Transform;
            dc.PrimitiveBlend = PrimitiveBlend.Add;
            if (_painter is not null)
            {
                var pattern = new LinePatternFrame
                {
                    Size = _size, Scale = _scale, Phase = phase, Seed = _seed, Brush = _ink, Strength = Strength,
                };
                _painter.Draw(dc, pattern);
            }
            else if (_shader is not null && _canvas.Bitmap is not null)
            {
                var (shaderTime, shaderPhase) = _timeBase.Uniforms(time, phase);
                var constants = new GpuFrameConstants
                {
                    Size = new Vector2(_size.X / _scale, _size.Y / _scale),
                    PixelScale = new Vector2(_scale, _scale),
                    Time = shaderTime,
                    Flow = shaderPhase,
                    Vividness = 1,
                    Strength = Strength,
                    Extra = new Vector4(SeedFraction(_seed, 0), SeedFraction(_seed, 1), _scale, 0),
                };
                _canvas.Draw(_shader, constants);
                dc.DrawBitmap(_canvas.Bitmap, new Vortice.RawRectF(0, 0, _canvas.Width, _canvas.Height), 1, InterpolationMode.NearestNeighbor, null, null);
            }
            dc.Transform = transform;
            dc.PrimitiveBlend = blend;
        }
        catch (SharpGenException ex)
        {
            _failed = true;
            _log.Render.Warn($"Line pattern \"{_pattern.Id}\": drawing failed; waiting for new resources.", ex);
        }
    }

    /// <summary>Takes up the song's seed when the title or artist changes (never per frame otherwise).</summary>
    private void Reseed(SceneSnapshot scene)
    {
        if (_seeded && ReferenceEquals(scene.Title, _title) && ReferenceEquals(scene.Artist, _artist)) return;
        _title = scene.Title;
        _artist = scene.Artist;
        var seed = TrackCharacter.SeedOf(_title, _artist);
        var changed = !_seeded || seed != _seed;
        _seed = seed;
        _seeded = true;
        if (changed && _painter is not null && _dc is not null && _factory is not null) _painter.Prepare(_dc, _factory, _size, _scale, _seed);
    }

    /// <summary>A stable 0...1 from the seed, for the shaders.</summary>
    internal static float SeedFraction(int seed, int salt)
    {
        var h = (uint)seed * 2654435761u ^ (uint)(salt + 1) * 2246822519u;
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        return (h >> 8) / 16777216f;
    }

    public void ReleaseResources()
    {
        _painter?.Release();
        _shader?.Dispose(); _shader = null;
        _canvas.Release();
        _ink?.Dispose(); _ink = null;
        _factory?.Dispose(); _factory = null;
        _dc = null;
    }

    public void Dispose()
    {
        ReleaseResources();
        _painter?.Dispose();
    }
}

/// <summary>The Direct2D painter for each line pattern that has one; the others are drawn by Patterns/&lt;id&gt;.hlsl.</summary>
internal static partial class LinePainters
{
    public static ILinePainter? For(string patternId) => Create(patternId);

    /// <summary>Implemented per pattern in Painters/*.cs; null for the patterns drawn by a shader.</summary>
    private static partial ILinePainter? Create(string patternId);
}
