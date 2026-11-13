// The sound under a lyric card's MP4: each strand (the song, a background video's own sound, audio files) rendered to
// samples, matched, levelled, ducked and summed. Ported from the Mac's LyricCardExport.swift (SoundLayer, Rendered,
// soundLayer, mixdown) and LyricCardSongAudio.swift (SongAudioClip, songWindows, songRange, songLayer): Lunalyr's own
// code. Decoding is the platform's (IAudioDecoder), so these rules run on any audio, real or synthetic.

namespace Lunalyr.Core.Cards;

/// <summary>Reads a file's first audio track as interleaved stereo float at <see cref="LyricCardMix.SampleRate"/>.</summary>
public interface IAudioDecoder
{
    /// <summary>
    /// The file's first audio track from <paramref name="from"/> for <paramref name="seconds"/> (to the end when null);
    /// null when it has no readable sound. Never plays anything.
    /// </summary>
    Task<float[]?> DecodeAsync(string path, double from = 0, double? seconds = null, CancellationToken cancellationToken = default);

    /// <summary>The audio track's length in seconds, or null when the file has none (or it can't be read).</summary>
    double? AudioDuration(string path);
}

/// <summary>A strand's samples, interleaved stereo, and where it fades out, if it stops before the end.</summary>
/// <param name="Samples">Interleaved stereo at <see cref="LyricCardMix.SampleRate"/>.</param>
/// <param name="FadeOut">Where it stops (in frames) and over how many seconds it fades there; applied after the
/// strand's loudness is measured, so it doesn't skew the matching.</param>
public sealed record RenderedSound(float[] Samples, (int End, double Seconds)? FadeOut = null);

/// <summary>What a strand of sound under the video is.</summary>
public enum SoundRole { Song, Background, File }

/// <summary>One strand of sound under the video: its slider level, what it is, and how to render it for a given length.</summary>
/// <param name="Volume">The slider position, 0 to 1.5 (see <see cref="LyricCardMix.Decibels"/>).</param>
/// <param name="Render">The strand's sound for that many seconds; null when it can't be read.</param>
/// <param name="Role">Song, background video or file.</param>
public sealed record SoundLayer(float Volume, Func<double, CancellationToken, Task<RenderedSound?>> Render, SoundRole Role = SoundRole.File);

/// <summary>Audio of the song, with where in the song it begins and ends.</summary>
/// <param name="Path">The file (a recording Lunalyr made, or the song's own file).</param>
/// <param name="SongStart">Song time of the file's first sample.</param>
/// <param name="SongEnd">Song time of its last; infinite for the whole track.</param>
public sealed record SongAudioClip(string Path, double SongStart, double SongEnd)
{
    public bool Covers(double from, double to) => SongStart <= from + 0.35 && SongEnd >= to - 0.35;
}

/// <summary>What the mix came to.</summary>
public enum MixOutcome { Written, Silent, Failed }

public static class LyricCardSound
{
    /// <summary>
    /// <paramref name="path"/>'s sound looped (or cut) to the video's length. A <paramref name="crossfade"/> (in seconds)
    /// overlaps each repeat with the end of the one before, as a background video's picture does. Null when the file
    /// has no sound.
    /// </summary>
    public static SoundLayer? FromFile(IAudioDecoder decoder, string path, float volume = 1, SoundRole role = SoundRole.File,
                                       double crossfade = 0)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        if (decoder.AudioDuration(path) is not { } length || !(length > 0.05)) return null;
        return new SoundLayer(volume, async (seconds, ct) =>
        {
            var frames = (int)Math.Round(seconds * LyricCardMix.SampleRate);
            var wanted = Math.Min(length, seconds + 0.05);
            var source = await decoder.DecodeAsync(path, 0, wanted, ct).ConfigureAwait(false);
            if (source is null || source.Length == 0) return null;
            // A video's sound track can stop before its picture does: pad it with silence to the file's length, so each
            // repeat starts with the picture's.
            var padded = (int)Math.Round(wanted * LyricCardMix.SampleRate) * LyricCardMix.Channels;
            if (role == SoundRole.Background && source.Length < padded) Array.Resize(ref source, padded);
            return new RenderedSound(LyricCardMix.Loop(source, frames, (int)(crossfade * LyricCardMix.SampleRate)));
        }, role);
    }

    /// <summary>
    /// The finished mix for <paramref name="seconds"/>: every layer rendered for that length, brought to the same
    /// loudness, set to its slider level (real gain, above 100% too), the song ducked under the background's sound if
    /// asked, summed, faded over the last <see cref="LyricCardMix.EndFade"/> seconds and limited to -1 dBFS.
    /// <see cref="MixResult.Silent"/> when every layer is off; null when one can't be read or it is cancelled.
    /// </summary>
    public static async Task<MixResult?> MixdownAsync(IReadOnlyList<SoundLayer> layers, double seconds, bool duck,
                                                      Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        const int channels = LyricCardMix.Channels;
        var frames = (int)Math.Round(seconds * LyricCardMix.SampleRate);
        var strands = new List<(SoundRole Role, float[] Samples)>();
        for (var index = 0; index < layers.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            var layer = layers[index];
            var level = LyricCardMix.Decibels(layer.Volume);
            if (!double.IsFinite(level)) continue;
            RenderedSound? rendered;
            try
            {
                rendered = await layer.Render(seconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            if (rendered is null) return null;
            var samples = rendered.Samples;
            if (samples.Length != frames * channels)
            {
                var fitted = new float[frames * channels];
                Array.Copy(samples, fitted, Math.Min(samples.Length, fitted.Length));
                samples = fitted;
            }
            else
            {
                samples = (float[])samples.Clone();
            }
            var gain = LyricCardMix.Linear(LyricCardMix.MatchingGain(samples) + level);
            if (rendered.FadeOut is { } fade) LyricCardMix.FadeOut(samples, fade.End, fade.Seconds);
            for (var i = 0; i < samples.Length; i++) samples[i] *= gain;
            strands.Add((layer.Role, samples));
            progress?.Invoke(0.8 * (index + 1) / layers.Count);
        }
        if (strands.Count == 0) return MixResult.Silent;
        if (cancellationToken.IsCancellationRequested) return null;
        if (duck && strands.FirstOrDefault(s => s.Role == SoundRole.Background).Samples is { } trigger
            && strands.Any(s => s.Role == SoundRole.Song))
        {
            var gains = LyricCardMix.DuckGains(trigger, frames);
            foreach (var strand in strands.Where(s => s.Role == SoundRole.Song))
                for (var frame = 0; frame < frames; frame++)
                    for (var channel = 0; channel < channels; channel++) strand.Samples[frame * channels + channel] *= gains[frame];
        }
        var mix = new float[frames * channels];
        foreach (var strand in strands)
            for (var i = 0; i < mix.Length; i++) mix[i] += strand.Samples[i];
        LyricCardMix.FadeOut(mix, frames, LyricCardMix.EndFade);
        LyricCardMix.Limit(mix);
        progress?.Invoke(1);
        return new MixResult(mix);
    }

    // ---- The song's own sound ----

    /// <summary>The stretch of the song each page of the card plays, in card order (as the frames time each page).</summary>
    public static IReadOnlyList<(double Start, double Duration)> SongWindows(LyricCardContent content)
    {
        var windows = new List<(double, double)>();
        for (var index = 0; index < content.Pages.Count; index++)
        {
            if (content.Pages[index].Animation is not { } animation || animation.FirstInk is not { } firstInk) continue;
            var start = Math.Min(firstInk, Math.Max(animation.Line.Start, firstInk - 0.12));
            var span = animation.End - start;
            if (!double.IsFinite(span) || span <= 0) continue;
            windows.Add((start, span + (index == content.Pages.Count - 1 ? 0.32 : 0)));
        }
        return windows;
    }

    /// <summary>The part of the song that has to be heard: the first window's start to the last one's end.</summary>
    public static (double From, double To)? SongRange(LyricCardContent content)
    {
        var windows = SongWindows(content);
        if (windows.Count == 0) return null;
        var first = windows.Min(w => w.Start);
        var last = windows.Max(w => w.Start + w.Duration);
        return last > first ? (first, last) : null;
    }

    /// <summary>
    /// The song's sound window by window, laid end to end. Where the clip doesn't cover a window the sound is silent; it
    /// fades out over <see cref="LyricCardMix.SongCutFade"/> where it stops, whether the video ends first or carries on.
    /// </summary>
    public static SoundLayer? SongLayer(IAudioDecoder decoder, SongAudioClip clip, IReadOnlyList<(double Start, double Duration)> windows,
                                        float volume = 1)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(clip);
        if (windows.Count == 0 || decoder.AudioDuration(clip.Path) is not { } available) return null;
        return new SoundLayer(volume, async (length, ct) =>
        {
            const double rate = LyricCardMix.SampleRate;
            const int channels = LyricCardMix.Channels;
            var frames = (int)Math.Round(length * rate);
            var output = new float[frames * channels];
            double cursor = 0;
            var lastSound = 0;
            foreach (var window in windows)
            {
                if (cursor >= length) break;
                // Any part of the window before the clip starts stays silent.
                var missing = Math.Max(0, clip.SongStart - window.Start);
                var from = Math.Max(0, window.Start - clip.SongStart);
                var want = Math.Min(window.Duration - missing, length - cursor - missing);
                var seconds = Math.Min(want, available - from);
                if (seconds > 0.02 && await decoder.DecodeAsync(clip.Path, from, seconds, ct).ConfigureAwait(false) is { } samples)
                {
                    var at = (int)Math.Round((cursor + missing) * rate) * channels;
                    var count = Math.Min(samples.Length, output.Length - at);
                    if (count > 0)
                    {
                        Array.Copy(samples, 0, output, at, count);
                        lastSound = (at + count) / channels;
                    }
                }
                cursor += window.Duration;
            }
            // Ending with the video, the MP4's own end fade is enough.
            var cut = windows.Sum(w => w.Duration) > length + 0.01;
            if (lastSound <= 0 || !(cut || lastSound < frames - (int)(rate * 0.01))) return new RenderedSound(output);
            return new RenderedSound(output, (lastSound, LyricCardMix.SongCutFade));
        }, SoundRole.Song);
    }
}

/// <summary>A finished mix: interleaved stereo at 48 kHz, or <see cref="Silent"/> when every layer was off.</summary>
public sealed record MixResult(float[]? Samples)
{
    public static MixResult Silent { get; } = new((float[]?)null);

    public bool IsSilent => Samples is null;
}

/// <summary>
/// The song recordings Lunalyr makes for cards: named <c>Lunalyr song {guid}.wav</c> in one folder, deleted when no
/// longer used (the Mac's <c>SongAudioSource</c> file rules). Only Lunalyr's own recordings are ever deleted, never a
/// song's own file.
/// </summary>
public static class SongRecordings
{
    public const string Prefix = "Lunalyr song ";
    public const string Extension = ".wav";

    public static string NewPath(string folder) => Path.Combine(folder, $"{Prefix}{Guid.NewGuid()}{Extension}");

    /// <summary>Only Lunalyr's own recordings, in <paramref name="folder"/> itself: never a song file from a library.</summary>
    public static bool IsRecording(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return false;
        var name = Path.GetFileName(path);
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return false;
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        return parent is not null && string.Equals(Path.TrimEndingDirectorySeparator(parent),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Deletes a recording that is no longer used. The song's own file is never touched.</summary>
    public static void Discard(SongAudioClip? clip, string folder)
    {
        if (clip is null || !IsRecording(clip.Path, folder)) return;
        try { File.Delete(clip.Path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Deletes recordings left by an earlier run (quit or crash mid-card).</summary>
    public static void DiscardLeftovers(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (!IsRecording(file, folder)) continue;
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>How a recording is getting on (the Mac's <c>SongAudioSource.RecordingUpdate</c>).</summary>
public abstract record RecordingUpdate
{
    /// <summary>Waiting for the song to reach the lines.</summary>
    public sealed record Waiting : RecordingUpdate;

    /// <summary><paramref name="Elapsed"/> of the <paramref name="Needed"/> seconds are recorded; <paramref name="SecondsLeft"/> is the countdown.</summary>
    public sealed record Recording(double Elapsed, double Needed, int SecondsLeft) : RecordingUpdate;

    /// <summary>The update to publish now, or null when the countdown shows the same second as <paramref name="last"/>: the window redraws once a second.</summary>
    public static RecordingUpdate? Next(double elapsed, double needed, RecordingUpdate? last)
    {
        var left = Math.Max(0, (int)Math.Ceiling(needed - elapsed));
        if (last is Recording { SecondsLeft: var shown } && shown == left) return null;
        return new Recording(Math.Min(Math.Max(0, elapsed), needed), needed, left);
    }
}
