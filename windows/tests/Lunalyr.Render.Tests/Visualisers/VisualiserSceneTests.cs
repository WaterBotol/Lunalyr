using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Visualisers;
using Lunalyr.Render.Visualisers;

namespace Lunalyr.Render.Tests.Visualisers;

/// <summary>Every native scene drawn on WARP from the Mac's render-test inputs.</summary>
public sealed class VisualiserSceneTests : IDisposable
{
    private const int Width = 160, Height = 90;
    private readonly VisualiserStill _still = new();

    public void Dispose() => _still.Dispose();

    public static TheoryData<VisualizerStyle> Styles => [.. VisualizerStyles.All];

    private static int Difference(byte[] a, byte[] b)
    {
        var sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum;
    }

    private static int Warmth(byte[] pixels)
    {
        var sum = 0;
        for (var i = 0; i < pixels.Length; i += 4) sum += pixels[i + 2] - pixels[i];
        return sum;
    }

    /// <summary>A sine through the analyser, as the Mac's <c>tone(_:into:)</c>.</summary>
    internal static AudioSnapshot Tone(double frequency, AudioAnalyzer? analyzer = null)
    {
        analyzer ??= new AudioAnalyzer();
        const double rate = 48_000;
        const int count = 2048;
        var pcm = new float[count];
        for (var pass = 0; pass < 18; pass++)
        {
            for (var i = 0; i < count; i++) pcm[i] = (float)(0.25 * Math.Sin(2 * Math.PI * frequency * (pass * count + i) / rate));
            analyzer.Append(pcm, rate);
        }
        return analyzer.Latest();
    }

    private static AlbumWallpaperPalette Grid(double hue) => new([.. Enumerable.Repeat(new Double3(hue, 1, 0.8), 9)]);

    // Every scene's shader is built into this assembly.
    [Fact]
    public void EverySceneHasItsShader()
    {
        foreach (var style in VisualizerStyles.All) Assert.True(VisualiserGpu.Has(style.Shader()), style.Title());
        Assert.True(VisualiserGpu.Has("Fullscreen"));
        Assert.True(VisualiserGpu.Has("ExposureMeasure"));
    }

    // Ported from VisualizerTests.swift: testEveryImportedSceneRendersAndChangesWithItsSpectrum
    [Theory]
    [MemberData(nameof(Styles))]
    public void EveryImportedSceneRendersAndChangesWithItsSpectrum(VisualizerStyle style)
    {
        byte[] Render(in AudioSnapshot sound, double sensitivity = 1, double time = 12, AlbumWallpaperPalette? palette = null) =>
            _still.Render(style, sound, Width, Height, time, sensitivity, palette);

        var quiet = Render(new AudioSnapshot());
        var idle = Render(AmbientSpectrum.At(12));
        var later = Render(AmbientSpectrum.At(16), time: 16);
        var visible = 0;
        for (var i = 0; i < idle.Length; i++) if (i % 4 != 3 && idle[i] > 16) visible++;
        Assert.True(visible > Width * Height / 100, $"{style.Title()} has a visible idle scene");
        Assert.NotEqual(idle, later); // moves during silence
        var sound = new AudioSnapshot();
        for (var i = 0; i < 64; i++) sound.Bands[i] = (float)(0.4 + 0.35 * Math.Sin(i * 0.3));
        for (var i = 0; i < 8; i++) sound.Components[i] = 0.6f;
        sound.Bass = 0.7f; sound.Mid = 0.5f; sound.Level = 0.7f;
        if (style.IsWavefield()) sound = Tone(220);
        var playing = Render(sound);
        Assert.True(Difference(quiet, playing) > 1000, style.Title());
        Assert.True(Warmth(Render(sound, palette: Grid(0))) > Width * Height, $"{style.Title()} takes a red cover");
        Assert.True(Warmth(Render(sound, palette: Grid(2.0 / 3))) < -Width * Height, $"{style.Title()} takes a blue cover");
        var gentle = Render(sound, sensitivity: 0.3);
        var strong = Render(sound, sensitivity: 3);
        Assert.True(Difference(gentle, strong) > Width * Height, $"{style.Title()} sensitivity must visibly change the same input");
        if (style is VisualizerStyle.ColourStrata or VisualizerStyle.Overprint)
        {
            HashSet<int> Changed(Action<AudioSnapshot[]> set)
            {
                var isolated = new[] { new AudioSnapshot() };
                set(isolated);
                var image = Render(isolated[0]);
                var changed = new HashSet<int>();
                for (var offset = 0; offset < image.Length; offset += 4)
                    for (var c = 0; c < 3; c++)
                        if (Math.Abs(image[offset + c] - quiet[offset + c]) > 2) { changed.Add(offset); break; }
                return changed;
            }
            var bass = Changed(s => s[0].Bass = 0.85f);
            var mids = Changed(s => s[0].Mid = 0.85f);
            var highs = Changed(s => s[0].Treble = 0.85f);
            foreach (var region in new[] { bass, mids, highs }) Assert.True(region.Count > 50, $"{style.Title()} responds to all three bands");
            Assert.True(SymmetricDifference(bass, mids) > 50);
            Assert.True(SymmetricDifference(bass, highs) > 50);
            Assert.True(SymmetricDifference(mids, highs) > 50);
        }
        if (style == VisualizerStyle.Tunnel)
        {
            var waveform = new AudioSnapshot();
            for (var i = 0; i < 256; i++) waveform.Waveform[i] = (float)(0.3 + 0.2 * Math.Sin(i * 0.1));
            Assert.NotEqual(Render(waveform, sensitivity: 0.3), Render(waveform, sensitivity: 3));
        }
        if (style.IsWavefield())
        {
            var high = Render(Tone(1100));
            Assert.True(Difference(playing, high) > 10_000, "Changing the note must redraw the field");
            if (style == VisualizerStyle.WavefieldSphere) Assert.Equal(new byte[] { 0, 0, 0, 255 }, playing[..4]); // Outside the sphere stays black
        }
        if (style == VisualizerStyle.CurtainAurora) CurtainsFollowTheFrequencyAxis(sound, quiet);
    }

    private static int SymmetricDifference(HashSet<int> a, HashSet<int> b)
    {
        var x = new HashSet<int>(a);
        x.SymmetricExceptWith(b);
        return x.Count;
    }

    private void CurtainsFollowTheFrequencyAxis(AudioSnapshot sound, byte[] quiet)
    {
        byte[] Render(in AudioSnapshot s, double sensitivity = 1) => _still.Render(VisualizerStyle.CurtainAurora, s, Width, Height, 12, sensitivity);
        sound.Bass = 0.45f;
        for (var i = 0; i < 16; i++) sound.Bands[i] = 0.45f;
        var medium = Render(sound, 3);
        sound.Bass = 0.9f;
        for (var i = 0; i < 16; i++) sound.Bands[i] = 0.9f;
        var loud = Render(sound, 3);
        Assert.True(Difference(medium, loud) / (double)(Width * Height * 3) > 0.75,
            "High sensitivity must preserve the difference between medium and loud bass");
        double Position(byte[] pixels, out double change)
        {
            change = 0;
            var weighted = 0.0;
            for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                {
                    var start = (y * Width + x) * 4;
                    var delta = 0.0;
                    for (var c = 0; c < 3; c++) delta += Math.Abs(pixels[start + c] - quiet[start + c]);
                    change += delta;
                    weighted += delta * (x + 0.5) / Width;
                }
            return weighted / Math.Max(1, change);
        }
        foreach (var centre in new[] { 8.0, 31.0, 54.0 })
        {
            var note = new AudioSnapshot();
            for (var i = 0; i < 64; i++) note.Bands[i] = (float)(0.85 * Math.Exp(-Math.Pow((i - centre) / 2.0, 2)));
            var position = Position(Render(note), out var change);
            Assert.True(change > 3000);
            Assert.True(Math.Abs(position - centre / 63.0) <= 0.12, "The responsive section stays at this note's position");
        }
        var positions = new List<double>();
        foreach (var frequency in new[] { 100.0, 1000, 8000 })
        {
            var analyzer = new AudioAnalyzer();
            analyzer.SetAutoGainEnabled(false);
            var position = Position(Render(Tone(frequency, analyzer)), out _);
            var expected = (64 * Math.Log(frequency / 30) / Math.Log(16_000.0 / 30) - 0.5) / 63;
            Assert.True(Math.Abs(position - expected) <= 0.15, $"{frequency} Hz");
            positions.Add(position);
        }
        Assert.True(positions[0] < positions[1]);
        Assert.True(positions[1] < positions[2]);
    }

    // Ported from VisualizerClockAndDeviceTests.swift: testColourRichnessChangesTheNewerScenesWithoutArtwork
    [Theory]
    [InlineData(VisualizerStyle.StarTrails)]
    [InlineData(VisualizerStyle.ColourStrata)]
    [InlineData(VisualizerStyle.Overprint)]
    public void ColourRichnessChangesTheNewerScenesWithoutArtwork(VisualizerStyle style)
    {
        const int width = 96, height = 54;
        double Chroma(double vividness)
        {
            var sound = new AudioSnapshot { Bass = 0.6f, Mid = 0.5f, Treble = 0.5f, Level = 0.5f };
            var pixels = _still.Render(style, sound, width, height, 8, 1, null, vividness);
            var sum = 0.0;
            for (var i = 0; i < pixels.Length; i += 4)
                sum += Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) - Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
            return sum / (width * height);
        }
        double muted = Chroma(0.4), rich = Chroma(1.8);
        Assert.True(rich > muted * 1.3 + 0.5, $"{style.Title()} responds to colour richness without artwork");
    }

    // Ported from VisualizerClockAndDeviceTests.swift: testFosforaShaderReadsItsOwnBassAndMidRegisters
    [Fact]
    public void FosforaShaderReadsItsOwnBassAndMidRegisters()
    {
        const int size = 96;
        float Luminance(VisualiserStill.VizUniformsAdjust adjust) =>
            MeanLuminance(_still.Render(VisualizerStyle.FosforaAurora, new AudioSnapshot(), size, size, 4, adjust: adjust));
        var quiet = Luminance((ref VizUniforms _) => { });
        var broadBass = Luminance((ref VizUniforms u) => u.Bass = 1);
        var broadMid = Luminance((ref VizUniforms u) => u.Mid = 1);
        var ownBass = Luminance((ref VizUniforms u) => u.P[3] = 1);
        var ownMid = Luminance((ref VizUniforms u) => u.P[4] = 1);
        Assert.True(Math.Abs(broadBass - quiet) <= 0.0005, "The 30-250 Hz register no longer drives a ribbon");
        Assert.True(Math.Abs(broadMid - quiet) <= 0.0005, "Nor does the 250 Hz-2 kHz one");
        // One thin ribbon on a dark frame: a several-fold rise in the mean.
        Assert.True(ownBass > quiet * 3 + 0.001, "60-150 Hz lights the bass ribbon");
        Assert.True(ownMid > quiet * 3 + 0.001, "400-1200 Hz lights the mid ribbon");
    }

    /// <summary>Mean relative luminance of BGRA pixels, as the Mac's GPUTestSupport.meanLuminance.</summary>
    internal static float MeanLuminance(byte[] bgra)
    {
        double sum = 0;
        for (var i = 0; i < bgra.Length; i += 4) sum += ExposureLimiter.Luminance(bgra[i + 2] / 255f, bgra[i + 1] / 255f, bgra[i] / 255f);
        return (float)(sum / (bgra.Length / 4));
    }
}
