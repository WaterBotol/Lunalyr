using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Motion;

/// <summary>
/// The Mac's C suite for the lyric motion maths (Tests/motion_test.c), case for case. The "original curves" cases blame
/// to the squashed import commit; their inputs and invariants are kept as the spec of the re-implemented curves.
/// </summary>
public class LyricMotionCTests
{
    private static readonly RollingStyle[] Styles = [RollingStyle.Fan, RollingStyle.Column, RollingStyle.Collage];

    // Ported from motion_test.c: original_curves (hand-off and ease)
    [Fact]
    public void OriginalCurvesHandOffAndEase()
    {
        Assert.Equal(0, RollingMotion.MotionHandoff(-1, .4));
        Assert.Equal(1, RollingMotion.MotionHandoff(2, .4));
        double previous = 0;
        for (var i = 0; i <= 1000; i++)
        {
            var value = RollingMotion.MotionHandoff(i * .001, .4);
            Assert.True(value >= previous && value <= 1);
            previous = value;
            var ease = RollingMotion.MotionEase(i * .001);
            Assert.True(ease >= 0 && ease <= 1.12);
        }
        Assert.Equal(0, RollingMotion.MotionEase(0));
        Assert.Equal(1, RollingMotion.MotionEase(1));
    }

    // Ported from motion_test.c: original_curves (rolling poses, per style)
    [Fact]
    public void OriginalCurvesRollingPoses()
    {
        foreach (var style in Styles)
        {
            Assert.Equal(0, RollingMotion.Pose(style, 5, 1, 10, 197, .6, false).Opacity); // retires before a stage edge
            var a = RollingMotion.Pose(style, 0, .5, 3, 197, 0, false);
            var b = RollingMotion.Pose(style, 0, .5, 3, 197, 1, false);
            Assert.True(b.Scale > a.Scale); // louder music swells the word being sung
            Assert.Equal(b, RollingMotion.Pose(style, 0, .5, 3, 197, 1, false)); // the same inputs give the same pose, whatever came before
            Assert.Equal(0, RollingMotion.Pose(style, 1, 5, .4, 197, 1, false).Opacity); // a word long finished has gone
            var reduced = RollingMotion.Pose(style, 0, .5, 3, 197, 1, true);
            Assert.True(reduced.Angle == 0 && reduced.Blur == 0);
            var hero = RollingMotion.Pose(style, 0, .2, .4, 197, 0, true);
            var neighbour = RollingMotion.Pose(style, 1, .2, .4, 197, 0, true);
            Assert.True(hero.Y - neighbour.Y > 1.2); // rank 0 and rank 1 sit more than a type size apart
            for (var i = 0; i < 900; i++)
            {
                var r = -1 + i * .01;
                var p = RollingMotion.Pose(style, r, .5, 2, 197, .8, false);
                var q = RollingMotion.Pose(style, r + .00001, .5, 2, 197, .8, false);
                Assert.True(Math.Abs(p.Y - q.Y) < .001 && Math.Abs(p.Opacity - q.Opacity) < .001);
                Assert.True(Math.Abs(p.Scale - q.Scale) < .001 && Math.Abs(p.Angle - q.Angle) < .001);
                Assert.True(double.IsFinite(p.Scale) && p.Scale > .3 && p.Scale < 1.5);
            }
            // Without breathing, the pose sits exactly on its baseline.
            var calm = RollingMotion.Pose(style, 1.3, .5, 2, 197, .8, true);
            Assert.True(Math.Abs(calm.Y - RollingMotion.Baseline(style, 1.3)) < 1e-12);
            Assert.True(RollingMotion.Baseline(style, 0) > RollingMotion.Baseline(style, 1));
            // The onset itself is continuous: breathing and energy start at zero.
            var before = RollingMotion.Pose(style, 0, -.00001, 3, 197, 1, false);
            var after = RollingMotion.Pose(style, 0, .00001, 3, 197, 1, false);
            Assert.True(Math.Abs(before.Y - after.Y) < 1e-4 && Math.Abs(before.Scale - after.Scale) < 1e-4);
            Assert.True(Math.Abs(before.Angle - after.Angle) < 1e-4);
        }
    }

    // Ported from motion_test.c: original_curves (vocal gesture, invalid input)
    [Fact]
    public void OriginalCurvesVocalGestureAndInvalidInput()
    {
        var settled = RollingMotion.VocalGesture(30, .4, 101, .7, false);
        Assert.True(settled.X == 0 && settled.Y == 0 && settled.Scale == 1);
        var first = RollingMotion.VocalGesture(.7, 2, 101, .3, false);
        var held = RollingMotion.VocalGesture(1.2, 2, 101, .3, false);
        Assert.NotEqual(first.Y, held.Y);
        var invalid = RollingMotion.Pose(RollingStyle.Fan, double.NaN, double.PositiveInfinity, double.NaN, 0, double.NaN, false);
        Assert.True(double.IsFinite(invalid.X) && double.IsFinite(invalid.Y) && double.IsFinite(invalid.Opacity));
    }

    // Ported from motion_test.c: springs
    [Fact]
    public void Springs()
    {
        Spring[] kinds = [new(14, .6), new(14, 1), new(9, 1.6), new(30, .85)];
        foreach (var s in kinds)
        {
            Assert.True(LyricMotion.SpringStep(-1, s) == 0 && LyricMotion.SpringStep(0, s) == 0);
            Assert.Equal(1, LyricMotion.SpringStep(20, s)); // lands exactly, so idle frames compare equal
            double previous = 0, peak = 0;
            for (var i = 1; i <= 4000; i++)
            {
                double t = i * .0005, x = LyricMotion.SpringStep(t, s);
                Assert.True(double.IsFinite(x) && Math.Abs(x - previous) < .02); // no jumps at 2 kHz
                var numeric = (LyricMotion.SpringStep(t + 1e-6, s) - LyricMotion.SpringStep(t - 1e-6, s)) / 2e-6;
                Assert.True(Math.Abs(numeric - LyricMotion.SpringVelocity(t, s)) < 1e-3 * Math.Max(1, Math.Abs(numeric)) + 1e-3);
                previous = x;
                if (x > peak) peak = x;
            }
            if (s.Zeta >= 1) Assert.True(peak <= 1 + 1e-9);
            else Assert.True(peak > 1 && peak < 1.2);
        }
        // Interrupting a move: the second step starts at zero velocity, so the sum keeps position and velocity
        // continuous where the target changes.
        var spring = new Spring(12, .7);
        const double cut = .08;
        var left = LyricMotion.SpringStep(cut - 1e-7, spring) - LyricMotion.SpringStep(cut - 1e-7 - cut, spring);
        var right = LyricMotion.SpringStep(cut + 1e-7, spring) - LyricMotion.SpringStep(cut + 1e-7 - cut, spring);
        Assert.True(Math.Abs(left - right) < 1e-5);
        var vl = LyricMotion.SpringVelocity(cut - 1e-7, spring) - LyricMotion.SpringVelocity(-1e-7, spring);
        var vr = LyricMotion.SpringVelocity(cut + 1e-7, spring) - LyricMotion.SpringVelocity(1e-7, spring);
        Assert.True(Math.Abs(vl - vr) < 1e-3);
        // Monotone settle that finishes exactly on time.
        double last = 0;
        for (var i = 0; i <= 1000; i++)
        {
            var x = LyricMotion.Settle(i * .0005, .4);
            Assert.True(x >= last - 1e-12 && x <= 1);
            last = x;
        }
        Assert.True(LyricMotion.Settle(.4, .4) == 1 && LyricMotion.Settle(0, .4) == 0 && LyricMotion.Settle(double.NaN, .4) == 0);
    }

    // Ported from motion_test.c: noise
    [Fact]
    public void Noise()
    {
        Assert.Equal(0, LyricMotion.Noise(7, 3)); // gradient noise is zero on lattice points
        double low = 0, high = 0, previous = LyricMotion.Noise(7, 0);
        for (var i = 1; i <= 20000; i++)
        {
            double t = i * .001, n = LyricMotion.Noise(7, t);
            Assert.True(n >= -1 && n <= 1 && Math.Abs(n - previous) < .01);
            previous = n;
            low = Math.Min(low, n);
            high = Math.Max(high, n);
            var n2 = LyricMotion.Noise2(7, t);
            Assert.True(n2 >= -1 && n2 <= 1);
        }
        Assert.True(low < -.2 && high > .2); // it actually moves
        Assert.Equal(LyricMotion.Noise(7, 2.5), LyricMotion.Noise(7, 2.5));
        Assert.NotEqual(LyricMotion.Noise(7, 2.5), LyricMotion.Noise(8, 2.5));
        Assert.True(LyricMotion.Noise(7, double.NaN) == 0 && LyricMotion.Jitter(1, 1) != LyricMotion.Jitter(2, 1));
    }

    // Ported from motion_test.c: word_motion
    [Fact]
    public void WordMotionCases()
    {
        // A word in a very fast line holds a moment after it has arrived instead of leaving a tenth of a second after
        // its onset.
        for (ulong seed = 1; seed < 40; seed++)
        {
            var quick = LyricMotion.ForWord(10.3, 10, 10.08, .1, 10.1, 1, seed, 0, false);
            Assert.True(quick.Reveal == 1 && quick.Leave == 0);
            Assert.Equal(1, LyricMotion.ForWord(11.2, 10, 10.08, .1, 10.1, 1, seed, 0, false).Leave);
        }
        // At rest before and long after a word: exact values, not approximately.
        var a = LyricMotion.ForWord(0, 10, 10.4, .4, double.PositiveInfinity, .5, 99, .3, false);
        var b = LyricMotion.ForWord(8, 10, 10.4, .4, double.PositiveInfinity, .5, 99, .3, false);
        Assert.True(a == b && a.Reveal == 0 && a.Heat == 0);
        var c = LyricMotion.ForWord(40, 10, 10.4, .4, double.PositiveInfinity, .5, 99, .3, false);
        var d = LyricMotion.ForWord(55, 10, 10.4, .4, double.PositiveInfinity, .5, 99, .3, false);
        Assert.True(c == d && c.Reveal == 1 && c.Enter == 1 && c.Heat == 0 && c.Scale == 1);
        // Continuous through the onset, the end of the word and the exit.
        const double exitStart = 11.0;
        var previous = LyricMotion.ForWord(9.5, 10, 10.4, .4, exitStart, .5, 99, .6, false);
        for (var i = 1; i <= 2400 * 3; i++)
        {
            var t = 9.5 + i / 2400.0;
            var m = LyricMotion.ForWord(t, 10, 10.4, .4, exitStart, .5, 99, .6, false);
            Assert.True(Math.Abs(m.Reveal - previous.Reveal) < .02 && Math.Abs(m.Enter - previous.Enter) < .03);
            Assert.True(Math.Abs(m.Heat - previous.Heat) < .03 && Math.Abs(m.Leave - previous.Leave) < .02);
            Assert.True(Math.Abs(m.Y - previous.Y) < .01 && Math.Abs(m.Scale - previous.Scale) < .01);
            Assert.True(Math.Abs(m.Angle - previous.Angle) < .05 && m.Heat >= 0 && m.Heat <= 1.15);
            previous = m;
        }
        // A held note swells further than a clipped syllable.
        double shortPeak = 0, longPeak = 0;
        for (var i = 0; i < 300; i++)
        {
            shortPeak = Math.Max(shortPeak, LyricMotion.ForWord(10 + i * .005, 10, 10.05, .1, double.PositiveInfinity, 0, 5, 0, false).Heat);
            longPeak = Math.Max(longPeak, LyricMotion.ForWord(10 + i * .005, 10, 11.5, 1.5, double.PositiveInfinity, 0, 5, 0, false).Heat);
        }
        Assert.True(longPeak > shortPeak + .1);
        // The exit runs in reading order.
        var first = LyricMotion.ForWord(11.12, 10, 10.4, .4, 11, 0, 99, 0, false);
        var last = LyricMotion.ForWord(11.12, 10, 10.4, .4, 11, 1, 99, 0, false);
        Assert.True(first.Leave > last.Leave);
        // Seeded: identical timing, different words, different motion.
        var one = LyricMotion.ForWord(10.07, 10, 10.9, .9, double.PositiveInfinity, .5, 1, 0, false);
        var two = LyricMotion.ForWord(10.07, 10, 10.9, .9, double.PositiveInfinity, .5, 2, 0, false);
        Assert.True(one.Enter != two.Enter || one.Y != two.Y);
        // Reduced motion: no gesture, no overshoot, opacity still arrives.
        var calm = LyricMotion.ForWord(10.3, 10, 11.5, 1.5, double.PositiveInfinity, .5, 99, 1, true);
        Assert.True(calm.X == 0 && calm.Y == 0 && calm.Angle == 0 && calm.Scale == 1 && calm.Enter == calm.Reveal);
        Assert.True(LyricMotion.RollingHandoff(-.1, .4, 3, 0) == 0 && LyricMotion.RollingHandoff(20, .4, 3, 0) == 1);
        // The rolling window keeps a word until the next one has handed over (3 s): every hand-off has landed.
        for (var cad = 0; cad <= 40; cad++)
        for (ulong seed = 0; seed < 64; seed++)
        for (var e = 0; e <= 4; e++)
        {
            var cadence = cad == 40 ? double.PositiveInfinity : .02 + cad * .5;
            Assert.Equal(1, LyricMotion.RollingHandoff(3, cadence, unchecked(seed * 0x9E3779B97F4A7C15UL), e * .25));
        }
        var rest = LyricMotion.IdleDrift(3, 12.5, 0);
        Assert.True(rest.X == 0 && rest.Y == 0 && rest.Scale == 1 && rest.Angle == 0);
        var drift = LyricMotion.IdleDrift(3, 12.5, 1);
        Assert.True(Math.Abs(drift.X) < .03 && Math.Abs(drift.Y) < .03 && Math.Abs(drift.Angle) < .5);
    }

    // Music re-anchors its position every couple of seconds with some error.
    private static double RawPosition(double wall)
    {
        var anchor = (int)(wall / 2);
        var error = (anchor % 3 - 1) * .04;
        return wall + error;
    }

    // Ported from motion_test.c: clock_and_follower
    [Fact]
    public void ClockAndFollower()
    {
        LyricClockState c30 = default, c60 = default;
        c30.Advance(0, RawPosition(0), true);
        c60.Advance(0, RawPosition(0), true);
        var previous = c60.Time;
        for (var frame = 1; frame <= 600; frame++)
        {
            var wall = frame / 60.0;
            Assert.False(c60.Advance(wall, RawPosition(wall), true));
            var step = c60.Time - previous;
            Assert.True(step > 1 / 60.0 * .85 && step < 1 / 60.0 * 1.15); // never a visible jump
            previous = c60.Time;
            Assert.False(c60.Advance(wall, RawPosition(wall), true)); // same frame twice
            Assert.Equal(previous, c60.Time);
            if (frame % 2 == 0)
            {
                c30.Advance(wall, RawPosition(wall), true);
                Assert.True(Math.Abs(c30.Time - c60.Time) < .002); // 30 and 60 fps agree
            }
        }
        Assert.True(Math.Abs(c60.Time - RawPosition(10)) < .05);
        // A seek is adopted at once and reported.
        Assert.True(c60.Advance(10.02, 42, true) && c60.Time == 42);
        // Paused: never runs backwards.
        var held = c60.Time;
        c60.Advance(10.1, 41.9, false);
        Assert.Equal(held, c60.Time);

        Follower f30 = default, f60 = default;
        f30.Follow(0, 0, .2, 0);
        f60.Follow(0, 0, .2, 0);
        for (var frame = 1; frame <= 120; frame++)
        {
            var v60 = f60.Follow(1, 1 / 60.0, .2, 0);
            if (frame % 2 == 0) Assert.True(Math.Abs(f30.Follow(1, 1 / 30.0, .2, 0) - v60) < 1e-9);
        }
        Follower limited = default;
        limited.Follow(0, 0, .05, 0);
        double last = 0;
        for (var frame = 1; frame <= 120; frame++)
        {
            var value = limited.Follow(frame % 8 < 4 ? 1 : 0, 1 / 60.0, .05, .4);
            Assert.True(Math.Abs(value - last) <= .4 / 60.0 + 1e-12); // rate cap holds against a square wave
            last = value;
        }
    }
}
