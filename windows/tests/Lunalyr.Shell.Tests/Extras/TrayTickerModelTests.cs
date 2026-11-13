using Lunalyr.Core.Extras;
using Lunalyr.Core.Lyrics;
using Lunalyr.Shell.Extras;

namespace Lunalyr.Shell.Tests.Extras;

/// <summary>What the tray lyric strip shows and when it wakes (the Mac's MenuBarLyricTicker.refresh), with ten points a character.</summary>
public class TrayTickerModelTests
{
    private static double TenPoints(string text) => text.Length * 10;

    private static readonly IReadOnlyList<LyricLine> Lines =
    [
        new() { Start = 10, End = 12, Text = "First" },
        new() { Start = 20, End = 22, Text = "A rather longer line" },
    ];

    private static ExtrasNow Song(double time, bool playing = true, bool paused = false) => new()
    {
        Title = "Song", Artist = "Artist", Playing = playing, Position = time, LyricTime = time, Lines = Lines, WallpaperPaused = paused,
    };

    [Fact]
    public void ItShowsTheLineAtAWidthSetOncePerSongAndWakesForTheNextChange()
    {
        var model = new TrayTickerModel(TenPoints);
        var step = model.Refresh(Song(13), 36, false, 0);
        Assert.Equal(new TickerShown("First", false, false), step.Shown);
        Assert.Equal(200 + TrayLyricTicker.WidthPadding, step.Width); // the widest line, not this one
        Assert.Equal(12 + TrayLyricTicker.FinishedAfter - 13 + 0.01, step.Wait!.Value, 9); // when it gives way to ♪
        Assert.Equal(TrayLyricTicker.MaxWait, model.Refresh(Song(11), 36, false, 0).Wait); // a scrub is caught within 2 s
        Assert.True(step.Changed && step.TextChanged);

        var same = model.Refresh(Song(13.2), 36, false, 0.5);
        Assert.False(same.Changed);
        var next = model.Refresh(Song(20.5), 36, false, 9.5);
        Assert.Equal("A rather longer line", next.Shown?.Text);
        Assert.Equal(step.Width, next.Width);
    }

    [Fact]
    public void PausedMusicDimsTheLineAndAPausedWallpaperSaysSo()
    {
        var model = new TrayTickerModel(TenPoints);
        var music = model.Refresh(Song(11, playing: false), 36, false, 0);
        Assert.Equal(new TickerShown("First", false, Dimmed: true), music.Shown);
        Assert.Equal(TrayLyricTicker.PausedTickInterval, music.Wait);

        var wallpaper = model.Refresh(Song(11, paused: true), 36, false, 1);
        Assert.Equal(new TickerShown(TrayLyricTicker.PausedText, PauseSymbol: true, Dimmed: false), wallpaper.Shown);
        Assert.Null(wallpaper.Wait); // nothing can change by itself
        Assert.Equal(TenPoints(TrayLyricTicker.PausedText) + TrayTickerModel.SymbolWidth + TrayLyricTicker.WidthPadding, wallpaper.Width);
    }

    [Fact]
    public void BetweenSongsAQuietNoteHoldsTheSpotBeforeItLeaves()
    {
        var model = new TrayTickerModel(TenPoints);
        // Nothing was showing: it stays away.
        Assert.Null(model.Refresh(ExtrasNow.Nothing, 36, false, 0).Shown);

        model.Refresh(Song(11), 36, false, 1);
        var gap = model.Refresh(ExtrasNow.Nothing, 36, false, 2);
        Assert.Equal(new TickerShown("♪", false, Dimmed: true), gap.Shown);
        Assert.Equal(TrayLyricTicker.HideDelay, gap.Wait!.Value, 9);
        var back = model.Refresh(Song(20.5), 36, false, 3); // the next song came in time
        Assert.Equal("A rather longer line", back.Shown?.Text);

        model.Refresh(ExtrasNow.Nothing, 36, false, 4);
        var gone = model.Refresh(ExtrasNow.Nothing, 36, false, 4 + TrayLyricTicker.HideDelay);
        Assert.Null(gone.Shown);
        Assert.True(gone.Changed);
    }

    [Fact]
    public void WhileTheNextSongsLyricsAreFoundTheWidthStays()
    {
        var model = new TrayTickerModel(TenPoints);
        var width = model.Refresh(Song(11), 36, false, 0).Width;
        var looking = new ExtrasNow { Title = "Next", Artist = "Artist", Playing = true, Position = 1, LyricTime = 1, LyricsLoading = true };
        var step = model.Refresh(looking, 36, false, 1);
        Assert.Equal("♪", step.Shown?.Text);
        Assert.Equal(width, step.Width);
        Assert.Null(step.Wait); // no timed lines: only ♪, which time alone can't change
    }

    [Fact]
    public void ReadingsShownInPlaceOfTheLyricsAreShownHereToo()
    {
        var model = new TrayTickerModel(TenPoints);
        var step = model.Refresh(Song(11) with { Replacements = new Dictionary<int, string> { [0] = "fāsuto" } }, 36, false, 0);
        Assert.Equal("fāsuto", step.Shown?.Text);
    }

    [Fact]
    public void ALongerLineSettingRemeasuresAndSleepingDisplaysStopTheWakes()
    {
        var model = new TrayTickerModel(TenPoints);
        var narrow = model.Refresh(Song(20.5), 16, false, 0);
        Assert.Equal(110 + TrayLyricTicker.WidthPadding, narrow.Width); // 16 characters: the longer half, "longer line"
        Assert.Equal("A rather", narrow.Shown?.Text); // shown in halves
        var wide = model.Refresh(Song(20.5), 36, false, 0.1);
        Assert.Equal(200 + TrayLyricTicker.WidthPadding, wide.Width);
        Assert.Null(model.Refresh(Song(20.6), 36, screensAsleep: true, 0.2).Wait);
    }
}
