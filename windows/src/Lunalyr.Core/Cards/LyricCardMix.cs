// Mixing a lyric card's sound: ported from the Mac's LyricCardMix.swift (Lunalyr's own code, no Verso lines).
//
// Each strand of sound (the song, the background video's own sound, audio files) is decoded to plain samples, brought
// to the same loudness, set to the level its slider asks for, summed, faded out at the end and run through a peak
// limiter, then encoded and laid under the video. Levels above 100% are real gain, a mastered song doesn't drown out a
// phone video at the same setting, and nothing clips.
//
// Samples are interleaved stereo float at SampleRate throughout.

namespace Lunalyr.Core.Cards;

public static class LyricCardMix
{
    public const double SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>Every strand is matched to this loudness (LUFS) before its slider level is applied.</summary>
    public const double ReferenceLoudness = -16;

    /// <summary>How far matching may turn a strand down or up, in dB. Near-silence stays near-silent.</summary>
    public const double MatchingMin = -30, MatchingMax = 24;

    /// <summary>The limiter's ceiling, in dBFS.</summary>
    public const double CeilingDecibels = -1;

    public static float Ceiling => (float)Math.Pow(10, CeilingDecibels / 20);

    /// <summary>Every MP4's sound fades out over its last this-many seconds, so it never ends on a click.</summary>
    public const double EndFade = 0.3;

    /// <summary>The song fades out over this where it is cut or stops before the MP4 does.</summary>
    public const double SongCutFade = 0.4;

    // ---- Slider levels ----

    /// <summary>Slider positions run from 0 to this; 1 is "as matched".</summary>
    public const double MaximumLevel = 1.5;

    /// <summary>
    /// The gain a slider position stands for, in dB: -∞ at 0, 40·log10(x) up to 1 (so 50% is -12 dB, clearly quieter),
    /// then straight up to +12 dB at 150%.
    /// </summary>
    public static double Decibels(double level)
    {
        if (!(level > 0.005)) return double.NegativeInfinity;
        if (level <= 1) return 40 * Math.Log10(level);
        return Math.Min(level, MaximumLevel) * 24 - 24;
    }

    /// <summary>What the slider shows: "Off", "−12 dB", "0 dB", "+12 dB".</summary>
    public static string Label(double level)
    {
        var db = Decibels(level);
        if (!double.IsFinite(db)) return "Off";
        var rounded = (int)Math.Round(db, MidpointRounding.AwayFromZero);
        if (rounded == 0) return "0 dB";
        return rounded > 0 ? $"+{rounded} dB" : $"−{-rounded} dB";
    }

    public static float Linear(double decibels) => double.IsFinite(decibels) ? (float)Math.Pow(10, decibels / 20) : 0;

    // ---- Loudness ----

    /// <summary>
    /// Integrated loudness in LUFS (ITU-R BS.1770: K-weighting, 400 ms blocks overlapping by 75%, absolute gate at -70
    /// and relative gate at -10 LU). Null for silence.
    /// </summary>
    public static double? Loudness(ReadOnlySpan<float> samples, int channels = Channels, double sampleRate = SampleRate)
    {
        var frames = samples.Length / channels;
        if (frames <= 0) return null;
        // Squared K-weighted samples, summed across channels, per 100 ms step.
        var step = Math.Max(1, (int)(sampleRate * 0.1));
        var steps = (frames + step - 1) / step;
        var energy = new double[steps];
        for (var channel = 0; channel < channels; channel++)
        {
            Biquad shelf = Biquad.KShelf(sampleRate), highPass = Biquad.KHighPass(sampleRate);
            for (var frame = 0; frame < frames; frame++)
            {
                var y = highPass.Process(shelf.Process(samples[frame * channels + channel]));
                energy[frame / step] += y * y;
            }
        }
        // 400 ms blocks every 100 ms; a clip shorter than one block is one block.
        var blocks = new List<double>();
        if (steps < 4)
        {
            blocks.Add(energy.Sum() / frames);
        }
        else
        {
            for (var start = 0; start <= steps - 4; start++)
            {
                var count = Math.Min(4 * step, frames - start * step);
                double sum = 0;
                for (var i = start; i < start + 4; i++) sum += energy[i];
                blocks.Add(sum / count);
            }
        }
        static double Lufs(double meanSquare) => -0.691 + 10 * Math.Log10(Math.Max(meanSquare, 1e-20));
        var absolute = blocks.Where(b => Lufs(b) > -70).ToList();
        if (absolute.Count == 0) return null;
        var threshold = Lufs(absolute.Sum() / absolute.Count) - 10;
        var relative = absolute.Where(b => Lufs(b) > threshold).ToList();
        return Lufs(relative.Sum() / Math.Max(1, relative.Count));
    }

    /// <summary>The gain, in dB, that brings <paramref name="samples"/> to <see cref="ReferenceLoudness"/>; 0 for silence.</summary>
    public static double MatchingGain(ReadOnlySpan<float> samples)
    {
        if (Loudness(samples) is not { } measured) return 0;
        return Math.Min(Math.Max(ReferenceLoudness - measured, MatchingMin), MatchingMax);
    }

    /// <summary>A second-order filter section (transposed direct form II).</summary>
    public struct Biquad
    {
        public double B0, B1, B2, A1, A2;
        private double _z1, _z2;

        public Biquad(double b0, double b1, double b2, double a1, double a2)
        {
            B0 = b0; B1 = b1; B2 = b2; A1 = a1; A2 = a2;
        }

        public double Process(double x)
        {
            var y = B0 * x + _z1;
            _z1 = B1 * x - A1 * y + _z2;
            _z2 = B2 * x - A2 * y;
            return y;
        }

        /// <summary>BS.1770's head-related high shelf (+4 dB above ~1.7 kHz), for any sample rate.</summary>
        public static Biquad KShelf(double rate)
        {
            double f0 = 1681.974450955533, gain = 3.999843853973347, q = 0.7071752369554196;
            var k = Math.Tan(Math.PI * f0 / rate);
            double vh = Math.Pow(10, gain / 20), vb = Math.Pow(vh, 0.4996667741545416);
            var a0 = 1 + k / q + k * k;
            return new Biquad((vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0,
                              2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        }

        /// <summary>BS.1770's RLB high-pass at ~38 Hz.</summary>
        public static Biquad KHighPass(double rate)
        {
            double f0 = 38.13547087602444, q = 0.5003270373238773;
            var k = Math.Tan(Math.PI * f0 / rate);
            var a0 = 1 + k / q + k * k;
            return new Biquad(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        }
    }

    // ---- Shaping ----

    /// <summary>
    /// Look-ahead peak limiter: no sample leaves above <paramref name="ceiling"/>. Gain comes down smoothly over the
    /// look-ahead before a peak and recovers over <paramref name="release"/>.
    /// </summary>
    public static void Limit(Span<float> samples, int channels = Channels, float? ceiling = null, double lookahead = 0.005,
                             double release = 0.1, double sampleRate = SampleRate)
    {
        var top = ceiling ?? Ceiling;
        var frames = samples.Length / channels;
        if (frames <= 0) return;
        var ahead = Math.Max(1, (int)(lookahead * sampleRate));
        // The gain each frame needs on its own.
        var needed = new float[frames];
        Array.Fill(needed, 1f);
        var any = false;
        for (var frame = 0; frame < frames; frame++)
        {
            float peak = 0;
            for (var channel = 0; channel < channels; channel++) peak = Math.Max(peak, Math.Abs(samples[frame * channels + channel]));
            if (peak > top)
            {
                needed[frame] = top / peak;
                any = true;
            }
        }
        if (!any) return;
        // The lowest need over the next `ahead` frames (a sliding minimum), then recovery limited by the release, then a
        // moving average over the look-ahead. Every value averaged for a frame is at most that frame's need, so the
        // average is too.
        var window = new List<int>();
        var head = 0;
        var released = new float[frames];
        var recovery = (float)(1 - Math.Exp(-1 / (release * sampleRate)));
        float previous = 1;
        var next = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            while (next < Math.Min(frames, frame + ahead + 1))
            {
                while (window.Count > head && needed[window[^1]] >= needed[next]) window.RemoveAt(window.Count - 1);
                window.Add(next);
                next++;
            }
            while (window[head] < frame) head++;
            if (head > 4096)
            {
                window.RemoveRange(0, head);
                head = 0;
            }
            var target = needed[window[head]];
            previous = Math.Min(target, previous + (1 - previous) * recovery);
            released[frame] = previous;
        }
        float sum = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            sum += released[frame];
            if (frame > ahead) sum -= released[frame - ahead - 1];
            var gain = Math.Min(sum / (Math.Min(frame, ahead) + 1), needed[frame]);
            for (var channel = 0; channel < channels; channel++)
            {
                var index = frame * channels + channel;
                samples[index] = Math.Min(Math.Max(samples[index] * gain, -top), top);
            }
        }
    }

    /// <summary>Fades to silence over <paramref name="seconds"/> ending at <paramref name="end"/> (in frames); silence after it.</summary>
    public static void FadeOut(Span<float> samples, int end, double seconds, int channels = Channels, double sampleRate = SampleRate)
    {
        var frames = samples.Length / channels;
        end = Math.Min(Math.Max(0, end), frames);
        // Sound that stops sooner than the fade lasts fades over what there is, starting from full level rather than
        // jumping part-way down.
        var length = Math.Max(1, Math.Min((int)(seconds * sampleRate), end));
        var start = Math.Max(0, end - length);
        for (var frame = start; frame < end; frame++)
        {
            var gain = (float)(end - frame - 1) / length;
            for (var channel = 0; channel < channels; channel++) samples[frame * channels + channel] *= gain;
        }
        samples[(end * channels)..(frames * channels)].Clear();
    }

    /// <summary>
    /// Per-frame gains that lower a strand by <paramref name="depth"/> dB while <paramref name="trigger"/> is above
    /// <paramref name="threshold"/> dBFS (measured as RMS over 10 ms), with <paramref name="attack"/> and
    /// <paramref name="release"/> time constants.
    /// </summary>
    public static float[] DuckGains(ReadOnlySpan<float> trigger, int frames, double threshold = -40, double depth = -12,
                                    double attack = 0.05, double release = 0.3, int channels = Channels,
                                    double sampleRate = SampleRate)
    {
        var block = Math.Max(1, (int)(sampleRate * 0.01));
        var low = Linear(depth);
        var limit = (float)Math.Pow(10, threshold / 20);
        float fall = (float)(1 - Math.Exp(-1 / (attack * sampleRate))), rise = (float)(1 - Math.Exp(-1 / (release * sampleRate)));
        var gains = new float[frames];
        Array.Fill(gains, 1f);
        float gain = 1;
        var available = trigger.Length / channels;
        var start = 0;
        while (start < frames)
        {
            var end = Math.Min(frames, start + block);
            float energy = 0;
            var counted = 0;
            for (var frame = start; frame < Math.Min(end, available); frame++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var x = trigger[frame * channels + channel];
                    energy += x * x;
                }
                counted += channels;
            }
            var rms = counted > 0 ? MathF.Sqrt(energy / counted) : 0;
            var target = rms > limit ? low : 1;
            for (var frame = start; frame < end; frame++)
            {
                gain += (target - gain) * (target < gain ? fall : rise);
                gains[frame] = gain;
            }
            start = end;
        }
        return gains;
    }

    /// <summary>
    /// <paramref name="source"/> repeated (or cut) to <paramref name="frames"/>. With a crossfade, each repeat starts
    /// <paramref name="crossfade"/> frames before the previous one ends and the two are blended linearly across that
    /// overlap, the same way the picture is.
    /// </summary>
    public static float[] Loop(ReadOnlySpan<float> source, int frames, int crossfade = 0, int channels = Channels)
    {
        var length = source.Length / channels;
        var output = new float[Math.Max(0, frames) * channels];
        if (length <= 0 || frames <= 0) return output;
        var overlap = length > frames ? 0 : Math.Min(Math.Max(0, crossfade), length / 2);
        var period = length - overlap;
        for (var pass = 0; pass * period < frames; pass++)
        {
            var offset = pass * period;
            var followed = offset + period < frames;
            for (var frame = 0; frame < Math.Min(length, frames - offset); frame++)
            {
                float gain = 1;
                if (pass > 0 && frame < overlap) gain *= (float)frame / overlap;
                if (followed && overlap > 0 && frame >= period) gain *= (float)(length - frame) / overlap;
                for (var channel = 0; channel < channels; channel++)
                    output[(offset + frame) * channels + channel] += source[frame * channels + channel] * gain;
            }
        }
        return output;
    }

    /// <summary>
    /// <paramref name="samples"/> (interleaved, <paramref name="channels"/> channels at <paramref name="rate"/>) as
    /// interleaved stereo at <see cref="SampleRate"/>: mono is doubled, more than two channels keep the first two, and
    /// another rate is resampled linearly. Windows' Source Reader doesn't always convert the rate itself.
    /// </summary>
    public static float[] ToStereo48k(ReadOnlySpan<float> samples, int channels, double rate)
    {
        if (channels <= 0) return [];
        var frames = samples.Length / channels;
        var stereo = new float[frames * 2];
        for (var f = 0; f < frames; f++)
        {
            var left = samples[f * channels];
            stereo[2 * f] = left;
            stereo[2 * f + 1] = channels > 1 ? samples[f * channels + 1] : left;
        }
        if (Math.Abs(rate - SampleRate) < 0.5 || frames == 0) return stereo;
        var outFrames = (int)Math.Round(frames * SampleRate / rate);
        var resampled = new float[outFrames * 2];
        var ratio = rate / SampleRate;
        for (var f = 0; f < outFrames; f++)
        {
            var position = f * ratio;
            var i = (int)position;
            var t = (float)(position - i);
            var j = Math.Min(frames - 1, i + 1);
            i = Math.Min(frames - 1, i);
            resampled[2 * f] = stereo[2 * i] + (stereo[2 * j] - stereo[2 * i]) * t;
            resampled[2 * f + 1] = stereo[2 * i + 1] + (stereo[2 * j + 1] - stereo[2 * i + 1]) * t;
        }
        return resampled;
    }
}
