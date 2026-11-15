using Lunalyr.Core.Visualisers.Capture;
using static Lunalyr.Core.Visualisers.Capture.AudioCaptureGate;

namespace Lunalyr.Core.Tests.Visualisers;

/// <summary>Capture demand and health are pure: no audio device or user setting is touched.</summary>
public class AudioCaptureGateTests
{
    private static Decision Decide(Func<Inputs, Inputs>? change = null)
    {
        var inputs = new Inputs { Enabled = true, VisualiserChosen = true };
        return AudioCaptureGate.Decide(change is null ? inputs : change(inputs));
    }

    // Ported from AudioCaptureGateTests.swift: testCaptureNeedsAnEnabledAwakeVisualiser
    [Fact]
    public void CaptureNeedsAnEnabledAwakeVisualiser()
    {
        Assert.Equal(Decision.Capture(null), Decide());
        Assert.Equal(Decision.Off, Decide(i => i with { Enabled = false }));
        Assert.Equal(Decision.Paused(PauseReason.Wallpaper), Decide(i => i with { VisualiserChosen = false }));
        Assert.Equal(Decision.Paused(PauseReason.DisplaysAsleep), Decide(i => i with { DisplaysAwake = false }));
        Assert.Equal(Decision.Paused(PauseReason.Power("Paused: power.")), Decide(i => i with { PowerPause = "Paused: power." }));
        // Switching off wins over everything else.
        Assert.Equal(Decision.Off, Decide(i => i with { Enabled = false, DisplaysAwake = false }));
    }

    // Ported from AudioCaptureGateTests.swift: testNothingPlayingStopsCaptureAfterTheHangTime
    [Fact]
    public void NothingPlayingStopsCaptureAfterTheHangTime()
    {
        Assert.Equal(Decision.Standby, Decide(i => i with { OutputIdleFor = double.PositiveInfinity }));
        Assert.Equal(Decision.Capture(6), Decide(i => i with { OutputIdleFor = 4 }));
        Assert.Equal(Decision.Standby, Decide(i => i with { OutputIdleFor = OutputHangTime }));
    }

    // Ported from AudioCaptureGateTests.swift: testCoveredWallpapersPauseAfterTheHangTime
    [Fact]
    public void CoveredWallpapersPauseAfterTheHangTime()
    {
        Assert.Equal(Decision.Capture(7), Decide(i => i with { SurfacesHiddenFor = 3 }));
        Assert.Equal(Decision.Paused(PauseReason.Hidden), Decide(i => i with { SurfacesHiddenFor = 10 }));
        // The earliest expiry is the next re-evaluation.
        Assert.Equal(Decision.Capture(2), Decide(i => i with { SurfacesHiddenFor = 3, OutputIdleFor = 8 }));
    }

    // Ported from AudioCaptureGateTests.swift: testAHangTimeThatStartsThisInstantIsScheduled
    [Fact]
    public void AHangTimeThatStartsThisInstantIsScheduled()
    {
        Assert.Equal(Decision.Capture(HiddenHangTime), Decide(i => i with { SurfacesHiddenFor = 0 }));
        Assert.Equal(Decision.Capture(OutputHangTime), Decide(i => i with { OutputIdleFor = 0 }));
        Assert.Equal(Decision.Capture(6), Decide(i => i with { SurfacesHiddenFor = 0, OutputIdleFor = 4 }));
        Assert.Equal(Decision.Capture(null), Decide(i => i with { SurfacesHiddenFor = null, OutputIdleFor = null }));
    }

    // Ported from AudioCaptureGateTests.swift: testPlaybackIsFollowedOnlyWhileItDecidesWhatHappens
    [Fact]
    public void PlaybackIsFollowedOnlyWhileItDecidesWhatHappens()
    {
        Assert.True(Decision.Capture(null).FollowsPlayback);
        Assert.True(Decision.Standby.FollowsPlayback);
        Assert.True(Decision.Paused(PauseReason.Hidden).FollowsPlayback);
        Assert.True(Decision.Paused(PauseReason.WallpaperPaused).FollowsPlayback);
        Assert.False(Decision.Off.FollowsPlayback);
        Assert.False(Decision.Paused(PauseReason.Wallpaper).FollowsPlayback);
        Assert.False(Decision.Paused(PauseReason.Power("Paused.")).FollowsPlayback);
        Assert.False(Decision.Paused(PauseReason.DisplaysAsleep).FollowsPlayback);
    }

    // Ported from AudioCaptureGateTests.swift: testUserStartRunsThroughThePromptWindowButNotThroughSleepOrPowerLimits
    [Fact]
    public void UserStartRunsThroughTheGraceButNotThroughSleepOrPowerLimits()
    {
        Assert.Equal(Decision.Capture(55), Decide(i => i with { SinceUserStart = 5, OutputIdleFor = double.PositiveInfinity }));
        Assert.Equal(Decision.Capture(55), Decide(i => i with { SinceUserStart = 5, SurfacesHiddenFor = 30 }));
        Assert.Equal(Decision.Standby, Decide(i => i with { SinceUserStart = 61, OutputIdleFor = double.PositiveInfinity }));
        Assert.Equal(Decision.Paused(PauseReason.DisplaysAsleep), Decide(i => i with { SinceUserStart = 5, DisplaysAwake = false }));
        Assert.Equal(Decision.Paused(PauseReason.Power("Paused.")), Decide(i => i with { SinceUserStart = 5, PowerPause = "Paused." }));
    }

    // Ported from AudioCaptureGateTests.swift: testPowerLimitsExplainWhatReplacedTheVisualiser (Windows' power names:
    // Energy saver for Low Power Mode)
    [Fact]
    public void PowerLimitsExplainWhatReplacedTheVisualiser()
    {
        Assert.Null(AudioCaptureGate.PowerPause(true, true, "Visualisers", false, true, true));
        Assert.Null(AudioCaptureGate.PowerPause(false, false, "Line patterns", true, false, false));
        Assert.Equal("Paused: Energy saver is showing line patterns. System audio resumes with the visualiser.",
            AudioCaptureGate.PowerPause(true, false, "Line patterns", false, true, false));
        Assert.Contains("Lighter wallpaper", AudioCaptureGate.PowerPause(true, false, "Line patterns", true, true, true));
        var battery = AudioCaptureGate.PowerPause(true, false, "Colour only", false, false, true);
        Assert.Equal("Paused: on battery, Lunalyr is showing colour only. System audio resumes with the visualiser.", battery);
        Assert.Equal(battery, PauseReason.Power(battery!).Message);
    }

    // Ported from AudioCaptureGateTests.swift: testSurfacesDrawByTheRuleTheirFrameClocksFollow (the gate's half; each
    // surface's own state comes from the render engine)
    [Fact]
    public void SurfacesDrawByTheRuleTheirFrameClocksFollow()
    {
        Assert.False(AnySurfaceDrawing([SurfaceDrawing.Hidden, SurfaceDrawing.Frozen]));
        Assert.True(AnySurfaceDrawing([SurfaceDrawing.Frozen, SurfaceDrawing.Drawing]));
        Assert.True(AnySurfaceDrawing([]));
    }

    // Ported from AudioCaptureGateTests.swift: testPauseWallpaperLeavesNothingDrawingOnlyWithoutAPreview
    [Fact]
    public void PauseWallpaperLeavesNothingDrawingOnlyWithoutAPreview()
    {
        Assert.True(Frozen([SurfaceDrawing.Frozen, SurfaceDrawing.Frozen], true));
        Assert.True(Frozen([], true));
        Assert.False(Frozen([SurfaceDrawing.Frozen, SurfaceDrawing.Drawing], true));
        Assert.False(Frozen([SurfaceDrawing.Frozen, SurfaceDrawing.Hidden], true));
        Assert.False(Frozen([SurfaceDrawing.Hidden], false));
        Assert.False(Frozen([], false));
    }

    // Ported from AudioCaptureGateTests.swift: testPauseWallpaperPausesCaptureAtOnce
    [Fact]
    public void PauseWallpaperPausesCaptureAtOnce()
    {
        Assert.Equal(Decision.Paused(PauseReason.WallpaperPaused), Decide(i => i with { SurfacesFrozen = true }));
        Assert.Equal(Decision.Paused(PauseReason.WallpaperPaused), Decide(i => i with { SurfacesFrozen = true, SinceUserStart = 5 }));
        Assert.Equal(Decision.Paused(PauseReason.WallpaperPaused), Decide(i => i with { SurfacesFrozen = true, OutputIdleFor = 3 }));
        Assert.Equal(Decision.Paused(PauseReason.WallpaperPaused), Decide(i => i with { SurfacesFrozen = true, SurfacesHiddenFor = 0 }));
        Assert.Equal(Decision.Off, Decide(i => i with { SurfacesFrozen = true, Enabled = false }));
        Assert.Equal(Decision.Paused(PauseReason.Wallpaper), Decide(i => i with { SurfacesFrozen = true, VisualiserChosen = false }));
        Assert.Equal(Decision.Paused(PauseReason.Power("Paused.")), Decide(i => i with { SurfacesFrozen = true, PowerPause = "Paused." }));
        Assert.Equal(Decision.Paused(PauseReason.DisplaysAsleep), Decide(i => i with { SurfacesFrozen = true, DisplaysAwake = false }));
        Assert.Contains("wallpaper is paused", PauseReason.WallpaperPaused.Message);
    }

    // Ported from AudioCaptureGateTests.swift: testAPlayerPlayingElsewhereIsNeitherAFaultNorAReasonToRecover (without the
    // Mac's switch to compatibility capture, which Windows doesn't have; "this PC" for "this Mac")
    [Fact]
    public void APlayerPlayingElsewhereIsNotAFault()
    {
        var connect = new AudioPlayback(true, "Spotify", false, false);
        Assert.True(connect.PlayingElsewhere);
        Assert.False(connect.PlayingHere);
        Assert.False(connect.OutputActive);
        var silence = AudioCaptureHealthRules.Evaluate(30, 600, 0, 30, true, connect.PlayingHere, connect.OutputActive);
        Assert.Equal(AudioCaptureHealth.Quiet, silence);
        var status = silence.Status(connect, "System audio", "");
        Assert.Equal(AudioCaptureState.Quiet, status.State);
        Assert.StartsWith("Nothing from Spotify is playing on this PC", status.Message);
        Assert.Contains("nothing from Spotify is playing on this PC", StandbyMessage(connect));

        var idleStream = new AudioPlayback(true, "Spotify", false, true);
        var quiet = AudioCaptureHealthRules.Evaluate(30, 600, 0, 30, true, idleStream.PlayingHere, idleStream.OutputActive);
        Assert.Equal(idleStream.ElsewhereMessage, quiet.Status(idleStream, "System audio", "").Message);

        var here = new AudioPlayback(true, "Spotify", true, true);
        var withdrawn = AudioCaptureHealthRules.Evaluate(30, 600, 0, 8, true, here.PlayingHere, here.OutputActive);
        Assert.Equal(AudioCaptureHealth.NotReceiving, withdrawn);
        var warning = withdrawn.Status(here, "System audio", "");
        Assert.Equal(AudioCaptureState.Attention(AudioCaptureProblem.NotReceiving), warning.State);
        Assert.Contains("while Spotify plays", warning.Message);
        Assert.Equal(StandbyMessageText, StandbyMessage(here));

        var unknown = new AudioPlayback(true, "Music", null, null);
        Assert.True(unknown.PlayingHere);
        Assert.True(unknown.OutputActive);
        Assert.True(new AudioPlayback(AnyOutput: null).OutputActive);
        Assert.False(new AudioPlayback(AnyOutput: false).OutputActive);
    }

    // Ported from AudioCaptureGateTests.swift: testCaptureStateFlagsProblemsForTheMenuBar
    [Fact]
    public void CaptureStateFlagsProblemsForTheTray()
    {
        Assert.True(AudioCaptureState.Attention(AudioCaptureProblem.NotReceiving).NeedsAttention);
        Assert.False(AudioCaptureState.Standby.NeedsAttention);
        Assert.False(AudioCaptureState.Paused.NeedsAttention);
        Assert.NotEqual(AudioCaptureState.Attention(AudioCaptureProblem.Stalled), AudioCaptureState.Attention(AudioCaptureProblem.Failed));
    }

    // Ported from AudioRecoveryTests.swift: testHealthReportsMissingAudioOnlyWhenItShouldArrive
    [Fact]
    public void HealthReportsMissingAudioOnlyWhenItShouldArrive()
    {
        static AudioCaptureHealth Health(double elapsed = 20, int packets = 300, double packetAge = 0, double silentFor = 0,
            bool quiet = false, bool playing = true, bool output = true) =>
            AudioCaptureHealthRules.Evaluate(elapsed, packets, packetAge, silentFor, quiet, playing, output);
        Assert.Equal(AudioCaptureHealth.Waiting, Health(elapsed: 2, packets: 0));
        Assert.Equal(AudioCaptureHealth.NoSamples, Health(elapsed: 4, packets: 0));
        Assert.Equal(AudioCaptureHealth.Stalled, Health(packetAge: 2));
        // With nothing playing, an absent or paused stream is not a fault (Windows' loopback sends nothing then).
        Assert.Equal(AudioCaptureHealth.Waiting, Health(elapsed: 30, packets: 0, playing: false, output: false));
        Assert.Equal(AudioCaptureHealth.Quiet, Health(packetAge: 5, quiet: true, playing: false, output: false));
        Assert.Equal(AudioCaptureHealth.Receiving, Health());
        Assert.Equal(AudioCaptureHealth.NotReceiving, Health(silentFor: 8, quiet: true));
        Assert.Equal(AudioCaptureHealth.Quiet, Health(silentFor: 5, quiet: true));
        Assert.Equal(AudioCaptureHealth.Quiet, Health(silentFor: 60, quiet: true, playing: false));
        Assert.True(AudioCaptureHealth.NotReceiving.NeedsAttention());
        Assert.True(AudioCaptureHealth.NoSamples.NeedsAttention());
        Assert.False(AudioCaptureHealth.Quiet.NeedsAttention());
    }
}
