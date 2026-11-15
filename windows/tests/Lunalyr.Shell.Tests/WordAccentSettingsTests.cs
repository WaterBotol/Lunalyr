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
