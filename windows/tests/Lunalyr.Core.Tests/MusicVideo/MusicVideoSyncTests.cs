using Lunalyr.Core.MusicVideo;
using static Lunalyr.Core.Tests.MusicVideo.Sources;

namespace Lunalyr.Core.Tests.MusicVideo;

// Ported from MusicVideoSyncTests.swift and MusicVideoOffsetTests.swift: the user's own offset (nudge, tap, drag), the
// length estimate, and nothing found automatically. No network, no audio, no player, no real settings.
public sealed class MusicVideoSyncTests
{
    private readonly MusicVideoWorld _world = new();
    private MusicVideoSong Song => _world.Song;
    private const string VideoId = "dQw4w9WgXcQ";

    private static readonly MusicVideoLyricLine[] SungLines =
    [
        new(15, "I've been tryna call"),
        new(20, "I've been on my own for long enough"),
        new(58, "I said, ooh, I'm blinded by the lights"),
        new(62.5, "No, I can't sleep until I feel your touch"),
        new(67, "I said, ooh, I'm drowning in the night"),
    ];

    private MusicVideoResolver LinkedResolver()
    {
        var resolver = _world.Resolver();
        resolver.Links.Set(VideoId, Song.Key);
        return resolver;
    }

    // Ported from MusicVideoSyncTests.swift: testDurationEstimateOnlyForAPlausibleIntro
    [Fact]
    public void DurationEstimateOnlyForAPlausibleIntro()
    {
        Assert.Equal(15, MusicVideoDurationEstimate.Intro(215.04, 200));
        Assert.Equal(2, MusicVideoDurationEstimate.Intro(202, 200));
        Assert.Null(MusicVideoDurationEstimate.Intro(201, 200));
        Assert.Null(MusicVideoDurationEstimate.Intro(260, 200));
        Assert.Null(MusicVideoDurationEstimate.Intro(190, 200));
        Assert.Null(MusicVideoDurationEstimate.Intro(null, 200));
    }

    // Ported from MusicVideoSyncTests.swift: testManualOffsetBeatsTheEstimate
    [Fact]
    public void ManualOffsetBeatsTheEstimate()
    {
        var offsets = new MusicVideoOffsets(_world.Storage);
        const string video = "yt:dQw4w9WgXcQ";
        Assert.Equal(MusicVideoOffsetOrigin.None, offsets.Origin(Song.Key, video));
        offsets.SetEstimate(15, Song.Key, video);
        Assert.Equal(15, offsets.Offset(Song.Key, video));
        Assert.Equal(MusicVideoOffsetOrigin.Estimated, offsets.Origin(Song.Key, video));
        offsets.Nudge(0.5, Song.Key, video);
        Assert.Equal(15.5, offsets.Offset(Song.Key, video), 9);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, offsets.Origin(Song.Key, video));
        offsets.Set(0, Song.Key);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, offsets.Origin(Song.Key, video));
        offsets.ClearManual(Song.Key);
        Assert.Equal(15, offsets.Offset(Song.Key, video));
        Assert.Equal(MusicVideoOffsetOrigin.Estimated, offsets.Origin(Song.Key, video));
        Assert.Equal(0, new MusicVideoOffsets(_world.Storage).Offset(Song.Key, video));
    }

    // Ported from MusicVideoSyncTests.swift: testSavedAutomaticOffsetsAreIgnoredAndDropped
    [Fact]
    public void SavedAutomaticOffsetsAreIgnoredAndDropped()
    {
        var other = new MusicVideoSong("Save Your Tears", "The Weeknd", "After Hours", 215);
        _world.Storage.Set(MusicVideoOffsets.RetiredAutomaticKey,
            "{\"" + Song.Key + "\":{\"video\":\"yt:dQw4w9WgXcQ\",\"offset\":12.3,\"kind\":\"captions\"},\"" + other.Key + "\":{\"video\":\"file:/a.m4v\",\"offset\":8.0,\"kind\":\"audio\"}}");
        _world.Storage.Set(MusicVideoOffsets.Key, "{\"" + other.Key + "\":4.5}");
        _world.Storage.Set(MusicVideoOffsets.ManualVideoKey, "{\"" + other.Key + "\":\"file:/a.m4v\"}");
        var offsets = new MusicVideoOffsets(_world.Storage);
        Assert.Null(_world.Storage.Get(MusicVideoOffsets.RetiredAutomaticKey));
        Assert.Equal(0, offsets.Offset(Song.Key, "yt:dQw4w9WgXcQ"));
        Assert.Equal(MusicVideoOffsetOrigin.None, offsets.Origin(Song.Key, "yt:dQw4w9WgXcQ"));
        offsets.SetEstimate(15, Song.Key, "yt:dQw4w9WgXcQ");
        Assert.Equal(15, offsets.Offset(Song.Key, "yt:dQw4w9WgXcQ"));
        Assert.Equal(4.5, offsets.Offset(other.Key, "file:/a.m4v"));
        Assert.Equal(MusicVideoOffsetOrigin.Manual, offsets.Origin(other.Key, "file:/a.m4v"));
    }

    // Ported from MusicVideoSyncTests.swift: testAVideoGetsOnlyTheEstimateUntilTheUserSetsIt
    [Fact]
    public async Task AVideoGetsOnlyTheEstimateUntilTheUserSetsIt()
    {
        var http = new FakeHttp();
        var youTube = _world.Resolver(http: http);
        youTube.Links.Set(VideoId, Song.Key);
        var controller = _world.Controller(youTube);
        controller.Update(_world.Track, enabled: true, online: true);
        await controller.SettleAsync();
        Assert.True(controller.IsShowing(YouTube(VideoId)));
        Assert.Empty(http.Urls);
        Assert.Equal(MusicVideoOffsetOrigin.None, controller.OffsetOrigin);
        Assert.Null(MusicVideoController.TimingLabel(controller.OffsetOrigin));
        controller.NoteVideoDuration(215, YouTube(VideoId));
        Assert.Equal(15, controller.CurrentOffset);
        Assert.Equal("Estimated", MusicVideoController.TimingLabel(controller.OffsetOrigin));

        var library = _world.Controller(_world.Resolver(new FakeLibrary(MusicVideoWorld.LocalPath)));
        library.Update(_world.Track, enabled: true, online: false);
        await library.SettleAsync();
        Assert.True(library.IsShowing(Local(MusicVideoWorld.LocalPath)));
        Assert.Equal(MusicVideoOffsetOrigin.None, library.OffsetOrigin);
    }

    // Ported from MusicVideoSyncTests.swift: testResetGoesBackToTheEstimate
    [Fact]
    public async Task ResetGoesBackToTheEstimate()
    {
        var controller = _world.Controller(LinkedResolver());
        controller.Update(_world.Track, enabled: true, online: true);
        await controller.SettleAsync();
        controller.NoteVideoDuration(212, YouTube(VideoId));
        Assert.Equal(12, controller.CurrentOffset);
        controller.NudgeOffset(-0.5);
        Assert.Equal(11.5, controller.CurrentOffset);
        Assert.Equal("Offset set by you", MusicVideoController.TimingLabel(controller.OffsetOrigin));
        controller.ResetOffset();
        Assert.Equal(12, controller.CurrentOffset);
        Assert.Equal(MusicVideoOffsetOrigin.Estimated, controller.OffsetOrigin);
    }

    // Ported from MusicVideoSyncTests.swift: testTimingLabelStates
    [Fact]
    public void TimingLabelStates()
    {
        Assert.Equal("Offset set by you", MusicVideoController.TimingLabel(MusicVideoOffsetOrigin.Manual));
        Assert.Equal("Estimated", MusicVideoController.TimingLabel(MusicVideoOffsetOrigin.Estimated));
        Assert.Null(MusicVideoController.TimingLabel(MusicVideoOffsetOrigin.None));
    }

    // Ported from MusicVideoSyncTests.swift: testTapLineChoiceAndMaths
    [Fact]
    public void TapLineChoiceAndMaths()
    {
        Assert.Equal(0.2, MusicVideoTapSync.ReactionTime);
        Assert.Equal(15, MusicVideoTapSync.Line(SungLines, 30, true)?.Start);
        Assert.Equal(15, MusicVideoTapSync.Line(SungLines, 3, false)?.Start);
        Assert.Equal(62.5, MusicVideoTapSync.Line(SungLines, 62.7, false)?.Start);
        Assert.Equal(62.5, MusicVideoTapSync.Line(SungLines, 61, false)?.Start);
        Assert.Equal(58, MusicVideoTapSync.Line(SungLines, 59, false)?.Start);
        Assert.Equal(67, MusicVideoTapSync.Line(SungLines, 300, false)?.Start);
        Assert.Null(MusicVideoTapSync.Line([], 3, true));
        Assert.True(MusicVideoTapSync.ArmedBeforeFirstLine(2, SungLines));
        Assert.False(MusicVideoTapSync.ArmedBeforeFirstLine(16, SungLines));
        Assert.Equal(8.4, MusicVideoTapSync.Offset(23.6, 15), 9);
        Assert.Equal(8.5, MusicVideoTapSync.Averaged([8.4, 8.6]));
        Assert.Null(MusicVideoTapSync.Averaged([]));
        Assert.Equal("Video starts 8.4 s ahead", MusicVideoTapSync.Describe(8.4));
        Assert.Equal("Video starts 2.0 s behind", MusicVideoTapSync.Describe(-2));
        Assert.Equal("Video starts with the song", MusicVideoTapSync.Describe(0.01));
    }

    // Ported from MusicVideoSyncTests.swift: testTapToSyncSetsTheUsersOffsetAndASecondTapRefinesIt
    [Fact]
    public async Task TapToSyncSetsTheUsersOffsetAndASecondTapRefinesIt()
    {
        double songTime = 2, clock = 100;
        var controller = _world.Controller(LinkedResolver(), () => (songTime, true), _ => SungLines, () => clock);
        controller.Update(_world.Track, enabled: true, online: false);
        await controller.SettleAsync();
        Assert.True(controller.CanTapSync);
        Assert.Equal("Tap to sync", MusicVideoTapSync.ButtonTitle(controller.TapState));

        controller.TapSyncButton();
        Assert.Equal(new MusicVideoTapState.Armed(true), controller.TapState);
        Assert.Equal("Tap now", MusicVideoTapSync.ButtonTitle(controller.TapState));
        Assert.Equal("Watch the video. Press any key, or Tap now, the moment the singing starts in the video. Esc cancels.",
            MusicVideoTapSync.Note(controller.TapState));
        songTime = 23.6;
        controller.NoteVideoSample(23.5, true, YouTube(VideoId));
        clock += 0.1;
        controller.TapSyncButton();
        Assert.Equal(8.4, controller.CurrentOffset, 9);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, controller.OffsetOrigin);
        Assert.Equal("Offset set by you", MusicVideoController.TimingLabel(controller.OffsetOrigin));
        Assert.Equal(new MusicVideoTapState.Done(8.4, 1, true), controller.TapState);
        Assert.Equal("Video starts 8.4 s ahead, from where the singing starts. Tap again to refine.", MusicVideoTapSync.Note(controller.TapState));
        Assert.Equal("Tap again", MusicVideoTapSync.ButtonTitle(controller.TapState));

        songTime = 60;
        controller.TapSyncButton();
        Assert.Equal(new MusicVideoTapState.Armed(false), controller.TapState);
        Assert.Equal("Watch the video. Press any key, or Tap now, when the next line is sung in the video. Esc cancels.",
            MusicVideoTapSync.Note(controller.TapState));
        clock += 10;
        songTime = 62.9;
        controller.NoteVideoSample(71.3, true, YouTube(VideoId));
        controller.Tap();
        Assert.Equal(8.5, controller.CurrentOffset, 9);
        Assert.Equal(new MusicVideoTapState.Done(8.5, 2, false), controller.TapState);
        Assert.Equal("Video starts 8.5 s ahead, from the next line sung in the video. Averaged over 2 taps.", MusicVideoTapSync.Note(controller.TapState));

        controller.NudgeOffset(0.5);
        Assert.Equal(MusicVideoTapState.Idle, controller.TapState);
        Assert.Equal(9.0, controller.CurrentOffset, 9);
    }

    // Ported from MusicVideoSyncTests.swift: testTapWithoutAVideoSampleUsesTheSyncedPosition
    [Fact]
    public async Task TapWithoutAVideoSampleUsesTheSyncedPosition()
    {
        double songTime = 1;
        var controller = _world.Controller(LinkedResolver(), () => (songTime, true), _ => SungLines);
        controller.Update(_world.Track, enabled: true, online: false);
        await controller.SettleAsync();
        controller.BeginTapSync();
        songTime = 23.6;
        controller.Tap();
        Assert.Equal(8.4, controller.CurrentOffset, 9);
    }

    // Ported from MusicVideoSyncTests.swift: testTapToSyncNeedsLyricsPlaybackAndAVideo
    [Fact]
    public async Task TapToSyncNeedsLyricsPlaybackAndAVideo()
    {
        IReadOnlyList<MusicVideoLyricLine>? lines = null;
        var playing = true;
        var controller = _world.Controller(LinkedResolver(), () => (5, playing), _ => lines);
        Assert.False(controller.CanTapSync);
        controller.Update(_world.Track, enabled: true, online: false);
        await controller.SettleAsync();
        controller.BeginTapSync();
        Assert.Equal(new MusicVideoTapState.Failed(MusicVideoTapFailure.NoLyrics), controller.TapState);
        Assert.Equal("Tap to sync needs this song’s timed lyrics, and they haven’t loaded.", MusicVideoTapSync.Note(controller.TapState));
        lines = SungLines;
        playing = false;
        controller.BeginTapSync();
        Assert.Equal(new MusicVideoTapState.Failed(MusicVideoTapFailure.NotPlaying), controller.TapState);
        playing = true;
        controller.BeginTapSync();
        controller.CancelTapSync();
        Assert.Equal(MusicVideoTapState.Idle, controller.TapState);
        Assert.False(controller.Offsets.HasManual(Song.Key));
        controller.BeginTapSync();
        controller.Update(null, enabled: true, online: false);
        Assert.Equal(MusicVideoTapState.Idle, controller.TapState);
        Assert.False(controller.CanTapSync);
    }

    // Ported from MusicVideoSyncTests.swift: testOnlyPlainKeysOutsideTextInputCountAsTheTap (Windows' virtual keys)
    [Fact]
    public void OnlyPlainKeysOutsideTextInputCountAsTheTap()
    {
        static MusicVideoTapSync.KeyAction Action(int vk, bool shortcut = false, bool repeat = false, bool typing = false) =>
            MusicVideoTapSync.KeyActionFor(vk, shortcut, repeat, typing);
        Assert.Equal(MusicVideoTapSync.KeyAction.Tap, Action(0x20)); // Space
        Assert.Equal(MusicVideoTapSync.KeyAction.Tap, Action(0x41)); // A (Shift held is still a plain key)
        Assert.Equal(MusicVideoTapSync.KeyAction.Tap, Action(0x26)); // ↑
        Assert.Equal(MusicVideoTapSync.KeyAction.Cancel, Action(0x1B));
        Assert.Equal(MusicVideoTapSync.KeyAction.Pass, Action(0x20, typing: true));
        Assert.Equal(MusicVideoTapSync.KeyAction.Pass, Action(0x1B, typing: true));
        Assert.Equal(MusicVideoTapSync.KeyAction.Pass, Action(0x56, shortcut: true)); // Ctrl+V
        Assert.Equal(MusicVideoTapSync.KeyAction.Pass, Action(0x11)); // Ctrl alone
        Assert.Equal(MusicVideoTapSync.KeyAction.Pass, Action(0x10)); // Shift alone
        Assert.Equal(MusicVideoTapSync.KeyAction.Pass, Action(0x5B)); // Windows key alone
        Assert.Equal(MusicVideoTapSync.KeyAction.Pass, Action(0x20, repeat: true));
    }

    // ---- MusicVideoOffsetTests.swift ----

    private async Task<MusicVideoController> Showing()
    {
        var controller = _world.Controller(LinkedResolver());
        controller.Update(_world.Track, enabled: true, online: false);
        await controller.SettleAsync();
        return controller;
    }

    // Ported from MusicVideoOffsetTests.swift: testVideoEarlierAndLaterMoveAQuarterSecondForThisVideo
    [Fact]
    public async Task VideoEarlierAndLaterMoveAQuarterSecondForThisVideo()
    {
        var controller = await Showing();
        controller.NoteVideoDuration(215, YouTube(VideoId));
        controller.VideoEarlier();
        Assert.Equal(15.25, controller.CurrentOffset, 9);
        controller.VideoEarlier();
        Assert.Equal(15.5, controller.CurrentOffset, 9);
        controller.VideoLater();
        controller.VideoLater();
        controller.VideoLater();
        Assert.Equal(14.75, controller.CurrentOffset, 9);
        Assert.Equal(MusicVideoOffsetOrigin.Manual, controller.OffsetOrigin);
        Assert.Equal(14.75, new MusicVideoOffsets(_world.Storage).Offset(Song.Key, "yt:" + VideoId), 9);
    }

    // Ported from MusicVideoOffsetTests.swift: testTheUsersOffsetBelongsToTheVideoItWasSetOn
    [Fact]
    public void TheUsersOffsetBelongsToTheVideoItWasSetOn()
    {
        var offsets = new MusicVideoOffsets(_world.Storage);
        offsets.SetEstimate(12, Song.Key, "yt:BBBBBBBBBBB");
        offsets.Set(8.4, Song.Key, "yt:AAAAAAAAAAA");
        Assert.Equal(8.4, offsets.Offset(Song.Key, "yt:AAAAAAAAAAA"));
        Assert.True(offsets.HasManual(Song.Key, "yt:AAAAAAAAAAA"));
        Assert.False(offsets.HasManual(Song.Key, "yt:BBBBBBBBBBB"));
        Assert.Equal(12, offsets.Offset(Song.Key, "yt:BBBBBBBBBBB"));
        Assert.Equal(MusicVideoOffsetOrigin.Estimated, offsets.Origin(Song.Key, "yt:BBBBBBBBBBB"));
        var reloaded = new MusicVideoOffsets(_world.Storage);
        Assert.Equal(8.4, reloaded.Offset(Song.Key, "yt:AAAAAAAAAAA"));
        Assert.False(reloaded.HasManual(Song.Key, "yt:BBBBBBBBBBB"));
        reloaded.ClearManual(Song.Key);
        Assert.False(new MusicVideoOffsets(_world.Storage).HasManual(Song.Key));
        _world.Storage.Set(MusicVideoOffsets.Key, "{\"" + Song.Key + "\":3.0}");
        Assert.Equal(3, new MusicVideoOffsets(_world.Storage).Offset(Song.Key, "yt:BBBBBBBBBBB"));
    }

    // Ported from MusicVideoOffsetTests.swift: testASmallChangeOfOffsetIsAppliedOnYouTube
    [Fact]
    public void ASmallChangeOfOffsetIsAppliedOnYouTube()
    {
        var corrector = new MusicVideoCorrector(MusicVideoCorrectionStyle.LedSeek);
        static MusicVideoDrift.Sample Playing(double drift) => new(100 + drift, true);
        Assert.Equal(MusicVideoAction.None, corrector.Decide(100, Playing(0.25), true, 10, 0));
        corrector.TargetMoved();
        Assert.Equal(MusicVideoAction.Seek(100 + MusicVideoSeekLatency.Initial, true), corrector.Decide(100, Playing(0.25), true, 10.25, 0));
        Assert.Equal(MusicVideoAction.None, corrector.Decide(100, Playing(0.05), true, 12, 0));
        corrector.TargetMoved();
        Assert.Equal(MusicVideoAction.None, corrector.Decide(100, Playing(0.08), true, 14, 0));
    }

    // Ported from MusicVideoOffsetTests.swift: testDriftThatLingersIsSoughtButNotOverAndOver
    [Fact]
    public void DriftThatLingersIsSoughtButNotOverAndOver()
    {
        var corrector = new MusicVideoCorrector(MusicVideoCorrectionStyle.LedSeek);
        var now = 10.0;
        MusicVideoAction Check(double drift)
        {
            now += 0.25;
            return corrector.Decide(100, new MusicVideoDrift.Sample(100 + drift, true), true, now, 0);
        }
        var seeks = 0;
        for (var i = 0; i < MusicVideoSeekHysteresis.LingerChecks - 1; i++)
            if (Check(0.6) != MusicVideoAction.None) seeks++;
        Assert.Equal(0, seeks);
        Assert.True(Check(0.6).IsSeek);
        var more = Enumerable.Range(0, 60).Select(_ => Check(0.6)).Count(a => a.IsSeek);
        Assert.InRange(more, 2, 6);
    }

    // Ported from MusicVideoOffsetTests.swift: testAVPlayerSeeksForAChangedOffsetAndEasesTheRest
    [Fact]
    public void RatePlayerSeeksForAChangedOffsetAndEasesTheRest()
    {
        var corrector = new MusicVideoCorrector(MusicVideoCorrectionStyle.Rate);
        corrector.TargetMoved();
        Assert.Equal(MusicVideoAction.Seek(50.05, true), corrector.Decide(50, new MusicVideoDrift.Sample(50.5, true), true, 10, 0.05));
        corrector.TargetMoved();
        Assert.Equal(MusicVideoActionKind.Rate, corrector.Decide(50, new MusicVideoDrift.Sample(50.25, true), true, 12, 0.05).Kind);
    }

    private sealed class SlowSurface : IMusicVideoPlaybackSurface
    {
        public Action<MusicVideoDrift.Sample?, double?>? Pending;
        public readonly List<double> Seeks = [];
        public double SeekLead => 0;
        public MusicVideoCorrectionStyle CorrectionStyle => MusicVideoCorrectionStyle.LedSeek;
        public void Sample(Action<MusicVideoDrift.Sample?, double?> done) => Pending = done;

        public void PlayVideo()
        {
        }

        public void PauseVideo()
        {
        }

        public void SeekVideo(double seconds) => Seeks.Add(seconds);
    }

    // Ported from MusicVideoOffsetTests.swift: testTheLoopComparesWithTheSongHalfwayThroughTheAnswerAndRetargets
    [Fact]
    public void TheLoopComparesWithTheSongHalfwayThroughTheAnswerAndRetargets()
    {
        double songTime = 40, clock = 100, offset = 0;
        var drifts = new List<double?>();
        var readAt = new List<double>();
        var surface = new SlowSurface();
        var loop = new MusicVideoSyncLoop(() => songTime, () => true, () => offset, () => clock)
        {
            Surface = surface,
            OnSample = (_, drift, at) =>
            {
                drifts.Add(drift);
                readAt.Add(at);
            },
        };
        loop.Tick();
        songTime += 0.2;
        clock += 0.2;
        surface.Pending!(new MusicVideoDrift.Sample(40.1, true), 215);
        Assert.Equal(0, drifts[^1]!.Value, 9);
        Assert.Equal(100.1, readAt[^1], 9);
        Assert.Equal(30, MusicVideoSyncLoop.Midpoint(10, 30, true));
        Assert.Equal(10.2, MusicVideoSyncLoop.Midpoint(10, 10.2, false));
        offset = 0.25;
        songTime += 0.25;
        clock += 0.25;
        loop.Tick();
        surface.Pending!(new MusicVideoDrift.Sample(songTime, true), 215);
        Assert.Single(surface.Seeks);
        Assert.Equal(40.45 + 0.25 + MusicVideoSeekLatency.Initial, surface.Seeks[0], 9);
    }

    // Ported from MusicVideoOffsetTests.swift: testLengthsOnResultsAreRead
    [Fact]
    public void LengthsOnResultsAreRead()
    {
        Assert.Equal(225, YouTubeSearch.LengthSeconds("3:45"));
        Assert.Equal(3723, YouTubeSearch.LengthSeconds("1:02:03"));
        Assert.Equal(59, YouTubeSearch.LengthSeconds("0:59"));
        Assert.Null(YouTubeSearch.LengthSeconds("LIVE"));
        Assert.Null(YouTubeSearch.LengthSeconds("3:4x"));
        Assert.Null(YouTubeSearch.LengthSeconds("12"));
        const string html = """<script>var ytInitialData = {"contents":[{"videoRenderer":{"videoId":"dQw4w9WgXcQ","title":{"runs":[{"text":"The Weeknd - Blinding Lights (Official Video)"}]},"ownerText":{"runs":[{"text":"TheWeekndVEVO"}]},"lengthText":{"accessibility":{"accessibilityData":{"label":"4 minutes, 22 seconds"}},"simpleText":"4:22"}}}]};</script>""";
        Assert.Equal(262, YouTubeSearchPage.Results(html).FirstOrDefault()?.Duration);
    }

    // Ported from MusicVideoOffsetTests.swift: testAnOfficialVideoAsLongAsTheSongComesFirst
    [Fact]
    public void AnOfficialVideoAsLongAsTheSongComesFirst()
    {
        YouTubeVideoResult[] results =
        [
            new("AAAAAAAAAAA", "The Weeknd - Blinding Lights (Official Music Video)", "TheWeekndVEVO", Duration: 262),
            new("BBBBBBBBBBB", "The Weeknd - Blinding Lights (Official Video)", "The Weeknd", Duration: 201),
            new("CCCCCCCCCCC", "The Weeknd - Blinding Lights", "The Weeknd", Duration: 200),
        ];
        Assert.Equal(["BBBBBBBBBBB", "AAAAAAAAAAA", "CCCCCCCCCCC"],
            YouTubeSearch.OfficialVideos(results, "Blinding Lights", "The Weeknd", 200).Select(r => r.VideoId));
        Assert.Equal(["AAAAAAAAAAA", "BBBBBBBBBBB", "CCCCCCCCCCC"],
            YouTubeSearch.OfficialVideos(results, "Blinding Lights", "The Weeknd").Select(r => r.VideoId));
        Assert.Equal("AAAAAAAAAAA", YouTubeSearch.OfficialVideos(results, "Blinding Lights", "The Weeknd", 230)[0].VideoId);
    }
}
