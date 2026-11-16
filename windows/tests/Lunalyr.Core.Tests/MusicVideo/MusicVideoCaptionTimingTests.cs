using System.Globalization;
using System.Text;
using Lunalyr.Core.MusicVideo;
using static Lunalyr.Core.Tests.MusicVideo.MusicVideoCaptionSyncTests;
using static Lunalyr.Core.Tests.MusicVideo.Sources;

namespace Lunalyr.Core.Tests.MusicVideo;

/// <summary>
/// Caption sync in the app: the timing from captions sits between the user's own and the estimate, the user's saved
/// timing for a video always wins (the owner's rule, 16 Nov 2026; the Mac's dcaa3c0), Reset goes back to the captions,
/// and the log says what was decided without lyric text. Ported from the "In the app" half of the Mac's
/// MusicVideoCaptionSyncTests.swift. Every seam is faked: no network, no player, no sound, no real settings.
/// </summary>
public sealed class MusicVideoCaptionTimingTests
{
    private readonly MusicVideoWorld _world = new();
    private const string VideoId = "wIgmyE5Juzw";
    private static MusicVideoSource Source => YouTube(VideoId);
    private const string Video = "yt:" + VideoId;

    private MusicVideoController Make(Func<IReadOnlyList<MusicVideoLyricLine>?> lyrics)
    {
        var resolver = _world.Resolver();
        resolver.Links.Set(VideoId, _world.Song.Key);
        return new MusicVideoController(resolver, new MusicVideoOffsets(_world.Storage), () => (30, true), _ => lyrics(), () => 100)
        {
            ReduceMotion = () => false,
        };
    }

    private async Task<MusicVideoController> Showing(IReadOnlyList<MusicVideoLyricLine>? lyrics)
    {
        var controller = Make(() => lyrics);
        controller.Update(_world.Track, enabled: true, online: false);
        await controller.SettleAsync();
        return controller;
    }

    /// <summary>Another song, then this one again: a new playback of the same video.</summary>
    private async Task Replay(MusicVideoController controller)
    {
        controller.Update(new MusicVideoTrack("Other", "Someone", 100, "X"), enabled: true, online: false);
        controller.Update(_world.Track, enabled: true, online: false);
        await controller.SettleAsync();
    }

    /// <summary>The desktop player's film button: Align, a nudge <paramref name="steps"/> times, Done.</summary>
    private static void AlignInDesktopPlayer(MusicVideoController controller, double delta, int steps = 1)
    {
        controller.NoteVideoSample(50, true, Source);
        Assert.True(controller.BeginAlign());
        for (var i = 0; i < steps; i++) controller.AlignNudge(delta);
        controller.AlignDone();
        Assert.False(controller.IsAligning);
    }

    /// <summary>YouTube's json3 timed text for <paramref name="cues"/>.</summary>
    private static byte[] Json3(IEnumerable<MusicVideoCaptionCue> cues)
    {
        var events = new List<string>();
        foreach (var cue in cues)
        {
            var start = (long)Math.Round(cue.Start * 1000, MidpointRounding.AwayFromZero);
            var words = cue.Words ?? [new MusicVideoCaptionCue.Word(cue.Start, cue.Text)];
            var segs = words.Select((w, i) => i == 0
                ? $"{{\"utf8\":\"{w.Text}\"}}"
                : string.Create(CultureInfo.InvariantCulture, $"{{\"utf8\":\" {w.Text}\",\"tOffsetMs\":{(long)Math.Round(w.Start * 1000, MidpointRounding.AwayFromZero) - start}}}"));
            events.Add($"{{\"tStartMs\":{start},\"dDurationMs\":3000,\"segs\":[{string.Join(",", segs)}]}}");
        }
        return Encoding.UTF8.GetBytes($"{{\"events\":[{string.Join(",", events)}]}}");
    }

    private static byte[] Captions(double offset, int lines = 8) => Json3(AsrCues(SongLines[..lines], offset));

    // Ported from MusicVideoCaptionSyncTests.swift: testCaptionsSetTheTimingFromCaptionsOverTheEstimate
    [Fact]
    public async Task CaptionsSetTheTimingFromCaptionsOverTheEstimate()
    {
        var controller = await Showing(SongLines);
        controller.NoteVideoDuration(224.7, Source);
        Assert.Equal(MusicVideoOffsetOrigin.Estimated, controller.OffsetOrigin);
        Assert.True(controller.WantsCaptions(Source));
        controller.NoteCaptions(Captions(20.6), Source);
        Assert.Equal(MusicVideoOffsetOrigin.Captions, controller.OffsetOrigin);
        Assert.Equal(20.6, controller.CurrentOffset, 0.03);
        Assert.Equal("From captions", MusicVideoController.TimingLabel(MusicVideoOffsetOrigin.Captions));
        Assert.True(controller.WantsCaptions(Source)); // always loaded
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testCaptionsBeforeTheLyricsAreMeasuredWhenTheLyricsArrive
    [Fact]
    public async Task CaptionsBeforeTheLyricsAreMeasuredWhenTheLyricsArrive()
    {
        IReadOnlyList<MusicVideoLyricLine>? lines = null;
        var controller = Make(() => lines);
        controller.Update(_world.Track, enabled: true, online: false);
        await controller.SettleAsync();
        controller.NoteCaptions(Captions(7), Source);
        Assert.Equal(MusicVideoOffsetOrigin.None, controller.OffsetOrigin);
        lines = SongLines;
        controller.NoteVideoSample(37, true, Source);
        Assert.Equal(MusicVideoOffsetOrigin.Captions, controller.OffsetOrigin);
        Assert.Equal(7, controller.CurrentOffset, 0.03);
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testTimingSavedBeforeCaptionsStillWinsAndResetHandsBackToThem
    [Fact]
    public async Task TimingSavedBeforeCaptionsStillWinsAndResetHandsBackToThem()
    {
        // An offset saved before caption sync existed.
        new MusicVideoOffsets(_world.Storage).Set(3, _world.Song.Key, Video);
        var controller = await Showing(SongLines);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, controller.OffsetOrigin);
        Assert.True(controller.WantsCaptions(Source)); // loaded even for a video the user timed
        controller.NoteCaptions(Captions(20.6), Source);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, controller.OffsetOrigin); // the user's saved timing wins over captions
        Assert.Equal(3, controller.CurrentOffset);
        Assert.EndsWith("skipped: the user's timing +3.00 s wins for this video", controller.CaptionLog[^1], StringComparison.Ordinal);
        controller.ResetOffset();
        Assert.Equal(MusicVideoOffsetOrigin.Captions, controller.OffsetOrigin); // Reset hands the video back to its captions
        Assert.Equal(20.6, controller.CurrentOffset, 0.03);
        Assert.False(new MusicVideoOffsets(_world.Storage).Values.ContainsKey(_world.Song.Key)); // and forgets the older timing
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testANudgeFromTheDesktopPlayerOverCaptionsIsSavedAndWins
    [Fact]
    public async Task ANudgeFromTheDesktopPlayerOverCaptionsIsSavedAndWins()
    {
        var controller = await Showing(SongLines);
        controller.NoteCaptions(Captions(20.6), Source);
        Assert.Equal(MusicVideoOffsetOrigin.Captions, controller.OffsetOrigin);

        AlignInDesktopPlayer(controller, MusicVideoAlignKey.Step, steps: 3);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, controller.OffsetOrigin);
        Assert.Equal(20.9, controller.CurrentOffset, 0.001); // moved from the captions' offset, once per press
        var saved = new MusicVideoOffsets(_world.Storage);
        Assert.Equal(20.9, saved.Offset(_world.Song.Key, Video), 0.001);
        Assert.Equal(new MusicVideoOffsets.ManualEntry(20.9, Video), saved.ManualEntryFor(_world.Song.Key));
        Assert.Contains(controller.CaptionLog, line => line.Contains("the user set +20.90 s over the captions' +20.60 s", StringComparison.Ordinal));

        // The player loads the captions again and they measure a little differently.
        controller.NoteCaptions(Captions(20.75), Source);
        Assert.Equal(20.75, controller.Offsets.CaptionedOffset(_world.Song.Key, Video)!.Value, 0.03);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, controller.OffsetOrigin); // captions measured again never undo the user's timing
        Assert.Equal(20.9, controller.CurrentOffset, 0.001);
        Assert.EndsWith("skipped: the user's timing +20.90 s wins for this video", controller.CaptionLog[^1], StringComparison.Ordinal);

        await Replay(controller);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, controller.OffsetOrigin); // kept for the next playback
        Assert.Equal(20.9, controller.CurrentOffset, 0.001);
        controller.NoteCaptions(Captions(20.6), Source);
        Assert.Equal(20.9, controller.CurrentOffset, 0.001); // and its captions don't undo it
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testTheUsersTimingOverCaptionsSurvivesARelaunch
    [Fact]
    public async Task TheUsersTimingOverCaptionsSurvivesARelaunch()
    {
        var first = await Showing(SongLines);
        first.NoteCaptions(Captions(20.6), Source);
        first.VideoLater();
        Assert.Equal(20.35, first.CurrentOffset, 0.001);

        // A new run: nothing in memory, the same settings, captions again.
        var second = await Showing(SongLines);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, second.OffsetOrigin);
        Assert.Equal(20.35, second.CurrentOffset, 0.001);
        second.NoteCaptions(Captions(20.6), Source);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, second.OffsetOrigin);
        Assert.Equal(20.35, second.CurrentOffset, 0.001);
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testANudgeMadeBeforeTheCaptionsArriveStillWins
    [Fact]
    public async Task ANudgeMadeBeforeTheCaptionsArriveStillWins()
    {
        var controller = await Showing(SongLines);
        controller.NoteVideoDuration(224.7, Source);
        Assert.Equal(MusicVideoOffsetOrigin.Estimated, controller.OffsetOrigin);
        controller.VideoEarlier();
        var mine = controller.CurrentOffset;
        controller.NoteCaptions(Captions(20.6), Source);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, controller.OffsetOrigin); // the captions came after the user's nudge
        Assert.Equal(mine, controller.CurrentOffset, 0.001);
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testSettingsAndMenuNudgesTakeTheSamePathAndResetGoesBackToCaptions
    [Fact]
    public async Task SettingsAndMenuNudgesTakeTheSamePathAndResetGoesBackToCaptions()
    {
        var controller = await Showing(SongLines);
        controller.NoteCaptions(Captions(20.6), Source);
        controller.NudgeOffset(0.5); // Settings' Video timing
        controller.VideoLater(); // the tray's Later
        Assert.Equal(20.85, controller.CurrentOffset, 0.001);
        Assert.Equal(20.85, new MusicVideoOffsets(_world.Storage).Offset(_world.Song.Key, Video), 0.001);
        controller.ResetOffset();
        Assert.Equal(MusicVideoOffsetOrigin.Captions, controller.OffsetOrigin); // Reset goes back to the timing from captions
        Assert.Equal(20.6, controller.CurrentOffset, 0.03);
        Assert.False(new MusicVideoOffsets(_world.Storage).HasManual(_world.Song.Key)); // and forgets the user's timing
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testAlignCancelOverCaptionsPutsBackWhatWasThere
    [Fact]
    public async Task AlignCancelOverCaptionsPutsBackWhatWasThere()
    {
        new MusicVideoOffsets(_world.Storage).Set(3, _world.Song.Key, Video);
        var controller = await Showing(SongLines);
        controller.NoteCaptions(Captions(20.6), Source);
        controller.NoteVideoSample(50, true, Source);
        Assert.True(controller.BeginAlign());
        controller.AlignNudge(1);
        Assert.Equal(4, controller.CurrentOffset, 0.001);
        controller.AlignCancel();
        Assert.Equal(MusicVideoOffsetOrigin.Manual, controller.OffsetOrigin);
        Assert.Equal(3, controller.CurrentOffset);
        Assert.Equal(new MusicVideoOffsets.ManualEntry(3, Video), new MusicVideoOffsets(_world.Storage).ManualEntryFor(_world.Song.Key));

        // Nothing saved before: Esc forgets the nudge, and captions apply.
        controller.ResetOffset();
        Assert.Equal(MusicVideoOffsetOrigin.Captions, controller.OffsetOrigin);
        Assert.True(controller.BeginAlign());
        controller.AlignNudge(1);
        controller.AlignCancel();
        Assert.Equal(MusicVideoOffsetOrigin.Captions, controller.OffsetOrigin);
        Assert.Equal(20.6, controller.CurrentOffset, 0.03);
        Assert.False(new MusicVideoOffsets(_world.Storage).HasManual(_world.Song.Key));
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testTheCaptionLogSaysWhatWasDecidedWithoutLyricText
    [Fact]
    public async Task TheCaptionLogSaysWhatWasDecidedWithoutLyricText()
    {
        IReadOnlyList<MusicVideoLyricLine>? lines = null;
        var controller = Make(() => lines);
        var logged = new List<string>();
        controller.CaptionLogged += logged.Add;
        controller.Update(_world.Track, enabled: true, online: false);
        await controller.SettleAsync();
        controller.NoteCaptions(Captions(20.6), Source);
        controller.NoteVideoSample(40, true, Source);
        controller.NoteVideoSample(41, true, Source);
        Assert.Equal([MusicVideoCaptionLog.WaitingLine], controller.CaptionLog); // once, not every sample
        lines = SongLines;
        controller.NoteVideoSample(42, true, Source);
        Assert.Equal("captions: 8 lines matched, 8 agree (spread 0.00 s), offset +20.60 s, applied", controller.CaptionLog[^1]);

        controller.NoteCaptions(Captions(5, lines: 3), Source);
        Assert.EndsWith(new MusicVideoCaptionSync.Verdict(MusicVideoCaptionSync.VerdictKind.TooFew).Summary, controller.CaptionLog[^1],
            StringComparison.Ordinal);
        Assert.Equal(controller.CaptionLog, logged);
        foreach (var line in controller.CaptionLog)
        {
            foreach (var lyric in SongLines) Assert.DoesNotContain(lyric.Text, line, StringComparison.Ordinal);
            Assert.DoesNotContain(VideoId, line, StringComparison.Ordinal);
        }
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testWithoutTrustedCaptionsTheSavedTimingStillApplies
    [Fact]
    public async Task WithoutTrustedCaptionsTheSavedTimingStillApplies()
    {
        var controller = await Showing(SongLines);
        controller.NoteVideoDuration(224.7, Source);
        controller.Offsets.Set(3, _world.Song.Key, Video);
        controller.NoteCaptions(Captions(20.6, lines: 3), Source);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, controller.OffsetOrigin);
        Assert.Equal(3, controller.CurrentOffset);
        controller.VideoEarlier();
        Assert.Equal(3.25, new MusicVideoOffsets(_world.Storage).Offset(_world.Song.Key, Video)); // without captions a nudge is saved as before
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testUntrustworthyCaptionsLeaveTheEstimate
    [Fact]
    public async Task UntrustworthyCaptionsLeaveTheEstimate()
    {
        var controller = await Showing(SongLines);
        controller.NoteVideoDuration(224.7, Source);
        controller.NoteCaptions(Captions(20.6, lines: 3), Source);
        Assert.Equal(MusicVideoOffsetOrigin.Estimated, controller.OffsetOrigin);
        Assert.Null(controller.LastCaptionResult?.Offset);
    }

    [Fact]
    public void TheCaptionLanguageFollowsTheLyricsScript()
    {
        string? Language(params string[] lines)
        {
            var song = lines.Select((t, i) => new MusicVideoLyricLine(i, t)).ToList();
            var controller = Make(() => song);
            controller.Update(_world.Track, enabled: true, online: false);
            return controller.CaptionLanguage();
        }
        Assert.Equal("ko", Language("밤편지를 써요", "이 밤 그날의"));
        Assert.Equal("ja", Language("夜に駆ける", "君と"));
        Assert.Equal("zh", Language("刮风这天", "我试过握着你手"));
        Assert.Null(Language("Sing along", "with me"));
    }
}
