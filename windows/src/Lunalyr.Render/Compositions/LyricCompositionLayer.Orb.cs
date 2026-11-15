// The thinking orb in an instrumental break (the Mac's 64eee43, 22128a2 and 13b8ed6; the countdown ring left out at the
// user's wish): thinking-orbs' dots (Lunalyr.Core.Motion.ThinkingOrbs, MIT) in the lyric ink, over a soft halo. The chosen
// style runs on the break's clock, follows the music when audio is captured, and in the last moments tightens, brightens
// and cross-fades to breathing before the next line.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;
using Lunalyr.Core.Motion.ThinkingOrbs;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Lunalyr.Render.Compositions;

/// <summary>
/// Where the orb hears the music: the app's sound analysis while audio capture runs (set at start-up, as the visualisers'
/// sound is); null or silent, the orb keeps its own clock.
/// </summary>
public static class OrbSound
{
    private static volatile Func<(double Level, double Bass, double Pulse)>? _source;

    public static void Use(Func<(double Level, double Bass, double Pulse)>? source) => _source = source;

    internal static (double Level, double Bass, double Pulse)? Latest() => _source?.Invoke();
}

internal abstract partial class LyricCompositionLayer
{
    private ID2D1GradientStopCollection? _orbHaloStops;
    private ID2D1RadialGradientBrush? _orbHalo;
    private Color4 _orbHaloColour;
    private ID2D1SolidColorBrush? _orbInk;
    private readonly OrbFrame _orbFrame = new();
    private readonly OrbAudioFollower _orbAudio = new();
    private readonly Color4[] _orbInks = new Color4[ThinkingOrbStage.Bands * ThinkingOrbStage.Strengths];
    private ulong _orbBreak;
    private OrbState _orbState = OrbState.Listening;
    private OrbState? _orbPrevious;

    private void DrawCue(ID2D1DeviceContext dc, InstrumentalCue cue, double wall, double yield = 1)
    {
        if (SceneShown is null || yield <= 0.002 || !(cue.Opacity > 0)) return;
        var reduce = ReduceMotion;
        var (style, mix) = ThinkingOrbStage.Style(StyleShown.OrbStyle);
        if (cue.Seed != _orbBreak)
        {
            // A new break: its own clock, and in Mix its own state (never the last break's).
            _orbPrevious = _orbBreak == 0 ? null : _orbState;
            _orbBreak = cue.Seed;
            _orbState = mix ? ThinkingOrbStage.Mix(cue.Seed, _orbPrevious) : style;
            _orbAudio.Reset();
        }
        else if (!mix) _orbState = style;
        if (!reduce && OrbSound.Latest() is { } sound) _orbAudio.Step(wall, sound.Level, sound.Bass, sound.Pulse);
        else _orbAudio.Quiet();

        var diameterPoints = ThinkingOrb.Diameter(BaseSize / Scale);
        var diameter = diameterPoints * Scale;
        var pose = ThinkingOrbStage.Pose(cue, _orbState, diameterPoints, diameter, reduce, _orbAudio);
        var ink = SettledInk;
        OkLab? accent = ink.Accent is { } a ? Perceptual.ToOkLab(new Lunalyr.Core.Colour.Double3(a.R, a.G, a.B)) : null;
        var colours = ThinkingOrb.Colours(ink, accent);
        var dark = Math.Clamp(ink.Dark, 0, 1);
        var shown = (float)(yield * cue.Opacity);
        var centre = StageCentre;
        dc.Transform = Matrix3x2.Identity;
        if (!EnsureOrbBrushes(dc, colours.Halo)) return;

        // The halo: one radial gradient behind everything, 0.8 of the diameter at the pose's scale.
        var poseScale = pose.K * pose.Native / (diameter * 1.3);
        var haloRadius = (float)(0.8 * diameter * poseScale);
        if (haloRadius > 0.5f)
        {
            _orbHalo!.Center = centre;
            _orbHalo.GradientOriginOffset = Vector2.Zero;
            _orbHalo.RadiusX = _orbHalo.RadiusY = haloRadius;
            _orbHalo.Opacity = (float)Math.Clamp(ThinkingOrbStage.Halo(pose.HaloAlpha, dark) * shown, 0, 1);
            dc.FillEllipse(new Ellipse(centre, haloRadius, haloRadius), _orbHalo);
        }

        // The bands × strengths of ink, made once a frame.
        for (var band = 0; band < ThinkingOrbStage.Bands; band++)
        {
            var lobe = ThinkingOrbStage.Lobe(band, colours);
            for (var s = 0; s < ThinkingOrbStage.Strengths; s++)
            {
                var c = Perceptual.ToColour(ThinkingOrbStage.Ink(lobe, s, dark));
                _orbInks[band * ThinkingOrbStage.Strengths + s] = new Color4((float)c.R, (float)c.G, (float)c.B, 1);
            }
        }
        DrawOrbLayer(dc, pose.First, pose, centre, shown);
        if (pose.Second is { } second) DrawOrbLayer(dc, second, pose, centre, shown);
    }

    /// <summary>One state's frame: its lines, then its dots far to near, at the layer's weight.</summary>
    private void DrawOrbLayer(ID2D1DeviceContext dc, in OrbLayer layer, in OrbStagePose pose, Vector2 centre, float shown)
    {
        var native = pose.Native;
        var swell = layer.Resolved.Mode == OrbMode.Wave ? pose.Swell : 1;
        var frame = ThinkingOrbEngine.Frame(layer.Resolved.Mode, native, layer.EngineT, layer.Resolved.Opts, _orbFrame, swell);
        var k = (float)pose.K;
        var half = native / 2f;
        var weight = (float)layer.Weight * shown;
        var brush = _orbInk!;
        foreach (var line in frame.Lines)
        {
            var colour = _orbInks[ThinkingOrbStage.Band((line.Y1 + line.Y2) / 2, native) * ThinkingOrbStage.Strengths
                                  + ThinkingOrbStage.Strength(line.White, pose.Brighten)];
            brush.Color = new Color4(colour.R, colour.G, colour.B, Math.Clamp((float)line.A * weight, 0, 1));
            dc.DrawLine(centre + new Vector2(((float)line.X1 - half) * k, ((float)line.Y1 - half) * k),
                        centre + new Vector2(((float)line.X2 - half) * k, ((float)line.Y2 - half) * k), brush,
                        MathF.Max(0.5f, (float)line.W * k));
        }
        var dotScale = (float)pose.DotScale;
        foreach (var dot in frame.Dots)
        {
            var alpha = Math.Clamp((float)dot.A * weight, 0, 1);
            var radius = (float)dot.R * k * dotScale;
            if (alpha < 0.004f || !(radius > 0.05f)) continue;
            var colour = _orbInks[ThinkingOrbStage.Band(dot.Y, native) * ThinkingOrbStage.Strengths
                                  + ThinkingOrbStage.Strength(dot.White, pose.Brighten)];
            brush.Color = new Color4(colour.R, colour.G, colour.B, alpha);
            var at = centre + new Vector2(((float)dot.X - half) * k, ((float)dot.Y - half) * k);
            dc.FillEllipse(new Ellipse(at, radius, radius), brush);
        }
    }

    /// <summary>The ink brush, and the halo's gradient, remade only when its colour (to 1/255) moves.</summary>
    private bool EnsureOrbBrushes(ID2D1DeviceContext dc, OkLab halo)
    {
        try
        {
            _orbInk ??= dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
            var c = Perceptual.ToColour(halo);
            var colour = new Color4(Step(c.R), Step(c.G), Step(c.B), 1);
            if (_orbHalo is not null && _orbHaloColour.Equals(colour)) return true;
            _orbHalo?.Dispose();
            _orbHaloStops?.Dispose();
            _orbHalo = null;
            _orbHaloStops = dc.CreateGradientStopCollection(
            [
                new GradientStop(0, colour),
                new GradientStop(0.55f, new Color4(colour.R, colour.G, colour.B, 0.4f)),
                new GradientStop(1, new Color4(colour.R, colour.G, colour.B, 0)),
            ], Gamma.StandardRgb, ExtendMode.Clamp);
            _orbHalo = dc.CreateRadialGradientBrush(new RadialGradientBrushProperties(), _orbHaloStops);
            _orbHaloColour = colour;
            return true;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return false;
        }

        static float Step(double channel) => (float)(Math.Round(Math.Clamp(channel, 0, 1) * 255) / 255);
    }

    private void ReleaseOrb()
    {
        _orbHalo?.Dispose();
        _orbHalo = null;
        _orbHaloStops?.Dispose();
        _orbHaloStops = null;
        _orbInk?.Dispose();
        _orbInk = null;
    }
}
