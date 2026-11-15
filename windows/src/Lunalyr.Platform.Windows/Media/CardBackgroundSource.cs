// A picture, animated GIF or video behind a lyric card (the Mac's LyricCardBackground, Lunalyr's own code): opened
// once, its frames read as they are needed, so a long video costs no more memory than a picture. Videos are read with
// only their video stream selected (their sound is read separately, decoded, only when the MP4 carries it).

using Lunalyr.Core.Cards;
using Lunalyr.Core.Colour;
using Lunalyr.Platform.Wallpaper;

namespace Lunalyr.Platform.Media;

/// <summary>A card's background, its first frame and its frames by time. Not thread-safe: one exporter reads it.</summary>
public sealed class CardBackgroundSource : IDisposable
{
    /// <summary>Pictures and frames are read at most this many pixels on their longer side.</summary>
    public const int MaxSide = 1920;

    private readonly AnimatedPicture? _animated;
    private readonly string? _video;
    private VideoCursor? _cursor;
    private VideoCursor? _seamCursor;

    private CardBackgroundSource(CardBackgroundInfo info, CoverArt first, AnimatedPicture? animated = null, string? video = null)
    {
        Info = info;
        First = first;
        _animated = animated;
        _video = video;
    }

    public CardBackgroundInfo Info { get; }

    /// <summary>The first frame (stills, the preview, the colours).</summary>
    public CoverArt First { get; }

    /// <summary>The file types the picker offers: the pictures and videos Windows can open.</summary>
    public static IReadOnlyList<string> Extensions => [.. WallpaperPicture.Extensions, ".gif", .. WallpaperPicture.VideoExtensions];

    /// <summary>Opens a picture, GIF or video; null when it can't be read.</summary>
    public static async Task<CardBackgroundSource?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (WallpaperPicture.IsVideo(path)) return await Task.Run(() => LoadVideo(path), cancellationToken).ConfigureAwait(false);
        if (string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase)
            && await Task.Run(() => AnimatedPicture.Open(path), cancellationToken).ConfigureAwait(false) is { } animated)
        {
            var first = animated.Frame(0);
            return new CardBackgroundSource(new CardBackgroundInfo(path, CardBackgroundKind.AnimatedImage, animated.Duration), first, animated);
        }
        CoverArt? still;
        try
        {
            still = await WallpaperPicture.DecodeAsync(path, MaxSide, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            still = null;
        }
        return still is null ? null : new CardBackgroundSource(new CardBackgroundInfo(path, CardBackgroundKind.Image, 0), still);
    }

    private static CardBackgroundSource? LoadVideo(string path)
    {
        using var cursor = VideoCursor.Open(path);
        if (cursor is null || !(cursor.Duration > 0)) return null;
        var first = cursor.FrameAt(0);
        if (first is null) return null;
        var info = new CardBackgroundInfo(path, CardBackgroundKind.Video, cursor.Duration, AudioFileDecoder.HasAudio(path));
        return new CardBackgroundSource(info, first, video: path);
    }

    /// <summary>The frame showing <paramref name="time"/> seconds in, looping when the clip is shorter.</summary>
    public CoverArt FrameAt(double time)
    {
        switch (Info.Kind)
        {
            case CardBackgroundKind.AnimatedImage:
                return _animated!.FrameAt(time);
            case CardBackgroundKind.Video:
                var duration = Info.Duration;
                var position = Math.Min(Math.Max(0, time % duration), Math.Max(0, duration - 0.05));
                _cursor ??= VideoCursor.Open(_video!);
                return _cursor?.FrameAt(position) ?? First;
            default:
                return First;
        }
    }

    /// <summary>
    /// The frame showing <paramref name="time"/> seconds in when the clip repeats as <paramref name="loop"/> says: inside
    /// a seam, the end of one pass blended into the start of the next (a second reader keeps the end, so neither seeks).
    /// </summary>
    public CoverArt FrameAt(double time, LoopTiming loop)
    {
        var sample = loop.Sample(time);
        var current = FrameAt(sample.Position);
        if (sample.Previous is not { } previous) return current;
        CoverArt before;
        if (Info.Kind == CardBackgroundKind.Video)
        {
            _seamCursor ??= VideoCursor.Open(_video!);
            before = _seamCursor?.FrameAt(Math.Min(previous, Math.Max(0, Info.Duration - 0.05))) ?? current;
        }
        else
        {
            before = FrameAt(previous);
        }
        return Blend(before, current, sample.Weight);
    }

    /// <summary><paramref name="top"/> drawn over <paramref name="bottom"/> at <paramref name="weight"/> (same sizes; else the top).</summary>
    public static CoverArt Blend(CoverArt bottom, CoverArt top, double weight)
    {
        if (weight <= 0) return bottom;
        if (weight >= 1 || bottom.Width != top.Width || bottom.Height != top.Height) return top;
        var pixels = new byte[bottom.Pixels.Length];
        var w = (float)weight;
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)Math.Round(bottom.Pixels[i] + (top.Pixels[i] - bottom.Pixels[i]) * w);
        return new CoverArt(bottom.Width, bottom.Height, pixels);
    }

    public void Dispose()
    {
        _animated?.Dispose();
        _cursor?.Dispose();
        _seamCursor?.Dispose();
    }

    /// <summary>
    /// Reads a video forward, frame by frame, for times that mostly increase (a card's frames): it seeks only to go back
    /// or far ahead, then decodes on to the frame showing the time asked for.
    /// </summary>
    private sealed unsafe class VideoCursor : IDisposable
    {
        private readonly VideoReader _reader;
        private CoverArt? _shown;
        private double _shownAt = double.NegativeInfinity;
        private CoverArt? _ahead;
        private double _aheadAt = double.NaN;
        private bool _ended;

        private VideoCursor(VideoReader reader) => _reader = reader;

        public double Duration => _reader.Duration;

        public static VideoCursor? Open(string path)
        {
            var reader = VideoReader.Open(path, 0, out _);
            return reader is null ? null : new VideoCursor(reader);
        }

        public CoverArt? FrameAt(double time)
        {
            // Back in time, or far ahead: seek. Otherwise decode on from where it is.
            if (_shown is null && double.IsNaN(_aheadAt) || time < _shownAt - 1e-6 || time > _shownAt + 2) Restart(time);
            // Decode on until the next frame starts after the time asked for.
            while (!_ended)
            {
                if (double.IsNaN(_aheadAt) && !ReadNext()) break;
                if (_aheadAt > time + 1e-6) break;
                _shown = _ahead;
                _shownAt = _aheadAt;
                _ahead = null;
                _aheadAt = double.NaN;
            }
            return _shown ?? _ahead;
        }

        private void Restart(double time)
        {
            _ended = false;
            _ahead = null;
            _aheadAt = double.NaN;
            _shown = null;
            _shownAt = double.NegativeInfinity;
            if (time > 0.5) _reader.Seek(time);
            else _reader.Rewind();
        }

        private bool ReadNext()
        {
            for (var tries = 0; tries < 64; tries++)
            {
                var result = _reader.Read(out var frame);
                try
                {
                    if (result == VideoRead.Frame && frame.Pixels is not null)
                    {
                        _ahead = Copy(frame);
                        _aheadAt = frame.Time;
                        return true;
                    }
                    if (result is VideoRead.EndOfStream or VideoRead.Failed)
                    {
                        _ended = true;
                        return false;
                    }
                }
                finally
                {
                    frame.Dispose();
                }
            }
            _ended = true;
            return false;
        }

        /// <summary>A software-decoded frame as an opaque, downscaled-if-needed bitmap.</summary>
        private CoverArt Copy(VideoFrame frame)
        {
            int w = _reader.Width, h = _reader.Height;
            var step = Math.Max(1, (int)Math.Ceiling(Math.Max(w, h) / (double)MaxSide));
            int ow = Math.Max(1, w / step), oh = Math.Max(1, h / step);
            var pixels = new byte[ow * oh * 4];
            for (var y = 0; y < oh; y++)
            {
                var row = frame.Pixels + (long)y * step * frame.Pitch;
                for (var x = 0; x < ow; x++)
                {
                    var source = row + x * step * 4;
                    var o = (y * ow + x) * 4;
                    pixels[o] = source[0];
                    pixels[o + 1] = source[1];
                    pixels[o + 2] = source[2];
                    pixels[o + 3] = 255;
                }
            }
            return new CoverArt(ow, oh, pixels);
        }

        public void Dispose() => _reader.Dispose();
    }
}
