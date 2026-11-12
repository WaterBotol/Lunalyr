using System.Text;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Pipeline;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Shell.Demo;
using Lunalyr.Shell.Lyrics;
using Lunalyr.Shell.LyricTools;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// The lyric tools of Settings › Timing and the tray flyout: Tap to sync against the player's own clock, searching under
/// your own words, using a match for the song (a pin), importing a file, and what the wallpaper shows when a song has no
/// lyrics. Silent fakes only: nothing is sent to a player and nothing goes to the network.
/// </summary>
public sealed class LyricToolsTests : IAsyncDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly ToolsLookup _lyrics = new();
    private readonly FakeRenderer _renderer = new();
    private readonly List<FakeMediaSession> _sessions = [];
    private readonly Dictionary<string, double> _saved = [];
    private readonly TempFolder _folder = new();
    private AppController? _controller;

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
        foreach (var session in _sessions) Assert.Empty(session.Commands); // nothing here sends a playback command
        _folder.Dispose();
    }

    private SongOffsets Offsets() => new(_saved, saved =>
    {
        _saved.Clear();
        foreach (var (key, value) in saved) _saved[key] = value;
    });

    private AppController Controller(AppSettings? settings = null, SongOffsets? offsets = null, ILyricLookup? lyrics = null)
    {
        _controller = new AppController(_source, lyrics ?? _lyrics, _renderer, settings ?? new AppSettings(), offsets ?? Offsets(),
            ReadingServices.Basic, new PlaybackGuard(), Logger.Null, options: new AppControllerOptions
            {
                TimeProvider = _time,
                TimelineWait = TimeSpan.FromMilliseconds(60),
                TimelinePoll = TimeSpan.FromMilliseconds(10),
                ArtworkRetries = [],
            });
        return _controller;
    }

    private FakeMediaSession Play(string title, string artist = "Artist", double duration = 200, double position = 0)
    {
        var session = new FakeMediaSession("Spotify.exe", _time);
        _sessions.Add(session);
        session.SetTrack(title, artist, "Album").SetPlayback(PlaybackStatus.Playing).SetTimeline(position, duration);
        _source.Add(session);
        return session;
    }

    private static LyricCandidate Candidate(LyricSourceId source, string label, string title = "Song", string artist = "Artist",
                                            double duration = 200) =>
        new(source, TestLyrics.Synced(label, "First line", "Second line", "Third line"), title, artist, duration, 1);

    // ---- Tap to sync ----

    // Ported from LyricTapSyncTests.swift: testKeysAfterTheResultAreKeptFromTheAppBehind (Windows' virtual keys stand in
    // for the Mac's key codes: Space 49 → 0x20, A 0 → 0x41, Esc 53 → 0x1B, Return 36 and keypad Enter 76 → 0x0D,
    // Command+Q → Ctrl+Q, Shift 56 → 0x10)
    [Fact]
    public void KeysAfterTheResultAreKeptFromTheAppBehind()
    {
        static TapKeyResponse Response(bool listening, int key, bool shortcut = false, bool repeats = false) =>
            TapSyncKeys.Response(listening, key, shortcut, repeats, textInputFocused: false);
        // Listening: Space and letters tap, Esc stops.
        Assert.Equal(TapKeyResponse.Tap, Response(true, 0x20));
        Assert.Equal(TapKeyResponse.Tap, Response(true, 0x41));
        Assert.Equal(TapKeyResponse.Stop, Response(true, 0x1B));
        // The result showing: a tap a beat late never reaches the app behind (Space there would pause the song); Esc or
        // Enter closes it.
        Assert.Equal(TapKeyResponse.Swallow, Response(false, 0x20));
        Assert.Equal(TapKeyResponse.Swallow, Response(false, 0x41));
        Assert.Equal(TapKeyResponse.Close, Response(false, 0x1B));
        Assert.Equal(TapKeyResponse.Close, Response(false, 0x0D));
        // Shortcuts and modifier keys always pass.
        Assert.Equal(TapKeyResponse.Pass, Response(false, 0x51, shortcut: true));
        Assert.Equal(TapKeyResponse.Pass, Response(true, 0x51, shortcut: true));
        Assert.Equal(TapKeyResponse.Pass, Response(false, 0x10));
        Assert.Equal(TapKeyResponse.Pass, TapSyncKeys.Response(false, 0x41, false, false, textInputFocused: true));
        // A held key is one tap, not many.
        Assert.Equal(TapKeyResponse.Pass, Response(true, 0x20, repeats: true));
        // Esc stops even with a modifier held.
        Assert.Equal(TapKeyResponse.Stop, Response(true, 0x1B, shortcut: true));
    }

    // Ported from LyricTapSyncTests.swift: testTheResultLooksAndStaysAsItShould (the symbols and Narrator's
    // announcements; the dismiss delays are in Core's LyricTapSyncTests)
    [Fact]
    public void TheResultLooksAsItShould()
    {
        Assert.Equal(TapSyncLook.Tapping, TapSyncKeys.Look(TapSyncPhase.Listening));
        Assert.Equal(TapSyncLook.Success, TapSyncKeys.Look(new TapSyncPhase.Applied(0, 0, 4, 4)));
        Assert.Equal(TapSyncLook.Warning, TapSyncKeys.Look(new TapSyncPhase.Failed(TapSyncFailure.SongChanged)));
        Assert.Equal(TapSyncLook.Stopped, TapSyncKeys.Look(new TapSyncPhase.Failed(TapSyncFailure.Cancelled)));
        Assert.Equal("Tap 2 of 4", LyricToolsText.TapAnnouncement(2));
        Assert.Equal("Tap 6", LyricToolsText.TapAnnouncement(6));
        Assert.Equal(4, new[] { TapSyncLook.Tapping, TapSyncLook.Success, TapSyncLook.Warning, TapSyncLook.Stopped }
            .Select(TapSyncKeys.Glyph).Distinct().Count());
    }

    [Fact]
    public async Task TapsAgainstThePlayersClockSetThisSongsOffsetAndUndoPutsItBack()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "One", "Two", "Three", "Four", "Five")));
        var offsets = Offsets();
        var controller = Controller(new AppSettings { LyricOffset = 0.2 }, offsets);
        controller.Start();
        var tapSync = controller.TapSync;
        tapSync.Start();
        Assert.Equal(new TapSyncPhase.Failed(TapSyncFailure.NotPlaying), tapSync.Session.Phase); // nothing playing yet

        Play("Song", position: 0);
        await controller.WhenIdleAsync();
        tapSync.Start();
        Assert.True(tapSync.IsListening);
        Assert.Equal([10.0, 14, 18, 22, 26], tapSync.Session.LineStarts);
        // Each tap half a second late (after the global offset and the reaction time): the lyrics move 0.5 s later.
        var position = 0.0;
        foreach (var start in new[] { 10.0, 14, 18, 22 })
        {
            var at = start - 0.2 + LyricTapSync.ReactionTime + 0.5;
            _time.AdvanceSeconds(at - position);
            position = at;
            tapSync.Tap();
        }

        Assert.IsType<TapSyncPhase.Applied>(tapSync.Session.Phase);
        var key = controller.CurrentQuery!.RecordingKey;
        Assert.Equal(-0.5, offsets.OffsetFor(key, "LRCLIB"), 2);
        Assert.Equal(-0.5, new SongOffsets(_saved).OffsetFor(key, "LRCLIB"), 2); // saved with the settings
        Assert.Equal(-0.5, controller.SongOffset!.Value, 2);
        Assert.True(tapSync.CanUndo);
        tapSync.Undo();
        Assert.Equal(0, offsets.OffsetFor(key, "LRCLIB"));
        Assert.False(tapSync.CanUndo);
    }

    // ---- Searching ----

    [Fact]
    public async Task ASearchAsksEverySourceWithTheWordsTypedForThePlayingRecording()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("NetEase", "Automatic")));
        var controller = Controller();
        controller.Start();
        Play("Song", "Artist");
        await controller.WhenIdleAsync();
        var key = controller.CurrentQuery!.RecordingKey;

        var best = Candidate(LyricSourceId.Lrclib, "LRCLIB", "Song (Original)", "Artist");
        var other = Candidate(LyricSourceId.Kugou, "Kugou", "Song (Original)", "Artist");
        var miss = new LyricRejection(Candidate(LyricSourceId.QQMusic, "QQ Music", "Song (Original)", "Artist", 320), "Rejected: duration 5:20 vs 3:20");
        _lyrics.Searched = new LyricLookupReport
        {
            Result = best.Result, Candidates = [best, other], Rejected = [miss], Revision = 1,
            States = new Dictionary<LyricSourceId, string> { [LyricSourceId.Lrclib] = "Match found · 1 other source agrees · showing" },
        };
        Assert.True(controller.SearchLyrics("  Song (Original) ", ""));
        await controller.WhenIdleAsync();

        var asked = Assert.Single(_lyrics.Searches);
        Assert.Equal("Song (Original)", asked.Title); // the typed title, trimmed
        Assert.Equal("Artist", asked.Artist); // a blank field keeps the song's own
        Assert.Equal(200, asked.Duration);
        Assert.Equal(key, controller.CurrentQuery!.RecordingKey); // still the playing recording
        var tools = controller.LyricTools;
        Assert.False(tools.IsSearching);
        Assert.Equal([best, other], tools.Candidates);
        Assert.Equal(miss, Assert.Single(tools.Rejected));
        Assert.Equal("Match found · 1 other source agrees · showing", LyricToolsText.SourceState(best, tools.States));
        Assert.Equal("LRCLIB", controller.Scene.Lyrics!.Provenance.Source); // the best match shows, as after any lookup
        Assert.Empty(_lyrics.Pins); // a search alone saves nothing
    }

    [Fact]
    public async Task UsingAMatchShowsItAtOnceAndSavesItForTheRecordingNotTheWordsTyped()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("NetEase", "Automatic")));
        var controller = Controller();
        controller.Start();
        Assert.False(controller.UseLyrics(Candidate(LyricSourceId.Lrclib, "LRCLIB"))); // nothing playing
        Play("Song", "Artist");
        await controller.WhenIdleAsync();

        var chosen = Candidate(LyricSourceId.Kugou, "Kugou", "Another title");
        Assert.True(controller.UseLyrics(chosen));
        Assert.Equal("Kugou", controller.Scene.Lyrics!.Provenance.Source);
        Assert.Equal(StatusText.Showing, controller.Status.Status);
        var (query, result) = Assert.Single(_lyrics.Pins);
        Assert.Equal(controller.CurrentQuery!.RecordingKey, query.RecordingKey);
        Assert.Same(chosen.Result, result);
        Assert.True(controller.LyricTools.Pinned);
        Assert.Equal(LyricToolsText.Chosen(true), controller.LyricTools.Note);

        // Back to automatic: the choice is forgotten and every source is asked again.
        _lyrics.Searched = TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Again"));
        Assert.True(controller.ReturnToAutomatic());
        await controller.WhenIdleAsync();
        Assert.Equal(controller.CurrentQuery!.RecordingKey, Assert.Single(_lyrics.Unpins).RecordingKey);
        Assert.Equal("Song", Assert.Single(_lyrics.Searches).Title);
        Assert.False(controller.LyricTools.Pinned);
        Assert.Equal("LRCLIB", controller.Scene.Lyrics!.Provenance.Source);
    }

    [Fact]
    public async Task AChoiceThatCantBeSavedStillShowsAndSaysSo()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("NetEase", "Automatic")));
        _lyrics.PinsWork = false;
        var controller = Controller();
        controller.Start();
        Play("Song");
        await controller.WhenIdleAsync();
        controller.UseLyrics(Candidate(LyricSourceId.Lrclib, "LRCLIB"));
        Assert.Equal("LRCLIB", controller.Scene.Lyrics!.Provenance.Source);
        Assert.False(controller.LyricTools.Pinned);
        Assert.Equal(LyricToolsText.Chosen(false), controller.LyricTools.Note);
    }

    [Fact]
    public async Task ANewSongForgetsTheLastSongsMatches()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("NetEase", "Automatic")));
        _lyrics.Answer("Next", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "Next")));
        var controller = Controller();
        controller.Start();
        Play("Song");
        await controller.WhenIdleAsync();
        _lyrics.Searched = new LyricLookupReport { Candidates = [Candidate(LyricSourceId.Lrclib, "LRCLIB")], Revision = 0 };
        controller.SearchLyrics();
        await controller.WhenIdleAsync();
        Assert.Single(controller.LyricTools.Candidates);

        _sessions[0].SetTrack("Next");
        await controller.WhenIdleAsync();
        Assert.Empty(controller.LyricTools.Candidates);
        Assert.Null(controller.LyricTools.Note);
    }

    [Fact]
    public async Task SearchingIsOffWhenLookupsAreOffForTheRun()
    {
        _lyrics.DisabledReason = "--no-network";
        var controller = Controller();
        controller.Start();
        Play("Song");
        await controller.WhenIdleAsync();
        Assert.Equal("--no-network", controller.SearchDisabledReason);
        Assert.False(controller.SearchLyrics("Song", "Artist"));
        Assert.Empty(_lyrics.Searches);
    }

    // ---- Importing ----

    [Fact]
    public async Task AnLrcFileIsUsedForThePlayingSongAndSaved()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("NetEase", "Automatic")));
        var controller = Controller();
        controller.Start();
        var path = Path.Combine(_folder.Path, "song.lrc");
        File.WriteAllText(path, "[00:01.00]First imported line\n[00:05.50]Second imported line\n", Encoding.UTF8);
        Assert.False(controller.ImportLyricsFile(path)); // nothing playing
        Play("Song");
        await controller.WhenIdleAsync();

        Assert.True(controller.ImportLyricsFile(path));
        var shown = Assert.IsType<LyricResult.Synced>(controller.Scene.Lyrics);
        Assert.Equal(LyricToolsText.ImportedSource, shown.Provenance.Source);
        Assert.Contains(shown.Lines, line => line.Text == "Second imported line");
        Assert.Equal(controller.CurrentQuery!.RecordingKey, Assert.Single(_lyrics.Pins).Query.RecordingKey);
        Assert.Equal(LyricToolsText.Imported(true), controller.LyricTools.Note);
        Assert.Null(controller.LyricTools.Warning);
    }

    [Fact]
    public async Task TtmlTextIsReadAsTtml()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("NetEase", "Automatic")));
        var controller = Controller();
        controller.Start();
        Play("Song");
        await controller.WhenIdleAsync();
        const string ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml"><body><div>
            <p begin="00:01.000" end="00:04.000">Hello from TTML</p>
            <p begin="00:05.000" end="00:08.000">Second line</p>
            </div></body></tt>
            """;
        Assert.True(controller.ImportLyrics("  " + ttml));
        var shown = Assert.IsType<LyricResult.Synced>(controller.Scene.Lyrics);
        Assert.Equal("Hello from TTML", shown.Lines.First(line => !line.IsInstrumental).Text);
    }

    [Fact]
    public async Task AFileThatCantBeUsedSaysWhyAndChangesNothing()
    {
        _lyrics.Answer("Song", TestLyrics.Found(TestLyrics.Synced("NetEase", "Automatic")));
        var controller = Controller();
        controller.Start();
        Play("Song");
        await controller.WhenIdleAsync();

        var words = Path.Combine(_folder.Path, "words.txt");
        File.WriteAllText(words, "Just words, no timing");
        Assert.False(controller.ImportLyricsFile(words));
        Assert.Equal(Core.Lyrics.Parsing.LyricDecodeResult.Message(Core.Lyrics.Parsing.LyricDecodeFailure.NoTimestamps), controller.LyricTools.Warning);

        var large = Path.Combine(_folder.Path, "large.lrc");
        File.WriteAllText(large, "[00:01.00]" + new string('a', LyricToolsText.ImportLimit + 10));
        Assert.False(controller.ImportLyricsFile(large));
        Assert.Equal(Core.Lyrics.Parsing.LyricDecodeResult.Message(Core.Lyrics.Parsing.LyricDecodeFailure.TooLarge), controller.LyricTools.Warning);

        Assert.False(controller.ImportLyricsFile(Path.Combine(_folder.Path, "missing.lrc")));
        Assert.Equal(Core.Lyrics.Parsing.LyricDecodeResult.Message(Core.Lyrics.Parsing.LyricDecodeFailure.Unopenable), controller.LyricTools.Warning);

        Assert.Equal("NetEase", controller.Scene.Lyrics!.Provenance.Source);
        Assert.Empty(_lyrics.Pins);
    }

    // ---- When none are found ----

    [Fact]
    public async Task ASongWithoutLyricsIsShownAsWhenNoneAreFoundSays()
    {
        var controller = Controller(new AppSettings { NoLyrics = "trackName" });
        controller.Start();
        Play("Instrumental");
        await controller.WhenIdleAsync();
        var scene = controller.Scene;
        Assert.Equal(StatusText.NoLyrics, scene.Status);
        Assert.True(scene.NoneFound);
        Assert.Equal(NoLyricsDisplay.TrackName, scene.Style.NoLyrics);

        controller.ApplySettings(new AppSettings { NoLyrics = "nothing" });
        Assert.Equal(NoLyricsDisplay.Nothing, controller.Scene.Style.NoLyrics);
        Assert.True(controller.Scene.NoneFound);
    }

    [Fact]
    public void OnlyVerdictsCountAsNoneFound()
    {
        foreach (var status in new[] { StatusText.NoLyrics, StatusText.Offline, StatusText.NoPosition, StatusText.Stream,
                     StatusText.LookupFailed, StatusText.LookupsOff })
            Assert.True(StatusText.IsNoneFound(status), status);
        foreach (var status in new[] { StatusText.LookingUp, StatusText.WaitingForTrack, StatusText.NothingPlaying, StatusText.Showing, null })
            Assert.False(StatusText.IsNoneFound(status), status);
    }

    [Fact]
    public void TheSettingKeepsTheMacsKeyAndValues()
    {
        Assert.Equal("notice", new AppSettings().NoLyrics);
        Assert.Equal(["notice", "trackName", "nothing"], LyricToolsText.NoLyricsChoices.Select(c => c.Value));
        Assert.Equal(["Show a short note", "Show title and artist", "Show nothing"], LyricToolsText.NoLyricsChoices.Select(c => c.Title));
        Assert.Equal("notice", (new AppSettings { NoLyrics = "sparkles" }).Normalized().NoLyrics);
        Assert.Equal(NoLyricsDisplay.Nothing, new AppSettings { NoLyrics = "nothing" }.Style().NoLyrics);
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { NoLyrics = "trackName" }, AppSettingsJsonContext.Default.AppSettings);
        Assert.Contains("\"noLyrics\": \"trackName\"", json);
        Assert.DoesNotContain("noLyricsShown", json, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The demo's canned matches ----

    [Fact]
    public async Task TheDemoOffersRankedMatchesAndRemembersAChoiceForTheRun()
    {
        var demo = new DemoLyricLookup();
        var song = DemoSongs.Songs[0];
        var query = new LyricQuery(song.Title, song.Artist, song.Album, song.Duration);
        var options = new AppSettings().SourceSnapshot();
        var found = await demo.SearchAsync(query, options, null, CancellationToken.None);
        Assert.Equal([LyricSourceId.Lrclib, LyricSourceId.NetEase], found.Candidates.Select(c => c.Source));
        Assert.StartsWith("Rejected: duration 3:58", Assert.Single(found.Rejected).Reason);
        Assert.EndsWith("· showing", found.States[LyricSourceId.Lrclib]);
        Assert.Equal("LRCLIB", found.Result!.Provenance.Source);
        Assert.Empty((await demo.SearchAsync(new LyricQuery("Not a demo song", "", null, 30), options, null, CancellationToken.None)).Candidates);

        Assert.True(demo.Pin(query, found.Candidates[1].Result));
        var pinned = await demo.LookupAsync(query, options, null, CancellationToken.None);
        Assert.True(pinned.Pinned);
        Assert.Equal("NetEase", pinned.Result!.Provenance.Source);
        demo.Unpin(query);
        Assert.False((await demo.LookupAsync(query, options, null, CancellationToken.None)).Pinned);
    }

    /// <summary>A lookup that answers by title, records searches and pins, and keeps pins only in memory.</summary>
    private sealed class ToolsLookup : ILyricLookup
    {
        private readonly FakeLyricLookup _inner = new();

        public string? DisabledReason { get; set; }

        public WordDistributor? WordDistributor => null;

        public LyricLookupReport Searched { get; set; } = new();

        public bool PinsWork { get; set; } = true;

        public List<LyricQuery> Searches { get; } = [];

        public List<(LyricQuery Query, LyricResult Result)> Pins { get; } = [];

        public List<LyricQuery> Unpins { get; } = [];

        public void Answer(string title, LyricLookupReport report) => _inner.Answer(title, report);

        public Task<LyricLookupReport> LookupAsync(LyricQuery query, LyricSourceSnapshot options,
            Func<LyricLookupReport, ValueTask>? onUpdate, CancellationToken cancellationToken) =>
            DisabledReason is null ? _inner.LookupAsync(query, options, onUpdate, cancellationToken) : Task.FromResult(new LyricLookupReport());

        public Task<LyricLookupReport> SearchAsync(LyricQuery query, LyricSourceSnapshot options,
            Func<LyricLookupReport, ValueTask>? onUpdate, CancellationToken cancellationToken)
        {
            lock (Searches) Searches.Add(query);
            return Task.FromResult(Searched);
        }

        public bool Pin(LyricQuery query, LyricResult result)
        {
            if (!PinsWork) return false;
            Pins.Add((query, result));
            return true;
        }

        public void Unpin(LyricQuery query) => Unpins.Add(query);
    }
}
