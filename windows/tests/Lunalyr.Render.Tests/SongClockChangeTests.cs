using Lunalyr.Render.Pacing;

namespace Lunalyr.Render.Tests;

public sealed class SongClockChangeTests
{
    [Fact]
    public void NothingPlayingStaysQuiet()
    {
        // The scene contract's "nothing playing" is NaN: reading it again and again is not new input, or an idle or
        // frozen wallpaper would redraw every refresh.
        Assert.False(SongClockChange.IsNewInput(double.NaN, false, double.NaN, false, 0.016));
        Assert.False(SongClockChange.IsNewInput(double.NaN, false, double.NaN, false, 10));
    }

    [Fact]
    public void ASongAppearingOrGoingAwayIsNewInput()
    {
        Assert.True(SongClockChange.IsNewInput(double.NaN, false, 12, false, 1));
        Assert.True(SongClockChange.IsNewInput(12, false, double.NaN, false, 1));
    }

    [Fact]
    public void PlayingAndPausingAreNewInput()
    {
        Assert.True(SongClockChange.IsNewInput(30, false, 30, true, 0.016));
        Assert.True(SongClockChange.IsNewInput(30, true, 30.016, false, 0.016));
    }

    [Fact]
    public void AClockRunningOnIsNotButASeekIs()
    {
        Assert.False(SongClockChange.IsNewInput(30, true, 30.016, true, 0.016));
        Assert.False(SongClockChange.IsNewInput(30, true, 30.3, true, 0.016)); // clock catching up, within tolerance
        Assert.True(SongClockChange.IsNewInput(30, true, 75, true, 0.016)); // forward seek
        Assert.True(SongClockChange.IsNewInput(30, true, 10, true, 0.016)); // backward seek
    }

    [Fact]
    public void AScrubWhilePausedIsNewInput()
    {
        Assert.False(SongClockChange.IsNewInput(42, false, 42, false, 5));
        Assert.True(SongClockChange.IsNewInput(42, false, 50, false, 5));
    }
}
