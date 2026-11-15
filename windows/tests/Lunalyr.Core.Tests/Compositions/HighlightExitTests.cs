using Lunalyr.Core.Compositions;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>Highlight exits (the Mac's 436adda): one smooth curve for tiles, text and the stage layouts' line leave.</summary>
public class HighlightExitTests
{
    private const double Frame = 1 / 30.0;

    [Fact]
    public void The_exit_curve_is_monotone_with_no_big_frame_step()
    {
        var last = 0.0;
        for (var t = 0.0; t <= 0.5; t += Frame)
        {
            var e = LyricHighlightTiming.ExitProgress(t);
            Assert.True(e >= last);
            Assert.True(e - last < 0.16, $"step {e - last} at {t}");
            last = e;
        }
        Assert.Equal(0, LyricHighlightTiming.ExitProgress(0));
        Assert.Equal(1, LyricHighlightTiming.ExitProgress(0.42));
    }

    [Fact]
    public void The_tile_shrinks_and_softens_as_it_leaves_and_reduce_motion_only_fades()
    {
        var pose = LyricHighlightTiming.Pose(10.21, 5, 10, reduceMotion: false);
        Assert.InRange(pose.Opacity, 0.4, 0.6);
        Assert.InRange(pose.Scale, 0.96, 0.98);
        Assert.InRange(pose.Blur, 0.02, 0.04);
        var still = LyricHighlightTiming.Pose(10.21, 5, 10, reduceMotion: true);
        Assert.Equal(1, still.Scale);
        Assert.Equal(0, still.Blur);
        Assert.Equal(pose.Opacity, still.Opacity, 6);
    }

    [Fact]
    public void The_text_eases_back_one_way_over_several_frames()
    {
        double? started = null, back = null;
        var last = 1.0;
        for (var t = 10.0; t <= 10.5; t += Frame)
        {
            var pose = LyricHighlightTiming.Pose(t, 5, 10, false);
            var text = LyricHighlightTiming.TextOpacity(pose, 0.4);
            Assert.True(text <= last + 1e-9, "the text only eases back");
            Assert.True(last - text < 0.5, $"text step {last - text} at {t}");
            if (text < 0.999 && started is null) started = t;
            if (text < 0.001 && back is null) back = t;
            if (pose.Opacity <= 0.001) Assert.True(text < 0.001, "back by the time the tile is gone");
            last = text;
        }
        Assert.NotNull(started);
        Assert.True(started >= 10, "not before the tile leaves");
        Assert.True((back ?? 10.5) - started >= 3 * Frame - 1e-9, "takes at least three frames");
    }

    [Fact]
    public void A_whole_tile_carries_its_text_colour_and_a_gone_one_none() =>
        Assert.Equal((1.0, 0.0), (LyricHighlightTiming.TextOpacity(1, 0.95, 0.5), LyricHighlightTiming.TextOpacity(0, 0.02, 0.5)));

    [Fact]
    public void A_carried_tile_holds_and_an_uncarried_one_is_gone_after_the_line_end()
    {
        HighlightSegment[] segments = [new(0, 1, 5, 6, 10)];
        var marks = new HighlightMark[2];
        Assert.Equal(1, HighlightSegment.Marks(segments, 11, false, marks, carried: true));
        Assert.Equal(1, marks[0].Pose.Opacity, 6);
        Assert.Equal(0, HighlightSegment.Marks(segments, 10.5, false, marks));
    }

    [Fact]
    public void Stage_lines_lose_little_on_their_first_frame_and_torque_little_on_its_last()
    {
        Assert.True(LyricHighlightTiming.ExitProgress(1 / 60.0) < 0.01);
        Assert.True(1 - TorqueGeometry.Fading(0.27 - 1 / 60.0, 0) < 0.1);
    }
}
