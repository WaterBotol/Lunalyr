namespace Lunalyr.Render.Tests;

/// <summary>The desktop player's hold on the wallpaper lyrics: a fade from wherever they are, never a jump.</summary>
public sealed class LyricFocusTests
{
    [Fact]
    public void FadesFromWhereItIsAndSettles()
    {
        using var engine = new RenderEngine();
        Assert.Equal(1, engine.LyricFocusOpacity);
        engine.SetLyricFocus(0.15, 0);
        Assert.Equal(0.15, engine.LyricFocusOpacity, 1e-9);
        // A long fade back starts where it is: nothing jumps.
        engine.SetLyricFocus(1, 60);
        Assert.InRange(engine.LyricFocusOpacity, 0.15, 0.2);
        engine.SetLyricFocus(0.8, 0);
        Assert.Equal(0.8, engine.LyricFocusOpacity, 1e-9);
        engine.SetLyricFocus(double.NaN, 0);
        Assert.Equal(1, engine.LyricFocusOpacity);
        engine.SetLyricFocus(-3, 0);
        Assert.Equal(0, engine.LyricFocusOpacity);
    }
}
