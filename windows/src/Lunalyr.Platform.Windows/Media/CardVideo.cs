// A lyric card's MP4, as the Mac's LyricCard.video and LyricCard.mux make it (LyricCardExport.swift, Lunalyr's own
// code): H.264 at 4 Mbit/s, High profile, BT.709 colour, a key frame at least every 60 frames, each frame shown as long
// as the card asks; then, when the card has sound, the finished video copied as it is under an AAC track (192 kbit/s,
// 48 kHz stereo) into a new file that replaces it. Media Foundation's Sink Writer does both; nothing is ever played.

using Lunalyr.Core.Cards;

namespace Lunalyr.Platform.Media;

/// <summary>Writes a silent H.264 MP4 from BGRA frames, each with its own length.</summary>
public sealed unsafe class CardVideoWriter : IDisposable
{
    private readonly SinkWriter _writer;
    private readonly uint _stream;
    private readonly byte[] _row;
    private long _time;
    private bool _finished;

    /// <summary>Starts an MP4 at <paramref name="path"/> (replaced if there) of <paramref name="width"/> × <paramref name="height"/> pixels (both even).</summary>
    public CardVideoWriter(string path, int width, int height, int framesPerSecond = 30, int bitRate = 4_000_000)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (width < 2 || height < 2 || width % 2 != 0 || height % 2 != 0) throw new ArgumentException("H.264 needs an even width and height.");
        Width = width;
        Height = height;
        Path = path;
        if (File.Exists(path)) File.Delete(path);
        _row = new byte[width * 4];
        _writer = SinkWriter.Create(path);
        nint output = 0, input = 0;
        try
        {
            output = MfInterop.CreateMediaType();
            MfInterop.SetGuid(output, MfInterop.MF_MT_MAJOR_TYPE, MfInterop.MFMediaType_Video);
            MfInterop.SetGuid(output, MfInterop.MF_MT_SUBTYPE, MfInterop.MFVideoFormat_H264);
            MfInterop.SetUInt32(output, MfInterop.MF_MT_AVG_BITRATE, (uint)bitRate);
            MfInterop.SetUInt64(output, MfInterop.MF_MT_FRAME_SIZE, ((ulong)width << 32) | (uint)height);
            MfInterop.SetUInt64(output, MfInterop.MF_MT_FRAME_RATE, ((ulong)framesPerSecond << 32) | 1);
            MfInterop.SetUInt64(output, MfInterop.MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);
            MfInterop.SetUInt32(output, MfInterop.MF_MT_INTERLACE_MODE, 2); // progressive
            MfInterop.SetUInt32(output, MfInterop.MF_MT_MPEG2_PROFILE, 100); // eAVEncH264VProfile_High
            MfInterop.SetUInt32(output, MfInterop.MF_MT_MAX_KEYFRAME_SPACING, 60);
            MfInterop.SetUInt32(output, MfInterop.MF_MT_VIDEO_PRIMARIES, 2); // MFVideoPrimaries_BT709
            MfInterop.SetUInt32(output, MfInterop.MF_MT_TRANSFER_FUNCTION, 5); // MFVideoTransFunc_709
            MfInterop.SetUInt32(output, MfInterop.MF_MT_YUV_MATRIX, 1); // MFVideoTransferMatrix_BT709
            _stream = _writer.AddStream(output);

            input = MfInterop.CreateMediaType();
            MfInterop.SetGuid(input, MfInterop.MF_MT_MAJOR_TYPE, MfInterop.MFMediaType_Video);
            MfInterop.SetGuid(input, MfInterop.MF_MT_SUBTYPE, MfInterop.MFVideoFormat_RGB32);
            MfInterop.SetUInt64(input, MfInterop.MF_MT_FRAME_SIZE, ((ulong)width << 32) | (uint)height);
            MfInterop.SetUInt64(input, MfInterop.MF_MT_FRAME_RATE, ((ulong)framesPerSecond << 32) | 1);
            MfInterop.SetUInt64(input, MfInterop.MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);
            MfInterop.SetUInt32(input, MfInterop.MF_MT_INTERLACE_MODE, 2);
            // A positive stride: rows top first, as the card's pixels come.
            MfInterop.SetUInt32(input, MfInterop.MF_MT_DEFAULT_STRIDE, (uint)(width * 4));
            MfInterop.SetUInt32(input, MfInterop.MF_MT_VIDEO_PRIMARIES, 2);
            MfInterop.SetUInt32(input, MfInterop.MF_MT_TRANSFER_FUNCTION, 5);
            _writer.SetInputMediaType(_stream, input);
            _writer.BeginWriting();
        }
        catch
        {
            _writer.Dispose();
            throw;
        }
        finally
        {
            MfInterop.Release(output);
            MfInterop.Release(input);
        }
    }

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Seconds written so far.</summary>
    public double Seconds => _time / 1e7;

    /// <summary>Adds a frame (opaque BGRA, rows top first, <see cref="Width"/> × 4 bytes a row) shown for <paramref name="seconds"/>.</summary>
    public void Write(ReadOnlySpan<byte> bgra, double seconds)
    {
        if (_finished) throw new InvalidOperationException("The video is finished.");
        if (bgra.Length < Width * Height * 4) throw new ArgumentException("Too few pixels for the frame.", nameof(bgra));
        var end = _time + (long)Math.Round(Math.Max(0.001, seconds) * 1e7);
        var sample = SinkWriter.Sample(bgra[..(Width * Height * 4)], _time, end - _time);
        try
        {
            _writer.WriteSample(_stream, sample);
        }
        finally
        {
            MfInterop.Release(sample);
        }
        _time = end;
    }

    /// <summary>Finishes the file. Without it (on Dispose) the file is left incomplete; the caller deletes it.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _writer.Finish();
    }

    public void Dispose() => _writer.Dispose();
}

/// <summary>Lays a finished mix under a finished MP4, copying its video as it is.</summary>
public static unsafe class CardSoundMuxer
{
    /// <summary>
    /// Writes <paramref name="video"/>'s video stream, untouched, and <paramref name="mix"/> (interleaved stereo float
    /// at 48 kHz) as AAC into a new file, then replaces <paramref name="video"/> with it. False, with the file untouched
    /// and nothing left behind, when it can't or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public static bool Mux(string video, float[] mix, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(video);
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(video))!;
        var output = System.IO.Path.Combine(folder, $"sound-{Guid.NewGuid():N}.mp4");
        var reader = MfInterop.OpenReader(video, out _);
        if (reader == 0) return false;
        var written = false;
        try
        {
            if (!MfInterop.SelectOnly(reader, MfInterop.FirstVideoStream)) return false;
            var length = MfInterop.Duration(reader);
            nint native;
            // IMFSourceReader: 5 GetNativeMediaType, 7 SetCurrentMediaType. The native type is the compressed stream: read
            // as it is, its samples are copied, not decoded.
            MfInterop.Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)MfInterop.Slot(reader, 5))(reader, MfInterop.FirstVideoStream, 0, &native), "GetNativeMediaType");
            try
            {
                MfInterop.Check(((delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)MfInterop.Slot(reader, 7))(reader, MfInterop.FirstVideoStream, null, native), "SetCurrentMediaType");
                using var writer = SinkWriter.Create(output);
                var videoStream = writer.AddStream(native);
                writer.SetInputMediaType(videoStream, native);
                var audioStream = AddAudio(writer);
                writer.BeginWriting();

                const int chunk = 4800; // 0.1 s
                var frames = mix.Length / 2;
                var pcm = new byte[chunk * 4];
                var audioAt = 0;
                void WriteAudioUntil(long time)
                {
                    while (audioAt < frames && audioAt * 1e7 / LyricCardMix.SampleRate <= time)
                    {
                        var count = Math.Min(chunk, frames - audioAt);
                        var span = mix.AsSpan(audioAt * 2, count * 2);
                        for (var i = 0; i < span.Length; i++)
                        {
                            var v = (int)Math.Round(Math.Clamp(span[i], -1f, 1f) * 32767);
                            pcm[2 * i] = (byte)v;
                            pcm[2 * i + 1] = (byte)(v >> 8);
                        }
                        var start = (long)Math.Round(audioAt * 1e7 / LyricCardMix.SampleRate);
                        var end = (long)Math.Round((audioAt + count) * 1e7 / LyricCardMix.SampleRate);
                        var sample = SinkWriter.Sample(pcm.AsSpan(0, count * 4), start, end - start);
                        try { writer.WriteSample(audioStream, sample); }
                        finally { MfInterop.Release(sample); }
                        audioAt += count;
                    }
                }

                while (true)
                {
                    if (cancellationToken.IsCancellationRequested) return false;
                    uint stream, flags;
                    long time;
                    nint sample;
                    // IMFSourceReader: 9 ReadSample.
                    var hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)MfInterop.Slot(reader, 9))(
                        reader, MfInterop.FirstVideoStream, 0, &stream, &flags, &time, &sample);
                    if (hr < 0 || (flags & MfInterop.ReadErrorFlag) != 0)
                    {
                        MfInterop.Release(sample);
                        return false;
                    }
                    if (sample != 0)
                    {
                        // The sound comes first up to each picture, so the file is interleaved as it is written.
                        WriteAudioUntil(time);
                        try { writer.WriteSample(videoStream, sample); }
                        finally { MfInterop.Release(sample); }
                        if (length > 0) progress?.Invoke(Math.Min(0.95, time / 1e7 / length));
                    }
                    if ((flags & MfInterop.ReadEndOfStreamFlag) != 0) break;
                }
                WriteAudioUntil(long.MaxValue);
                if (cancellationToken.IsCancellationRequested) return false;
                writer.Finish();
                written = true;
            }
            finally
            {
                MfInterop.Release(native);
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            MfInterop.Release(reader);
            if (!written) TryDelete(output);
        }
        try
        {
            File.Move(output, video, overwrite: true);
            progress?.Invoke(1);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(output);
            return false;
        }
    }

    /// <summary>The AAC track: 48 kHz stereo at 192 kbit/s, fed 16-bit PCM.</summary>
    private static uint AddAudio(SinkWriter writer)
    {
        nint output = 0, input = 0;
        try
        {
            output = MfInterop.CreateMediaType();
            MfInterop.SetGuid(output, MfInterop.MF_MT_MAJOR_TYPE, MfInterop.MFMediaType_Audio);
            MfInterop.SetGuid(output, MfInterop.MF_MT_SUBTYPE, MfInterop.MFAudioFormat_AAC);
            MfInterop.SetUInt32(output, MfInterop.MF_MT_AUDIO_SAMPLES_PER_SECOND, 48_000);
            MfInterop.SetUInt32(output, MfInterop.MF_MT_AUDIO_NUM_CHANNELS, 2);
            MfInterop.SetUInt32(output, MfInterop.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            MfInterop.SetUInt32(output, MfInterop.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24_000);
            var stream = writer.AddStream(output);

            input = MfInterop.CreateMediaType();
            MfInterop.SetGuid(input, MfInterop.MF_MT_MAJOR_TYPE, MfInterop.MFMediaType_Audio);
            MfInterop.SetGuid(input, MfInterop.MF_MT_SUBTYPE, MfInterop.MFAudioFormat_PCM);
            MfInterop.SetUInt32(input, MfInterop.MF_MT_AUDIO_SAMPLES_PER_SECOND, 48_000);
            MfInterop.SetUInt32(input, MfInterop.MF_MT_AUDIO_NUM_CHANNELS, 2);
            MfInterop.SetUInt32(input, MfInterop.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            MfInterop.SetUInt32(input, MfInterop.MF_MT_AUDIO_BLOCK_ALIGNMENT, 4);
            MfInterop.SetUInt32(input, MfInterop.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 48_000 * 4);
            MfInterop.SetUInt32(input, MfInterop.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            writer.SetInputMediaType(stream, input);
            return stream;
        }
        finally
        {
            MfInterop.Release(output);
            MfInterop.Release(input);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Reads a file's first audio track (an audio file, or a video's sound) as interleaved stereo float at 48 kHz, through a
/// Source Reader with only that stream selected. Decoding only: nothing is played.
/// </summary>
public sealed unsafe class AudioFileDecoder : IAudioDecoder
{
    public static AudioFileDecoder Instance { get; } = new();

    public double? AudioDuration(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var reader = MfInterop.OpenReader(path, out _);
        if (reader == 0) return null;
        try
        {
            if (!MfInterop.SelectOnly(reader, MfInterop.FirstAudioStream)) return null;
            nint native;
            if (((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)MfInterop.Slot(reader, 5))(reader, MfInterop.FirstAudioStream, 0, &native) < 0)
                return null;
            MfInterop.Release(native);
            var duration = MfInterop.Duration(reader);
            return duration > 0 ? duration : null;
        }
        finally
        {
            MfInterop.Release(reader);
        }
    }

    /// <summary>Whether <paramref name="path"/> has an audio track at all (a video background's sound).</summary>
    public static bool HasAudio(string path) => Instance.AudioDuration(path) is not null;

    public Task<float[]?> DecodeAsync(string path, double from = 0, double? seconds = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Decode(path, from, seconds, cancellationToken), cancellationToken);

    private static float[]? Decode(string path, double from, double? seconds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var reader = MfInterop.OpenReader(path, out _);
        if (reader == 0) return null;
        try
        {
            if (!MfInterop.SelectOnly(reader, MfInterop.FirstAudioStream)) return null;
            var type = MfInterop.CreateMediaType();
            try
            {
                MfInterop.SetGuid(type, MfInterop.MF_MT_MAJOR_TYPE, MfInterop.MFMediaType_Audio);
                MfInterop.SetGuid(type, MfInterop.MF_MT_SUBTYPE, MfInterop.MFAudioFormat_Float);
                if (((delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)MfInterop.Slot(reader, 7))(reader, MfInterop.FirstAudioStream, null, type) < 0)
                    return null;
            }
            finally
            {
                MfInterop.Release(type);
            }
            // What the decoder actually delivers: its own rate and channels (converted below), not always what was asked.
            nint current;
            MfInterop.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)MfInterop.Slot(reader, 6))(reader, MfInterop.FirstAudioStream, &current), "GetCurrentMediaType");
            int channels;
            double rate;
            try
            {
                channels = (int)(MfInterop.GetUInt32(current, MfInterop.MF_MT_AUDIO_NUM_CHANNELS) ?? 2);
                rate = MfInterop.GetUInt32(current, MfInterop.MF_MT_AUDIO_SAMPLES_PER_SECOND) ?? 48_000;
            }
            finally
            {
                MfInterop.Release(current);
            }
            if (channels <= 0 || rate <= 0) return null;
            if (from > 0) MfInterop.Seek(reader, from);
            var wantFrames = seconds is { } s ? (long)Math.Round(Math.Max(0, s) * rate) : long.MaxValue;
            var read = new List<float>(seconds is { } w ? (int)Math.Min(int.MaxValue / 2, w * rate * channels + 4096) : 1 << 16);
            long? first = null;
            while (read.Count / channels < wantFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint stream, flags;
                long time;
                nint sample;
                var hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)MfInterop.Slot(reader, 9))(
                    reader, MfInterop.FirstAudioStream, 0, &stream, &flags, &time, &sample);
                if (hr < 0 || (flags & MfInterop.ReadErrorFlag) != 0)
                {
                    MfInterop.Release(sample);
                    return null;
                }
                if (sample != 0)
                {
                    try
                    {
                        // A seek lands on a packet at or before the time asked: skip what comes before it.
                        var skip = 0L;
                        if (first is null)
                        {
                            first = time;
                            if (from > 0) skip = Math.Max(0, (long)Math.Round((from - time / 1e7) * rate));
                        }
                        Append(sample, channels, skip, read);
                    }
                    finally
                    {
                        MfInterop.Release(sample);
                    }
                }
                if ((flags & MfInterop.ReadEndOfStreamFlag) != 0) break;
            }
            var frames = (int)Math.Min(read.Count / channels, wantFrames);
            var interleaved = new float[frames * channels];
            read.CopyTo(0, interleaved, 0, interleaved.Length);
            return LyricCardMix.ToStereo48k(interleaved, channels, rate);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            MfInterop.Release(reader);
        }
    }

    private static void Append(nint sample, int channels, long skipFrames, List<float> into)
    {
        nint buffer;
        // IMFSample: 41 ConvertToContiguousBuffer.
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)MfInterop.Slot(sample, 41))(sample, &buffer) < 0) return;
        try
        {
            byte* data;
            uint length;
            if (((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)MfInterop.Slot(buffer, 3))(buffer, &data, null, &length) < 0) return;
            try
            {
                var floats = new ReadOnlySpan<float>(data, (int)(length / 4));
                var skip = (int)Math.Min(floats.Length, skipFrames * channels);
                foreach (var value in floats[skip..]) into.Add(value);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, int>)MfInterop.Slot(buffer, 4))(buffer);
            }
        }
        finally
        {
            MfInterop.Release(buffer);
        }
    }
}
