using Lunalyr.Render.Pacing;
using Fade = Lunalyr.Render.Pacing.LayerCrossfade<string>;

namespace Lunalyr.Render.Tests;

/// <summary>
/// Ported from WallpaperTransitionTests.swift (the Mac's 5857455 and a4e4f6a): when the fade into a new background may
/// start, so a song change goes from the old picture to the new one, never through the stand-in.
/// </summary>
public sealed class WallpaperTransitionTests
{
    private const double Start = 10_000;
    private const double Duration = 0.8;
    private static double At(double seconds) => Start + seconds;

    private const string VideoA = "musicVideo.youtube:a", VideoB = "musicVideo.youtube:b", Pending = "musicVideo", Visualiser = "visualizer";

    // Ported from WallpaperTransitionTests.swift: testAVideoIsReleasedByItsFirstFrameOrItsWait
    [Fact]
    public void AVideoIsReleasedByItsFirstFrameOrItsWait()
    {
        Assert.True(WallpaperTransition.CanDraw(TransitionGate.Video, firstFrameDrawn: true, overdue: false));
        Assert.True(WallpaperTransition.CanDraw(TransitionGate.Video, firstFrameDrawn: false, overdue: true)); // onto its stand-in
        Assert.False(WallpaperTransition.CanDraw(TransitionGate.Video, firstFrameDrawn: false, overdue: false));
        Assert.Equal(WallpaperTransition.VideoFirstFrameWait, WallpaperTransition.FirstFrameWaitFor(TransitionGate.Video));
        Assert.Equal(WallpaperTransition.FirstFrameWait, WallpaperTransition.FirstFrameWaitFor(TransitionGate.FirstFrame));
    }

    // Ported from WallpaperTransitionTests.swift: testTheStandInOnlyShowsWithNothingElseToHold
    [Fact]
    public void TheStandInOnlyShowsWithNothingElseToHold()
    {
        Assert.False(WallpaperTransition.CanDraw(TransitionGate.VideoLookup, firstFrameDrawn: true, overdue: false));
        Assert.True(WallpaperTransition.CanDraw(TransitionGate.VideoLookup, firstFrameDrawn: false, overdue: false, firstLayer: true));
        // A backstop only: the look-up's own timeout picks the fallback first.
        Assert.True(WallpaperTransition.FirstFrameWaitFor(TransitionGate.VideoLookup) > WallpaperTransition.VideoLookupWait);
        Assert.Equal(3, WallpaperTransition.VideoLookupWait);
    }

    // Ported from WallpaperTransitionTests.swift: testNothingIsHeldOverYourWallpaperThatWouldLeaveBlack
    [Fact]
    public void NothingIsHeldOverYourWallpaper()
    {
        Assert.True(WallpaperTransition.CanDraw(TransitionGate.Video, firstFrameDrawn: false, overdue: false, overOpaque: false));
        Assert.True(WallpaperTransition.CanDraw(TransitionGate.VideoLookup, firstFrameDrawn: false, overdue: false, overOpaque: false));
    }

    // Ported from WallpaperTransitionTests.swift: testAnEffectWaitsOnlyForItsCompile, testStillLayersFadeAtOnce
    [Fact]
    public void StillLayersFadeAtOnceAndAnEffectWaitsForItsCompile()
    {
        Assert.True(WallpaperTransition.CanDraw(TransitionGate.None, firstFrameDrawn: false, overdue: false));
        Assert.False(WallpaperTransition.CanDraw(TransitionGate.Compile, firstFrameDrawn: true, overdue: true, compiled: false));
        Assert.True(WallpaperTransition.CanDraw(TransitionGate.Compile, firstFrameDrawn: false, overdue: false, compiled: true));
    }

    // Ported from WallpaperTransitionTests.swift: testTheClockKeepsRunningForWhatIsHeldOnScreen
    [Fact]
    public void TheClockKeepsRunningForWhatIsHeldOnScreen()
    {
        Assert.True(WallpaperTransition.BackgroundAnimates([(true, true), (false, false)], held: true));
        Assert.False(WallpaperTransition.BackgroundAnimates([(true, true), (false, false)], held: false));
        Assert.False(WallpaperTransition.BackgroundAnimates([(true, false), (false, false)], held: true));
        Assert.True(WallpaperTransition.BackgroundAnimates([(true, true)], held: false));
        Assert.False(WallpaperTransition.BackgroundAnimates([], held: true));
    }

    // Ported from WallpaperTransitionTests.swift: testVideoToVideoHoldsTheOldFrameUntilTheNewOneShows
    [Fact]
    public void VideoToVideoHoldsTheOldFrameUntilTheNewOneShows()
    {
        var fade = new Fade(VideoA);
        fade.Change(Pending, ready: false, Start, Duration); // the look-up: held, never faded to
        Assert.True(fade.IsHeld);
        Assert.Equal([new Fade.Drawn(VideoA, 1), new Fade.Drawn(Pending, 0)], fade.Layers(At(2), Duration));
        fade.Change(VideoB, ready: false, At(3), Duration); // found: the new video, held for its first picture
        Assert.Equal([VideoA, VideoB], fade.Entries.Select(e => e.Layer));
        Assert.Equal([new Fade.Drawn(VideoA, 1), new Fade.Drawn(VideoB, 0)], fade.Layers(At(4), Duration));
        fade.BecameReady(At(4));
        var halfway = fade.Layers(At(4 + Duration / 2), Duration);
        Assert.Equal(1, halfway[0].Opacity); // the outgoing frame stays fully drawn beneath: no dip to black
        Assert.Equal(0.5, halfway[1].Opacity, 9);
        Assert.Equal([new Fade.Drawn(VideoB, 1)], fade.Layers(At(4 + Duration), Duration));
    }

    // Ported from WallpaperTransitionTests.swift: testVideoToFallbackSkipsTheStandIn
    [Fact]
    public void VideoToFallbackSkipsTheStandIn()
    {
        var fade = new Fade(VideoA);
        fade.Change(Pending, ready: false, Start, Duration);
        fade.Change(Visualiser, ready: true, At(3.2), Duration); // the look-up gave up: the fallback, in one fade
        Assert.Equal([VideoA, Visualiser], fade.Entries.Select(e => e.Layer));
        Assert.Equal([VideoA, Visualiser], fade.Layers(At(3.2 + Duration / 2), Duration).Select(d => d.Layer));
    }

    // Ported from WallpaperTransitionTests.swift: testReturningToTheVideoOnScreenChangesNothing
    [Fact]
    public void ReturningToTheVideoOnScreenChangesNothing()
    {
        var fade = new Fade(VideoA);
        fade.Change(Pending, ready: false, Start, Duration);
        fade.Change(VideoA, ready: false, At(1), Duration);
        Assert.Equal([VideoA], fade.Entries.Select(e => e.Layer));
        Assert.False(fade.IsHeld);
        Assert.Null(fade.FadesEnd(Duration));
    }

    // Ported from WallpaperTransitionTests.swift: testTheHoldIsBounded
    [Fact]
    public void TheHoldIsBounded()
    {
        Assert.Equal(WallpaperTransition.VideoLookupWait + WallpaperTransition.VideoFirstFrameWait, WallpaperTransition.LongestHold);
        Assert.True(WallpaperTransition.LongestHold <= 8);
    }
}
