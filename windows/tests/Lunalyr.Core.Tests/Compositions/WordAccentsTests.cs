using Lunalyr.Core.Compositions;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>Word symbols and action words (the Mac's ad5a6a6 and cba108f): matching, the line plan and the motion.</summary>
public class WordAccentsTests
{
    [Theory]
    [InlineData("Love", "love")]
    [InlineData("LOVE!", "love")]
    [InlineData("don't", "dont")]
    [InlineData("24/7", "247")]
    [InlineData("café", null)]
    [InlineData("愛", null)]
    [InlineData("...", null)]
    [InlineData("", null)]
    public void EnglishKey_keeps_only_a_to_z_and_digits(string word, string? key) =>
        Assert.Equal(key, WordAccents.EnglishKey(word));

    [Theory]
    [InlineData("staring", "stare")]
    [InlineData("loving", "love")]
    [InlineData("spinning", "spin")]
    [InlineData("dropped", "drop")]
    [InlineData("timing", "time")]
    [InlineData("flying", "fly")]
    [InlineData("lovin'", "love")]
    [InlineData("lovin’", "love")]
    [InlineData("cries", "cry")]
    [InlineData("cried", "cry")]
    [InlineData("stars", "star")]
    [InlineData("falling", "fall")]
    [InlineData("rising", "rise")]
    public void EnglishForms_reach_the_stem(string word, string stem) =>
        Assert.Contains(stem, WordAccents.EnglishForms(word));

    [Fact]
    public void Staring_never_reads_as_star()
    {
        Assert.DoesNotContain("star", WordAccents.EnglishForms("staring"));
        Assert.Null(WordAccents.SymbolFor("staring", WordAccentLists.Defaults));
    }

    [Fact]
    public void Forms_never_add_a_form_under_three_letters()
    {
        foreach (var word in new[] { "shed", "red", "bed", "sing", "ties", "us", "ring" })
            Assert.All(WordAccents.EnglishForms(word).Skip(1), form => Assert.True(form.Length >= 3, $"{word}: {form}"));
    }

    [Fact]
    public void The_first_listed_form_wins()
    {
        Assert.Equal("heart.fill", WordAccents.SymbolFor("Loving", WordAccentLists.Defaults));
        Assert.Equal("heart.fill", WordAccents.SymbolFor("loves", WordAccentLists.Defaults));
        Assert.Equal(WordAction.Spin, WordAccents.ActionFor("spinning", WordAccentLists.Defaults));
        Assert.Equal(WordAction.Dip, WordAccents.ActionFor("dropped", WordAccentLists.Defaults));
        Assert.Equal(WordAction.Float, WordAccents.ActionFor("flying", WordAccentLists.Defaults));
    }

    [Fact]
    public void A_glued_run_is_matched_as_its_joined_text() =>
        Assert.Equal("sun.max", WordAccents.SymbolFor("sunshine", WordAccentLists.Defaults));

    [Fact]
    public void A_line_takes_two_symbols_once_each_and_one_action()
    {
        string[] runs = ["I", "love", "him", "and", "her", "fire", "fall", "home", "spin"];
        var plan = WordAccents.Plan(runs, symbols: true, actions: true, WordAccentLists.Defaults);
        Assert.Equal("heart.fill", plan[1].Symbol);
        Assert.Equal("person", plan[2].Symbol);
        Assert.Null(plan[4].Symbol);
        Assert.Null(plan[5].Symbol);
        Assert.Null(plan[7].Symbol);
        Assert.Equal(WordAction.Flicker, plan[5].Action);
        Assert.Null(plan[6].Action);
        Assert.Null(plan[8].Action);
    }

    [Fact]
    public void The_person_symbol_is_not_repeated()
    {
        var plan = WordAccents.Plan(["him", "her"], true, false, WordAccentLists.Defaults);
        Assert.Equal("person", plan[0].Symbol);
        Assert.Null(plan[1].Symbol);
    }

    [Fact]
    public void A_word_can_take_a_symbol_and_an_action()
    {
        var plan = WordAccents.Plan(["burning"], true, true, WordAccentLists.Defaults);
        Assert.Equal("flame", plan[0].Symbol);
        Assert.Equal(WordAction.Flicker, plan[0].Action);
    }

    [Fact]
    public void Switched_off_plans_nothing()
    {
        var plan = WordAccents.Plan(["love", "spin"], false, false, WordAccentLists.Defaults);
        Assert.All(plan, accent => Assert.True(accent.IsEmpty));
        Assert.Null(WordAccents.Plan(["love"], false, true, WordAccentLists.Defaults)[0].Symbol);
    }

    [Fact]
    public void Stored_lists_fold_keys_and_drop_what_cannot_be_used()
    {
        var symbols = WordAccents.CleanSymbols(new Dictionary<string, string>
        {
            ["Love!"] = "heart.fill", ["café"] = "star", ["moon"] = "no.such.symbol", ["zebra"] = "star",
        });
        Assert.Equal(["love", "zebra"], symbols.Select(pair => pair.Key));
        var actions = WordAccents.CleanActions(new Dictionary<string, string> { ["Spin"] = "spin", ["x"] = "teleport" });
        Assert.Equal([new KeyValuePair<string, WordAction>("spin", WordAction.Spin)], actions);
        Assert.Equal(WordAccents.DefaultSymbols.Count, WordAccents.CleanSymbols(null).Count);
    }

    [Fact]
    public void Lists_show_defaults_first_then_the_users_words_a_to_z()
    {
        var stored = new Dictionary<string, string> { ["zebra"] = "star", ["apple"] = "star", ["heart"] = "heart", ["love"] = "heart.fill" };
        Assert.Equal(["love", "heart", "apple", "zebra"], WordAccents.Ordered(stored, WordAccents.DefaultSymbols).Select(pair => pair.Key));
    }

    [Fact]
    public void Every_default_symbol_has_a_glyph() =>
        Assert.All(WordAccents.DefaultSymbols, pair => Assert.NotNull(WordSymbolGlyphs.Glyph(pair.Value)));

    [Theory]
    [MemberData(nameof(Actions))]
    public void Motion_is_identity_before_and_after(WordAction action)
    {
        var duration = WordActionMotion.Duration(action);
        foreach (var reduce in new[] { false, true })
        {
            Assert.True(WordActionMotion.Pose(action, -1, reduce).IsIdentity);
            Assert.True(WordActionMotion.Pose(action, 0, reduce).IsIdentity);
            Assert.True(WordActionMotion.Pose(action, duration, reduce).IsIdentity);
            Assert.True(WordActionMotion.Pose(action, duration + 3, reduce).IsIdentity);
            Assert.False(WordActionMotion.Pose(action, duration * 0.45, reduce).IsIdentity);
        }
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void Reduce_motion_only_shows_a_colour_cue(WordAction action)
    {
        for (var t = 0.01; t < 1; t += 0.05)
        {
            var pose = WordActionMotion.Pose(action, t, reduceMotion: true);
            Assert.Equal((0.0, 0.0, 1.0, 1.0, 1.0), (pose.Dx, pose.Dy, pose.TurnScale, pose.Scale, pose.Opacity));
        }
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void Colour_cues_never_lighten(WordAction action)
    {
        for (var t = 0.0; t < 1; t += 0.02)
        {
            var (r, g, b) = WordActionMotion.Pose(action, t, false).Multiply;
            Assert.InRange(r, 0, 1);
            Assert.InRange(g, 0, 1);
            Assert.InRange(b, 0, 1);
        }
    }

    [Fact]
    public void Motions_follow_the_mac()
    {
        Assert.Equal(-1, WordActionMotion.Pose(WordAction.Spin, 0.4, false).TurnScale, 6);
        Assert.Equal(-0.24, WordActionMotion.Pose(WordAction.Hop, 0.7 * 0.3, false).Dy, 6);
        Assert.True(WordActionMotion.Pose(WordAction.Dip, 0.3, false).Dy > 0.1);
        Assert.True(WordActionMotion.Pose(WordAction.Float, 0.45, false).Dy < -0.15);
        Assert.Equal(1.16, WordActionMotion.Pose(WordAction.Grow, 0.4, false).Scale, 6);
        Assert.Equal(0.84, WordActionMotion.Pose(WordAction.Shrink, 0.4, false).Scale, 6);
        Assert.InRange(WordActionMotion.Pose(WordAction.Dissolve, 0.45, false).Opacity, 0.29, 0.35);
        Assert.True(WordActionMotion.Pose(WordAction.Frost, 0.45, false).Cool > 0.6);
        var reduced = WordActionMotion.Pose(WordAction.Flicker, 0.3, true);
        Assert.Equal(reduced.Warm, reduced.Glow);
    }

    [Fact]
    public void The_symbol_fades_in_over_three_tenths_of_a_second()
    {
        Assert.Equal(0, WordActionMotion.SymbolOpacity(-0.2));
        Assert.Equal(0.5, WordActionMotion.SymbolOpacity(0.15), 6);
        Assert.Equal(1, WordActionMotion.SymbolOpacity(0.3));
    }

    public static TheoryData<WordAction> Actions()
    {
        var data = new TheoryData<WordAction>();
        foreach (var action in WordAccents.AllActions) data.Add(action);
        return data;
    }
}
