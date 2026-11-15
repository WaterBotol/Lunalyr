using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Motion;

/// <summary>Word presence (Mac queue item 5): today's look at 1, the user's chosen strength at 2.</summary>
public class WordPresenceTests
{
    [Fact]
    public void Strength_one_is_todays_look()
    {
        Assert.True(WordPresence.At(1).IsToday);
        Assert.Equal((0.0, 1.0, 0.0), WordPresence.Stage(1, 0, 1, 1));
    }

    [Fact]
    public void The_chosen_strength_is_the_stronger_sample()
    {
        Assert.Equal(2, WordPresence.Chosen);
        Assert.Equal(new WordPresence(1.8, 1.7, 1.35, 1.8), WordPresence.At(WordPresence.Chosen));
    }

    [Fact]
    public void Strength_is_held_to_one_to_two()
    {
        Assert.Equal(WordPresence.At(2), WordPresence.At(5));
        Assert.True(WordPresence.At(0.3).IsToday);
        Assert.True(WordPresence.At(double.NaN).IsToday);
    }

    [Fact]
    public void A_stage_word_rises_in_swells_while_sung_and_drifts_up_as_it_leaves()
    {
        var arriving = WordPresence.Stage(2, 0, 0, 0);
        Assert.Equal(0.32, arriving.Dy, 6);
        Assert.Equal(0.88, arriving.Scale, 6);
        var sung = WordPresence.Stage(2, 1, 1, 0);
        Assert.Equal(0, sung.Dy, 6);
        Assert.Equal(1.06, sung.Scale, 6);
        Assert.Equal(0, sung.Blur, 6);
        var leaving = WordPresence.Stage(2, 1, 0, 1);
        Assert.Equal(-0.26, leaving.Dy, 6);
        Assert.Equal(0.96, leaving.Scale, 6);
    }
}
