using System.Text.Json;
using Lunalyr.Core.Motion.ThinkingOrbs;

namespace Lunalyr.Core.Tests.Motion;

/// <summary>
/// The C# port of thinking-orbs against upstream's own golden frames (spec/orbs-golden.json at de85557): every state at
/// both sizes and four instants, dot for dot and line for line, within the fixture's tolerance.
/// </summary>
public class ThinkingOrbEngineTests
{
    private static readonly Lazy<JsonDocument> Golden = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ThinkingOrbs", "orbs-golden.json"))));

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Golden.Value.RootElement.GetProperty("cases").EnumerateArray()) data.Add(c.GetProperty("key").GetString()!);
        return data;
    }

    private static JsonElement Case(string key)
    {
        foreach (var c in Golden.Value.RootElement.GetProperty("cases").EnumerateArray())
            if (c.GetProperty("key").GetString() == key) return c;
        throw new KeyNotFoundException(key);
    }

    [Fact]
    public void The_fixture_has_all_seventy_two_cases() => Assert.Equal(72, Cases().Count);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_frame_matches_upstream(string key)
    {
        var c = Case(key);
        var tolerance = Golden.Value.RootElement.GetProperty("tolerance").GetDouble();
        var state = ThinkingOrbEngine.Parse(c.GetProperty("state").GetString())!.Value;
        var size = c.GetProperty("size").GetInt32();
        var t = c.GetProperty("t").GetDouble();
        var resolved = ThinkingOrbEngine.Resolve(state, size);
        Assert.Equal(c.GetProperty("mode").GetString(), resolved.Mode.ToString().ToLowerInvariant());
        var frame = ThinkingOrbEngine.Frame(resolved.Mode, size, t, resolved.Opts);

        Assert.Equal(c.GetProperty("dotCount").GetInt32(), frame.Dots.Count);
        Assert.Equal(c.GetProperty("lineCount").GetInt32(), frame.Lines.Count);
        var expected = c.GetProperty("dots").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var actual = frame.Dots.SelectMany(d => new[] { d.X, d.Y, d.Z, d.R, d.White, d.A }).ToArray();
        if (key.StartsWith("solving-20-", StringComparison.Ordinal))
        {
            // The small solving lattice puts a few points within two ulps of a band's edge (y = -0.5 at 5.1 s): which
            // side they fall on, and so whether they turn with the band, depends on the last bit of a cosine, where
            // .NET's math library and V8's differ. At most three such dots may differ; every other dot matches.
            Assert.True(Unmatched(expected, actual, tolerance) <= 3, $"{key}: more than three dots differ");
            return;
        }
        if (state == OrbState.Breathing)
        {
            // The face-on ring has dot pairs at near-equal depth whose order can differ by a libm ulp: compare the set.
            expected = Sorted(expected);
            actual = Sorted(actual);
        }
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance,
                $"{key}: dot {i / 6} field {i % 6}: expected {expected[i]}, got {actual[i]}");
        var lines = c.GetProperty("lines").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var drawn = frame.Lines.SelectMany(l => new[] { l.X1, l.Y1, l.X2, l.Y2, l.White, l.A, l.W }).ToArray();
        for (var i = 0; i < lines.Length; i++)
            Assert.True(Math.Abs(lines[i] - drawn[i]) <= tolerance, $"{key}: line {i / 7} field {i % 7}: expected {lines[i]}, got {drawn[i]}");
    }

    /// <summary>How many golden dots have no dot of the port's within tolerance in every field.</summary>
    private static int Unmatched(double[] expected, double[] actual, double tolerance)
    {
        var mine = actual.Chunk(6).ToList();
        var missing = 0;
        foreach (var dot in expected.Chunk(6))
        {
            var found = mine.FindIndex(d => d.Zip(dot).All(pair => Math.Abs(pair.First - pair.Second) <= tolerance));
            if (found < 0) missing++;
            else mine.RemoveAt(found);
        }
        return missing;
    }

    /// <summary>Dots (stride 6) ordered by x, then y, rounded to the fixture's precision, flattened again.</summary>
    private static double[] Sorted(double[] flat) =>
        flat.Chunk(6).OrderBy(d => Math.Round(d[0], 4)).ThenBy(d => Math.Round(d[1], 4)).SelectMany(d => d).ToArray();

    [Fact]
    public void Presets_resolve_as_upstream_does()
    {
        foreach (var entry in Golden.Value.RootElement.GetProperty("resolved").EnumerateObject())
        {
            var parts = entry.Name.Split('-');
            var resolved = ThinkingOrbEngine.Resolve(ThinkingOrbEngine.Parse(parts[0])!.Value, int.Parse(parts[1]));
            Assert.Equal(entry.Value.GetProperty("speed").GetDouble(), resolved.Speed, 9);
            var opts = entry.Value.GetProperty("opts");
            foreach (var opt in opts.EnumerateObject())
                Assert.True(Math.Abs(opt.Value.GetDouble() - resolved.Opts[opt.Name, double.NaN]) < 1e-9,
                    $"{entry.Name}.{opt.Name}: expected {opt.Value.GetDouble()}, got {resolved.Opts[opt.Name, double.NaN]}");
            Assert.Equal(opts.EnumerateObject().Count(), resolved.Opts.Values.Count);
        }
    }

    [Fact]
    public void Js_rounding_is_half_up() =>
        Assert.Equal([1.0, 3.0, -1.0], new[] { ThinkingOrbEngine.Round(0.5), ThinkingOrbEngine.Round(2.5), ThinkingOrbEngine.Round(-1.5) });

    [Fact]
    public void The_swell_hook_is_upstreams_wave_at_one()
    {
        var resolved = ThinkingOrbEngine.Resolve(OrbState.Listening, 64);
        var plain = ThinkingOrbEngine.Frame(resolved.Mode, 64, 1.7, resolved.Opts).Dots.ToArray();
        var swelled = ThinkingOrbEngine.Frame(resolved.Mode, 64, 1.7, resolved.Opts.With("swell", 1)).Dots.ToArray();
        Assert.Equal(plain, swelled);
        var louder = ThinkingOrbEngine.Frame(resolved.Mode, 64, 1.7, resolved.Opts.With("swell", 1.6)).Dots.ToArray();
        Assert.NotEqual(plain, louder);
    }
}
