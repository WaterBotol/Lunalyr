using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// "Match the music": the layout rotation (ported from the Mac's MatchTheMusicTests) and Windows' own track character,
/// which stands in for the Mac's TrackMood. The Mac's moods come from a genre, a pace and a cover saturation; Windows has
/// no genre, so the same songs are described by pace and cover alone.
/// </summary>
public class MatchTheMusicTests
{
    private static readonly IReadOnlyCollection<string> Every = [.. LayoutRotation.Order];

    /// <summary>The Mac test's songs: (genre, words a second), cover saturation 0.5, seed i × 7919.</summary>
    private static TrackCharacter[] Songs() =>
        [.. new[] { 2.0, 1.0, 3.0, 1.1, 3.2, 2.0, 0, 0.9 }.Select((pace, i) =>
            TrackCharacter.From(pace > 0 ? pace : null, 0.5, null, null, i * 7919))];

    // Ported from MatchTheMusicTests.swift: testEachNewSongTakesADifferentLayoutThanTheLast
    [Fact]
    public void EachNewSongTakesADifferentLayoutThanTheLast()
    {
        var rotation = new LayoutRotation();
        string? previous = null;
        foreach (var (song, index) in Songs().Select((song, index) => (song, index)))
        {
            var layout = rotation.Layout($"Artist|Song {index}", song, Every);
            Assert.NotEqual(previous, layout);
            Assert.True(LayoutRotation.Suits(layout, song.Energy));
            previous = layout;
        }
    }

    [Fact]
    public void WithOnlyTheLayoutsWindowsHasSongsTakeThemInTurn()
    {
        var rotation = new LayoutRotation();
        var picks = Enumerable.Range(0, 6).Select(index => rotation.Layout($"A|{index}", TrackCharacter.Neutral, ["editorial", "spoken", "gradient"])).ToList();
        Assert.Equal(["spoken", "editorial", "spoken", "editorial", "spoken", "editorial"], picks);
    }

    // Ported from MatchTheMusicTests.swift: testReplayingASongKeepsItsLayout
    [Fact]
    public void ReplayingASongKeepsItsLayout()
    {
        var rotation = new LayoutRotation();
        var first = rotation.Layout("A|One", TrackCharacter.Neutral, Every);
        rotation.Layout("B|Two", TrackCharacter.Neutral, Every);
        Assert.Equal(first, rotation.Layout("A|One", TrackCharacter.Neutral, Every));
    }

    // Ported from MatchTheMusicTests.swift: testRotationVisitsEveryLayoutOverEnoughSongs
    [Fact]
    public void RotationVisitsEveryLayoutOverEnoughSongs()
    {
        var rotation = new LayoutRotation();
        var seen = Enumerable.Range(0, 24).Select(index => rotation.Layout($"A|{index}", TrackCharacter.Neutral, Every)).ToHashSet();
        Assert.Equal(LayoutRotation.Order.ToHashSet(), seen);
    }

    // Ported from MatchTheMusicTests.swift: testSlowSongsSkipTheBusyLayouts (the Mac's "ambient" genre is not known here;
    // the pace alone makes the song slow)
    [Fact]
    public void SlowSongsSkipTheBusyLayouts()
    {
        var slow = TrackCharacter.From(0.8, 0.5, null, null, 1);
        Assert.True(slow.Energy < 0.3, $"{slow.Energy}");
        var rotation = new LayoutRotation();
        for (var index = 0; index < 16; index++)
            Assert.DoesNotContain(rotation.Layout($"S|{index}", slow, Every), new[] { "cascade", "constellation", "constellationEditorial" });
    }

    // Ported from MatchTheMusicTests.swift: testSongsWithoutAGenreAreToldApartByCoverAndCadence
    [Fact]
    public void SongsAreToldApartByCoverAndCadence()
    {
        var vivid = TrackCharacter.From(3.0, 0.95, null, null, 1);
        var washed = TrackCharacter.From(1.0, 0.1, null, null, 1);
        Assert.True(vivid.Energy > washed.Energy + 0.3, $"{vivid.Energy} vs {washed.Energy}");
        Assert.InRange(vivid.Energy, 0, 1);
        Assert.InRange(washed.Energy, 0, 1);
    }

    // Ported from MatchTheMusicTests.swift: testALayoutSetToZeroNeverAppears
    [Fact]
    public void ALayoutSetToZeroNeverAppears()
    {
        var rotation = new LayoutRotation();
        rotation.SetCount(0, "cutout");
        rotation.SetCount(0, "echo");
        var seen = Enumerable.Range(0, 40).Select(index => rotation.Layout($"A|{index}", TrackCharacter.Neutral, Every)).ToHashSet();
        Assert.DoesNotContain("cutout", seen);
        Assert.DoesNotContain("echo", seen);
        Assert.Contains("editorial", seen);
    }

    // Ported from MatchTheMusicTests.swift: testACountOfThreeGivesThatLayoutThreeSongsInARow
    [Fact]
    public void ACountOfThreeGivesThatLayoutThreeSongsInARow()
    {
        var rotation = new LayoutRotation();
        foreach (var layout in LayoutRotation.Order) rotation.SetCount(layout == "editorial" ? 3 : 0, layout);
        rotation.SetCount(1, "spoken");
        var picks = Enumerable.Range(0, 8).Select(index => rotation.Layout($"S|{index}", TrackCharacter.Neutral, Every)).ToList();
        Assert.Equal(3, picks.Take(4).Count(pick => pick == "editorial"));
        Assert.Equal(new HashSet<string> { "editorial", "spoken" }, picks.ToHashSet());
        Assert.Equal(6, picks.Count(pick => pick == "editorial"));
    }

    // Ported from MatchTheMusicTests.swift: testOnlyOneLayoutChosenMeansEverySongGetsIt
    [Fact]
    public void OnlyOneLayoutChosenMeansEverySongGetsIt()
    {
        var rotation = new LayoutRotation();
        foreach (var layout in LayoutRotation.Order) rotation.SetCount(layout == "cutout" ? 1 : 0, layout);
        for (var index = 0; index < 6; index++) Assert.Equal("cutout", rotation.Layout($"O|{index}", TrackCharacter.Neutral, Every));
    }

    // Ported from MatchTheMusicTests.swift: testSwitchingEveryLayoutOffFallsBackToTheEnergyMatch
    [Fact]
    public void SwitchingEveryLayoutOffFallsBackToTheCharacterMatch()
    {
        var rotation = new LayoutRotation();
        foreach (var layout in LayoutRotation.Order) rotation.SetCount(0, layout);
        Assert.Equal(LayoutMatch.For(TrackCharacter.Neutral, Every), rotation.Layout("X|1", TrackCharacter.Neutral, Every));
    }

    // Ported from MatchTheMusicTests.swift: testTheChoicesSurviveARelaunch (the counts and the cursor are settings on
    // Windows; a new rotation built from them carries on)
    [Fact]
    public void TheChoicesSurviveARelaunch()
    {
        var first = new LayoutRotation();
        var cursors = new List<int>();
        first.CursorChanged += cursors.Add;
        first.SetCount(4, "echo");
        first.SetCount(0, "cascade");
        first.Layout("A|1", TrackCharacter.Neutral, Every);
        var second = new LayoutRotation(cursors[^1], new Dictionary<string, int> { ["echo"] = 4, ["cascade"] = 0 });
        Assert.Equal(4, second.Count("echo"));
        Assert.Equal(0, second.Count("cascade"));
        Assert.Equal(1, second.Count("cutout"));
        Assert.Equal(first.Cursor, second.Cursor);
        Assert.NotEqual(first.Layout("A|1", TrackCharacter.Neutral, Every), second.Layout("B|2", TrackCharacter.Neutral, Every));
    }

    [Fact]
    public void TheMatchByCharacterKeepsToTheLayoutsThere()
    {
        var dense = new TrackCharacter(0.9, 0.2, 2);
        var sparse = new TrackCharacter(0.2, 0.9, 2);
        Assert.Equal("constellation", LayoutMatch.For(dense, Every));
        Assert.Equal("echo", LayoutMatch.For(sparse, Every));
        Assert.Equal("editorial", LayoutMatch.For(new TrackCharacter(0.5, 0.5, 2), Every));
        Assert.Equal("scattered", LayoutMatch.For(new TrackCharacter(0.5, 0.5, 3), Every));
        IReadOnlyCollection<string> windows = ["editorial", "spoken"];
        Assert.Equal("spoken", LayoutMatch.For(dense, windows));
        Assert.Equal("editorial", LayoutMatch.For(sparse, windows));
        Assert.Equal("editorial", LayoutMatch.For(new TrackCharacter(0.5, 0.5, 3), windows));
    }

    [Fact]
    public void ASongsCharacterComesFromItsLyricAndCover()
    {
        static LyricLine Line(double start, double step, params string[] words) => new()
        {
            Start = start, End = start + step * words.Length, Text = string.Join(" ", words), WordTimed = true,
            Words = [.. words.Select((word, i) => new LyricWord(word, start + i * step, start + i * step + step * 0.9))],
        };
        // Fast, machine-even words on a vivid cover; slow, uneven ones on a grey cover.
        var fast = Enumerable.Range(0, 6).Select(i => Line(10 + i * 3, 0.25, "go", "go", "go", "go", "go", "go", "go", "go")).ToList();
        LyricLine[] slow =
        [
            new() { Start = 10, End = 18, Text = "softly now", WordTimed = true, Words = [new("softly", 10, 12.5), new("now", 14.5, 17)] },
            new() { Start = 20, End = 28, Text = "the long road home", WordTimed = true,
                    Words = [new("the", 20, 20.4), new("long", 20.6, 23), new("road", 24.9, 25.4), new("home", 25.6, 28)] },
            new() { Start = 30, End = 38, Text = "and on and on", WordTimed = true,
                    Words = [new("and", 30, 30.3), new("on", 30.4, 32), new("and", 34, 34.2), new("on", 34.3, 37)] },
        ];
        var vivid = TrackCharacter.Of(fast, [new Vector3(1, 0.1f, 0.4f), new Vector3(0.1f, 0.4f, 1)], "Fast", "Band");
        var grey = TrackCharacter.Of(slow, [new Vector3(0.5f, 0.5f, 0.52f)], "Slow", "Band");
        Assert.True(vivid.Energy > 0.68, $"{vivid.Energy}");
        Assert.True(vivid.Organic < 0.45, $"{vivid.Organic}");
        Assert.True(grey.Energy < 0.38, $"{grey.Energy}");
        Assert.Equal(TrackCharacter.SeedOf("Fast", "Band"), vivid.Seed);
        Assert.Equal(TrackCharacter.SeedOf(" fast ", "BAND"), vivid.Seed);
        // Nothing known: neutral, but still seeded by the song.
        var unknown = TrackCharacter.Of(null, [], "Song", "Artist");
        Assert.Equal(0.5, unknown.Organic);
    }
}
