using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Platform.Readings;
using Lunalyr.Shell.Demo;
using Lunalyr.Shell.Lyrics;

namespace Lunalyr.Shell.Tests;

/// <summary>The demo's songs through the app's own presentation steps: credits for the credits layer, phrase readings.</summary>
public class DemoExtrasTests
{
    private static readonly Lazy<ReadingServices> Windows = new(() =>
        WindowsReadingServices.Create(Path.Combine(Path.GetTempPath(), "lunalyr-tests", "dictionaries-shell")));

    private static LyricResult.Synced Presented(DemoSong song) => Assert.IsType<LyricResult.Synced>(
        LyricPresentation.Prepare(song.Lyrics, new LyricQuery(song.Title, song.Artist, song.Album, song.Duration), repairingGaps: true));

    [Fact]
    public void TheEnglishSongShowsItsTwoCredits()
    {
        var song = DemoSongs.Songs[0];
        var lines = Presented(song).Lines;
        Assert.Equal(2, lines.Count(line => line.Credit == true));
        var cues = CreditCues.Cues(lines, song.Title, song.Artist);
        Assert.Equal(2, cues.Count);
        Assert.StartsWith("Produced by", cues[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("Written by", cues[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePhraseSpacedJapaneseLinesReadPhraseByPhrase()
    {
        var song = DemoSongs.Songs[1];
        var lines = Presented(song).Lines;
        var romanizer = new LyricRomanizer(Windows.Value);
        var readings = romanizer.Readings(lines, RomanizationOptions.Default, TestContext.Current.CancellationToken);
        var spaced = Enumerable.Range(0, lines.Count).Where(row => lines[row].Text.Trim().Contains(' ', StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(spaced);
        foreach (var row in spaced)
        {
            var set = LineReadings.For(lines[row], readings.GetValueOrDefault(row), RomanizationDisplay.Below);
            var runs = set.RunReadings(set.Words, set.Glue);
            Assert.NotNull(runs);
            Assert.True(runs!.Count(reading => reading is not null) >= 2, $"{lines[row].Text}: {string.Join(" | ", runs)}");
        }
    }
}
