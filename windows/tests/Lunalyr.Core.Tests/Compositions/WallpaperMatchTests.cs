using Lunalyr.Core.Compositions;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// "Custom Mix" for the wallpaper (WallpaperMatch). The catalogue comes from the layers that draw effects and
/// patterns, so these tests use invented ones spread over the three axes; the songs are the Mac test's.
/// </summary>
public class WallpaperMatchTests
{
    /// <summary>The Mac test's songs (MatchTheMusicTests.moods): words a second, cover saturation 0.5, seed i × 7919.</summary>
    private static TrackCharacter[] Songs() =>
        [.. new[] { 2.0, 1.0, 3.0, 1.1, 3.2, 2.0, 0, 0.9 }.Select((pace, i) =>
            TrackCharacter.From(pace > 0 ? pace : null, 0.5, null, null, i * 7919))];

    /// <summary>Twelve invented styles, a grid over energy and organic, density following energy.</summary>
    private static WallpaperCandidate[] Catalogue(string prefix, bool heavyTop = false) =>
        [.. Enumerable.Range(0, 12).Select(i =>
        {
            double energy = (i % 4) / 3.0, organic = (i / 4) / 2.0;
            return new WallpaperCandidate($"{prefix}{i:00}", new WallpaperCharacter(energy, energy * 0.8 + 0.1, organic), heavyTop && energy > 0.9);
        })];

    // Ported from MatchTheMusicTests.swift: testMatchedChoicesVaryAcrossSongs (its effect seeds are index × 31; its
    // patterns follow the song's mood, here its character and seed).
    [Fact]
    public void MatchedChoicesVaryAcrossSongs()
    {
        var effects = Catalogue("effect");
        var patterns = Catalogue("pattern");
        var shaders = new HashSet<string>();
        var lines = new HashSet<string>();
        foreach (var (song, index) in Songs().Select((s, i) => (s, i)))
        {
            var target = WallpaperCharacter.Of(song);
            shaders.Add(WallpaperMatch.Style(effects, null, target, index * 31, savingPower: false)!);
            lines.Add(WallpaperMatch.Pick("pattern", song, null, null, effects, patterns).StyleId!);
        }
        Assert.True(shaders.Count > 2, $"{shaders.Count} effects");
        Assert.True(lines.Count > 2, $"{lines.Count} patterns");
    }

    [Fact]
    public void AnEnergeticSongGetsAnEffectAndACalmOneAPattern()
    {
        var fast = TrackCharacter.From(3.2, 0.95, 40, null, 1);
        var slow = TrackCharacter.From(0.8, 0.2, 12, null, 1);
        Assert.True(fast.Energy > WallpaperMatch.EffectEnergy);
        Assert.True(slow.Energy <= WallpaperMatch.EffectEnergy);
        Assert.Equal(WallpaperKind.Effect, WallpaperMatch.Pick("auto", fast, null, null, Catalogue("e"), Catalogue("p")).Kind);
        Assert.Equal(WallpaperKind.Pattern, WallpaperMatch.Pick("auto", slow, null, null, Catalogue("e"), Catalogue("p")).Kind);
        // The type decides over the song: GPU effects always draws an effect, Line patterns a pattern.
        Assert.Equal(WallpaperKind.Effect, WallpaperMatch.Pick("shader", slow, null, null, Catalogue("e"), Catalogue("p")).Kind);
        Assert.Equal(WallpaperKind.Pattern, WallpaperMatch.Pick("pattern", fast, null, null, Catalogue("e"), Catalogue("p")).Kind);
    }

    [Fact]
    public void TheSameSongAlwaysGetsTheSameWallpaper()
    {
        foreach (var song in Songs())
        {
            var first = WallpaperMatch.Pick("auto", song, null, null, Catalogue("e"), Catalogue("p"));
            Assert.Equal(first, WallpaperMatch.Pick("auto", song, null, null, Catalogue("e"), Catalogue("p")));
        }
        Assert.Equal(WallpaperMatch.Noise(7919), WallpaperMatch.Noise(7919));
        Assert.InRange(WallpaperMatch.Noise(int.MinValue), 0, 1);
        Assert.NotEqual(WallpaperMatch.Noise(1), WallpaperMatch.Noise(1337));
    }

    [Fact]
    public void APinnedStyleIsUsedAndAnUnknownOneIsMatched()
    {
        var song = Songs()[0];
        Assert.Equal("e07", WallpaperMatch.Pick("shader", song, "e07", null, Catalogue("e"), Catalogue("p")).StyleId);
        Assert.Equal("p03", WallpaperMatch.Pick("pattern", song, null, "p03", Catalogue("e"), Catalogue("p")).StyleId);
        // A pinned id this build lacks (a newer settings file) falls back to the match.
        var matched = WallpaperMatch.Pick("shader", song, null, null, Catalogue("e"), Catalogue("p")).StyleId;
        Assert.Equal(matched, WallpaperMatch.Pick("shader", song, "gone", null, Catalogue("e"), Catalogue("p")).StyleId);
    }

    [Fact]
    public void SavingPowerLeavesHeavyEffectsOut()
    {
        var effects = Catalogue("e", heavyTop: true);
        var frantic = new WallpaperCharacter(1, 1, 0);
        for (var seed = 0; seed < 40; seed++)
        {
            var id = WallpaperMatch.Style(effects, null, frantic, seed, savingPower: true)!;
            Assert.False(effects.Single(e => e.Id == id).Heavy);
        }
        // Even pinned, a heavy effect waits while power is saved.
        var heavy = effects.First(e => e.Heavy).Id;
        Assert.NotEqual(heavy, WallpaperMatch.Style(effects, heavy, frantic, 1, savingPower: true));
        Assert.Equal(heavy, WallpaperMatch.Style(effects, heavy, frantic, 1, savingPower: false));
    }

    [Fact]
    public void MatchTheMusicTakesWhateverKindExistsAndNothingWhenNoneDoes()
    {
        var fast = TrackCharacter.From(3.2, 0.95, 40, null, 1);
        var slow = TrackCharacter.From(0.8, 0.2, 12, null, 1);
        Assert.Equal(WallpaperKind.Pattern, WallpaperMatch.Pick("auto", fast, null, null, [], Catalogue("p")).Kind);
        Assert.Equal(WallpaperKind.Effect, WallpaperMatch.Pick("auto", slow, null, null, Catalogue("e"), []).Kind);
        var none = WallpaperMatch.Pick("auto", fast, null, null, [], []);
        Assert.Null(none.StyleId);
        Assert.Null(none.LayerId);
        // A chosen type with an empty catalogue draws nothing of its own (the app shows the album colours).
        Assert.Null(WallpaperMatch.Pick("shader", fast, null, null, [], Catalogue("p")).LayerId);
        Assert.Equal("shader.e01", new WallpaperPick(WallpaperKind.Effect, "e01").LayerId);
        Assert.Equal("pattern.p01", new WallpaperPick(WallpaperKind.Pattern, "p01").LayerId);
    }

    [Fact]
    public void ASongIsMatchedToTheNearestStyles()
    {
        var effects = Catalogue("e");
        var still = new WallpaperCharacter(0, 0.1, 0);
        // Whatever the seed, a still, geometric song lands among the four styles nearest it.
        var nearest = effects.OrderBy(e => WallpaperMatch.Distance(e.Character, still)).Take(WallpaperMatch.Nearest).Select(e => e.Id).ToHashSet();
        for (var seed = 0; seed < 50; seed++) Assert.Contains(WallpaperMatch.Style(effects, null, still, seed, false)!, nearest);
    }
}
