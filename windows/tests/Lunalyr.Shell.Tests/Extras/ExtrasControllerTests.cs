using Lunalyr.Core.Extras;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Playback;
using Lunalyr.Shell.Demo;
using Lunalyr.Shell.Extras;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;

namespace Lunalyr.Shell.Tests.Extras;

/// <summary>What Discord status and the tray lyric read from a real controller following the demo player.</summary>
public sealed class ExtrasControllerTests
{
    [Fact]
    public async Task TheDemoSongsLinesReachDiscordAndTheTrayLyric()
    {
        using var source = new DemoMediaSessionSource();
        var renderer = new FakeRenderer();
        await using var controller = new AppController(source, new DemoLyricLookup(), renderer, new AppSettings(), new SongOffsets(),
            ReadingServices.Basic, new PlaybackGuard(), Logger.Null);
        controller.Start();
        var now = controller.ReadExtras();
        for (var i = 0; i < 300 && now.Lines is null; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            now = controller.ReadExtras();
        }

        Assert.Equal("Neon Harbour", now.Title);
        Assert.Equal(DemoSongs.Artist, now.Artist);
        Assert.True(now.Playing);
        Assert.True(double.IsFinite(now.Position) && double.IsFinite(now.LyricTime));
        Assert.True(now.Duration > 0);
        var lines = Assert.IsAssignableFrom<IReadOnlyList<LyricLine>>(now.Lines);

        // Mid-line, both read the line being sung (Discord 2.5 s ahead of the music).
        var sung = lines.First(l => !l.IsInstrumental && l.Credit != true && l.Text.Trim().Length > 0);
        var at = (sung.Start + sung.End) / 2;
        Assert.Equal(sung.Text.Trim(), DiscordPresence.Line(lines, at, now.Replacements));
        var model = new TrayTickerModel(text => text.Length * 8);
        var step = model.Refresh(now with { LyricTime = at }, TrayLyricTicker.DefaultLength, false, 0);
        Assert.StartsWith(TrayLyricTicker.Pages(sung.Text, TrayLyricTicker.MaxWidth(36, t => t.Length * 8), t => t.Length * 8)[0],
            step.Shown!.Text, StringComparison.Ordinal);

        // Credits (flagged as instrumental, as on the Mac) are drawn by their own layer, never read out as a sung line.
        Assert.Contains(lines, l => l.Credit == true);
        foreach (var line in lines.Where(l => l.Credit == true))
        {
            var middle = (line.Start + line.End) / 2;
            Assert.NotEqual(line.Text.Trim(), DiscordPresence.Line(lines, middle));
        }
    }
}
