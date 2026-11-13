// Recording the song's own sound for a lyric card's MP4: the Mac's SongAudioSource.record and SongAudioRecorder.finish
// (LyricCardSongAudio.swift, SongAudioRecorder.swift, Lunalyr's own code), with one difference the Windows brief asks
// for: Lunalyr never plays, pauses or seeks the player. The Mac seeks to just before the lines and plays them; here the
// recorder waits for the person to play the song through the lines themselves, and records only while that stretch
// plays. Recording starts only from the card window's "Get the song's sound" button, never on its own.

namespace Lunalyr.Core.Cards;

/// <summary>A running capture of what the PC plays (WASAPI loopback on Windows), buffered on its own thread.</summary>
public interface ISongCapture : IDisposable
{
    /// <summary>The capture's sample rate.</summary>
    double SampleRate { get; }

    /// <summary>Moves what was captured since the last call into <paramref name="stereo"/> (interleaved stereo).</summary>
    void Drain(List<float> stereo);

    /// <summary>Why the capture stopped on its own (a device gone), or null while it runs.</summary>
    string? Failure { get; }
}

/// <summary>Starts captures. The app's records the default output; tests pass synthetic ones.</summary>
public interface ISongCaptureSource
{
    /// <summary>Starts capturing now (or throws <see cref="SongRecordingException"/> with words for the window).</summary>
    ISongCapture Start();
}

/// <summary>A capture source that never captures: <c>--demo</c>, and any run that must stay away from the PC's sound.</summary>
public sealed class RefusingSongCaptureSource(string reason) : ISongCaptureSource
{
    public string Reason { get; } = reason;

    public ISongCapture Start() => throw new SongRecordingException(Reason);
}

/// <summary>A recording that couldn't be made, in words for the card window.</summary>
public sealed class SongRecordingException(string message) : Exception(message);

/// <summary>Waits for the song to reach the lines, records them as they play, and writes the recording.</summary>
public sealed class SongRecorder(ISongCaptureSource source, TimeProvider? time = null)
{
    /// <summary>Longest recording: two minutes.</summary>
    public const double MaximumSeconds = 120;

    /// <summary>The recording starts this long before the lines.</summary>
    public const double PreRoll = 1.0;

    /// <summary>How long it waits for the song to be played through the lines.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Records the song from <paramref name="from"/> to <paramref name="to"/> (song seconds) as it plays, reading the
    /// player's position from <paramref name="clock"/> (its own position, not the lyrics'), and writes it in
    /// <paramref name="folder"/>. Throws <see cref="SongRecordingException"/> with words for the window when it can't.
    /// </summary>
    public async Task<SongAudioClip> RecordAsync(double from, double to, Func<(double Seconds, bool Playing)> clock,
                                                 Action<RecordingUpdate> update, string folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(update);
        if (!(to > from)) throw new SongRecordingException("Pick lines with timing to record the song.");
        if (to + 0.5 - (from - PreRoll) >= MaximumSeconds)
            throw new SongRecordingException("That stretch is too long to record (the limit is two minutes).");
        update(new RecordingUpdate.Waiting());
        // Wait for the song to be playing just before the lines: Lunalyr doesn't move the player itself.
        var started = _time.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (seconds, playing) = clock();
            if (playing && double.IsFinite(seconds) && seconds >= from - PreRoll - 0.25 && seconds <= from) break;
            if (_time.GetElapsedTime(started) > WaitLimit)
                throw new SongRecordingException("The song didn’t reach these lines. Play it from just before them, then try again.");
            await Task.Delay(TimeSpan.FromMilliseconds(40), _time, cancellationToken).ConfigureAwait(false);
        }
        using var capture = source.Start();
        var songStart = clock().Seconds;
        var needed = to + 0.5 - songStart;
        var samples = new List<float>((int)Math.Min(int.MaxValue / 4, needed * capture.SampleRate * 2 + 8192));
        RecordingUpdate? shown = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (seconds, playing) = clock();
            if (!playing || !double.IsFinite(seconds)) throw new SongRecordingException("The song stopped while recording.");
            // A jump back or far ahead is the person seeking: the recording no longer lines up with the song.
            if (seconds < songStart - 0.5 || seconds > to + 5) throw new SongRecordingException("The song moved while recording. Try again.");
            capture.Drain(samples);
            if (capture.Failure is { } failure) throw new SongRecordingException(failure);
            if (seconds >= to + 0.5) break;
            if (samples.Count / 2 / capture.SampleRate > MaximumSeconds)
                throw new SongRecordingException("That stretch is too long to record (the limit is two minutes).");
            if (RecordingUpdate.Next(seconds - songStart, needed, shown) is { } next)
            {
                shown = next;
                update(next);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(40), _time, cancellationToken).ConfigureAwait(false);
        }
        capture.Drain(samples);
        return Finish(samples, capture.SampleRate, songStart, folder);
    }

    /// <summary>
    /// Writes what was heard as a recording in <paramref name="folder"/>, starting at <paramref name="songStart"/>.
    /// Silence means nothing was heard (the PC muted, or another output in use), and nothing is written.
    /// </summary>
    public static SongAudioClip Finish(IReadOnlyList<float> stereo, double sampleRate, double songStart, string folder)
    {
        var frames = stereo.Count / 2;
        if (frames <= sampleRate * 0.5) throw new SongRecordingException("Nothing was heard. Turn the volume up and play the song through the lines.");
        float peak = 0;
        for (var i = 0; i < stereo.Count; i += 32) peak = Math.Max(peak, Math.Abs(stereo[i]));
        if (peak <= 0.0005) throw new SongRecordingException("Nothing was heard. Turn the volume up and play the song through the lines.");
        Directory.CreateDirectory(folder);
        var path = SongRecordings.NewPath(folder);
        WavFile.Write(path, stereo, 2, sampleRate);
        return new SongAudioClip(path, songStart, songStart + frames / sampleRate);
    }
}

/// <summary>16-bit PCM WAV files: song recordings, and synthetic sound for the tests.</summary>
public static class WavFile
{
    /// <summary>Writes interleaved float samples (−1…1) as 16-bit PCM.</summary>
    public static void Write(string path, IReadOnlyList<float> samples, int channels, double sampleRate)
    {
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        var rate = (int)Math.Round(sampleRate);
        var data = samples.Count * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + data);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(data);
        for (var i = 0; i < samples.Count; i++) writer.Write((short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32767));
    }
}
