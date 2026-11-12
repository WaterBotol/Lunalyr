using System.Globalization;
using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Visualisers;
using L = Lunalyr.Core.Visualisers.StarTrailsLayout;

namespace Lunalyr.Core.Tests.Visualisers;

/// <summary>
/// Star Trails' CPU simulation and its state buffer. The Mac tests that render frames on the GPU are left to the
/// renderer's offscreen tests; those that mix state checks with a render keep their state half here.
/// </summary>
public class StarTrailsTests
{
    private const double Aspect = 16.0 / 9;

    // MARK: Simulation

    // Ported from StarTrailsTests.swift: testStateLayoutMatchesTheShader (the sizes, and the shader's slot constants as
    // StarTrails.metal states them; the Windows shader is checked against this layout where it is built)
    [Fact]
    public void StateLayoutMatchesTheShader()
    {
        Assert.Equal(205, L.Float4Count);
        Assert.Equal(3280, L.ByteCount);
        Assert.True(L.ByteCount <= 4096, "Small enough for setFragmentBytes on the static path");
        (string Name, int Value, int Shader)[] expected =
        [
            ("HDR0", L.Hdr0, 0), ("HDR1", L.Hdr1, 1), ("HDR2", L.Hdr2, 2), ("ENV0", L.Env0, 3), ("ENV1", L.Env1, 4),
            ("STAGE", L.Stage, 5), ("CLK", L.Clock, 6), ("CRAFT", L.Craft, 7), ("ONSET", L.Onset, 11),
            ("LIVE", L.Live, 51), ("AST", L.Asteroid, 52), ("TRAIL", L.Trail, 72), ("kTrailN", L.TrailSamples, 56),
            ("kAst", L.Asteroids, 10), ("SKY", L.Sky, 184), ("SKYTINT", L.SkyTints, 195), ("LYR", L.Word, 204),
        ];
        foreach (var (name, value, shader) in expected) Assert.True(value == shader, name);
        Assert.Equal(6, L.BufferIndex);
    }

    /// <summary>
    /// A seeded, musical input: kicks at 2 Hz, snares on the off-beat, hats at 8 Hz, with exact onset times on the wall
    /// clock, and slowly moving sustained classes.
    /// </summary>
    private static StarTrailsSound Music(double t)
    {
        var s = new StarTrailsSound();
        (uint, double?) Onsets(double rate, double offset)
        {
            var n = Math.Floor((t - offset) * rate);
            return n < 0 ? (0, null) : ((uint)(n + 1), offset + n / rate);
        }
        (s.KickOnsets, s.KickTime) = Onsets(2, 0.1);
        (s.SnareOnsets, s.SnareTime) = Onsets(1, 0.35);
        (s.HatOnsets, s.HatTime) = Onsets(8, 0.02);
        double Decay(double? time, double tau) => time is { } start ? Math.Exp(-Math.Max(0, t - start) / tau) : 0;
        s.Kick = Decay(s.KickTime, 0.26); s.Snare = Decay(s.SnareTime, 0.22); s.Hat = Decay(s.HatTime, 0.15);
        s.Bass = 0.5 + 0.3 * Math.Sin(t * 1.3); s.Vocal = Math.Max(0, Math.Sin(t * 0.7)); s.Pad = 0.4 + 0.2 * Math.Sin(t * 0.3);
        s.Level = 0.45;
        return s;
    }

    private static StarTrailsSimulation Run(double fps, double seconds, bool calm = false)
    {
        var simulation = new StarTrailsSimulation(calm: calm);
        var frames = (int)Math.Round(seconds * fps, MidpointRounding.AwayFromZero);
        for (var i = 1; i <= frames; i++) simulation.Advance(1 / fps, 1 / fps, Music(i / fps));
        return simulation;
    }

    private static Vector4[] Slice(Vector4[] state, int from, int to) => state[from..to];

    // Ported from StarTrailsTests.swift: testDeterministicAndRepeatedFramesChangeNothing
    [Fact]
    public void DeterministicAndRepeatedFramesChangeNothing()
    {
        StarTrailsSimulation a = Run(30, 4), b = Run(30, 4);
        var packed = a.Pack(Aspect);
        Assert.Equal(packed, b.Pack(Aspect));
        Assert.Equal(packed, a.Pack(Aspect)); // Packing is pure
        a.Advance(0, 0, Music(4));
        Assert.Equal(packed, a.Pack(Aspect)); // A repeated frame changes nothing
        Assert.DoesNotContain(packed, v => !(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W)));
    }

    // Ported from StarTrailsTests.swift: testFrameRateIndependence
    [Fact]
    public void FrameRateIndependence()
    {
        var reference = Run(120, 10).Pack(Aspect);
        foreach (var fps in new[] { 24.0, 30, 60 })
        {
            var state = Run(fps, 10).Pack(Aspect);
            for (var c = 0; c < 2; c++)
            {
                Vector4 a = state[L.Craft + 2 * c], b = reference[L.Craft + 2 * c];
                var distance = Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
                Assert.True(distance < 0.0012f, $"craft {c} at {fps} fps: {distance}");
            }
            var trail = 0f;
            for (var i = L.Trail; i < L.Float4Count; i++) trail = Math.Max(trail, Math.Abs(state[i].X - reference[i].X));
            Assert.True(trail < 0.0012f, $"trail at {fps} fps: {trail}");
            Assert.True(Math.Abs(state[L.Hdr1].Z - reference[L.Hdr1].Z) <= 0.0004f, $"scroll at {fps} fps");
            // Onset ages are exact: the newest kick (at 9.6 s) is 0.4 s old at every rate.
            var ages = Enumerable.Range(0, 6).Select(slot => state[L.Onset + 2 * slot].Y).Where(age => age >= 0).ToArray();
            var newest = ages.Length > 0 ? ages.Min() : -1;
            Assert.True(Math.Abs(newest - 0.4) <= 1e-4, $"{fps} fps: {newest}");
        }
    }

    // Ported from StarTrailsTests.swift: testRingsNeverEvictALitSlot
    [Fact]
    public void RingsNeverEvictALitSlot()
    {
        var simulation = new StarTrailsSimulation();
        var sound = new StarTrailsSound();
        for (var i = 1; i <= 300; i++)
        {
            // Fast rolls of every drum, calm toggled midway.
            if (i % 3 == 0 || i % 7 == 0)
            {
                unchecked { sound.KickOnsets += 1; sound.SnareOnsets += 1; sound.HatOnsets += 1; }
            }
            sound.Kick = 0.9; sound.Snare = 0.9; sound.Hat = 0.9;
            if (i == 150) simulation.CalmTarget = 1;
            simulation.Advance(1.0 / 60, 1.0 / 60, sound);
        }
        for (var kind = 0; kind < StarTrailsSimulation.Rings.Count; kind++)
        {
            var ring = StarTrailsSimulation.Rings[kind];
            Assert.True(simulation.YoungestEviction[kind] >= ring.Life - 1e-9, $"kind {kind}");
            Assert.True(simulation.YoungestEviction[kind] < double.PositiveInfinity, $"the roll filled ring {kind}");
        }
        var state = simulation.Pack(Aspect);
        for (var slot = 0; slot < 20; slot++)
        {
            if (!(state[L.Onset + 2 * slot].Y >= 0)) continue;
            var strength = state[L.Onset + 2 * slot].Z;
            Assert.True(strength >= 0.3f && strength <= 1, $"strength of slot {slot}: {strength}");
        }
    }

    // Ported from StarTrailsTests.swift: testCalmRampsOverHalfASecond
    [Fact]
    public void CalmRampsOverHalfASecond()
    {
        var simulation = Run(60, 1);
        simulation.CalmTarget = 1;
        for (var i = 0; i < 15; i++) simulation.Advance(1.0 / 60, 1.0 / 60, Music(1));
        Assert.True(Math.Abs(simulation.Calm - 0.5) <= 0.01, $"{simulation.Calm}");
        for (var i = 0; i < 15; i++) simulation.Advance(1.0 / 60, 1.0 / 60, Music(1));
        Assert.True(Math.Abs(simulation.Calm - 1) <= 1e-9, $"{simulation.Calm}");
    }

    // Ported from StarTrailsTests.swift: testKickEpicentresAvoidTheLyricStage
    [Fact]
    public void KickEpicentresAvoidTheLyricStage()
    {
        var simulation = new StarTrailsSimulation();
        for (var serial = 1; serial <= 2000; serial++)
        {
            var rng = StarTrailsRandom.Seeded(serial, 11);
            var e = simulation.Epicentres(ref rng);
            foreach (var (u, y) in new[] { (e.X, e.Y), (e.Z, e.W) })
            {
                var dx = (u - 0.5) / 0.32;
                var dy = (y - 0.5) / 0.24;
                Assert.True(Math.Sqrt(dx * dx + dy * dy) >= 1.15 - 1e-9, $"serial {serial}");
            }
        }
    }

    /// <summary>
    /// Hits are light, never motion: at the same level, drums change nothing about where the craft fly, where they
    /// point or the paths their trails take; they only light them.
    /// </summary>
    // Ported from StarTrailsTests.swift: testHitsLightTheCraftButNeverMoveThem
    [Fact]
    public void HitsLightTheCraftButNeverMoveThem()
    {
        StarTrailsSimulation plain = new(), drums = new();
        static Vector4[] Path(Vector4[] s)
        {
            var path = new List<Vector4> { s[L.Craft], s[L.Craft + 2] };
            for (var i = 0; i < 2 * L.TrailSamples; i++) path.Add(new Vector4(s[L.Trail + i].X, 0, 0, 0));
            return [.. path];
        }
        float bloom = 0, sparkle = 0, packet = 0;
        for (var i = 1; i <= 240; i++)
        {
            var hits = Music(i / 60.0);
            var still = hits;
            still.Kick = 0; still.Snare = 0; still.Hat = 0;
            still.KickOnsets = 0; still.SnareOnsets = 0; still.HatOnsets = 0;
            still.KickTime = null; still.SnareTime = null; still.HatTime = null;
            plain.Advance(1.0 / 60, 1.0 / 60, still);
            drums.Advance(1.0 / 60, 1.0 / 60, hits);
            Vector4[] a = plain.Pack(Aspect), b = drums.Pack(Aspect);
            Assert.True(Path(a).AsSpan().SequenceEqual(Path(b)), $"Frame {i}: a hit moved a craft or bent its trail");
            for (var c = 0; c < 2; c++)
            {
                bloom = Math.Max(bloom, b[L.Craft + 2 * c + 1].W);
                sparkle = Math.Max(sparkle, b[L.Craft + 2 * c + 1].Z);
                for (var j = 0; j < L.TrailSamples; j++) packet = Math.Max(packet, b[L.Trail + c * L.TrailSamples + j].W);
            }
        }
        Assert.True(bloom > 0.3f, "Kicks bloom the engines");
        Assert.True(sparkle > 0.3f, "Snares sparkle on the craft");
        Assert.True(packet > 0.3f, "Kicks lay bright packets into the trails");
    }

    // Ported from StarTrailsTests.swift: testTrailPulseRespondsToKickAndBassAttacks
    [Fact]
    public void TrailPulseRespondsToKickAndBassAttacks()
    {
        static (float Peak, float Tail) Pulse(Func<StarTrailsSound, StarTrailsSound> change, bool calm = false, double punch = 1)
        {
            var simulation = new StarTrailsSimulation(calm: calm) { Punch = punch };
            var sound = new StarTrailsSound();
            const double dt = 1.0 / 60;
            simulation.Advance(dt, dt, sound);
            sound = change(sound);
            float peak = 0, tail = 0;
            for (var i = 0; i < 60; i++)
            {
                simulation.Advance(dt, dt, sound);
                tail = simulation.Pack(Aspect)[L.Env0].X;
                peak = Math.Max(peak, tail);
            }
            return (peak, tail);
        }
        static StarTrailsSound Kick(StarTrailsSound s) => s with { Kick = 0.9, KickOnsets = 1 };
        static StarTrailsSound Bass(StarTrailsSound s) => s with { Bass = 0.75 };
        Assert.True(Pulse(Kick).Peak > 0.7f);
        Assert.True(Pulse(s => s with { Kick = 0.9 }).Peak > 0.7f, "Band-only kick envelope");
        Assert.True(Pulse(Bass).Peak > 0.6f);
        Assert.True(Pulse(Bass).Tail < 0.01f, "A sustained bass note pulses only at its attack");
        Assert.Equal(0f, Pulse(s => s with { SnareOnsets = 1, HatOnsets = 1, Snare = 1, Hat = 1 }).Peak);
        Assert.Equal(0f, Pulse(Kick, calm: true).Peak);
        Assert.Equal(0f, Pulse(Bass, punch: 0).Peak);
    }

    /// <summary>
    /// The craft glide: little jerk and turn per frame, and a kick-only song whose level pumps with every kick flies as
    /// smoothly as a steady one at the same mean level.
    /// </summary>
    // Ported from StarTrailsTests.swift: testCraftGlideSmoothly
    [Fact]
    public void CraftGlideSmoothly()
    {
        static (double Jerk, double Turn) Flight(Func<double, StarTrailsSound> sound)
        {
            var simulation = new StarTrailsSimulation();
            const double fps = 30.0;
            List<(double X, double Y)>[] points = [[], []];
            List<double>[] headings = [[], []];
            for (var i = 1; i <= (int)(10 * fps); i++)
            {
                simulation.Advance(1 / fps, 1 / fps, sound(i / fps));
                var state = simulation.Pack(Aspect);
                for (var c = 0; c < 2; c++)
                {
                    var craft = state[L.Craft + 2 * c];
                    points[c].Add((craft.X, craft.Y));
                    headings[c].Add(craft.Z);
                }
            }
            double jerk = 0, turn = 0;
            for (var c = 0; c < 2; c++)
            {
                var p = points[c];
                var h = headings[c];
                for (var i = 1; i < p.Count - 2; i++)
                {
                    var x = p[i + 2].X - 3 * p[i + 1].X + 3 * p[i].X - p[i - 1].X;
                    var y = p[i + 2].Y - 3 * p[i + 1].Y + 3 * p[i].Y - p[i - 1].Y;
                    jerk = Math.Max(jerk, Math.Sqrt(x * x + y * y) * fps * fps * fps);
                }
                for (var i = 1; i < h.Count; i++)
                {
                    var d = h[i] - h[i - 1];
                    turn = Math.Max(turn, Math.Abs(d - 2 * Math.PI * Math.Round(d / (2 * Math.PI), MidpointRounding.AwayFromZero)));
                }
            }
            return (jerk, turn * 180 / Math.PI);
        }
        var kicks = Flight(t =>
        {
            var s = new StarTrailsSound();
            var n = Math.Floor(t * 2);
            s.KickOnsets = (uint)(n + 1); s.KickTime = n / 2;
            s.Kick = Math.Exp(-(t - n / 2) / 0.2);
            s.Level = 0.2 + 0.6 * s.Kick; // the level pumps with every kick
            return s;
        });
        var steady = Flight(_ => new StarTrailsSound { Level = 0.42 });
        var report = string.Format(CultureInfo.InvariantCulture, "kick-only: jerk {0:F3} H/s³, turn {1:F3}°/frame; steady: {2:F3}, {3:F3}",
                                   kicks.Jerk, kicks.Turn, steady.Jerk, steady.Turn);
        Assert.True(kicks.Jerk < 1.5, report);
        Assert.True(kicks.Turn < 1.0, report);
        Assert.True(kicks.Jerk < 1.15 * steady.Jerk + 0.05, $"Kicks do not roughen the flight: {report}");
        Assert.True(kicks.Turn < 1.15 * steady.Turn + 0.02, $"Kicks do not swing the heading: {report}");
    }

    // Ported from StarTrailsTests.swift: testSharedSimulationAdvancesOncePerSceneTime
    [Fact]
    public void SharedSimulationAdvancesOncePerSceneTime()
    {
        var shared = new StarTrailsSimulations();
        var response = new VisualizerResponse();
        var frame = new AudioSnapshot();
        var simulation = shared.Advance(1, 10, frame, response, 1);
        shared.Advance(1 + 0.7 / 30, 10 + 1.0 / 30, frame, response, 1);
        double wall = simulation.Wall, motion = simulation.Motion;
        Assert.True(Math.Abs(wall - 1.0 / 30) <= 1e-9, $"{wall}");
        Assert.True(Math.Abs(motion - StarTrailsSimulation.Substep * 264 - 1.0 / 30) <= 1e-9, "Motion is scene time / 0.7");
        // A second display drawing the same frame does not advance it again.
        shared.Advance(1 + 0.7 / 30, 10 + 1.0 / 30 + 0.001, frame, response, 1);
        Assert.Equal(wall, simulation.Wall);
        // A rebase of the scene clock is not motion.
        shared.Advance(0, 10 + 2.0 / 30, frame, response, 1);
        Assert.Equal(motion, simulation.Motion);
        Assert.True(Math.Abs(simulation.Wall - (wall + 1.0 / 30)) <= 1e-9);
    }

    // Not a Mac test: the static path's snapshot is a fresh simulation run to the given time, packed whole.
    [Fact]
    public void SnapshotRunsAFreshSimulationToTheTime()
    {
        var frame = new AudioSnapshot { Level = 0.5f, Beat = 0.8f, Mid = 0.4f };
        var snapshot = StarTrailsSimulation.Snapshot(frame, 2, 1, calm: false, Aspect, RenderPalette.Earth);
        Assert.Equal(L.Float4Count, snapshot.Length);
        Assert.DoesNotContain(snapshot, v => !(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W)));
        Assert.Equal(snapshot, StarTrailsSimulation.Snapshot(frame, 2, 1, calm: false, Aspect, RenderPalette.Earth));
        Assert.Equal(Packed(Sky(RenderPalette.Earth).Tints), Slice(snapshot, L.SkyTints, L.Word));
        var manual = new StarTrailsSimulation(sky: Sky(RenderPalette.Earth));
        for (var i = 0; i < 60; i++) manual.Advance(1.0 / 30, 1.0 / 30, StarTrailsSound.From(frame, 1, (i + 1) / 30.0));
        Assert.Equal(manual.Pack(Aspect), snapshot);
        Assert.NotEqual(snapshot, StarTrailsSimulation.Snapshot(frame, 3, 1, calm: false, Aspect, RenderPalette.Earth));
    }

    /// <summary>
    /// The camera follows the craft: the scenery drifts steadily against the craft's mean heading, with parallax (clouds
    /// slowest, then dust stars, bright stars, asteroids), measured in the state.
    /// </summary>
    // Ported from StarTrailsTests.swift: testSceneryDriftsAgainstTheCraftHeading (its state half; the on-screen star
    // tracking needs the shader)
    [Fact]
    public void SceneryDriftsAgainstTheCraftHeading()
    {
        const double axis = StarTrailsSimulation.Axis;
        var simulation = Run(30, 2);
        double meanX = 0, meanY = 0;
        var previous = simulation.Pack(Aspect);
        static double Speed(float now, float before)
        {
            var d = (double)now - before;
            return 30 * (d - 256 * Math.Round(d / 256, MidpointRounding.AwayFromZero));
        }
        for (var i = 1; i <= 300; i++)
        {
            // 10 s: about three orbits.
            simulation.Advance(1.0 / 30, 1.0 / 30, Music(2 + i / 30.0));
            var s = simulation.Pack(Aspect);
            for (var c = 0; c < 2; c++)
            {
                meanX += Math.Cos(s[L.Craft + 2 * c].Z);
                meanY += Math.Sin(s[L.Craft + 2 * c].Z);
            }
            // Each layer scrolls at its own constant speed; far layers slower.
            var cloud = Speed(s[L.Clock].Z, previous[L.Clock].Z);
            var dust = Speed(s[L.Hdr1].W, previous[L.Hdr1].W);
            var star = Speed(s[L.Hdr1].Z, previous[L.Hdr1].Z);
            Assert.True(Math.Abs(cloud - StarTrailsSimulation.CloudDrift) <= 2e-3, $"clouds, frame {i}: {cloud}");
            Assert.True(Math.Abs(dust - StarTrailsSimulation.DustDrift) <= 2e-3, $"dust, frame {i}: {dust}");
            Assert.True(Math.Abs(star - StarTrailsSimulation.StarDrift) <= 2e-3, $"stars, frame {i}: {star}");
            // Asteroids drift the same way, faster than every star.
            for (var k = 0; k < L.Asteroids; k++)
            {
                var dx = (double)(s[L.Asteroid + 2 * k].X - previous[L.Asteroid + 2 * k].X);
                var dy = (double)(s[L.Asteroid + 2 * k].Y - previous[L.Asteroid + 2 * k].Y);
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (!(length < 0.1) || s[L.Asteroid + 2 * k + 1].X != previous[L.Asteroid + 2 * k + 1].X) continue; // respawned
                var off = Math.Atan2(dy, dx) - (axis + Math.PI);
                off -= 2 * Math.PI * Math.Round(off / (2 * Math.PI), MidpointRounding.AwayFromZero);
                Assert.True(Math.Abs(off) < 0.01, $"asteroid {k}, frame {i}: {off}");
                Assert.True(30 * length > StarTrailsSimulation.StarDrift, $"asteroid {k}, frame {i}");
            }
            previous = s;
        }
        Assert.True(StarTrailsSimulation.CloudDrift < StarTrailsSimulation.DustDrift);
        Assert.True(StarTrailsSimulation.DustDrift < StarTrailsSimulation.StarDrift);
        Assert.True(Math.Abs(Math.Atan2(meanY, meanX) - axis) <= 1.5 * Math.PI / 180, "The flight axis is the craft's mean heading");
    }

    // MARK: Sky colours

    private static StarTrailsSky Sky(AlbumWallpaperPalette palette) => StarTrailsSky.Album(palette);

    private static Vector4[] Packed(ReadOnlySpan<Double4> tints)
    {
        var result = new Vector4[tints.Length];
        for (var i = 0; i < tints.Length; i++) result[i] = tints[i].ToVector4();
        return result;
    }

    // Ported from StarTrailsTests.swift: testAlbumSkyUsesAllNineCoverSamplesWithoutMovingTheirHues
    [Fact]
    public void AlbumSkyUsesAllNineCoverSamplesWithoutMovingTheirHues()
    {
        var grid = Enumerable.Range(0, 9).Select(i => new Double3(i / 9.0, 0.84, 0.8)).ToArray();
        var cover = new AlbumWallpaperPalette(grid);
        var album = StarTrailsSky.Album(cover);
        Assert.Equal(9, album.Tints.Length);
        static (double, double, double) Normalised(double x, double y, double z)
        {
            var length = Math.Sqrt(x * x + y * y + z * z);
            return (x / length, y / length, z / length);
        }
        for (var i = 0; i < 9; i++)
        {
            var sample = cover.ArtworkStops[i];
            var tint = album.Tints[i];
            var (lx, ly, lz) = Normalised(Perceptual.Linear(sample.X), Perceptual.Linear(sample.Y), Perceptual.Linear(sample.Z));
            var (tx, ty, tz) = Normalised(tint.X, tint.Y, tint.Z);
            Assert.True(Math.Sqrt((lx - tx) * (lx - tx) + (ly - ty) * (ly - ty) + (lz - tz) * (lz - tz)) < 1e-6, $"tint {i}");
            Assert.True(tint.X * 0.2126 + tint.Y * 0.7152 + tint.Z * 0.0722 <= 1, $"tint {i}");
            Assert.Equal(1, tint.W);
        }
        foreach (var neutral in Sky(RenderPalette.Bw).Tints)
        {
            Assert.True(Math.Abs(neutral.X - neutral.Y) <= 1e-7);
            Assert.True(Math.Abs(neutral.X - neutral.Z) <= 1e-7);
        }
    }

    /// <summary>The original violet sky is exactly the reference's, settled or after a fade back.</summary>
    // Ported from StarTrailsTests.swift: testOriginalSkyIsUnchanged
    [Fact]
    public void OriginalSkyIsUnchanged()
    {
        Double3[] reference =
        [
            new(0.031, 0.008, 0.075), new(0.055, 0.035, 0.112), new(0.082, 0.043, 0.145), new(0.118, 0.059, 0.188),
            new(0.180, 0.078, 0.259), new(0.235, 0.110, 0.310), new(0.298, 0.141, 0.353), new(0.360, 0.165, 0.392),
            new(0.647, 0.282, 0.565), new(0.831, 0.369, 0.706), new(1.0, 0.97, 1.0),
        ];
        Assert.Equal(reference, StarTrailsSky.OriginalColours);
        var expected = reference.Select(c => new Vector4((float)c.X, (float)c.Y, (float)c.Z, 0)).ToArray();
        var simulation = new StarTrailsSimulation();
        Assert.Equal(expected, Slice(simulation.Pack(Aspect), L.Sky, L.SkyTints));
        simulation.SkyTarget = Sky(RenderPalette.Vivid);
        for (var i = 0; i < 60; i++) simulation.Advance(1.0 / 30, 1.0 / 30, new StarTrailsSound());
        simulation.SkyTarget = StarTrailsSky.Original;
        for (var i = 0; i < 360; i++) simulation.Advance(1.0 / 30, 1.0 / 30, new StarTrailsSound());
        var restored = simulation.Pack(Aspect);
        Assert.Equal(expected, Slice(restored, L.Sky, L.SkyTints)); // settled back exactly
        Assert.Equal(Packed(StarTrailsSky.Original.Tints), Slice(restored, L.SkyTints, L.Word));
    }

    /// <summary>
    /// A new cover's sky crossfades over about 2.5 s: no pop at the start, small steps, settled by 3 s, the same at any
    /// frame rate.
    /// </summary>
    // Ported from StarTrailsTests.swift: testSkyCrossfadesSmoothly
    [Fact]
    public void SkyCrossfadesSmoothly()
    {
        StarTrailsSky from = Sky(RenderPalette.Blue), to = Sky(RenderPalette.Vivid);
        List<Vector4[]> Fade(double fps, double seconds)
        {
            var simulation = new StarTrailsSimulation(sky: from) { SkyTarget = to };
            var frames = new List<Vector4[]>();
            for (var i = 0; i < (int)(seconds * fps); i++)
            {
                simulation.Advance(1 / fps, 1 / fps, new StarTrailsSound());
                frames.Add(Slice(simulation.Pack(Aspect), L.SkyTints, L.Word));
            }
            return frames;
        }
        static float Distance(Vector4[] a, Vector4[] b) => a.Zip(b, (x, y) => (x - y).Length()).DefaultIfEmpty(0).Max();
        var start = Packed(from.Tints);
        var end = Packed(to.Tints);
        var total = Distance(start, end);
        Assert.True(total > 0.2f, $"{total}");
        var frames = Fade(60, 4);
        Assert.True(Distance(frames[0], start) < 0.001f * total, "starts without a pop");
        float largest = 0;
        for (var i = 1; i < frames.Count; i++) largest = Math.Max(largest, Distance(frames[i], frames[i - 1]));
        Assert.True(largest < 0.015f * total, $"small steps every frame: {largest}");
        var progress = 1 - Distance(frames[59], end) / total;
        Assert.True(progress >= 0.2f && progress <= 0.8f, $"about half way after 1 s: {progress}");
        Assert.True(Distance(frames[179], end) < 0.05f * total, "nearly there after 3 s");
        List<Vector4[]> slow = Fade(30, 1.5), fast = Fade(120, 1.5);
        Assert.True(Distance(slow[44], fast[179]) < 0.01f * total, "frame-rate independent");
    }

    /// <summary>The shared simulation follows the cover it is given.</summary>
    // Ported from StarTrailsTests.swift: testStarTrailsColoursDefaultToTheAlbum (the shared-simulation half; the stored
    // "Colours" preference is not in Core)
    [Fact]
    public void StarTrailsColoursDefaultToTheAlbum()
    {
        var shared = new StarTrailsSimulations();
        var frame = new AudioSnapshot();
        var simulation = shared.Advance(1, 1, frame, new VisualizerResponse(), 1, palette: RenderPalette.Earth);
        Assert.True(simulation.SkyTarget == Sky(RenderPalette.Earth), "starts on the cover's sky");
        Assert.Equal(Packed(Sky(RenderPalette.Earth).Tints), Slice(simulation.Pack(Aspect), L.SkyTints, L.Word)); // no fade in from violet
        shared.Advance(2, 2, frame, new VisualizerResponse(), 1, palette: null);
        Assert.True(simulation.SkyTarget == StarTrailsSky.Original, "no cover: the violet sky");
    }
}
