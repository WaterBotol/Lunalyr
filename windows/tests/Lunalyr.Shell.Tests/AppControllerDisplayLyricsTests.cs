using Lunalyr.Core.Floating;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Shell.Extras;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// What each display shows once the displays are known (the Mac's 0da0eef7, FloatingLyricsTests): a floating window on
/// each Floating or Both display, lyrics on each Desktop or Both wallpaper, each surface in its own style.
/// </summary>
public sealed class AppControllerDisplayLyricsTests : IAsyncDisposable
{
    private const string Main = "main-display", Side = "side-display";
    private static readonly ConnectedDisplay[] Two = [new(Main, @"\\.\DISPLAY1"), new(Side, @"\\.\DISPLAY2")];

    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeLyricLookup _lyrics = new();
    private readonly FakeRenderer _renderer = new();
    private readonly List<FakeMediaSession> _sessions = [];
    private AppController? _controller;

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
        foreach (var session in _sessions) Assert.Empty(session.Commands);
    }

    private AppController Controller(AppSettings settings)
    {
        var options = new AppControllerOptions
        {
            TimeProvider = _time,
            TimelineWait = TimeSpan.FromMilliseconds(60),
            TimelinePoll = TimeSpan.FromMilliseconds(10),
            LateTimelinePoll = TimeSpan.FromMilliseconds(20),
            ArtworkRetries = [],
            ArtworkRechecks = [],
        };
        _controller = new AppController(_source, _lyrics, _renderer, settings, new SongOffsets(), ReadingServices.Basic,
            new PlaybackGuard(), Logger.Null, null, options);
        return _controller;
    }

    private async Task<AppController> PlayingWithLyrics(AppSettings settings)
    {
        _lyrics.Answer("Human Nature", TestLyrics.Found(TestLyrics.Synced("LRCLIB", "First line", "Second line")));
        var session = new FakeMediaSession("Spotify.exe", _time);
        _sessions.Add(session);
        session.SetTrack("Human Nature", "Yot Club", "Rare Hymns").SetPlayback(PlaybackStatus.Playing).SetTimeline(1, 200);
        _source.Add(session);
        var controller = Controller(settings);
        controller.SetDisplays(Two);
        controller.Start();
        await controller.WhenIdleAsync();
        return controller;
    }

    private static AppSettings Showing(DisplayLyrics main, DisplayLyrics side) =>
        new AppSettings().WithDisplayChoice(Main, main).WithDisplayChoice(Side, side);

    [Fact]
    public async Task BothFloatsAWindowAndKeepsTheWallpaperLyrics()
    {
        await PlayingWithLyrics(Showing(DisplayLyrics.Both, DisplayLyrics.None));
        var window = Assert.Single(_renderer.FloatingWindows!);
        Assert.Equal(Main, window.Display);
        Assert.True(window.Enabled);
        Assert.True(window.Shown);
        // The main display keeps its desktop lyrics; the other shows its wallpaper alone.
        Assert.Equal([@"\\.\DISPLAY2"], _renderer.DesktopLyricsOff!);
    }

    [Fact]
    public async Task EachFloatingDisplayGetsItsOwnWindowAndItsWallpaperDrawsNone()
    {
        await PlayingWithLyrics(Showing(DisplayLyrics.Floating, DisplayLyrics.Floating));
        Assert.Equal([Main, Side], _renderer.FloatingWindows!.Select(w => w.Display));
        Assert.Equal([@"\\.\DISPLAY1", @"\\.\DISPLAY2"], _renderer.DesktopLyricsOff!);
    }

    [Fact]
    public async Task DesktopEverywhereFloatsNothing()
    {
        await PlayingWithLyrics(Showing(DisplayLyrics.Desktop, DisplayLyrics.Desktop));
        Assert.Empty(_renderer.FloatingWindows!);
        Assert.Empty(_renderer.DesktopLyricsOff!);
    }

    [Fact]
    public async Task AFloatingWindowDrawsItsDisplaysFloatingStyle()
    {
        // The Desktop style is edited; the main display's floating window draws its own kept Floating style.
        var spoken = MonitorLooks.Top(new AppSettings { LyricComposition = "spoken" }, MonitorLookKeys.Style);
        var settings = MonitorLooks.Store(Showing(DisplayLyrics.Both, DisplayLyrics.Desktop), spoken, new LookSlot(Main, LyricsSurface.Floating));
        await PlayingWithLyrics(settings);
        var looks = _renderer.Looks!.Value;
        Assert.Equal("spoken", looks.Floating[Main].LyricId);
        Assert.Equal("spoken", Assert.Single(_renderer.FloatingWindows!).Layout);
        // The edited slot draws the scene's own style; the side display's desktop draws its parked copy.
        Assert.False(looks.Desktop.ContainsKey(@"\\.\DISPLAY1"));
        Assert.True(looks.Desktop.ContainsKey(@"\\.\DISPLAY2"));
    }

    [Fact]
    public async Task EditingTheFloatingStyleDrawsItOnTheEditedDisplaysWindow()
    {
        // The main display is the one edited (as the host's DisplaysChanged sets it before any switch).
        var settings = (Showing(DisplayLyrics.Both, DisplayLyrics.Desktop) with { LookMonitor = Main }).WithLyricsSurface(LyricsSurface.Floating);
        await PlayingWithLyrics(settings);
        var looks = _renderer.Looks!.Value;
        Assert.False(looks.Floating.ContainsKey(Main));
        // Its wallpaper draws its Desktop style, parked.
        Assert.True(looks.Desktop.ContainsKey(@"\\.\DISPLAY1"));
    }

    [Fact]
    public async Task ASongsRememberedStyleReachesEverySurface()
    {
        // The Mac applies style memory when drawing, to every surface: the parked Floating style takes the song's colour too.
        var controller = await PlayingWithLyrics(Showing(DisplayLyrics.Both, DisplayLyrics.Desktop));
        await controller.WhenIdleAsync();
        var song = controller.CurrentQuery!;
        controller.ApplySettings(StyleMemory.Remember(controller.Settings, new RememberedStyle("cascade", "musicReactive"), song, StyleScope.Artist));
        var looks = _renderer.Looks!.Value;
        Assert.Equal(Core.Colour.LyricColourMode.MusicReactive, looks.Floating[Main].Style!.ColourMode);
        Assert.Equal(Core.Colour.LyricColourMode.MusicReactive, looks.Desktop[@"\\.\DISPLAY2"].Style!.ColourMode);
    }

    [Fact]
    public async Task EditPositionWorksWhileAnyDisplayFloats()
    {
        var controller = await PlayingWithLyrics(Showing(DisplayLyrics.Desktop, DisplayLyrics.Floating));
        controller.SetFloatingEditing(true);
        Assert.True(controller.FloatingEditing);
        Assert.True(Assert.Single(_renderer.FloatingWindows!).Editing);
        // Floating turned off everywhere ends edit position.
        controller.ApplySettings(controller.Settings.WithDisplayChoice(Side, DisplayLyrics.Desktop));
        Assert.False(controller.FloatingEditing);
        Assert.Empty(_renderer.FloatingWindows!);
    }

    [Fact]
    public async Task PauseWallpaperLeavesAFloatingDisplayRunning()
    {
        await PlayingWithLyrics(Showing(DisplayLyrics.Desktop, DisplayLyrics.Floating) with { WallpaperPaused = true });
        Assert.True(_renderer.IsRunning);
    }

    [Fact]
    public async Task PauseWallpaperStopsItWhenNothingFloats()
    {
        // The surface still says Floating (the style being edited), but no display floats.
        await PlayingWithLyrics(Showing(DisplayLyrics.Desktop, DisplayLyrics.None).WithLyricsSurface(LyricsSurface.Floating) with { WallpaperPaused = true });
        Assert.False(_renderer.IsRunning);
    }

    [Fact]
    public void TheLinkTogglesTheMainDisplayOnAndEveryFloatingDisplayOff()
    {
        var settings = Showing(DisplayLyrics.Desktop, DisplayLyrics.None);
        var extras = new LunalyrExtras(() => settings, change =>
        {
            settings = change(settings);
            return true;
        }, new ExtrasHooks { Displays = () => [Main, Side] });
        extras.ToggleFloating();
        Assert.Equal(DisplayLyrics.Floating, settings.DisplayChoice(Main));
        Assert.Equal(DisplayLyrics.None, settings.DisplayChoice(Side));
        extras.ToggleFloating();
        Assert.Equal(DisplayLyrics.Desktop, settings.DisplayChoice(Main));
        // None stays None after toggling off.
        Assert.Equal(DisplayLyrics.None, settings.DisplayChoice(Side));
    }
}
