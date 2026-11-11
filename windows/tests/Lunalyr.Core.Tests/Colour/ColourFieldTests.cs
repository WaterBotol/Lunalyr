using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Colour;

/// <summary>The colour field's motion and palette fade (our own design; see ColourField.cs).</summary>
public class ColourFieldTests
{
    [Fact]
    public void MassesStayNearTheirPlacesAndCoverTheScreen()
    {
        const double width = 1920, height = 1080;
        for (var t = 0.0; t < 2000; t += 0.7)
        for (var i = 0; i < ColourField.MassCount; i++)
        {
            var mass = ColourField.At(i, t, width, height);
            Assert.InRange(mass.X, 0, width);
            Assert.InRange(mass.Y, 0, height);
            // Big enough to overlap their neighbours, never so big one colour floods the screen.
            Assert.InRange(mass.Radius, 0.45 * height, 0.75 * width);
        }
    }

    [Fact]
    public void MotionIsCalmAtTheDefaultSpeed()
    {
        // At Motion speed 1 no mass travels faster than 1.2% of the screen's width a second.
        const double width = 1920, height = 1080, step = 1 / 60.0;
        for (var i = 0; i < ColourField.MassCount; i++)
        {
            var previous = ColourField.At(i, ColourField.Clock(0, 1, false), width, height);
            for (var wall = step; wall < 600; wall += step)
            {
                var mass = ColourField.At(i, ColourField.Clock(wall, 1, false), width, height);
                var travelled = Math.Sqrt((mass.X - previous.X) * (mass.X - previous.X) + (mass.Y - previous.Y) * (mass.Y - previous.Y));
                Assert.True(travelled / step < 0.012 * width, $"mass {i} at {wall}: {travelled / step} px/s");
                previous = mass;
            }
        }
    }

    [Fact]
    public void ItMovesVisiblyOverAMinuteAndSlowerWithReduceMotion()
    {
        static double Moved(bool reduce)
        {
            var total = 0.0;
            for (var i = 0; i < ColourField.MassCount; i++)
            {
                var a = ColourField.At(i, ColourField.Clock(0, 1, reduce), 1920, 1080);
                var b = ColourField.At(i, ColourField.Clock(60, 1, reduce), 1920, 1080);
                total += Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            }
            return total / ColourField.MassCount;
        }
        Assert.True(Moved(false) > 100, $"{Moved(false)}");
        Assert.True(Moved(true) < Moved(false) * 0.3);
        Assert.Equal(ColourField.Clock(10, 1, false) * ColourField.ReducedMotionRate, ColourField.Clock(10, 1, true), 1e-12);
        Assert.Equal(0, ColourField.Clock(double.NaN, 1, false));
    }

    [Fact]
    public void TheArrangementDoesNotComeBackAround()
    {
        // Sampled every field-second for the first hour of wall time, no later arrangement repeats the first.
        var first = Enumerable.Range(0, ColourField.MassCount).Select(i => ColourField.At(i, 0, 1, 1)).ToArray();
        for (var t = 5.0; t < ColourField.Clock(3600, 1, false); t += 1)
        {
            var distance = 0.0;
            for (var i = 0; i < ColourField.MassCount; i++)
            {
                var m = ColourField.At(i, t, 1, 1);
                distance += Math.Abs(m.X - first[i].X) + Math.Abs(m.Y - first[i].Y);
            }
            Assert.True(distance > 0.01, $"repeats at {t}");
        }
    }

    [Fact]
    public void FalloffIsSoftAndEdgeless()
    {
        Assert.Equal(ColourField.Peak, ColourField.Falloff(0));
        Assert.Equal(0, ColourField.Falloff(1));
        Assert.Equal(0, ColourField.Falloff(1.5));
        var previous = ColourField.Falloff(0);
        for (var d = 0.01; d <= 1; d += 0.01)
        {
            var value = ColourField.Falloff(d);
            Assert.True(value <= previous);
            previous = value;
        }
        // No edge: the last stretch fades gently into nothing.
        Assert.True(ColourField.Falloff(0.95) < 0.01);
    }

    [Fact]
    public void LuminanceMatchesTheColourFieldEstimate()
    {
        var cover = new AlbumWallpaperPalette(AlbumColourGrid.FallbackHsb);
        var stops = cover.Stops.Select(s => new Vector4(s, 1)).ToArray();
        Assert.Equal(LyricLegibility.ColourFieldLuminance(cover, null), ColourField.Luminance(stops), 1e-6);
    }

    [Fact]
    public void FadeEasesFromWhatIsOnScreen()
    {
        var fade = new ColourFade(2);
        Vector4[] red = [new(1, 0, 0, 1), new(1, 0, 0, 1)], blue = [new(0, 0, 1, 1), new(0, 0, 1, 1)], green = [new(0, 1, 0, 1), new(0, 1, 0, 1)];
        fade.Reset(red);
        Assert.False(fade.IsFading(0));
        Span<Vector4> now = stackalloc Vector4[2];
        fade.Current(0, now);
        Assert.Equal(red[0], now[0]);
        fade.Change(blue, 10);
        Assert.True(fade.IsFading(10.1));
        fade.Current(10, now);
        Assert.Equal(red[0], now[0]);
        fade.Current(10 + WallpaperMotion.SurfaceFade / 2, now);
        Assert.Equal(0.5f, now[0].X, 1e-6f);
        fade.Current(10 + WallpaperMotion.SurfaceFade, now);
        Assert.Equal(blue[0], now[0]);
        // A change mid-fade starts where the screen is, not from either end.
        fade.Change(red, 20);
        fade.Change(green, 20 + WallpaperMotion.SurfaceFade / 2);
        fade.Current(20 + WallpaperMotion.SurfaceFade / 2, now);
        Assert.Equal(0.5f, now[0].X, 1e-6f);
        Assert.Equal(0.5f, now[0].Z, 1e-6f);
        Assert.Equal(0f, now[0].Y, 1e-6f);
    }
}
