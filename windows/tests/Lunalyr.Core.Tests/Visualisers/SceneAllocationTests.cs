using System.Numerics;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Visualisers;
using static Lunalyr.Core.Tests.Visualisers.VisualiserTestSupport;

namespace Lunalyr.Core.Tests.Visualisers;

/// <summary>
/// The scenes' per-frame CPU work must not allocate once warmed up: advancing the shared Star Trails simulation, packing
/// its state, picking the sung word and the Wavefield modal bank's frame. Not a Mac test: the Mac states the rule in
/// StarTrailsSimulation's documentation.
/// </summary>
public class SceneAllocationTests
{
    [Fact]
    public void StarTrailsAndWavefieldFramesDoNotAllocate()
    {
        var analyzer = new AudioAnalyzer();
        for (var pass = 0; pass < 18; pass++) analyzer.Append(Sine(220, 0.25, 48_000, 2048, pass * 2048), 48_000);
        var tone = analyzer.Latest();
        var silence = new AudioSnapshot();
        LyricLine[] lines =
        [
            new() { Start = 0, End = 2, Text = "one two", WordTimed = true, Words = [new("one", 0, 1), new("two", 1, 2)] },
            new() { Start = 2, End = 4, Text = "three", Words = [new("three", 2, 4)] },
            new() { Start = 4, End = 600, Text = "four five six", WordTimed = true,
                    Words = [new("four", 4, 5), new("five", 5, 6), new("six", 6, 600)] },
        ];
        // Built before measuring: RenderPalette makes a new palette on every read.
        var earth = RenderPalette.Earth;
        var vivid = RenderPalette.Vivid;
        var shared = new StarTrailsSimulations();
        var bank = new WavefieldModes();
        var state = new Vector4[StarTrailsLayout.Float4Count];
        var options = new float[WavefieldFrame.OptionSlots];
        var calm = new VisualizerResponse(reduceMotion: true);
        var lively = new VisualizerResponse();
        double sink = 0;
        uint kicks = 0, snares = 0, hats = 0;
        void Run(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                var clock = i / 60.0;
                // Detector frames with fast drum rolls (they fill and evict the onset rings), then band-only ones.
                var frame = i % 400 < 300 ? tone : silence;
                if (i % 400 < 300)
                {
                    if (i % 3 == 0) kicks++;
                    if (i % 5 == 0) snares++;
                    hats++;
                    frame.Sound.PresentationTime = clock;
                    frame.Sound.Kick = 0.9f; frame.Sound.Snare = 0.6f; frame.Sound.Hat = 0.5f;
                    frame.Sound.Bass = (float)(0.5 + 0.4 * Math.Sin(clock * 3)); frame.Sound.Vocal = 0.6f; frame.Sound.Pad = 0.4f;
                    frame.Sound.KickOnsets = kicks; frame.Sound.SnareOnsets = snares; frame.Sound.HatOnsets = hats;
                    frame.Sound.KickTime = clock - 0.01; frame.Sound.SnareTime = clock - 0.02; frame.Sound.HatTime = clock;
                    frame.Beat = i % 3 == 0 ? 1 : 0.2f;
                }
                var word = StarTrailsLyricWord.Newest(lines, clock % 8);
                var palette = (i / 120 % 3) switch { 0 => earth, 1 => null, _ => vivid };
                var simulation = shared.Advance(clock * 0.7, clock, frame, i % 900 < 450 ? lively : calm, 1.2, palette, word);
                simulation.Pack(16.0 / 9, state);
                simulation.Pack(4.0 / 3, state, StarTrailsSimulation.Flight.Beside);
                var wave = bank.Frame(frame, clock, (float)clock, 1.5f, reduced: i % 900 >= 450);
                wave.Apply(options);
                sink += state[StarTrailsLayout.Craft].X + state[StarTrailsLayout.Word].X + wave.Modes[0].Shape.Z + options[13];
            }
        }
        Run(0, 900); // warm up
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run(900, 900 + 60 * 16); // 16 s: asteroids respawn, the cover changes eight times, both Wavefield paths run
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(double.IsFinite(sink));
        Assert.NotNull(shared.Simulation);
        Assert.True(shared.Simulation.YoungestEviction[0] < double.PositiveInfinity, "the kick roll evicted ring slots");
    }
}
