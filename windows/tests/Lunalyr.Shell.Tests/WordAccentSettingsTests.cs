using System.Text.Json;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Floating;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary>Word symbols and action words in Settings (the Mac's custom.wordSymbols.* and custom.actionWords.*).</summary>
public class WordAccentSettingsTests
{
    [Fact]
    public void Both_are_on_with_the_default_lists()
    {
        var settings = new AppSettings();
        Assert.True(settings.WordSymbolsEnabled);
        Assert.True(settings.ActionWordsEnabled);
        Assert.Null(settings.WordSymbolsWords);
        Assert.Null(settings.ActionWordsWords);
        var style = settings.Style();
        Assert.True(style.WordSymbols);
        Assert.True(style.ActionWords);
        Assert.Same(WordAccentLists.Defaults, style.WordLists);
    }

    [Fact]
    public void A_stored_list_reaches_the_renderer_cleaned()
    {
        var settings = new AppSettings
        {
            WordSymbolsWords = new Dictionary<string, string> { ["Zebra"] = "star", ["moon"] = "not.a.symbol" },
            ActionWordsWords = new Dictionary<string, string> { ["twirl"] = "spin" },
        };
        var lists = settings.Style().WordLists;
        Assert.Equal("star", lists.Symbols["zebra"]);
        Assert.False(lists.Symbols.ContainsKey("moon"));
        Assert.False(lists.Symbols.ContainsKey("love"));
        Assert.Equal(WordAction.Spin, lists.Actions["twirl"]);
        // The same stored lists give an equal style, so the renderer keeps its plans.
        Assert.Equal(settings.Style(), settings.Style());
    }

    [Fact]
    public void The_switches_belong_to_each_surface_and_the_lists_are_shared()
    {
        Assert.Contains("wordSymbolsEnabled", LyricStyleProfiles.Keys);
        Assert.Contains("actionWordsEnabled", LyricStyleProfiles.Keys);
        Assert.DoesNotContain("wordSymbolsWords", LyricStyleProfiles.Keys);
        Assert.DoesNotContain("actionWordsWords", LyricStyleProfiles.Keys);
    }

    // The Mac's custom.actionWords.strength (16 Nov 2026): subtle 0.2, middle 0.5, wild 1.0, wild by default.
    [Theory]
    [InlineData("subtle", 0.2)]
    [InlineData("middle", 0.5)]
    [InlineData("wild", 1.0)]
    public void Action_word_strength_reaches_the_renderer_as_the_Macs_amount(string stored, double amount)
    {
        var settings = new AppSettings { ActionWordsStrength = stored }.Normalized();
        Assert.Equal(stored, settings.ActionWordsStrength);
        Assert.Equal(amount, settings.Style().ActionStrength);
    }

    [Fact]
    public void Action_word_strength_is_wild_by_default_and_an_unknown_value_reads_as_wild()
    {
        Assert.Equal("wild", new AppSettings().ActionWordsStrength);
        Assert.Equal(1.0, new AppSettings().Style().ActionStrength);
        var unknown = new AppSettings { ActionWordsStrength = "explosive" }.Normalized();
        Assert.Equal("wild", unknown.ActionWordsStrength);
        Assert.Equal(1.0, new AppSettings { ActionWordsStrength = "Subtle" }.Style().ActionStrength);
    }

    [Fact]
    public void Action_word_strength_round_trips_and_belongs_to_each_surface()
    {
        var json = JsonSerializer.Serialize(new AppSettings { ActionWordsStrength = "middle" }, AppSettingsJsonContext.Default.AppSettings);
        Assert.Equal("middle", JsonDocument.Parse(json).RootElement.GetProperty("actionWordsStrength").GetString());
        Assert.Equal("middle", JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!.Normalized().ActionWordsStrength);
        Assert.Contains("actionWordsStrength", LyricStyleProfiles.Keys);

        var floating = new AppSettings { ActionWordsStrength = "subtle" }.WithLyricsSurface(LyricsSurface.Floating);
        Assert.Equal("subtle", floating.ActionWordsStrength);
        var changed = (floating with { ActionWordsStrength = "middle" }).WithLyricsSurface(LyricsSurface.Desktop);
        Assert.Equal("subtle", changed.ActionWordsStrength);
    }

    [Fact]
    public void Lists_round_trip_and_restoring_defaults_stores_none()
    {
        var settings = new AppSettings { WordSymbolsWords = new Dictionary<string, string> { ["love"] = "heart" } };
        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        var back = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!.Normalized();
        Assert.Equal("heart", back.WordSymbolsWords!["love"]);
        var restored = (back with { WordSymbolsWords = null }).Normalized();
        Assert.Null(restored.WordSymbolsWords);
    }

    [Fact]
    public void A_stored_surface_style_from_before_comes_back_with_both_on()
    {
        // The floating style was stored before these settings existed: it lacks both keys.
        var old = new Dictionary<string, JsonElement> { ["lyricComposition"] = JsonSerializer.SerializeToElement("hinge") };
        var settings = new AppSettings { WordSymbolsEnabled = false, ActionWordsEnabled = false, LyricStyleFloating = old };
        var floating = settings.WithLyricsSurface(LyricsSurface.Floating);
        Assert.True(new AppSettings { ShowInterludes = false, LyricStyleFloating = old }.WithLyricsSurface(LyricsSurface.Floating).ShowInterludes, "an older switch that defaults on comes back on too");
        Assert.True(floating.WordSymbolsEnabled);
        Assert.True(floating.ActionWordsEnabled);
        // And the desktop keeps its own switches off for when it comes back.
        var back = floating.WithLyricsSurface(LyricsSurface.Desktop);
        Assert.False(back.WordSymbolsEnabled);
        Assert.False(back.ActionWordsEnabled);
    }
}
