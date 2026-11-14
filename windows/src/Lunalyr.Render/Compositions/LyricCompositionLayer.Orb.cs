// The thinking orb in an instrumental break (the Mac's ff727bb, in place of the three interlude dots): five radial
// gradients, a halo, three drifting lobes and a core, posed and coloured by Lunalyr.Core.Motion.ThinkingOrb.

using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Lunalyr.Render.Compositions;

internal abstract partial class LyricCompositionLayer
{
    private const int OrbHalo = 0, OrbCore = 4;
    private readonly ID2D1GradientStopCollection?[] _orbStops = new ID2D1GradientStopCollection?[5];
    private readonly ID2D1RadialGradientBrush?[] _orbBrushes = new ID2D1RadialGradientBrush?[5];
    private readonly Color4[] _orbColours = new Color4[5];
    private readonly float[] _orbEdges = new float[5];

    private void DrawCue(ID2D1DeviceContext dc, InstrumentalCue cue, double yield = 1)
    {
        // An orb not there yet draws nothing (and needs no ink: a song's opening frames do not wait for it).
        if (SceneShown is null || yield <= 0.002 || ThinkingOrb.Pose(cue, ReduceMotion, increaseContrast: false) is not { } pose) return;
        var ink = SettledInk;
        OkLab? accent = ink.Accent is { } a ? Perceptual.ToOkLab(new Lunalyr.Core.Colour.Double3(a.R, a.G, a.B)) : null;
        var colours = ThinkingOrb.Colours(ink, accent);
        var edge = (float)pose.LobeEdge;
        if (!Orb(dc, OrbHalo, colours.Halo, 0.45f, 0.45f) || !Orb(dc, 1, colours.Primary, edge, 0.75f)
            || !Orb(dc, 2, colours.Neighbour1, edge, 0.75f) || !Orb(dc, 3, colours.Neighbour2, edge, 0.75f)
            || !Orb(dc, OrbCore, colours.Core, 0.5f, 0.5f)) return;

        var radius = (float)(ThinkingOrb.Diameter(BaseSize / Scale) * Scale / 2 * pose.Scale);
        var centre = StageCentre;
        var shown = yield * pose.Opacity;
        dc.Transform = Matrix3x2.Identity;
        Fill(OrbHalo, centre, radius * 2, pose.HaloAlpha);
        // Lobes 1, 2, then 0 on top (a little softer).
        Fill(2, Place(pose.Lobe1), (float)pose.Lobe1.Radius * radius, pose.LobeAlpha);
        Fill(3, Place(pose.Lobe2), (float)pose.Lobe2.Radius * radius, pose.LobeAlpha);
        Fill(1, Place(pose.Lobe0), (float)pose.Lobe0.Radius * radius, pose.LobeAlpha * 0.9);
        Fill(OrbCore, centre + new Vector2(-0.12f, -0.16f) * radius, radius * 0.55f, pose.CoreAlpha);

        Vector2 Place(OrbLobe lobe) => centre + new Vector2((float)lobe.X, (float)lobe.Y) * radius;
        void Fill(int index, Vector2 at, float r, double alpha)
        {
            var brush = _orbBrushes[index]!;
            if (!(r > 0.1f)) return;
            brush.Center = at;
            brush.GradientOriginOffset = Vector2.Zero;
            brush.RadiusX = brush.RadiusY = r;
            brush.Opacity = (float)Math.Clamp(alpha * shown, 0, 1);
            dc.FillEllipse(new Ellipse(at, r, r), brush);
        }
    }

    /// <summary>
    /// The gradient for one part, remade only when its colour (to 1/255) or its edge stop moves: full at the centre,
    /// <paramref name="middle"/> of it at <paramref name="at"/>, clear at the rim. Its alpha is the brush's opacity.
    /// </summary>
    private bool Orb(ID2D1DeviceContext dc, int index, OkLab lab, float at, float middle)
    {
        var c = Perceptual.ToColour(lab);
        var colour = new Color4(Step(c.R), Step(c.G), Step(c.B), 1);
        if (_orbBrushes[index] is not null && _orbColours[index].Equals(colour) && _orbEdges[index] == at) return true;
        _orbBrushes[index]?.Dispose();
        _orbStops[index]?.Dispose();
        _orbBrushes[index] = null;
        try
        {
            _orbStops[index] = dc.CreateGradientStopCollection(
            [
                new GradientStop(0, colour),
                new GradientStop(at, new Color4(colour.R, colour.G, colour.B, middle)),
                new GradientStop(1, new Color4(colour.R, colour.G, colour.B, 0)),
            ], Gamma.StandardRgb, ExtendMode.Clamp);
            _orbBrushes[index] = dc.CreateRadialGradientBrush(new RadialGradientBrushProperties(), _orbStops[index]!);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return false;
        }
        _orbColours[index] = colour;
        _orbEdges[index] = at;
        return true;

        static float Step(double channel) => (float)(Math.Round(Math.Clamp(channel, 0, 1) * 255) / 255);
    }

    private void ReleaseOrb()
    {
        for (var i = 0; i < _orbBrushes.Length; i++)
        {
            _orbBrushes[i]?.Dispose();
            _orbBrushes[i] = null;
            _orbStops[i]?.Dispose();
            _orbStops[i] = null;
        }
    }
}
