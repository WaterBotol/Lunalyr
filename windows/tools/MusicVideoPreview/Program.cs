// MusicVideoPreview <timecode.mp4> <out folder> [offset seconds] [fps]
//
// Draws the Music video wallpaper offscreen as the desktop would: the "musicVideo" layer (the clip as the playing song's
// video file) under the Editorial lyrics of the demo's first song ("Neon Harbour"), at moments of the demo's clock, with
// the video offset given. The clip is tools/WallpaperTestMedia's time-coded one (`WallpaperTestMedia <folder> timecode
// 640 360 30 50`): each frame carries its number in blocks across its top third, so the frame drawn is read back from the
// render and checked against the song's time plus the offset. Writes music-video-t<seconds>.png and report.md.

using System.Globalization;
using System.Text;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Compositions;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Demo;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: MusicVideoPreview <timecode.mp4> <out folder> [offset seconds] [clip fps]");
    return 2;
}
var clip = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var offset = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 0;
var fps = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 30;
Directory.CreateDirectory(output);

const int Width = 1280, Height = 720;
var song = DemoSongs.Songs[0];
var palette = ArtworkDecoderPalette(song.Cover);
var feed = new Feed { Inputs = new MusicVideoInputs(clip, 0, true, offset, 0.35, 0) };
using var surface = new OffscreenSurface(Width, Height, 1.25f);
using var video = new MusicVideoLayerFactory(feed).Create();
using var lyrics = new EditorialFactory(ReadingServices.Basic.WordBreaker).Create();
surface.Prepare(video);
surface.Prepare(lyrics);
var scene = new SceneSnapshot
{
    Title = song.Title, Artist = song.Artist, Album = song.Album, Lyrics = song.Lyrics, Palette = palette, Duration = song.Duration,
    Style = LyricStyle.Default with { Credits = false, OpeningTitle = false },
};

var report = new StringBuilder();
report.AppendLine(CultureInfo.InvariantCulture, $"# Music video sync, offscreen ({Width}×{Height}, WARP)");
report.AppendLine();
report.AppendLine(CultureInfo.InvariantCulture, $"Clip: `{Path.GetFileName(clip)}` ({fps} fps, time-coded). Song: \"{song.Title}\" (the demo's clock). Video offset: {MusicVideoTiming.Format(offset)}.");
report.AppendLine();
report.AppendLine("| Song time | Video time asked | Frame expected | Frame drawn | |");
report.AppendLine("|---|---|---|---|---|");
var failures = 0;
var wall = 0.0;
// Moments of the demo song (its intro, its first lines, the held note, the break), then a jump back, as a scrub would.
double[] moments = [2, DemoSongs.EnglishIntro + 0.5, DemoSongs.EnglishMoments.Backing, DemoSongs.EnglishMoments.Duet,
    DemoSongs.EnglishMoments.HeldNote, DemoSongs.EnglishMoments.Break, 12];
foreach (var t in moments)
{
    feed.Inputs = feed.Inputs with { SongTime = t };
    var expected = (int)Math.Floor((t + offset) * fps + 1e-6);
    var drawn = -1;
    for (var i = 0; i < 400 && drawn != expected; i++)
    {
        wall += 1 / 60.0;
        surface.Draw([video, lyrics], scene, t, true, wall, 1 / 60.0);
        drawn = FrameNumber(surface.ReadPixels());
        if (drawn != expected) Thread.Sleep(10);
    }
    var file = Path.Combine(output, string.Create(CultureInfo.InvariantCulture, $"music-video-t{t:0.#}.png"));
    surface.SavePng(file);
    var ok = drawn == expected;
    if (!ok) failures++;
    report.AppendLine(CultureInfo.InvariantCulture, $"| {t:0.0} s | {t + offset:0.000} s | {expected} | {drawn} | {(ok ? "in step" : "OFF")} |");
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"t={t:0.0}: expected frame {expected}, drawn {drawn} -> {Path.GetFileName(file)}"));
}
report.AppendLine();
report.AppendLine(failures == 0 ? "Every frame drawn was the one the song's clock asked for." : $"{failures} moment(s) out of step.");
File.WriteAllText(Path.Combine(output, "report.md"), report.ToString());
return failures == 0 ? 0 : 1;

// The frame number in the top third: 12 blocks, bit 0 on the left; the threshold sits between the row's darkest and
// brightest (the dimming and the scrim darken white).
static int FrameNumber(byte[] pixels)
{
    var y = Height / 8;
    var values = new double[12];
    for (var bit = 0; bit < 12; bit++)
    {
        var x = (int)((bit + 0.5) * Width / 12);
        var i = (y * Width + x) * 4;
        values[bit] = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;
    }
    var threshold = (values.Min() + values.Max()) / 2;
    if (values.Max() - values.Min() < 20) threshold = 128;
    var number = 0;
    for (var bit = 0; bit < 12; bit++) if (values[bit] > threshold) number |= 1 << bit;
    return number;
}

static Palette ArtworkDecoderPalette(byte[] cover)
{
    var pixels = Lunalyr.Platform.Imaging.ArtworkDecoder.Decode(cover) ?? throw new InvalidDataException("demo cover");
    return PaletteMapper.FromAlbum(new AlbumWallpaperPalette(AlbumColourGrid.Extract(pixels)));
}

internal sealed class Feed : IMusicVideoFeed
{
    public MusicVideoInputs Inputs;

    public MusicVideoInputs Read() => Inputs;

    public void Report(string path, double? duration, double videoTime, bool playing)
    {
    }

    public void Failed(string path, string reason) => Console.Error.WriteLine($"failed: {reason}");

    public CoverArt? FrozenFrame => null;
}
