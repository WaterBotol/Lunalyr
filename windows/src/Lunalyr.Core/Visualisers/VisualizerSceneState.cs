// Ported from Lunalyr for Mac's AudioVisualizerView.Coordinator (prepare, sceneInputs; Lunalyr's own lines): what one
// surface's scene draws next frame, without the GPU. Lunalyr.Render's visualiser layer owns one per scene it draws.

using System.Numerics;

namespace Lunalyr.Core.Visualisers;

/// <summary>
/// One surface's scene state between frames: the scene clock on the shared timelines, the smoothed sound, the idle
/// fallback (a quiet ambient spectrum once the music has rested), the sound types the surface was shown, Wavefield's
/// modal bank and the render-scale ladder. Not thread-safe: the render thread owns it. Never allocates per frame.
/// </summary>
public sealed class VisualizerSceneState
{
    /// <summary>The colours a scene without a cover gets: all zero, which tells the shaders to keep their own.</summary>
    public const int ColourStops = 5;

    private readonly VisualizerSmoother _smoothing = new();
    private readonly SoundTypeIdleGate _soundGate = new();
    private WavefieldModes _wavefieldModes = new();
    private bool _hasStyle;
    private object? _palette;
    private double _lastAudibleTime = double.NegativeInfinity;
    private AudioSnapshot _frame = new();

    /// <summary>The render-scale ladder (internal state the host adjusts from its GPU timer).</summary>
    public RenderScaleLadder Ladder;

    public VisualizerStyle Style { get; private set; } = VisualizerStyles.Default;

    public double SceneTime { get; private set; }

    public VisualizerResponse Response { get; private set; } = new();

    /// <summary>The smoothed sound the next frame draws.</summary>
    public ref readonly AudioSnapshot PreparedSound => ref _frame;

    /// <summary>
    /// Takes the frame's inputs. <paramref name="time"/> is the wallpaper's flow clock (already × Speed);
    /// <paramref name="sound"/> is what the listener hears at <paramref name="clock"/> (monotonic seconds);
    /// <paramref name="palette"/> identifies the cover (compared with <see cref="object.Equals(object?)"/>; null for
    /// none). Returns true when the scene changed, so the host measures the new one afresh.
    /// </summary>
    public bool Prepare(VisualizerStyle style, double time, VisualizerResponse response, object? palette, in AudioSnapshot sound,
        double clock, VisualizerTimelines timelines)
    {
        // A surface's first frame is not a break: it may be the copy of the outgoing scene that fades out while another
        // surface keeps the same timeline running.
        var settled = _hasStyle;
        var rebuilt = settled && Style != style;
        if (Style.IsWavefield() && !style.IsWavefield()) _wavefieldModes = new WavefieldModes();
        // Each scene costs the GPU differently: a new one starts at full resolution and is measured afresh.
        if (rebuilt) Ladder.Reset();
        var newPalette = settled && !Equals(palette, _palette);
        Style = style;
        _hasStyle = true;
        Response = response;
        _palette = palette;
        var target = sound;
        // The captured RMS is scaled by 2.4. Keep the live source through short rests, even if capture continues
        // delivering silence.
        var recentlyAudible = clock - _lastAudibleTime < 0.6;
        var threshold = recentlyAudible ? 0.0006f : 0.0012f;
        var audible = target.Wavefield.Rms >= threshold * 2.4f;
        var returned = settled && audible && !recentlyAudible;
        // Music returning after a rest, a new scene and a new cover are moments where the scene clock can restart unnoticed.
        SceneTime = timelines.Time(style, time, response.MotionScale, clock, rebuilt || newPalette || returned);
        if (audible)
        {
            _lastAudibleTime = clock;
        }
        else if (!recentlyAudible)
        {
            var idle = style.IsWavefield() ? new AudioSnapshot() : AmbientSpectrum.At(SceneTime);
            idle.Sound = target.Sound;
            target = idle;
        }
        // While idle nothing new flares and the counters hold; the sustained classes fade out and back in.
        target.Sound = _soundGate.Frame(target.Sound, audible || recentlyAudible, clock);
        _frame = _smoothing.Frame(target, clock);
        return rebuilt;
    }

    /// <summary>
    /// The frame's uniforms and per-scene inputs, advancing Wavefield's modal bank exactly once. <paramref name="colours"/>
    /// receives the cover's five stops with the colour richness in w (all zero without a cover);
    /// <paramref name="modes"/> receives Wavefield's modes (at least one element; the count is in the uniforms' p3).
    /// </summary>
    public VizUniforms Inputs(double width, double height, double sensitivity, double vividness, double clock,
        ReadOnlySpan<Vector3> coverStops, Span<Vector4> colours, Span<WavefieldModeUniforms> modes)
    {
        var u = VisualizerUniforms.Build(Style, width, height, SceneTime, _frame, sensitivity, speed: 1, Response.Calm,
            vividness, Response.Punch);
        if (Style.IsWavefield())
        {
            var next = _wavefieldModes.Frame(_frame, clock, u.Time, u.Sensitivity, Response.Calm, Response.TransientScale,
                (float)Response.MotionScale, Response.PatternRate);
            next.Apply(u.P);
            next.Modes.CopyTo(modes);
            if (next.ModeCount == 0) modes[0] = default;
        }
        for (var i = 0; i < ColourStops; i++)
            colours[i] = coverStops.Length >= ColourStops ? new Vector4(coverStops[i], u.P[15]) : Vector4.Zero;
        return u;
    }
}
