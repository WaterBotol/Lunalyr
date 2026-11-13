// Making a lyric card's MP4, GIF and PNG: the Mac's LyricCard.export, video, gif, stillPNG, renderFrames and mux
// (LyricCard.swift, LyricCardExport.swift, Lunalyr's own code). The frames come from Core's schedule and are drawn by
// the Render painter offscreen; the MP4 goes through Media Foundation's Sink Writer, the PNG and GIF through WIC.
// Nothing is played: sound is decoded, mixed and encoded, never rendered.

using Lunalyr.Core.Cards;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Media;
using Lunalyr.Render.Cards;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell.Cards;

/// <summary>Makes cards. Each export draws on its own offscreen device, off the caller's thread.</summary>
public sealed class LyricCardExporter(IEnumerable<IRenderLayerFactory> factories, IAudioDecoder? decoder = null, bool hardware = true,
                                      Logger? logger = null)
{
    private readonly IReadOnlyList<IRenderLayerFactory> _factories = [.. factories];
    private readonly IAudioDecoder _decoder = decoder ?? AudioFileDecoder.Instance;
    private readonly CategoryLogger _log = (logger ?? Logger.Null).App;

    /// <summary>The PNG's scale: twice the card's size (2160 × 2700 for a portrait card).</summary>
    public const double StillScale = 2;

    /// <summary>The card window's preview scale.</summary>
    public const double PreviewScale = 0.4;

    public IAudioDecoder Decoder => _decoder;

    private LyricCardPainter Painter(LyricCardJob job, double scale) =>
        new(job.Content.Aspect, scale, _factories, job.Content.HasTrackDetails, hardware);

    /// <summary>The still the PNG and the preview show: the first few lines together, as one paragraph.</summary>
    public (int Width, int Height, byte[] Pixels) Still(LyricCardJob job, double scale = StillScale)
    {
        ArgumentNullException.ThrowIfNull(job);
        var content = job.Content with { Pages = [job.Content.StillPage] };
        using var painter = Painter(job with { Content = content }, scale);
        var lyrics = content.Pages[0].Words.Count > 0 ? new CardLyrics.Paragraph(content.Pages[0]) : (CardLyrics)new CardLyrics.None();
        painter.Draw(content, job.Look, job.Background?.First, lyrics);
        return (painter.Width, painter.Height, painter.ReadPixels());
    }

    /// <summary>One crisp PNG of the selected lines together, at <paramref name="scale"/> (twice the card's size by default).</summary>
    public byte[] StillPng(LyricCardJob job, double scale = StillScale)
    {
        var (width, height, pixels) = Still(job, scale);
        return CardImages.EncodePng(pixels, width, height);
    }

    /// <summary>
    /// Draws the card's animation one frame at a time, handing each (BGRA, its length in seconds, the frame) to
    /// <paramref name="sink"/>, which says whether to go on. Returns false when there was nothing to draw or it stopped.
    /// </summary>
    public bool RenderFrames(LyricCardJob job, double scale, double minimumCadence, int? budget, ClipPlan? plan,
                             Func<byte[], double, CardFrame, bool> sink, Action<double>? progress = null,
                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(sink);
        var content = job.Content;
        using var painter = Painter(job, scale);
        var pixels = new byte[painter.Width * painter.Height * 4];
        var scenes = new Dictionary<int, CardPageScene>();
        var expected = LyricCardFrames.Expected(content, minimumCadence, budget, plan);
        var produced = 0;
        var any = false;
        CoverArt? Background(double time)
        {
            if (job.Background is not { } background) return null;
            if (!background.Info.IsAnimated) return background.First;
            return plan?.BackgroundLoop is { } loop ? background.FrameAt(time, loop) : background.FrameAt(time);
        }
        foreach (var frame in LyricCardFrames.Schedule(content, minimumCadence, budget, plan))
        {
            if (cancellationToken.IsCancellationRequested) return false;
            any = true;
            var page = content.Pages[frame.Page];
            CardLyrics lyrics;
            if (frame.Kind == CardFrameKind.Timed && page.Animation is { } animation)
            {
                if (!scenes.TryGetValue(frame.Page, out var scene))
                {
                    var readings = frame.Page < job.Readings.Count ? job.Readings[frame.Page] : null;
                    scene = CardPageScene.For(animation, content, job.Look, readings?.Words, readings?.Line);
                    scenes[frame.Page] = scene;
                }
                lyrics = new CardLyrics.Timed(scene, frame.SongTime, frame.CardTime, frame.LyricOpacity, frame.Delay);
            }
            else
            {
                lyrics = page.Words.Count > 0 ? new CardLyrics.Paragraph(page, frame.LyricOpacity) : new CardLyrics.None();
            }
            painter.Draw(content, job.Look, Background(frame.CardTime), lyrics);
            painter.ReadPixels(pixels);
            if (!sink(pixels, frame.Delay, frame)) return false;
            produced++;
            progress?.Invoke(Math.Min(1, produced / (double)expected));
        }
        return any && !cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// An H.264 video of the card's animation at two thirds of its size, 30 frames a second, lasting as
    /// <paramref name="plan"/> says (<see cref="LyricCardTiming.Plan"/> by default). False, with no file left, when it
    /// can't be made or is cancelled.
    /// </summary>
    public bool Video(LyricCardJob job, string path, ClipPlan? plan = null, Action<double>? progress = null,
                      CancellationToken cancellationToken = default)
    {
        plan ??= LyricCardTiming.Plan(job.Content);
        var size = job.Content.Size;
        var width = (int)Math.Round(size.Width * LyricCardFrames.VideoScale);
        var height = (int)Math.Round(size.Height * LyricCardFrames.VideoScale);
        CardVideoWriter? writer = null;
        var ok = false;
        try
        {
            writer = new CardVideoWriter(path, width, height);
            ok = RenderFrames(job, LyricCardFrames.VideoScale, 1 / LyricCardFrames.VideoFramesPerSecond, null, plan,
                (pixels, delay, _) =>
                {
                    writer.Write(pixels, delay);
                    return true;
                }, progress, cancellationToken);
            if (ok) writer.Finish();
            return ok;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _log.Warn("A lyric card's MP4 couldn't be made.", ex);
            ok = false;
            return false;
        }
        finally
        {
            writer?.Dispose();
            if (!ok) TryDelete(path);
        }
    }

    /// <summary>
    /// The card as a looping GIF at its full size. <paramref name="progress"/> runs 0 to
    /// <see cref="LyricCardFrames.GifDrawingShare"/> while the frames are drawn (each is encoded as it comes), then
    /// <paramref name="encoding"/> is called while the file is finished.
    /// </summary>
    public byte[]? Gif(LyricCardJob job, double scale = LyricCardFrames.GifScale, Action<double>? progress = null, Action? encoding = null,
                       CancellationToken cancellationToken = default)
    {
        var size = job.Content.Size;
        using var gif = new GifWriter((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));
        var ok = RenderFrames(job, scale, LyricCardFrames.GifCadence, LyricCardFrames.GifFrameBudget(job.Content.Pages.Count), null,
            (pixels, delay, _) =>
            {
                gif.Add(pixels, delay);
                return !cancellationToken.IsCancellationRequested;
            }, value => progress?.Invoke(value * LyricCardFrames.GifDrawingShare), cancellationToken);
        if (!ok || cancellationToken.IsCancellationRequested) return null;
        encoding?.Invoke();
        return gif.Finish();
    }

    // ---- Sound ----

    /// <summary>
    /// Every strand of the card's sound, in the Mac's order: the song, the chosen sound file and the extra sounds, then a
    /// video background's own sound (repeating with the picture, crossfaded at the same seams).
    /// </summary>
    public List<SoundLayer> SoundLayers(LyricCardContent content, ClipPlan plan, Action<string> notes)
    {
        var layers = new List<SoundLayer>();
        var level = (float)content.SoundVolume;
        if (content.SongAudio is { } clip)
        {
            var windows = LyricCardSound.SongWindows(content);
            if (LyricCardSound.SongRange(content) is { } range && !clip.Covers(range.From, range.To))
                notes("Parts of these lines aren’t in the song’s recording, so they’re silent. Record the song again.");
            if (LyricCardSound.SongLayer(_decoder, clip, windows, level) is { } song) layers.Add(song);
            else notes("The MP4 has no song: couldn’t use the song’s audio.");
        }
        var tracks = new List<CardSoundTrack>(content.ExtraSounds);
        if (content.SoundFile is { } file) tracks.Insert(0, new CardSoundTrack(file, content.SoundVolume));
        foreach (var track in tracks)
        {
            if (LyricCardSound.FromFile(_decoder, track.Path, (float)track.Volume) is { } layer) layers.Add(layer);
            else notes($"Couldn’t read the audio in {Path.GetFileName(track.Path)}");
        }
        if (content.BackgroundSoundSource is { } video && content.BackgroundVolume > 0
            && LyricCardSound.FromFile(_decoder, video, (float)content.BackgroundVolume, SoundRole.Background,
                plan.BackgroundLoop?.Crossfade ?? 0) is { } background)
            layers.Add(background);
        return layers;
    }

    /// <summary>
    /// Mixes <paramref name="layers"/> for the video's length and lays the result under <paramref name="video"/>, which
    /// is replaced. True with the video left silent when every layer is off; false, with the file untouched, when it can't
    /// or is cancelled. Progress runs 0 to 1.
    /// </summary>
    public async Task<bool> MuxAsync(string video, double seconds, IReadOnlyList<SoundLayer> layers, bool duck = false,
                                     Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (layers.Count == 0 || !(seconds > 0)) return false;
        // The mixing and the AAC encoding run off the caller's thread: a minute of sound is millions of samples a strand.
        return await Task.Run(async () =>
        {
            var mixed = await LyricCardSound.MixdownAsync(layers, seconds, duck, value => progress?.Invoke(value * 0.8), cancellationToken)
                .ConfigureAwait(false);
            if (mixed is null || cancellationToken.IsCancellationRequested) return false;
            if (mixed.IsSilent) return true; // every layer turned off: leave the video silent
            return CardSoundMuxer.Mux(video, mixed.Samples!, value => progress?.Invoke(0.8 + 0.2 * value), cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---- Export ----

    /// <summary>
    /// Makes each chosen format in <paramref name="directory"/> (MP4, then GIF, then PNG), never over an earlier card
    /// ("Lunalyr Lyric Card 2.mp4"…). Returns the files made; what a cancelled export made is left for the caller to
    /// discard (<see cref="LyricCardExportPlan.DiscardExport"/>).
    /// </summary>
    public async Task<IReadOnlyList<(CardFormat Format, string Path)>> ExportAsync(LyricCardJob job, IReadOnlySet<CardFormat> formats,
        string directory, Action<string>? notes = null, Action<ExportPhase>? phase = null, Action<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        notes ??= _ => { };
        Directory.CreateDirectory(directory);
        var order = CardFormats.All.Where(formats.Contains).ToList();
        var content = job.Content;
        var plan = LyricCardTiming.Plan(content);
        var sound = LyricCardExportPlan.HasSound(content);
        var shares = LyricCardExportPlan.ProgressShares(order, plan.Duration, content.Pages.Count, sound);
        var made = new List<(CardFormat, string)>();
        foreach (var format in order)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var file = LyricCardExportPlan.AvailableFile(LyricCardExportPlan.FileName, format.Raw(), directory);
            var range = shares.TryGetValue(format, out var r) ? r : (0, 1);
            void Share(double value) => progress?.Invoke(range.From + (range.To - range.From) * Math.Clamp(value, 0, 1));
            phase?.Invoke(new ExportPhase.Drawing(format));
            switch (format)
            {
                case CardFormat.Png:
                    var png = await Task.Run(() => StillPng(job), cancellationToken).ConfigureAwait(false);
                    if (await TryWriteAsync(file, png).ConfigureAwait(false)) made.Add((format, file));
                    break;
                case CardFormat.Gif:
                    var gif = await Task.Run(() => Gif(job, progress: Share, encoding: () => phase?.Invoke(new ExportPhase.EncodingGif()),
                        cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
                    if (gif is not null && await TryWriteAsync(file, gif).ConfigureAwait(false)) made.Add((format, file));
                    break;
                default:
                    if (plan.Note is { } note) notes(note);
                    // The frames take the bar up to the sound's share, the mix the rest.
                    var framesShare = sound ? 1 - LyricCardExportPlan.MixShare : 1;
                    var drawn = await Task.Run(() => Video(job, file, plan, value => Share(value * framesShare), cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    if (!drawn) break;
                    made.Add((format, file));
                    var layers = SoundLayers(content, plan, notes);
                    if (layers.Count > 0)
                    {
                        phase?.Invoke(new ExportPhase.MixingSound());
                        var mixed = await MuxAsync(file, plan.Duration, layers, content.DuckSong,
                            value => Share(framesShare + (1 - framesShare) * value), cancellationToken).ConfigureAwait(false);
                        if (!mixed && !cancellationToken.IsCancellationRequested) notes("The MP4 has no sound: couldn’t add it.");
                    }
                    break;
            }
            Share(1);
        }
        return made;
    }

    private static async Task<bool> TryWriteAsync(string path, byte[] data)
    {
        try
        {
            await File.WriteAllBytesAsync(path, data).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Lunalyr's own folder for cards made to be copied: a fresh one per card, under the temporary folder.</summary>
    public static string ExportDirectory(string? root = null) =>
        Path.Combine(root ?? Path.Combine(Path.GetTempPath(), "Lunalyr Lyric Cards"), Guid.NewGuid().ToString());
}
