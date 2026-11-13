// LyricCardPreview [out-folder]: lyric cards of the --demo songs, made offscreen by the app's own exporter, for looking
// at them: stills in every shape, a frame of every layout, cards over a picture, a GIF and a video, the MP4's length
// rules as frame strips, a GIF, and MP4s with sound mixed from synthetic tones (checked by decoding, never played).
// Writes into docs/previews/lyric-cards by default. Uses the GPU (WARP with -warp).

using System.Globalization;
using System.Text;
using Lunalyr.Core.Cards;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Platform.Imaging;
using Lunalyr.Platform.Media;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Cards;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Cards;
using Lunalyr.Shell.Demo;
using Lunalyr.Shell.Rendering;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var hardware = !args.Contains("-warp");
var output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith('-')) ?? Path.Combine("docs", "previews", "lyric-cards"));
Directory.CreateDirectory(output);
var work = Directory.CreateTempSubdirectory("lunalyr-card-preview-").FullName;
var report = new StringBuilder();
void Say(string line)
{
    Console.WriteLine(line);
    report.AppendLine(line);
}

var readings = WindowsReadingServices.Create(Path.Combine(work, "Dictionaries"));
var exporter = new LyricCardExporter(WallpaperLayers.Factories(readings.WordBreaker), AudioFileDecoder.Instance, hardware);
try
{
    var english = Scene(DemoSongs.Songs[0], readings);
    var japanese = Scene(DemoSongs.Songs[1], readings);
    var style = LyricStyle.Default with { Readings = ReadingPlacement.Below };
    // -colour <mode>: only the English portrait still, in that text colour (cards take the Lyric style's colour).
    if (Array.IndexOf(args, "-colour") is var at and >= 0 && at + 1 < args.Length)
    {
        var mode = LyricColourModes.SavedChoice(args[at + 1]) ?? LyricColourModes.DefaultMode;
        var coloured = LyricCardBuilder.Build(english, Pick(english, "Hold me"), "editorial", CardAspect.Portrait,
                                              style with { ColourMode = mode }, reduceMotion: false);
        Save($"still-english-{mode.RawValue()}.png", exporter.Still(coloured, 0.4));
        return;
    }

    // The English song's "Hold me (oh oh) through the night" (backing vocals) and the two lines after it; the Japanese
    // song's first three lines.
    var englishRows = Pick(english, "Hold me");
    var japaneseRows = LyricCardBuilder.Rows(japanese).Take(3).Select(r => r.Id).ToList();
    Say($"English rows: {string.Join(", ", englishRows)}; Japanese rows: {string.Join(", ", japaneseRows)}");

    // ---- Stills in every shape (the PNG, at the preview's 0.4 of the card) ----
    foreach (var (name, scene, rows) in new[] { ("english", english, englishRows), ("japanese", japanese, japaneseRows) })
    {
        foreach (var aspect in CardAspects.All)
        {
            var job = LyricCardBuilder.Build(scene, rows, "editorial", aspect, style, reduceMotion: false);
            Save($"still-{name}-{aspect.Raw()}.png", exporter.Still(job, 0.4));
        }
        var one = LyricCardBuilder.Build(scene, rows.Take(1).ToList(), "editorial", CardAspect.Portrait, style, false);
        Save($"still-{name}-one-line.png", exporter.Still(one, 0.4));
    }
    var songAlone = LyricCardBuilder.Build(english with { Lyrics = null }, [], "editorial", CardAspect.Portrait, style, false);
    Save("still-song-alone.png", exporter.Still(songAlone, 0.4));
    var hebrew = new LyricCardJob
    {
        Content = new LyricCardContent { Pages = [new CardPage(["אני", "אוהב", "את", "הלילה"])], Title = "Invented", Artist = DemoSongs.Artist, Aspect = CardAspect.Square },
        Look = CardLook.ForCard(english.Palette, english.Cover, style),
    };
    Save("still-right-to-left.png", exporter.Still(hebrew, 0.4));

    // ---- A frame of every layout, mid-line (what the MP4 and GIF are drawn with) ----
    foreach (var kind in Enum.GetValues<CompositionKind>())
    {
        var id = kind.Id();
        foreach (var (name, scene, row) in new[] { ("english", english, englishRows[0]), ("japanese", japanese, japaneseRows[0]) })
        {
            var job = LyricCardBuilder.Build(scene, [row], id, CardAspect.Portrait, style, reduceMotion: false);
            var frames = new List<(byte[], CardFrame)>();
            exporter.RenderFrames(job, 0.4, 0.1, null, null, (pixels, _, frame) =>
            {
                frames.Add(((byte[])pixels.Clone(), frame));
                return true;
            });
            var (pixelsAt, _) = frames[(int)(frames.Count * 0.7)];
            var (w, h) = Size(CardAspect.Portrait, 0.4);
            Save($"layout-{id}-{name}.png", (w, h, pixelsAt));
        }
    }

    // ---- Backgrounds: a picture, an animated GIF and a video ----
    var picture = Path.Combine(work, "picture.png");
    File.WriteAllBytes(picture, CardImages.EncodePng(Paint(1600, 900, Landscape), 1600, 900));
    var gif = Path.Combine(work, "loop.gif");
    File.WriteAllBytes(gif, CardImages.EncodeGif([.. Enumerable.Range(0, 12).Select(i => (Paint(480, 480, (x, y, w, h) => Orbit(x, y, w, h, i / 12.0)), 0.1))], 480, 480)!);
    var clip = Path.Combine(work, "clip.mp4");
    WriteClip(clip, 5);
    var tone = Path.Combine(work, "tone.wav");
    WavFile.Write(tone, Sine(330, 0.25, 5, 48_000), 1, 48_000);
    CardSoundMuxer.Mux(clip, LyricCardMix.Loop(LyricCardMix.ToStereo48k(Sine(330, 0.25, 1, 48_000), 1, 48_000), 5 * 48_000));
    foreach (var (name, path) in new[] { ("picture", picture), ("gif", gif), ("video", clip) })
    {
        using var background = await CardBackgroundSource.LoadAsync(path) ?? throw new InvalidOperationException($"{name} didn't load");
        Say($"Background {name}: {background.Info.Kind}, {background.Info.Duration:0.00} s, sound {background.Info.HasAudio}");
        var palette = CardLook.PaletteFor(ToArtwork(background.First));
        var job = LyricCardBuilder.Build(english, englishRows.Take(1).ToList(), "editorial", CardAspect.Portrait, style, false)
            .WithBackground(background, palette);
        Save($"background-{name}-still.png", exporter.Still(job, 0.4));
        Strip($"background-{name}-frames.png", job, 0.25, LyricCardTiming.Plan(job.Content), 6);
    }

    // ---- The MP4's length: Match background, Match lyrics, Max 60 s, and a short video that loops ----
    {
        using var background = await CardBackgroundSource.LoadAsync(clip) ?? throw new InvalidOperationException("clip");
        var baseJob = LyricCardBuilder.Build(english, englishRows.Take(1).ToList(), "spoken", CardAspect.Square, style, false)
            .WithBackground(background, null);
        foreach (var (name, length) in new[] { ("match-background", ClipLength.MatchBackground), ("match-lyrics", ClipLength.MatchLyrics) })
        {
            var job = baseJob with { Content = baseJob.Content with { ClipLength = length } };
            var plan = LyricCardTiming.Plan(job.Content);
            Say($"Length {name}: {plan.Duration:0.00} s, lyrics {plan.LyricsDuration:0.00} s, offers choice {plan.OffersChoice}, "
                + $"loop crossfade {plan.BackgroundLoop?.Crossfade:0.0}; \"{plan.Summary}\"");
            Strip($"length-{name}.png", job, 0.25, plan, 8);
        }
        var three = LyricCardBuilder.Build(english, englishRows, "editorial", CardAspect.Square, style, false).WithBackground(background, null);
        var cut = LyricCardTiming.Plan(three.Content);
        Say($"Length three lines over 5 s: {cut.Duration:0.00} s, lines cut {cut.LinesCut}; \"{cut.Summary}\"");
        var all = LyricCardBuilder.Build(english, [.. LyricCardBuilder.Rows(english).Select(r => r.Id)], "editorial", CardAspect.Square, style, false);
        var capped = LyricCardTiming.Plan(all.Content with { CapsLength = true });
        Say($"Length whole song, Max 60 s: {LyricCardTiming.Plan(all.Content).Duration:0.0} s → {capped.Duration:0.0} s; \"{capped.Summary}\"");
    }

    // ---- Line-timed lyrics (LRC: the line's start and end, words spread by estimate, not word-timed) ----
    {
        string[] texts = ["Every", "little", "window", "on", "the", "boulevard"];
        var words = texts.Select((w, i) => new LyricWord(w, 20 + i * 0.4, 20 + i * 0.4 + 0.38)).ToList();
        var line = new LyricLine { Start = 20, End = 22.6, Text = string.Join(' ', texts), Words = words, WordTimed = false };
        var scene = english with { Lyrics = new LyricResult.Synced([line], new LyricProvenance("Line-by-line", "LRCLIB")), WordReadings = new Dictionary<int, IReadOnlyList<LyricWord>>(), Readings = new Dictionary<int, string>() };
        foreach (var id in new[] { "editorial", "spoken", "scattered" })
        {
            var job = LyricCardBuilder.Build(scene, [0], id, CardAspect.Square, style, false);
            Strip($"line-timed-{id}.png", job, 0.25, LyricCardTiming.Plan(job.Content), 6);
        }
    }

    // ---- A GIF (at the preview's scale, to keep the repository small) ----
    {
        var job = LyricCardBuilder.Build(english, englishRows.Take(1).ToList(), "cutout", CardAspect.Square, style, false);
        var bytes = exporter.Gif(job, 0.4)!;
        File.WriteAllBytes(Path.Combine(output, "card-cutout.gif"), bytes);
        var info = CardImages.ReadGif(Path.Combine(output, "card-cutout.gif"))!;
        Say($"GIF: {info.Frames.Count} frames of {info.Frames[0].Width}×{info.Frames[0].Height}, delays {info.Frames.Min(f => f.Delay):0.00}–{info.Frames.Max(f => f.Delay):0.00} s, loop {info.LoopCount}");
    }

    // ---- One export of every format, the window's own path (ExportAsync), with a sound file under the MP4 ----
    {
        var job = LyricCardBuilder.Build(english, englishRows, "editorial", CardAspect.Portrait, style, false);
        var tone2 = Path.Combine(work, "export-tone.wav");
        WavFile.Write(tone2, Sine(440, 0.3, 2, 44_100), 1, 44_100);
        job = job with { Content = job.Content with { ExtraSounds = [new CardSoundTrack(tone2)] } };
        var phases = new List<string>();
        var notes = new List<string>();
        var files = await exporter.ExportAsync(job, new HashSet<CardFormat>(CardFormats.All), Path.Combine(work, "export"), notes.Add, p => phases.Add(p.Title));
        Say($"Export: {string.Join(", ", files.Select(f => $"{Path.GetFileName(f.Path)} {new FileInfo(f.Path).Length / 1024} KB"))}; phases {string.Join(" → ", phases)}"
            + (notes.Count > 0 ? $"; notes: {string.Join(" / ", notes)}" : ""));
        var mp4 = files.First(f => f.Format == CardFormat.Mp4).Path;
        Check("Exported MP4", mp4, []);
        var gifInfo = CardImages.ReadGif(files.First(f => f.Format == CardFormat.Gif).Path)!;
        Say($"Exported GIF: {gifInfo.Frames.Count} frames of {gifInfo.Frames[0].Width}×{gifInfo.Frames[0].Height}, loop {gifInfo.LoopCount}");
        var png = await CardBackgroundSource.LoadAsync(files.First(f => f.Format == CardFormat.Png).Path);
        Say($"Exported PNG: {png?.First.Width}×{png?.First.Height}");
        png?.Dispose();
    }

    // ---- MP4s with sound (synthetic tones), decoded back and measured ----
    {
        var job = LyricCardBuilder.Build(english, englishRows.Take(1).ToList(), "editorial", CardAspect.Square, style, false);
        // Silent audio: a file of silence mixed under the card.
        var silence = Path.Combine(work, "silence.wav");
        WavFile.Write(silence, new float[48_000 * 2], 1, 48_000);
        var silentJob = job with { Content = job.Content with { SoundFile = silence } };
        var notes = new List<string>();
        var made = await exporter.ExportAsync(silentJob, new HashSet<CardFormat> { CardFormat.Mp4 }, Path.Combine(work, "silent"), notes.Add);
        Check("MP4 with silent audio", made[0].Path, notes);

        var song = Path.Combine(work, "song.wav");
        WavFile.Write(song, Sine(600, 0.5, 60, 48_000), 1, 48_000);
        var range = LyricCardSound.SongRange(job.Content)!.Value;
        foreach (var (songLevel, videoLevel) in new[] { (1.0, 1.0), (0.5, 1.0), (1.5, 1.0), (1.0, 0.5) })
        {
            using var background = await CardBackgroundSource.LoadAsync(clip) ?? throw new InvalidOperationException("clip");
            var mixJob = job.WithBackground(background, null);
            mixJob = mixJob with
            {
                Content = mixJob.Content with
                {
                    SongAudio = new SongAudioClip(song, 0, 60), SoundVolume = songLevel, BackgroundVolume = videoLevel,
                    ClipLength = ClipLength.MatchLyrics,
                },
            };
            notes.Clear();
            var files = await exporter.ExportAsync(mixJob, new HashSet<CardFormat> { CardFormat.Mp4 }, Path.Combine(work, $"mix-{songLevel}-{videoLevel}"), notes.Add);
            var mono = Mono(files[0].Path);
            var songDb = Db(Goertzel(600, mono, 4800, mono.Length - 24000));
            var videoDb = Db(Goertzel(330, mono, 4800, mono.Length - 24000));
            Say($"Mix song {LyricCardMix.Label(songLevel)}, video {LyricCardMix.Label(videoLevel)}: song {songDb:0.0} dB, video {videoDb:0.0} dB, "
                + $"song − video {songDb - videoDb:0.0} dB (asked {LyricCardMix.Decibels(songLevel) - LyricCardMix.Decibels(videoLevel):0.0}), "
                + $"peak {mono.Max(Math.Abs):0.000}{(notes.Count > 0 ? $"; notes: {string.Join(" / ", notes)}" : "")}");
        }
    }
}
finally
{
    File.WriteAllText(Path.Combine(output, "report.txt"), report.ToString());
    try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

void Check(string what, string path, List<string> notes)
{
    var info = MediaProbe.Read(path)!;
    var mono = Mono(path);
    using var reader = VideoReader.Open(path, 0, out _)!;
    var decoded = 0;
    while (true)
    {
        var result = reader.Read(out var frame);
        frame.Dispose();
        if (result == VideoRead.Frame) decoded++;
        else if (result is VideoRead.EndOfStream or VideoRead.Failed) break;
    }
    Say($"{what}: H.264 {info.IsH264} {info.Width}×{info.Height}, {info.VideoFrames} frames ({decoded} decoded), {info.Duration:0.00} s, "
        + $"{info.FrameRate:0.0} fps; AAC {info.IsAac}, {info.AudioSeconds:0.00} s, {mono.Length} samples decoded, peak {mono.DefaultIfEmpty().Max(Math.Abs):0.0000}"
        + (notes.Count > 0 ? $"; notes: {string.Join(" / ", notes)}" : ""));
}

void Strip(string name, LyricCardJob job, double scale, ClipPlan plan, int count)
{
    var shots = new List<(double Time, byte[] Pixels)>();
    var time = 0.0;
    exporter.RenderFrames(job, scale, 1 / 30.0, null, plan, (pixels, delay, _) =>
    {
        shots.Add((time, (byte[])pixels.Clone()));
        time += delay;
        return true;
    });
    var (w, h) = Size(job.Content.Aspect, scale);
    var picks = Enumerable.Range(0, count).Select(i => shots.MinBy(s => Math.Abs(s.Time - plan.Duration * (i + 0.5) / count)).Pixels).ToList();
    const int gap = 8;
    var sheetWidth = count * w + (count - 1) * gap;
    var sheet = new byte[sheetWidth * h * 4];
    for (var i = 0; i < picks.Count; i++)
        for (var y = 0; y < h; y++)
            Array.Copy(picks[i], y * w * 4, sheet, (y * sheetWidth + i * (w + gap)) * 4, w * 4);
    Save(name, (sheetWidth, h, sheet));
    Say($"{name}: {shots.Count} frames over {time:0.00} s; strip at {string.Join(", ", Enumerable.Range(0, count).Select(i => (plan.Duration * (i + 0.5) / count).ToString("0.0")))} s");
}

void Save(string name, (int Width, int Height, byte[] Pixels) image)
{
    File.WriteAllBytes(Path.Combine(output, name), CardImages.EncodePng(image.Pixels, image.Width, image.Height));
    Console.WriteLine($"Wrote {name}");
}

static (int W, int H) Size(CardAspect aspect, double scale) =>
    ((int)Math.Round(aspect.Size().Width * scale), (int)Math.Round(aspect.Size().Height * scale));

static SceneSnapshot Scene(DemoSong song, ReadingServices readings)
{
    var pixels = ArtworkDecoder.Decode(song.Cover)!;
    var palette = PaletteMapper.FromAlbum(new AlbumWallpaperPalette(AlbumColourGrid.Extract(pixels)));
    var synced = (LyricResult.Synced)song.Lyrics;
    var words = new LyricRomanizer(readings).Readings(synced.Lines, RomanizationOptions.Default);
    return new SceneSnapshot
    {
        Title = song.Title, Artist = song.Artist, Album = song.Album, Lyrics = song.Lyrics, Palette = palette,
        Cover = ArtworkDecoder.DecodeCover(song.Cover),
        WordReadings = words.ToDictionary(p => p.Key, p => (IReadOnlyList<LyricWord>)p.Value),
        Readings = words.ToDictionary(p => p.Key, p => string.Join(' ', p.Value.Select(w => w.Text).Where(t => t.Length > 0))),
    };
}

static List<int> Pick(SceneSnapshot scene, string start)
{
    var rows = LyricCardBuilder.Rows(scene);
    var first = rows.ToList().FindIndex(r => r.Text.StartsWith(start, StringComparison.Ordinal));
    return [.. rows.Skip(Math.Max(0, first)).Take(3).Select(r => r.Id)];
}

static ArtworkPixels ToArtwork(CoverArt art)
{
    var rgba = new byte[art.Pixels.Length];
    for (var i = 0; i < rgba.Length; i += 4)
    {
        rgba[i] = art.Pixels[i + 2];
        rgba[i + 1] = art.Pixels[i + 1];
        rgba[i + 2] = art.Pixels[i];
        rgba[i + 3] = 255;
    }
    return new ArtworkPixels(art.Width, art.Height, rgba);
}

static byte[] Paint(int w, int h, Func<int, int, int, int, (byte R, byte G, byte B)> colour)
{
    var pixels = new byte[w * h * 4];
    for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (r, g, b) = colour(x, y, w, h);
            var o = (y * w + x) * 4;
            pixels[o] = b;
            pixels[o + 1] = g;
            pixels[o + 2] = r;
            pixels[o + 3] = 255;
        }
    return pixels;
}

// A sky: pale blue at the top to warm sand at the bottom, a sun right of centre (as WallpaperTestMedia paints it).
static (byte, byte, byte) Landscape(int x, int y, int w, int h)
{
    double t = y / (double)h, dx = x - w * 0.68, dy = y - h * 0.42, sun = Math.Exp(-(dx * dx + dy * dy) / (2 * Math.Pow(h * 0.08, 2)));
    static byte Mix(double a, double b, double t, double sun) => (byte)Math.Clamp(a + (b - a) * t + sun * 80, 0, 255);
    return (Mix(150, 235, t, sun), Mix(200, 205, t, sun), Mix(245, 160, t, sun));
}

// A disc going round on a teal field.
static (byte, byte, byte) Orbit(int x, int y, int w, int h, double t)
{
    double cx = w * (0.5 + 0.3 * Math.Cos(t * 2 * Math.PI)), cy = h * (0.5 + 0.3 * Math.Sin(t * 2 * Math.PI));
    var inside = (x - cx) * (x - cx) + (y - cy) * (y - cy) < (w * 0.12) * (w * 0.12);
    return inside ? ((byte)250, (byte)200, (byte)90) : ((byte)20, (byte)(80 + 60 * y / h), (byte)(100 + 40 * x / w));
}

static void WriteClip(string path, double seconds)
{
    const int w = 640, h = 360, fps = 30;
    using var writer = new CardVideoWriter(path, w, h, fps, 2_000_000);
    for (var i = 0; i < seconds * fps; i++)
    {
        var t = i / (seconds * fps);
        writer.Write(Paint(w, h, (x, y, ww, hh) =>
        {
            var phase = (x + y) / (double)(ww + hh) + t;
            return ((byte)(128 + 100 * Math.Sin(2 * Math.PI * phase)), (byte)(110 + 90 * Math.Sin(2 * Math.PI * (phase + 1 / 3.0))),
                    (byte)(140 + 100 * Math.Sin(2 * Math.PI * (phase + 2 / 3.0))));
        }), 1.0 / fps);
    }
    writer.Finish();
}

static float[] Sine(double frequency, double amplitude, double seconds, double rate) =>
    [.. Enumerable.Range(0, (int)(seconds * rate)).Select(i => (float)(Math.Sin(i * 2 * Math.PI * frequency / rate) * amplitude))];

static float[] Mono(string path)
{
    var stereo = AudioFileDecoder.Instance.DecodeAsync(path).GetAwaiter().GetResult() ?? [];
    var mono = new float[stereo.Length / 2];
    for (var i = 0; i < mono.Length; i++) mono[i] = (stereo[2 * i] + stereo[2 * i + 1]) / 2;
    return mono;
}

static double Goertzel(double frequency, float[] samples, int from, int to)
{
    var w = 2 * Math.PI * frequency / 48_000;
    var c = 2 * Math.Cos(w);
    double s1 = 0, s2 = 0;
    for (var i = from; i < to; i++)
    {
        var s = samples[i] + c * s1 - s2;
        s2 = s1;
        s1 = s;
    }
    return 2 * Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - c * s1 * s2)) / Math.Max(1, to - from);
}

static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-12));
