using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Motion;

/// <summary>
/// The Mac's C suite for word geometry, reveal and vocal gaps (Tests/geometry_test.c), case for case. The suite blames
/// to the squashed import commit; its inputs and expected values are the spec of the re-implemented functions.
/// <c>vc_match_score</c>'s four cases belong to the ranking workstream and are not here.
/// </summary>
public class GeometryCTests
{
    private static bool Inside(Box b, double x, double y) =>
        x > b.X + 1e-6 && x < b.X + b.Width - 1e-6 && y > b.Y + 1e-6 && y < b.Y + b.Height - 1e-6;

    // Ported from geometry_test.c: the envelope holds every corner at every scale and turn up to its peak
    [Fact]
    public void EveryRotatedGrowingCornerFitsTheEnvelope()
    {
        for (var angle = 0; angle <= 20; angle++)
        {
            var envelope = ConstellationGeometry.WordEnvelope(240, 42, 1.20, angle);
            for (var phase = 0; phase <= 100; phase++)
            {
                var rotation = angle * (phase / 100.0) * 0.017453292519943295;
                var scale = .8 + .377 * (phase / 100.0);
                for (var x = -1; x <= 1; x += 2)
                for (var y = -1; y <= 1; y += 2)
                {
                    double px = x * 120 * scale, py = y * 21 * scale;
                    double rx = px * Math.Cos(rotation) - py * Math.Sin(rotation), ry = px * Math.Sin(rotation) + py * Math.Cos(rotation);
                    Assert.True(Math.Abs(rx) <= envelope.Width / 2 + 1e-8);
                    Assert.True(Math.Abs(ry) <= envelope.Height / 2 + 1e-8);
                }
            }
        }
        Assert.Equal(0, ConstellationGeometry.WordEnvelope(double.NaN, 42, 1.2, 10).Width);
    }

    // Ported from geometry_test.c: word reveal (rises steadily, the same after a seek), preview and settle
    [Fact]
    public void RevealVisibilityAndSettle()
    {
        double previous = 0;
        for (var i = 0; i < 500; i++)
        {
            var value = WordReveal.Reveal(i / 100.0, 1, 2);
            Assert.True(value >= previous && value >= 0 && value <= 1);
            previous = value;
        }
        Assert.Equal(0, WordReveal.Reveal(.9, 1, 2));
        Assert.True(WordReveal.Reveal(1.1, 1, 2) > 0 && WordReveal.Reveal(1.1, 1, 2) < 1);
        Assert.Equal(1, WordReveal.Reveal(3, 1, 2));
        Assert.Equal(0, WordReveal.Reveal(.5, 1, 2)); // earlier than the start again: back to 0
        Assert.Equal(0, WordReveal.Reveal(double.NaN, 1, 2));
        Assert.Equal(0, WordReveal.Visibility(.5, 1, 2, true, true, .5));
        Assert.True(Math.Abs(WordReveal.Visibility(.75, 1, 2, true, true, .5) - .125) < 1e-8);
        Assert.Equal(0, WordReveal.Visibility(.5, 1, 2, true, false, .5));
        Assert.Equal(0, WordReveal.Visibility(.5, 1, 2, false, true, .5));
        Assert.Equal(.25, WordReveal.Visibility(1, 1, 2, true, false, .5)); // the preview's quarter carries straight on at the onset
        Assert.True(WordReveal.Visibility(1.1, 1, 2, true, false, .5) > .25);
        Assert.Equal(1, WordReveal.Visibility(3, 1, 2, true, false, .5));
        Assert.Equal(0, WordReveal.Visibility(.5, 1, 2, true, false, .5)); // before the start again
        for (var i = 0; i <= 100; i++)
        {
            var settle = WordReveal.Settle(i / 100.0);
            Assert.True(settle >= 0 && settle <= 1.05);
        }
    }

    // Ported from geometry_test.c: vocal gaps
    [Fact]
    public void VocalGapsCases()
    {
        VocalWindow[] vocals = [new(10, 14), new(22, 28)];
        Assert.Equal(new VocalGap(0, 10, 0), VocalGaps.Find(vocals, 5, 40, 2.4));
        Assert.Null(VocalGaps.Find(vocals, 12, 40, 2.4));
        Assert.Equal(new VocalGap(14, 22, 1), VocalGaps.Find(vocals, 18, 40, 2.4));
        Assert.Equal(new VocalGap(28, 40, -1), VocalGaps.Find(vocals, 32, 40, 2.4));
        Assert.Null(VocalGaps.Find(vocals, 40, 40, 2.4));
        VocalWindow[] breath = [new(0, 5), new(6, 10)];
        Assert.Null(VocalGaps.Find(breath, 5.5, 20, 2.4));
        VocalWindow[] overlapping = [new(0, 8), new(4, 10), new(14, 18)];
        Assert.Null(VocalGaps.Find(overlapping, 6, 22, 2.4));
        var gap = VocalGaps.Find(overlapping, 12, 22, 2.4);
        Assert.True(gap is { Start: 10, End: 14 });
        Assert.Equal(-1, VocalGaps.Find([], 5, 20, 2.4)!.Value.NextVocal);
        Assert.Null(VocalGaps.Find(vocals, double.NaN, 40, 2.4));
    }

    // Ported from geometry_test.c: estimated line ends
    [Fact]
    public void EstimatedLineEnds()
    {
        Assert.Equal(14, EstimatedWords.EstimatedLineEnd(10, 14, 4, .45)); // a short window is left alone
        Assert.True(EstimatedWords.EstimatedLineEnd(10, 50, 4, .45) < 17); // a 40 s window for four words is cut
        Assert.Equal(30, EstimatedWords.EstimatedLineEnd(10, 30, 20, .50)); // twenty words fill twenty seconds
        Assert.True(EstimatedWords.EstimatedLineEnd(10, 90, 4, double.NaN) < 17); // an unknown pace falls back to the default
    }

    private static readonly Box View = new(0, 0, 1200, 700);

    private static Box[] Words() =>
    [
        new(30, 300, 110, 45), new(220, 80, 140, 50), new(490, 125, 60, 35),
        new(650, 350, 400, 120), new(140, 530, 160, 50), new(550, 550, 90, 40),
    ];

    // Ported from geometry_test.c: connections between words
    [Fact]
    public void Connections()
    {
        var links = new Segment[64];
        Box[] horizontal = [new(20, 100, 100, 40), new(300, 100, 200, 40)];
        var n = ConstellationGeometry.BuildConnections(horizontal, View, 6, links);
        Assert.Equal(1, n);
        Assert.True(Math.Abs(links[0].Ax - 126) < 1e-6 && Math.Abs(links[0].Bx - 294) < 1e-6);
        Assert.True(Math.Abs(links[0].Ay - 120) < 1e-6 && Math.Abs(links[0].By - 120) < 1e-6);
        Box[] vertical = [new(100, 20, 50, 60), new(100, 220, 50, 60)];
        Assert.Equal(1, ConstellationGeometry.BuildConnections(vertical, View, 6, links));
        Assert.True(Math.Abs(links[0].Ay - 86) < 1e-6 && Math.Abs(links[0].By - 214) < 1e-6);
        Box[] overlap = [new(20, 30, 150, 80), new(70, 50, 100, 80)];
        Assert.Equal(0, ConstellationGeometry.BuildConnections(overlap, View, 6, links));
        Box[] invalid = [new(double.NaN, 30, 50, 50), new(100, 50, 40, 40)];
        Assert.Equal(0, ConstellationGeometry.BuildConnections(invalid, View, 6, links));
        Box[] offscreen = [new(-500, -500, 30, 30), new(100, 50, 40, 40)];
        Assert.Equal(0, ConstellationGeometry.BuildConnections(offscreen, View, 6, links));
        Assert.Equal(0, ConstellationGeometry.BuildConnections(horizontal, View, 6, Span<Segment>.Empty));
        Assert.Equal(0, ConstellationGeometry.BuildConnections([], View, 6, links));

        var words = Words();
        n = ConstellationGeometry.BuildConnections(words, View, 8, links);
        Assert.True(n > 0 && n <= 5);
        // Chain: every link joins a word to the one before it.
        for (var i = 0; i < n; i++) Assert.Equal(links[i].From + 1, links[i].To);
        for (var i = 0; i < n; i++)
        {
            var s = links[i];
            Assert.True(double.IsFinite(s.Ax) && double.IsFinite(s.Ay) && double.IsFinite(s.Bx) && double.IsFinite(s.By));
            Assert.True(s.Ax >= 0 && s.Ax <= 1200 && s.Bx >= 0 && s.Bx <= 1200);
            Assert.True(s.Ay >= 0 && s.Ay <= 700 && s.By >= 0 && s.By <= 700);
            foreach (var word in words)
            {
                Assert.False(Inside(word, s.Ax, s.Ay));
                Assert.False(Inside(word, s.Bx, s.By));
                for (var t = 1; t < 100; t++)
                {
                    var f = t / 100.0;
                    Assert.False(Inside(word, s.Ax + (s.Bx - s.Ax) * f, s.Ay + (s.By - s.Ay) * f));
                }
            }
        }
        // Enlarge one box by 10%: no link may cross any box afterwards.
        words[3] = words[3] with { Width = words[3].Width * 1.10, Height = words[3].Height * 1.10 };
        n = ConstellationGeometry.BuildConnections(words, View, 8, links);
        Assert.True(n <= 5);
        for (var i = 0; i < n; i++)
        foreach (var word in words)
        for (var t = 1; t < 100; t++)
        {
            var f = t / 100.0;
            Assert.False(Inside(word, links[i].Ax + (links[i].Bx - links[i].Ax) * f, links[i].Ay + (links[i].By - links[i].Ay) * f));
        }
    }

    // Ported from geometry_test.c: the connection cache (a frame with the same layout reuses the links)
    [Fact]
    public void ConnectionCacheReusesGeometry()
    {
        var words = Words();
        words[3] = words[3] with { Width = words[3].Width * 1.10, Height = words[3].Height * 1.10 };
        var links = new Segment[64];
        var n = ConstellationGeometry.BuildConnections(words, View, 8, links);
        var memo = new ConnectionCache();
        var cached = new Segment[64];
        for (var frame = 0; frame < 1000; frame++)
        {
            var found = memo.Connections(words, View, 8, cached);
            Assert.True(found == n && memo.Builds == 1);
            for (var j = 0; j < found; j++) Assert.Equal(links[j], cached[j]);
        }
        Assert.True(n > 1);
        Assert.True(memo.Connections(words, View, 8, cached.AsSpan(0, 1)) == 1 && memo.Builds == 1);
        Assert.True(memo.Connections(words, View, 8, cached) == n && memo.Builds == 1);
        var resized = View with { Width = 1000 };
        memo.Connections(words, resized, 8, cached);
        Assert.Equal(2, memo.Builds);
        words[0] = words[0] with { Width = words[0].Width + 10 };
        memo.Connections(words, resized, 8, cached);
        Assert.Equal(3, memo.Builds);
        memo.Connections(words, resized, 9, cached);
        Assert.Equal(4, memo.Builds);
        memo.Connections(words.AsSpan(0, 5), resized, 9, cached);
        Assert.Equal(5, memo.Builds);
        var secondDisplay = new ConnectionCache();
        secondDisplay.Connections([new(20, 100, 100, 40), new(300, 100, 200, 40)], View, 6, cached);
        Assert.True(secondDisplay.Builds == 1 && memo.Builds == 5);
        var empty = new ConnectionCache();
        Box[] overlap = [new(20, 30, 150, 80), new(70, 50, 100, 80)];
        for (var frame = 0; frame < 10; frame++) Assert.Equal(0, empty.Connections(overlap, View, 6, cached));
        Assert.Equal(1, empty.Builds); // a build that found no links is remembered as well
        Assert.Equal(0, memo.Connections(new Box[65], View, 8, cached));
        Assert.Equal(0, memo.Connections(words, View, 8, Span<Segment>.Empty));
    }
}
