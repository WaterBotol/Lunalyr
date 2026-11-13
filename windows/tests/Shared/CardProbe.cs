// Shared helpers for the lyric card tests, ported from the Mac's MixProbe (LyricCardMixTests.swift): synthetic tones,
// the one-line card the Mac's tests use, and analysis of samples (decoded, never played).

using Lunalyr.Core.Cards;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;

namespace Lunalyr.Tests.Shared;

public static class CardProbe
{
    /// <summary>A sine at 48 kHz, interleaved stereo (the Mac's <c>stereoSine</c>).</summary>
    public static float[] StereoSine(double frequency, double amplitude, double seconds, double rate = 48_000)
    {
        var frames = (int)(seconds * rate);
        var output = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var x = (float)(Math.Sin(i * 2 * Math.PI * frequency / rate) * amplitude);
            output[2 * i] = x;
            output[2 * i + 1] = x;
        }
        return output;
    }

    /// <summary>A mono sine at <paramref name="rate"/> (as a file the Mac's <c>tone</c> writes holds it).</summary>
    public static float[] MonoSine(double frequency, double amplitude, double seconds, double rate = 44_100)
    {
        var frames = (int)(rate * seconds);
        var output = new float[frames];
        for (var i = 0; i < frames; i++) output[i] = (float)(Math.Sin(i * 2 * Math.PI * frequency / rate) * amplitude);
        return output;
    }

    /// <summary>Amplitude of <paramref name="frequency"/> in <paramref name="samples"/>[from..to) (Goertzel).</summary>
    public static double Amplitude(double frequency, IReadOnlyList<float> samples, int from, int to, double rate = 48_000)
    {
        var w = 2 * Math.PI * frequency / rate;
        var coefficient = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        for (var i = from; i < to; i++)
        {
            var s = samples[i] + coefficient * s1 - s2;
            s2 = s1;
            s1 = s;
        }
        var power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(0, power)) / (to - from);
    }

    public static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-12));

    /// <summary>RMS of <paramref name="samples"/>[from..to) in dBFS.</summary>
    public static double RmsDb(IReadOnlyList<float> samples, int from, int to)
    {
        if (to <= from) return -240;
        double sum = 0;
        for (var i = from; i < to; i++) sum += (double)samples[i] * samples[i];
        return 10 * Math.Log10(Math.Max(sum / (to - from), 1e-24));
    }

    /// <summary>The left channel of interleaved stereo.</summary>
    public static float[] Left(IReadOnlyList<float> stereo)
    {
        var left = new float[stereo.Count / 2];
        for (var i = 0; i < left.Length; i++) left[i] = stereo[2 * i];
        return left;
    }

    /// <summary>
    /// A card of word-timed lines (the Mac's <c>MixProbe.content</c>): by default one line from 10.0 s to 11.6 s ("Hold
    /// on tight tonight"), lasting about 1.9 s with its rest, square, with a cover and Reduce Motion.
    /// </summary>
    public static LyricCardContent Content(params (double Start, double End)[] lines)
    {
        if (lines.Length == 0) lines = [(10, 11.6)];
        string[] texts = ["Hold", "on", "tight", "tonight"];
        var pages = new List<CardPage>();
        for (var index = 0; index < lines.Length; index++)
        {
            var (start, end) = lines[index];
            var step = (end - start) / texts.Length;
            var words = texts.Select((w, i) => new LyricWord(w, start + i * step, start + i * step + step * 0.9)).ToList();
            var line = new LyricLine { Start = start, End = end, Text = string.Join(' ', texts), Words = words, WordTimed = true };
            pages.Add(new CardPage(texts, CardAnimation.Create(line, CompositionKind.Editorial, index)));
        }
        return new LyricCardContent
        {
            Pages = pages, Title = "A Song", Artist = "An Artist", HasArtwork = true, Aspect = CardAspect.Square, ReduceMotion = true,
        };
    }

    /// <summary>
    /// The Mac's "Hold on tight tonight" card with its words 0.3 s apart (LyricCardBackgroundTests.content): 10.0 to
    /// 11.6 s, square.
    /// </summary>
    public static LyricCardContent SpacedContent()
    {
        var words = "Hold on tight tonight".Split(' ').Select((w, i) => new LyricWord(w, 10 + i * 0.3, 10.28 + i * 0.3)).ToList();
        var line = new LyricLine { Start = 10, End = 11.6, Text = "Hold on tight tonight", Words = words, WordTimed = true };
        return new LyricCardContent
        {
            Pages = [new CardPage(words.Select(w => w.Text).ToList(), CardAnimation.Create(line, CompositionKind.Editorial, 0))],
            Title = "A Song", Artist = "An Artist", HasArtwork = true, Aspect = CardAspect.Square, ReduceMotion = true,
        };
    }

    /// <summary>300 Hz for the first second, 1200 Hz for the next two, at 48 kHz mono (the Mac's <c>twoPartTone</c>).</summary>
    public static float[] TwoPartTone()
    {
        var samples = new float[48_000 * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            var f = i < 48_000 ? 300.0 : 1200.0;
            samples[i] = (float)(Math.Sin(i * 2 * Math.PI * f / 48_000) * 0.3);
        }
        return samples;
    }
}

/// <summary>
/// An in-memory <see cref="IAudioDecoder"/>: each "file" is a list of samples at its own rate and channel count,
/// converted to 48 kHz stereo as the platform's decoder delivers it. Nothing touches a disk or a device.
/// </summary>
public sealed class MemoryAudioDecoder : IAudioDecoder
{
    private readonly Dictionary<string, (float[] Samples, int Channels, double Rate)> _files = new(StringComparer.Ordinal);

    public MemoryAudioDecoder Add(string path, float[] samples, int channels = 1, double rate = 48_000)
    {
        _files[path] = (samples, channels, rate);
        return this;
    }

    public double? AudioDuration(string path) =>
        _files.TryGetValue(path, out var file) ? file.Samples.Length / (double)file.Channels / file.Rate : null;

    public Task<float[]?> DecodeAsync(string path, double from = 0, double? seconds = null, CancellationToken cancellationToken = default)
    {
        if (!_files.TryGetValue(path, out var file)) return Task.FromResult<float[]?>(null);
        var stereo = LyricCardMix.ToStereo48k(file.Samples, file.Channels, file.Rate);
        var start = Math.Min(stereo.Length / 2, (int)Math.Round(Math.Max(0, from) * LyricCardMix.SampleRate));
        var count = seconds is { } s ? Math.Min(stereo.Length / 2 - start, (int)Math.Round(Math.Max(0, s) * LyricCardMix.SampleRate)) : stereo.Length / 2 - start;
        return Task.FromResult<float[]?>(stereo.AsSpan(start * 2, Math.Max(0, count) * 2).ToArray());
    }
}
