using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Artwork;
using Lunalyr.Shell.Demo;
using Lunalyr.Shell.Rendering;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary><c>--demo</c>: the silent built-in player, its songs and covers, and the controller following it.</summary>
public sealed class DemoTests
{
    [Fact]
    public async Task TheDemoPlayerIsOneSilentSessionWhoseClockRunsInRealTime()
    {
        var time = new ManualTimeProvider();
        using var source = new DemoMediaSessionSource(time);
        var session = Assert.Single(source.Sessions);
        Assert.Same(session, source.CurrentSession);
        Assert.Equal("Lunalyr demo", session.SourceApp.DisplayName);

        var track = await session.GetMediaPropertiesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("Neon Harbour", DemoSongs.Artist, DemoSongs.Album), (track!.Title, track.Artist, track.AlbumTitle));
        Assert.True(track.HasThumbnail);
        Assert.Equal(PlaybackStatus.Playing, session.GetPlaybackInfo().Status);
        Assert.Equal(MediaControls.None, session.GetPlaybackInfo().Controls);

        var start = session.GetTimeline()!.Value;
        Assert.Equal(TimeSpan.FromSeconds(DemoSongs.Songs[0].Duration), start.Duration);
        Assert.Equal(TimeSpan.Zero, start.Position);
        time.AdvanceSeconds(12.5);
        var later = session.GetTimeline()!.Value;
        Assert.Equal(12.5, later.Position.TotalSeconds, 3);
        Assert.Equal(time.UtcNow, later.LastUpdatedTime);
    }

    [Fact]
    public async Task EveryCommandIsRefused()
    {
        using var source = new DemoMediaSessionSource(new ManualTimeProvider());
        var session = source.Session;
        Assert.False(await session.TryPlayAsync());
        Assert.False(await session.TryPauseAsync());
        Assert.False(await session.TryTogglePlayPauseAsync());
        Assert.False(await session.TrySkipNextAsync());
        Assert.False(await session.TrySkipPreviousAsync());
        Assert.False(await session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(3)));
        Assert.False(await session.TryChangePlaybackRateAsync(2));
        Assert.False(await session.TryChangeShuffleActiveAsync(true));
        Assert.False(await session.TryChangeRepeatModeAsync(MediaRepeatMode.Track));
    }

    [Fact]
    public async Task TheSongsLoopOnToTheNext()
    {
        var songs = new[]
        {
            DemoSongs.Songs[0] with { Duration = 0.15 },
            DemoSongs.Songs[1] with { Duration = 0.15 },
        };
        using var source = new DemoMediaSessionSource(TimeProvider.System, songs);
        var changes = 0;
        source.Session.MediaPropertiesChanged += (_, _) => Interlocked.Increment(ref changes);
        for (var i = 0; i < 300 && Volatile.Read(ref changes) < 2; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(Volatile.Read(ref changes) >= 2, "the demo moved on to the next song and looped");
    }

    [Fact]
    public async Task EachSongHasACoverWithItsOwnColours()
    {
        var palettes = new List<Palette>();
        foreach (var song in DemoSongs.Songs)
        {
            using var stream = new MemoryStream(song.Cover);
            var palette = await ArtworkPalettes.FromStreamAsync(stream, TestContext.Current.CancellationToken);
            Assert.NotNull(palette);
            Assert.Equal(9, palette.ArtworkStops.Count);
            palettes.Add(palette);
        }

        Assert.NotEqual(palettes[0].Base, palettes[1].Base);
    }

    [Fact]
    public async Task TheDemoLyricsAreWordTimedAndHaveWhatThePreviewsNeed()
    {
        var lookup = new DemoLyricLookup();
        Assert.Null(lookup.DisabledReason);
        var options = new AppSettings().SourceSnapshot();
        var ct = TestContext.Current.CancellationToken;

        var english = Assert.IsType<LyricResult.Synced>((await lookup.LookupAsync(new LyricQuery("Neon Harbour", DemoSongs.Artist, null, 37), options, null, ct)).Result);
        Assert.All(english.Lines.Where(line => line.Credit != true), line => Assert.True(line.WordTimed, line.Text));
        Assert.Equal(2, english.Lines.Count(line => line.Credit == true && line.Words.Count == 0)); // credits for the credits layer
        Assert.Contains(english.Lines, line => line.Words.Any(word => word.End - word.Start > 3)); // a held note
        Assert.Contains(english.Lines, line => line.Words.Count >= 12); // a line long enough to wrap
        Assert.Contains(english.Lines, line => line.Text.Contains("(oh", StringComparison.Ordinal)); // backing vocals

        var japanese = Assert.IsType<LyricResult.Synced>((await lookup.LookupAsync(new LyricQuery("Evening Road", DemoSongs.Artist, null, 30), options, null, ct)).Result);
        // Unspaced, and spaced by phrase (each phrase then reads under itself).
        Assert.Contains(japanese.Lines, line => !line.Text.Trim().Contains(' ', StringComparison.Ordinal));
        Assert.Contains(japanese.Lines, line => line.Text.Trim().Contains(' ', StringComparison.Ordinal));
        Assert.True(japanese.Lines.Count >= 5);

        Assert.Null((await lookup.LookupAsync(new LyricQuery("Something Else", "Someone", null, 200), options, null, ct)).Result);
    }

    [Fact]
    public async Task TheControllerFollowsTheDemoWithLyricsAndAlbumColours()
    {
        using var source = new DemoMediaSessionSource();
        var renderer = new FakeRenderer();
        await using var controller = new AppController(source, new DemoLyricLookup(), renderer, new AppSettings(), new SongOffsets(),
            ReadingServices.Basic, new PlaybackGuard(), Logger.Null);
        controller.Start();
        for (var i = 0; i < 300 && (renderer.Last?.Lyrics is null || ReferenceEquals(renderer.Last.Palette, PaletteMapper.Fallback)); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        var scene = renderer.Last!;
        Assert.Equal("Neon Harbour", scene.Title);
        Assert.IsType<LyricResult.Synced>(scene.Lyrics);
        Assert.NotSame(PaletteMapper.Fallback, scene.Palette);
        Assert.Equal("Lunalyr demo", controller.Status.Player);
        var (seconds, playing) = controller.ReadClock();
        Assert.True(playing);
        Assert.InRange(seconds, 0, 10);
    }

    [Fact]
    public void DemoImpliesNoNetworkAndNoPlaybackAndStatsTakesAFile()
    {
        var demo = ShellOptions.Parse(["--demo"]);
        Assert.True(demo.Demo);
        Assert.True(demo.NoNetwork);
        Assert.True(demo.NoPlayback);
        Assert.Empty(demo.Problems);

        var stats = ShellOptions.Parse(["--stats", "out\\stats.json"]);
        Assert.Equal(Path.GetFullPath("out\\stats.json"), stats.StatsPath);
        Assert.Contains("--stats needs a value.", ShellOptions.Parse(["--stats"]).Problems);
    }

    [Theory]
    [InlineData("gradient", "gradient")]
    [InlineData("colourOnly", "colour")]
    [InlineData("colour", "colour")]
    [InlineData("auto", "gradient")]
    [InlineData("", "gradient")]
    [InlineData("visualizer", "visualizer")]
    public void TheBackgroundSettingPicksALayer(string setting, string layer) =>
        Assert.Equal(layer, EngineWallpaperRenderer.BackgroundLayerFor(setting));
}
