using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary>What the tray flyout asks of the controller: this song's lyric timing and the line being sung. Silent fakes only.</summary>
public sealed class AppControllerFlyoutTests : IAsyncDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeLyricLookup _lyrics = new();
    private readonly FakeRenderer _renderer = new();
    private readonly List<FakeMediaSession> _sessions = [];
    private readonly Dictionary<string, double> _saved = [];
    private AppController? _controller;

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
        foreach (var session in _sessions) Assert.Empty(session.Commands); // nothing here sends a command
    }

    private SongOffsets Offsets() => new(_saved, saved =>
    {
        _saved.Clear();
        foreach (var (key, value) in saved) _saved[key] = value;
    });

    private AppController Controller(SongOffsets offsets, AppSettings? settings = null)
    {
        _controller = new AppController(_source, _lyrics, _renderer, settings ?? new AppSettings(), offsets, ReadingServices.Basic,
            new PlaybackGuard(), Logger.Null, options: new AppControllerOptions
            {
                TimeProvider = _time,
                TimelineWait = TimeSpan.FromMilliseconds(60),
                TimelinePoll = TimeSpan.FromMilliseconds(10),
                ArtworkRetries = [],
            });
        return _controller;
    }

    private void Play(string title, string artist, string album, double duration, double position = 30)
    {
        var session = new FakeMediaSession("Spotify.exe", _time);
        _sessions.Add(session);
        session.SetTrack(title, artist, album).SetPlayback(PlaybackStatus.Playing).SetTimeline(position, duration);
        _source.Add(session);
    }

    // Ported from LyricExtrasTests.swift: testSyncNudgeIsSavedForThePlayingRecordingInTenthSteps
    [Fact]
    public async Task TestSyncNudgeIsSavedForThePlayingRecordingInTenthSteps()
    {
        _lyrics.Answer("Midnight Rain", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "First line", "Second line")));
        var offsets = Offsets();
        var controller = Controller(offsets);
        controller.Start();
        Assert.Null(controller.NudgeSongOffset(0.1)); // nothing playing: nothing to nudge
        Play("Midnight Rain", "Taylor Swift", "Midnights", 174);
        await controller.WhenIdleAsync();

        controller.NudgeSongOffset(0.1);
        controller.NudgeSongOffset(0.1);
        Assert.Equal(0.3, controller.NudgeSongOffset(0.1)!.Value, 9);
        Assert.Equal("Lyrics 0.1 s earlier · +0.30 s for this song’s lyric source", Flyout.FlyoutText.NudgeNote(0.1, 0.3));
        Assert.Equal(0.2, controller.NudgeSongOffset(-0.1)!.Value, 9);

        // Saved per recording and lyric source, as the Mac keys them.
        var query = new LyricQuery("Midnight Rain", "Taylor Swift", "Midnights", 174);
        Assert.Equal(0.2, new SongOffsets(_saved).OffsetFor(query.RecordingKey, "LRCLIB"), 9);
        var live = new LyricQuery("Midnight Rain", "Taylor Swift", "Live", 181);
        Assert.Equal(0, offsets.OffsetFor(live.RecordingKey, "LRCLIB")); // other recordings are untouched
    }

    // Ported from DesignSystemTests.swift: testNudgingSavesTheOffsetForThePlayingSongInTheModelsGiven (Windows has no
    // on-wallpaper note yet, so the announcement count isn't ported; Narrator hears the note from the flyout itself)
    [Fact]
    public async Task TestNudgingSavesTheOffsetForThePlayingSong()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "First line", "Second line")));
        var controller = Controller(Offsets());
        controller.Start();
        Assert.False(controller.CanAdjustSongOffset);
        Assert.Null(controller.NudgeSongOffset(0.1)); // idle: nothing to nudge
        Play("Song", "Artist", "Album", 200);
        await controller.WhenIdleAsync();

        Assert.True(controller.CanAdjustSongOffset);
        Assert.Equal(0, controller.SongOffset);
        Assert.Equal(0.1, controller.NudgeSongOffset(0.1));
        Assert.Equal(0.2, controller.NudgeSongOffset(0.1));
        Assert.Equal(0.2, controller.SongOffset, 4);
        controller.ResetSongOffset();
        Assert.Equal(0, controller.SongOffset);
        Assert.Empty(_saved); // back to the lyrics' own timing leaves nothing stored
    }

    [Fact]
    public async Task ANudgeMovesTheWallpapersClockAndTellsTheFlyout()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "First line", "Second line")));
        var controller = Controller(Offsets());
        controller.Start();
        Play("Song", "Artist", "Album", 200);
        await controller.WhenIdleAsync();
        var before = controller.ReadClock().Seconds;
        var raised = 0;
        controller.StatusChanged += (_, _) => raised++;

        controller.NudgeSongOffset(0.5);
        Assert.Equal(before + 0.5, controller.ReadClock().Seconds, 6); // positive: lyrics earlier, so the clock runs ahead
        Assert.True(raised >= 1);
    }

    [Fact]
    public async Task TheLineBeingSungFollowsTheWallpapersClock()
    {
        // Lines at 10–13.5 s and 14–17.5 s; the player is at 30 s.
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "First line", "Second line")));
        var controller = Controller(Offsets());
        controller.Start();
        Assert.Equal(Flyout.LyricNow.None, controller.CurrentLyric());
        Play("Song", "Artist", "Album", 200, position: 15);
        await controller.WhenIdleAsync();

        var now = controller.CurrentLyric();
        Assert.Equal(("Second line", 1, (string?)null), (now.Line, now.Index, now.Next));
        Assert.Equal(("LRCLIB", "Line-synced"), (now.Source, now.Precision));
        Assert.False(now.IsLoading || now.NotFound);

        // Five seconds earlier on the lyric clock reads the first line again.
        controller.NudgeSongOffset(-5);
        Assert.Equal(("First line", 0), (controller.CurrentLyric().Line, controller.CurrentLyric().Index));
    }

    [Fact]
    public async Task NoLyricsReadsAsNotFound()
    {
        _lyrics.Answer("Silent", new LyricLookupReport());
        var controller = Controller(Offsets());
        controller.Start();
        Play("Silent", "Artist", "Album", 200);
        await controller.WhenIdleAsync();
        var now = controller.CurrentLyric();
        Assert.True(now.NotFound);
        Assert.Null(now.Line);
    }
}
