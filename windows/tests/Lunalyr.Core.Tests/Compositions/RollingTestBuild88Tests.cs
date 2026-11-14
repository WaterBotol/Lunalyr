using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// Mac Test Build 88's touches to Spoken, Cascade and Cutout and to the title card (SpokenTypographyView.swift,
/// LyricMotion.c, OpeningTitle.swift). The Mac has no unit tests of its own for these; the expected values are worked
/// out by hand from its code.
/// </summary>
public class RollingTestBuild88Tests
{
    [Fact]
    public void SungWordsStepBackInTwoTiersAndRetireSooner()
    {
        // Spoken: the last word at about three quarters, the one before a little over half, nothing retired.
        Assert.Equal(1, RollingTokenState.Recede(0, CompositionKind.Spoken), 9);
        Assert.Equal(1, RollingTokenState.Recede(-1, CompositionKind.Spoken), 9);
        Assert.Equal(0.74, RollingTokenState.Recede(1, CompositionKind.Spoken), 9);
        Assert.Equal(0.58, RollingTokenState.Recede(2, CompositionKind.Spoken), 9);
        Assert.Equal(0.58, RollingTokenState.Recede(4, CompositionKind.Spoken), 9);
        // Cascade retires from rank 1.7 over 1.2; Cutout from 1.4 over 1.0.
        Assert.Equal(0.74, RollingTokenState.Recede(1, CompositionKind.Cascade), 9);
        Assert.Equal(0.58 * (1 - Easing.Smoothstep((2 - 1.7) / 1.2)), RollingTokenState.Recede(2, CompositionKind.Cascade), 9);
        Assert.Equal(0, RollingTokenState.Recede(2.9, CompositionKind.Cascade), 9);
        Assert.Equal(0.58 * (1 - Easing.Smoothstep(0.6)), RollingTokenState.Recede(2, CompositionKind.Cutout), 9);
        Assert.Equal(0, RollingTokenState.Recede(2.4, CompositionKind.Cutout), 9);
    }

    [Fact]
    public void OnlyTheWordInFocusWearsItsLabel()
    {
        Assert.Equal(1, RollingLabel.Hold(0));
        Assert.Equal(1, RollingLabel.Hold(0.35));
        Assert.Equal(0.5, RollingLabel.Hold(0.675), 9);
        Assert.Equal(0, RollingLabel.Hold(1));
        Assert.Equal(1, RollingLabel.Hold(-1.5), 9);
        // A label's padding grows with its type, at least 11 by 3 points.
        Assert.Equal((11.0, 3.0), RollingLabel.TilePadding(20, 1));
        Assert.Equal((26.0, 8.0), RollingLabel.TilePadding(100, 1));
        Assert.Equal((27.5, 8.0), RollingLabel.TilePadding(100, 2.5));
    }

    [Fact]
    public void LabelsAreTheHighlightsTileWithItsChromaEasedBack()
    {
        var tile = OkLab.FromLch(0.72, 0.15, 0.6);
        var calm = RollingLabel.Calm(tile);
        Assert.Equal(tile.L, calm.L, 12);
        Assert.Equal(tile.Hue, calm.Hue, 9);
        Assert.Equal(0.15 * 0.78, calm.Chroma, 9);
    }

    [Fact]
    public void EachRollingLayoutKeepsOneMotionPersonality()
    {
        var word = RollingWords.Prepare([new LyricWord("tonight", 10, 10.4), new LyricWord("again", 10.5, 11)], 50)[0];
        // Spoken: a calm critically damped glide over 0.9 × the cadence (0.35–0.6 s), never past 1.
        var glide = Math.Min(0.6, Math.Max(0.35, word.Cadence * 0.9));
        Assert.Equal(LyricMotion.Settle(0.2, glide), RollingWords.Handoff(0.2, word, 0, CompositionKind.Spoken), 12);
        for (var age = 0.0; age < 1; age += 0.01)
            Assert.InRange(RollingWords.Handoff(age, word, 0, CompositionKind.Spoken), 0, 1);
        Assert.Equal(1, RollingWords.Handoff(glide, word, 0, CompositionKind.Spoken));
        // Cutout: a livelier spring (rising over half the cadence, 0.15–0.30 s) that runs past and settles back.
        var rise = Math.Min(0.30, Math.Max(0.15, word.Cadence * 0.5));
        Assert.Equal(LyricMotion.SpringStep(0.2, new Spring(3.36 / rise, 0.55)),
                     RollingWords.Handoff(0.2, word, 0, CompositionKind.Cutout), 12);
        var peak = Enumerable.Range(0, 200).Max(i => RollingWords.Handoff(i * 0.005, word, 0, CompositionKind.Cutout));
        Assert.InRange(peak, 1.05, 1.2);
        // Cascade keeps the seeded spring.
        Assert.Equal(LyricMotion.RollingHandoff(0.2, word.Cadence, word.Seed, 0.3),
                     RollingWords.Handoff(0.2, word, 0.3, CompositionKind.Cascade), 12);
        // The cursor takes the layout's own curve.
        RollingWord[] one = [word];
        Assert.Equal(LyricMotion.Settle(0.2, glide), RollingWords.Cursor(one, 10.2, 0, false, CompositionKind.Spoken), 12);
    }

    [Fact]
    public void TheColumnIsAFlushSteadyColumnAndTheCollageKeepsItsOffsets()
    {
        // The column: x is 0 at every rank, the sung word a size up (0.80 → 1.10).
        for (var rank = -2.0; rank <= 5; rank += 0.5)
            Assert.Equal(0, RollingMotion.Pose(RollingStyle.Column, rank, 1, 0.5, 77, 0, true).X);
        Assert.Equal(1.10, RollingMotion.Pose(RollingStyle.Column, 0, -1, 0.5, 77, 0, true).Scale, 9);
        Assert.Equal(0.80, RollingMotion.Pose(RollingStyle.Column, 3, -1, 0.5, 77, 0, true).Scale, 3);
        // The collage: its own offset, sin(1.3 phase) × 0.42 + sin(0.72 rank) × 0.12; the sung label 0.80 → 1.06.
        const ulong seed = 500;
        var phase = (double)(seed % 1021) / 1021 * 6.2831853;
        var collage = RollingMotion.Pose(RollingStyle.Collage, 1, -1, 0.5, seed, 0, true);
        Assert.Equal(Math.Sin(phase * 1.3) * 0.42 + Math.Sin(0.72) * 0.12, collage.X, 9);
        Assert.Equal(1.06, RollingMotion.Pose(RollingStyle.Collage, 0, -1, 0.5, seed, 0, true).Scale, 9);
    }

    [Fact]
    public void CascadePutsEachWordsEdgeOnTheColumn()
    {
        SizeD[] sizes = [new(200, 40), new(100, 40)];
        RollingStack.Slot[] slots = [new() { Rank = 0, Align = 1 }, new() { Rank = 1, Align = 1, Dx = 17 }];
        var centres = RollingStack.Centres(sizes, slots, new PointD(500, 300), 10);
        Assert.Equal(500 + 100, centres[0].X, 9);
        Assert.Equal(500 + 17 + 50, centres[1].X, 9);
        // Right to left: the right edge.
        Assert.Equal(500 - 100, RollingStack.Centres(sizes[..1], [slots[0] with { Align = -1 }], new PointD(500, 300), 10)[0].X, 9);
    }

    [Fact]
    public void CutoutSitsLowerWithMoreAirAndEveryLayoutKeepsTitleSafe()
    {
        Assert.Equal(0.59, RollingStack.FocusHeight(CompositionKind.Cutout));
        Assert.Equal(0.54, RollingStack.FocusHeight(CompositionKind.Cascade));
        Assert.Equal(16.0 / 46, RollingStack.Gap(CompositionKind.Cutout), 12);
        Assert.Equal(8.0 / 46, RollingStack.Gap(CompositionKind.Spoken), 12);
        Assert.Equal((100.0, 50.0), RollingStack.Margins(new SizeD(2000, 1000), 60));
        Assert.Equal((22.0, 22.0), RollingStack.Margins(new SizeD(200, 100), 100));
        // A vertical margin of its own.
        Assert.Equal(1, WordStageFit.Scale(new SizeD(100, 40), new SizeD(1000, 600), new PointD(500, 60), 1, 0, 10));
        Assert.True(WordStageFit.Scale(new SizeD(100, 40), new SizeD(1000, 600), new PointD(500, 60), 1, 0, 10, 50) < 1);
    }

    [Fact]
    public void TheTitleCardGlowsFullyOverADarkPictureAndGentlyUnderDarkType()
    {
        Assert.Equal(1, OpeningTitle.GlowStrength(dark: false));
        Assert.Equal(0.35, OpeningTitle.GlowStrength(dark: true));
    }
}
