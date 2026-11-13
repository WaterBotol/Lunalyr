using Lunalyr.Core.Cards;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Platform.Media;
using Lunalyr.Render.Cards;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Cards;
using Lunalyr.Shell.Rendering;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests.Cards;

/// <summary>
/// Synthetic media for the card tests (the Mac's MixProbe): tones written as WAV, small H.264 clips of solid colours,
/// cards drawn on WARP, and files decoded back for checking. Nothing is ever played.
/// </summary>
internal sealed class CardMedia : IDisposable
{
    public CardMedia() => Folder = Directory.CreateTempSubdirectory("lunalyr-card-").FullName;

    public string Folder { get; }

    public string File(string name) => Path.Combine(Folder, name);

    public static LyricCardExporter Exporter() =>
        new(WallpaperLayers.Factories(ReadingServices.Basic.WordBreaker), AudioFileDecoder.Instance, hardware: false);

    /// <summary>A 64-pixel square cover painted in code: an orange to purple diagonal.</summary>
    public static CoverArt Cover(int size = 64)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var t = (x + y) / (2.0 * size);
                var o = (y * size + x) * 4;
                pixels[o] = (byte)(40 + 160 * t);
                pixels[o + 1] = (byte)(140 - 100 * t);
                pixels[o + 2] = (byte)(240 - 80 * t);
                pixels[o + 3] = 255;
            }
        return new CoverArt(size, size, pixels);
    }

    /// <summary>A card job for <paramref name="content"/>, coloured by <see cref="Cover"/> (or the fallback colours).</summary>
    public static LyricCardJob Job(LyricCardContent content, bool cover = true)
    {
        var art = cover && content.HasArtwork ? Cover() : null;
        var palette = art is null ? PaletteMapper.Fallback : CardLook.PaletteFor(ArtworkFrom(art));
        return new LyricCardJob { Content = content, Look = CardLook.ForCard(palette, art, LyricStyle.Default) };
    }

    public static ArtworkPixels ArtworkFrom(CoverArt art)
    {
        var rgba = new byte[art.Pixels.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = art.Pixels[i + 2];
            rgba[i + 1] = art.Pixels[i + 1];
            rgba[i + 2] = art.Pixels[i];
            rgba[i + 3] = art.Pixels[i + 3];
        }
        return new ArtworkPixels(art.Width, art.Height, rgba);
    }

    /// <summary>A sine tone as a WAV (mono at 44.1 kHz unless asked), the Mac's <c>MixProbe.tone</c>.</summary>
    public string Tone(string name, double frequency, double amplitude, double seconds, double rate = 44_100, int channels = 1)
    {
        var mono = CardProbe.MonoSine(frequency, amplitude, seconds, rate);
        var samples = channels == 1 ? mono : mono.SelectMany(s => Enumerable.Repeat(s, channels)).ToArray();
        var path = File(name);
        WavFile.Write(path, samples, channels, rate);
        return path;
    }

    /// <summary>A small H.264 video of <paramref name="seconds"/>, coloured by <paramref name="colour"/>(time), 10 frames a second.</summary>
    public string Video(string name, double seconds, Func<double, (byte R, byte G, byte B)>? colour = null, int size = 64)
    {
        colour ??= _ => (51, 102, 153);
        var path = File(name);
        using var writer = new CardVideoWriter(path, size, size, 10, 500_000);
        var frames = (int)Math.Round(seconds * 10);
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < frames; i++)
        {
            var (r, g, b) = colour(i / 10.0);
            for (var p = 0; p < pixels.Length; p += 4)
            {
                pixels[p] = b;
                pixels[p + 1] = g;
                pixels[p + 2] = r;
                pixels[p + 3] = 255;
            }
            writer.Write(pixels, 0.1);
        }
        writer.Finish();
        return path;
    }

    /// <summary>
    /// Lays <paramref name="tone"/>'s sound under <paramref name="video"/>, looped or cut to the video's length (the Mac's
    /// <c>addSound</c>), or to <paramref name="seconds"/> when given (a sound track shorter than its picture).
    /// </summary>
    public static bool AddSound(string video, string tone, double? seconds = null)
    {
        seconds ??= MediaProbe.Read(video)!.Duration;
        var samples = AudioFileDecoder.Instance.DecodeAsync(tone).GetAwaiter().GetResult()!;
        var frames = (int)Math.Round(seconds.Value * LyricCardMix.SampleRate);
        var looped = LyricCardMix.Loop(samples, frames);
        return CardSoundMuxer.Mux(video, looped);
    }

    /// <summary>A file's first audio track decoded at 48 kHz with its two channels averaged (the Mac's <c>decodeMono</c>).</summary>
    public static float[] DecodeMono(string path)
    {
        var stereo = AudioFileDecoder.Instance.DecodeAsync(path).GetAwaiter().GetResult();
        Assert.NotNull(stereo);
        var mono = new float[stereo!.Length / 2];
        for (var i = 0; i < mono.Length; i++) mono[i] = (stereo[2 * i] + stereo[2 * i + 1]) / 2;
        return mono;
    }

    /// <summary>The brightness (0–1) of BGRA pixel (x, y).</summary>
    public static double Brightness(byte[] bgra, int width, int x, int y)
    {
        var o = (y * width + x) * 4;
        return Math.Max(bgra[o], Math.Max(bgra[o + 1], bgra[o + 2])) / 255.0;
    }

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
