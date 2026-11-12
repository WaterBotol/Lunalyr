// What happens to captured PCM on its way to the analysers. Re-implemented for Windows from a description of the Mac's
// AudioPCMProcessor (whose structure traces back to the Verso import, so it is not translated): decode up to two
// channels of interleaved PCM (32/64-bit float, 16/24/32-bit integer) without allocating, hand them to VisualizerAudio
// in pieces that end on the analyser's 1,024-sample hop so its pass rate never depends on the packet size, stamp each
// piece with the capture time of its last sample, keep live levels, notice real (non-zero) sound, label the format,
// count packets and dropped frames, flag a format it can't decode once, and publish a report at most 15 times a second.

using System.Buffers.Binary;

namespace Lunalyr.Core.Visualisers.Capture;

/// <summary>A sample encoding capture can decode.</summary>
public enum PcmEncoding { Float32, Float64, Int16, Int24, Int32 }

/// <summary>An interleaved PCM stream: its encoding, channels, rate and bytes per frame.</summary>
public readonly record struct PcmFormat(PcmEncoding Encoding, int Channels, double SampleRate, int BlockAlign)
{
    public int SampleBytes => Encoding switch
    {
        PcmEncoding.Float64 => 8,
        PcmEncoding.Int16 => 2,
        PcmEncoding.Int24 => 3,
        _ => 4,
    };

    /// <summary>
    /// The format for a stream described as Windows does (<c>WAVEFORMATEX</c>, its extensible sub-format resolved):
    /// null for anything that would be misread (big containers, odd widths, rates outside 8-192 kHz), so a format is
    /// never silently decoded as something else.
    /// </summary>
    public static PcmFormat? From(bool isFloat, int bitsPerSample, int validBits, int channels, int blockAlign, double sampleRate)
    {
        if (channels <= 0 || blockAlign <= 0 || blockAlign > 512 || !double.IsFinite(sampleRate) || sampleRate < 8_000 || sampleRate > 192_000)
            return null;
        var width = blockAlign / channels;
        if (width * channels != blockAlign) return null;
        if (isFloat)
        {
            if (bitsPerSample == 32 && width == 4) return new PcmFormat(PcmEncoding.Float32, channels, sampleRate, blockAlign);
            if (bitsPerSample == 64 && width == 8) return new PcmFormat(PcmEncoding.Float64, channels, sampleRate, blockAlign);
            return null;
        }
        if (bitsPerSample == 16 && width == 2) return new PcmFormat(PcmEncoding.Int16, channels, sampleRate, blockAlign);
        if (bitsPerSample == 24 && width == 3) return new PcmFormat(PcmEncoding.Int24, channels, sampleRate, blockAlign);
        // 24-bit samples in 32-bit containers read as 32-bit integers: the padding is the low byte.
        if (bitsPerSample == 32 && width == 4 && validBits is 0 or 24 or 32) return new PcmFormat(PcmEncoding.Int32, channels, sampleRate, blockAlign);
        return null;
    }

    /// <summary>"48000 Hz · 2ch · 32bit float", for diagnostics.</summary>
    public string Label(int analysedChannels) =>
        $"{(int)SampleRate} Hz · {analysedChannels}ch · " + Encoding switch
        {
            PcmEncoding.Float32 => "32bit float",
            PcmEncoding.Float64 => "64bit float",
            PcmEncoding.Int16 => "16bit",
            PcmEncoding.Int24 => "24bit",
            _ => "32bit",
        };
}

/// <summary>Live levels for the settings page's meters: RMS and the analyser's three registers, about 0...1.</summary>
public readonly record struct AudioLevels(float Rms = 0, float Bass = 0, float Mid = 0, float Treble = 0);

/// <summary>What capture last measured, as the controller reads it.</summary>
public readonly record struct CaptureReport
{
    public AudioLevels Levels { get; init; }
    /// <summary>Packets accepted since the feed was made.</summary>
    public int Packets { get; init; }
    /// <summary>Frames the stream lost (a discontinuity) or that were skipped.</summary>
    public long DroppedFrames { get; init; }
    public string FormatLabel { get; init; }
    /// <summary>When the last packet arrived, and when the last non-zero sample did (null before any).</summary>
    public double DeliveredAt { get; init; }
    public double? AudibleAt { get; init; }
    /// <summary>A format that can't be decoded, said once.</summary>
    public string? Problem { get; init; }
}

/// <summary>
/// Feeds captured PCM to a <see cref="VisualizerAudio"/>. <see cref="Write"/> runs on the capture thread and never
/// allocates; <see cref="Read"/> may be called from any thread.
/// </summary>
public sealed class CaptureFeed
{
    /// <summary>Frames decoded per piece of work; scratch buffers are allocated once.</summary>
    public const int ChunkFrames = 4_096;
    /// <summary>Matches the analyser's hop.</summary>
    public const int AnalyserHop = 1_024;
    public const double PublishInterval = 1.0 / 15;
    public const string UnsupportedMessage = "This audio format isn’t supported by Lunalyr's analysis.";

    private readonly VisualizerAudio _target;
    private readonly Lock _lock = new();
    private readonly float[] _left = new float[ChunkFrames];
    private readonly float[] _right = new float[ChunkFrames];
    private int _channels;
    private int _hopRemaining = AnalyserHop;
    private PcmFormat _labelled;
    private bool _unsupportedSaid;

    // Shared with readers, under _lock.
    private CaptureReport _report = new() { FormatLabel = "" };
    private double _lastPublish = double.NegativeInfinity;
    private int _packets;
    private long _dropped;
    private double? _audibleAt;
    private string? _problem;
    private double _delivered;
    private float _rms;

    public CaptureFeed(VisualizerAudio target) => _target = target;

    public VisualizerAudio Target => _target;

    /// <summary>
    /// One captured packet: <paramref name="frames"/> interleaved frames in <paramref name="data"/> (null data, or
    /// <paramref name="silent"/>, means the stream marked it silent: zeros). <paramref name="lastSampleTime"/> is the
    /// capture time of its last frame on the analyser's clock; <paramref name="now"/> the arrival time.
    /// </summary>
    public void Write(ReadOnlySpan<byte> data, int frames, PcmFormat? format, double lastSampleTime, double now, bool silent = false,
        bool discontinuity = false)
    {
        if (format is not { } f)
        {
            lock (_lock)
            {
                if (!_unsupportedSaid) _problem = UnsupportedMessage;
                _unsupportedSaid = true;
            }
            return;
        }
        if (frames <= 0) return;
        var channels = Math.Min(2, f.Channels);
        if (channels != _channels)
        {
            _channels = channels;
            _target.SetChannelCount(channels);
        }
        var peak = 0f;
        double sum = 0;
        var rate = f.SampleRate;
        for (var start = 0; start < frames; start += ChunkFrames)
        {
            var count = Math.Min(ChunkFrames, frames - start);
            if (silent || data.IsEmpty)
            {
                Array.Clear(_left, 0, count);
                Array.Clear(_right, 0, count);
            }
            else
            {
                Decode(data[(start * f.BlockAlign)..], f, count);
            }
            for (var i = 0; i < count; i++)
            {
                var l = _left[i];
                var r = channels > 1 ? _right[i] : l;
                var m = Math.Max(Math.Abs(l), Math.Abs(r));
                if (m > peak) peak = m;
                sum += Math.Max(l * l, r * r);
            }
            // Pieces end on the analyser's hop, each stamped with the capture time of its own last sample.
            var offset = 0;
            while (offset < count)
            {
                var piece = Math.Min(count - offset, _hopRemaining);
                var framesAfter = frames - (start + offset + piece);
                var time = lastSampleTime - framesAfter / rate;
                _target.Append(_left.AsSpan(offset, piece), rate, 0, time);
                if (channels > 1) _target.Append(_right.AsSpan(offset, piece), rate, 1, time);
                offset += piece;
                _hopRemaining -= piece;
                if (_hopRemaining == 0) _hopRemaining = AnalyserHop;
            }
        }
        var rms = (float)Math.Sqrt(sum / frames);
        lock (_lock)
        {
            _packets += 1;
            _delivered = now;
            _rms = float.IsFinite(rms) ? rms : 0;
            // The stream doesn't say how many frames a discontinuity lost; it counts as one.
            if (discontinuity) _dropped += 1;
            // Exact digital silence is not audio; real playback, even a quiet passage, carries some non-zero samples.
            if (peak > 0) _audibleAt = now;
            if (f != _labelled)
            {
                _labelled = f;
                _report = _report with { FormatLabel = f.Label(channels) };
            }
        }
    }

    /// <summary>Notes frames the capture had to skip.</summary>
    public void NoteDropped(long frames)
    {
        lock (_lock) _dropped += Math.Max(0, frames);
    }

    /// <summary>
    /// The latest report, refreshed at most every <see cref="PublishInterval"/> (a format problem is reported at once).
    /// Null while there is nothing new to say.
    /// </summary>
    public CaptureReport? Read(double now)
    {
        lock (_lock)
        {
            var problem = _problem;
            if (problem is null && (_packets == _report.Packets || now - _lastPublish < PublishInterval)) return null;
            _problem = null;
            _lastPublish = now;
            var snapshot = _target.Latest(_delivered > 0 ? _delivered : now);
            _report = _report with
            {
                Levels = new AudioLevels(Math.Min(1, _rms * 2.4f), snapshot.Bass, snapshot.Mid, snapshot.Treble),
                Packets = _packets,
                DroppedFrames = _dropped,
                DeliveredAt = _delivered,
                AudibleAt = _audibleAt,
                Problem = problem,
            };
            return _report;
        }
    }

    private void Decode(ReadOnlySpan<byte> data, PcmFormat f, int count)
    {
        var block = f.BlockAlign;
        var width = f.SampleBytes;
        var stereo = f.Channels > 1;
        for (var i = 0; i < count; i++)
        {
            var frame = data.Slice(i * block, block);
            _left[i] = Sample(frame, f.Encoding);
            _right[i] = stereo ? Sample(frame[width..], f.Encoding) : 0;
        }
    }

    private static float Sample(ReadOnlySpan<byte> s, PcmEncoding encoding)
    {
        var value = encoding switch
        {
            PcmEncoding.Float32 => BinaryPrimitives.ReadSingleLittleEndian(s),
            PcmEncoding.Float64 => (float)BinaryPrimitives.ReadDoubleLittleEndian(s),
            PcmEncoding.Int16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32_768f,
            PcmEncoding.Int24 => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8_388_608f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2_147_483_648f,
        };
        return float.IsFinite(value) ? value : 0;
    }
}
