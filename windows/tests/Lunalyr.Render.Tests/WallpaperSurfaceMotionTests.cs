using Lunalyr.Render.Pacing;
using Fade = Lunalyr.Render.Pacing.LayerCrossfade<string>;
using Hold = Lunalyr.Render.Pacing.BackgroundChoiceHold<string, int>;

namespace Lunalyr.Render.Tests;

/// <summary>The wallpaper's background transitions: the crossfade stack, the song-change hold and the first-frame gate.</summary>
public sealed class WallpaperSurfaceMotionTests
{
    private const double Start = 10_000;
    private const double Duration = 0.8;
    private const double Tol = 1e-9;

    private static double At(double seconds) => Start + seconds;

    /// <summary>How much of each layer reaches the screen, composited bottom to top.</summary>
    private static Dictionary<string, double> Weights(List<Fade.Drawn> drawn)
    {
        var weights = new Dictionary<string, double>();
        foreach (var layer in drawn)
        {
            foreach (var key in weights.Keys.ToList()) weights[key] *= 1 - layer.Opacity;
            weights[layer.Layer] = weights.GetValueOrDefault(layer.Layer) + layer.Opacity;
        }
        return weights;
    }

    private static void AssertDrawn(List<Fade.Drawn> drawn, params (string Layer, double Opacity)[] expected)
    {
        Assert.Equal(expected.Select(e => e.Layer), drawn.Select(d => d.Layer));
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i].Opacity, drawn[i].Opacity, 1e-6);
    }

    // Ported from WallpaperSurfaceMotionTests.swift: testACrossfadeBetweenOpaqueLayersKeepsFullCoverage
    [Fact]
    public void ACrossfadeBetweenOpaqueLayersKeepsFullCoverage()
    {
        var fade = new Fade("A");
        fade.Change("B", ready: true, Start, Duration);
        for (double step = 0; step <= Duration + 1e-9; step += 0.05)
            Assert.Equal(1, Weights(fade.Layers(At(step), Duration)).Values.Sum(), Tol);
        AssertDrawn(fade.Layers(At(Duration / 2), Duration), ("A", 1), ("B", 0.5));
    }

    // Ported from WallpaperSurfaceMotionTests.swift: testATransparentLayerFadesOutWhatItReplaces
    [Fact]
    public void ATransparentLayerFadesOutWhatItReplaces()
    {
        var fade = new Fade("colour");
        fade.Change("desktop", ready: true, Start, Duration);
        AssertDrawn(fade.Layers(At(Duration / 2), Duration, l => l != "desktop"), ("colour", 0.5), ("desktop", 0.5));
        // And back: the colour field covers the desktop as it fades in.
        fade.Change("colour", ready: true, At(5), Duration);
        AssertDrawn(fade.Layers(At(5 + Duration / 2), Duration, l => l != "desktop"), ("desktop", 1), ("colour", 0.5));
    }

    // Ported from WallpaperSurfaceMotionTests.swift: testAnInterruptedCrossfadeCarriesOn
    [Fact]
    public void AnInterruptedCrossfadeCarriesOn()
    {
        var fade = new Fade("A");
        fade.Change("B", ready: true, Start, Duration);
        double interrupt = At(0.3);
        var before = Weights(fade.Layers(interrupt, Duration));
        fade.Change("C", ready: true, interrupt, Duration);
        var after = Weights(fade.Layers(interrupt, Duration));
        Assert.Equal(before.GetValueOrDefault("A"), after.GetValueOrDefault("A"), Tol);
        Assert.Equal(before.GetValueOrDefault("B"), after.GetValueOrDefault("B"), Tol);
        Assert.Equal(0, after.GetValueOrDefault("C"), Tol);
        Assert.Equal(["A", "B", "C"], fade.Entries.Select(e => e.Layer));

        // B reaches full while C is still coming in: A is no longer drawn.
        Assert.Equal(["B", "C"], fade.Layers(At(Duration + 0.01), Duration).Select(d => d.Layer));
        // C done: only C, and the next change lets the rest go.
        Assert.Equal(["C"], fade.Layers(At(0.31 + Duration), Duration).Select(d => d.Layer));
        fade.Change("D", ready: true, At(5), Duration);
        Assert.Equal(["C", "D"], fade.Entries.Select(e => e.Layer));
    }

    // Ported from WallpaperSurfaceMotionTests.swift: testAChangeReplacedBeforeItShowedIsSkipped
    [Fact]
    public void AChangeReplacedBeforeItShowedIsSkipped()
    {
        var fade = new Fade("A");
        fade.Change("B", ready: true, Start, Duration);
        fade.Change("C", ready: true, At(0.001), Duration);
        Assert.Equal(["A", "C"], fade.Entries.Select(e => e.Layer));
    }

    // Ported from WallpaperSurfaceMotionTests.swift: testAtMostThreeLayersAreKept
    [Fact]
    public void AtMostThreeLayersAreKept()
    {
        var fade = new Fade("A");
        string[] next = ["B", "C", "D", "E"];
        for (int index = 0; index < next.Length; index++)
        {
            double date = At(0.1 * (index + 1));
            var before = Weights(fade.Layers(date, Duration));
            fade.Change(next[index], ready: true, date, Duration);
            var after = Weights(fade.Layers(date, Duration));
            foreach (var (key, weight) in before) Assert.Equal(weight, after.GetValueOrDefault(key), 0.05);
        }
        Assert.Equal(Fade.MaximumLayers, fade.Entries.Count);
        Assert.Equal("E", fade.Current);
        Assert.Equal(["A", "B", "E"], fade.Entries.Select(e => e.Layer));
    }

    // Ported from WallpaperSurfaceMotionTests.swift: testTheClockRunsUntilTheLastFadeEnds
    [Fact]
    public void TheClockRunsUntilTheLastFadeEnds()
    {
        var fade = new Fade("A");
        Assert.Null(fade.FadesEnd(Duration));
        fade.Change("B", ready: true, Start, Duration);
        fade.Change("C", ready: true, At(0.4), Duration);
        Assert.Equal(At(0.4 + Duration), fade.FadesEnd(Duration)!.Value, 1e-6);
        // A layer still preparing does not keep the clock running.
        var held = new Fade("A");
        held.Change("B", ready: true, Start, Duration);
        held.Change("D", ready: false, At(0.5), Duration);
        Assert.Equal(["A", "B", "D"], held.Entries.Select(e => e.Layer));
        Assert.Equal(At(Duration), held.FadesEnd(Duration)!.Value, 1e-6);
    }

    // Ported from WallpaperSurfaceMotionTests.swift: testTheFirstLayerFadesInFromBlackOnceReady
    [Fact]
    public void TheFirstLayerFadesInFromBlackOnceReady()
    {
        var fade = new Fade("aurora", ready: false, fadingInAt: Start);
        AssertDrawn(fade.Layers(At(5), Duration), ("aurora", 0));
        fade.BecameReady(At(5));
        AssertDrawn(fade.Layers(At(5 + Duration / 2), Duration), ("aurora", 0.5));
        Assert.Equal([new Fade.Drawn("aurora", 1)], fade.Layers(At(5 + Duration), Duration));
        // Replaced before it ever showed: the next one fades in from black.
        var early = new Fade("aurora", ready: true, fadingInAt: Start);
        early.Change("plasma", ready: true, Start, Duration);
        Assert.Equal(["plasma"], early.Entries.Select(e => e.Layer));
    }

    // Ported from WallpaperSurfaceMotionTests.swift: testAChangeWhilePausedIsShownAtOnce
    [Fact]
    public void AChangeWhilePausedIsShownAtOnce()
    {
        var fade = new Fade("A");
        fade.Change("B", ready: true, double.NegativeInfinity, Duration);
        Assert.Equal([new Fade.Drawn("B", 1)], fade.Layers(Start, Duration));
        var held = new Fade("A");
        held.Change("B", ready: false, Start, Duration);
        held.BecameReady(double.NegativeInfinity);
        Assert.Equal([new Fade.Drawn("B", 1)], held.Layers(Start, Duration));
    }

    // Ported from WallpaperSurfaceMotionTests.swift: testASongChangeWaitsForItsInputsThenChangesOnce
    [Fact]
    public void ASongChangeWaitsForItsInputsThenChangesOnce()
    {
        var hold = new Hold(1);
        Assert.True(hold.Update("first", 1, settled: true, Start));
        int changes = 0;
        // The track arrives; the palette and the lyrics follow.
        if (hold.Update("second", 2, settled: false, At(1))) changes++;
        Assert.Equal(1, hold.Choice); // the last song's background holds meanwhile
        if (hold.Update("second", 3, settled: false, At(1.4))) changes++;
        if (hold.Update("second", 4, settled: true, At(1.9))) changes++;
        Assert.Equal(1, changes);
        Assert.Equal(4, hold.Choice);
        // A lyric upgrade later in the song does not move it.
        Assert.False(hold.Update("second", 5, settled: true, At(60)));
        Assert.Equal(4, hold.Choice);
    }

    // Ported from WallpaperSurfaceMotionTests.swift: testASongWhoseInputsNeverSettleIsTakenAtTheLimit
    [Fact]
    public void ASongWhoseInputsNeverSettleIsTakenAtTheLimit()
    {
        var hold = new Hold(1);
        hold.Update("first", 1, settled: true, Start);
        Assert.False(hold.Update("second", 2, settled: false, At(1)));
        Assert.Equal(At(1 + Hold.SettleLimit), hold.Deadline);
        Assert.False(hold.Update("second", 2, settled: false, At(3.4)));
        Assert.True(hold.Update("second", 2, settled: false, At(3.5)));
        Assert.Equal(2, hold.Choice);
        Assert.Null(hold.Deadline);
    }

    // Ported from WallpaperSurfaceMotionTests.swift: testNothingPlayingKeepsTheLastChoice
    [Fact]
    public void NothingPlayingKeepsTheLastChoice()
    {
        var hold = new Hold(1);
        hold.Update("first", 7, settled: true, Start);
        Assert.False(hold.Update(null, 0, settled: false, At(1)));
        Assert.Equal(7, hold.Choice);
        Assert.False(hold.Update("first", 9, settled: true, At(2)));
        Assert.Equal(7, hold.Choice);
        hold.Update("second", 2, settled: false, At(3));
        hold.Update("third", 3, settled: false, At(5));
        Assert.Equal(At(5 + Hold.SettleLimit), hold.Deadline); // each new song waits from its own start
    }
}
