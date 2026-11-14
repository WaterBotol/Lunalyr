// MusicVideoPreview framing <out folder> <clip>... : "Fill the screen" against "Show the whole video" (the Mac's
// MusicVideoFraming, Test Build 88), offscreen.
// - Video files: each clip (tools/WallpaterTestMedia timecode clips of any shape) drawn by the "musicVideo" layer filled and
//   whole, on a 16:9 (1280×720) and a 16:10 (1280×800) surface, with the demo song's cover as the backdrop.
// - YouTube: the "musicVideo.youtube" layer with a still painted in code as YouTube makes one (16:9, the picture
//   letterboxed or pillarboxed inside it with black bars), filled and whole, covered (the still and the backdrop) and
//   uncovered (the see-through part drawn magenta, where the player's window shows), at each clip's shape.
// Writes <name>.png per render and framing.md. Checks: shown whole, the bands are never black and never see-through; the
// see-through part is the visible picture only. Silent: only the clips' video streams are read; nothing reaches YouTube.

using System.Globalization;
using System.Text;
using Lunalyr.Core.Colour;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Demo;
using Vortice.Mathematics;

internal static class FramingPreview
{
    private static readonly BackgroundOptions Plain = new() { Grain = false, ScrimStrength = 0, ManualBacking = 0 };

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: MusicVideoPreview framing <out folder> <clip.mp4>...");
            return 2;
        }
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var song = DemoSongs.Songs[0];
        var cover = Lunalyr.Platform.Imaging.ArtworkDecoder.DecodeCover(song.Cover) ?? throw new InvalidDataException("demo cover");
        var pixels = Lunalyr.Platform.Imaging.ArtworkDecoder.Decode(song.Cover) ?? throw new InvalidDataException("demo cover");
        var palette = PaletteMapper.FromAlbum(new AlbumWallpaperPalette(AlbumColourGrid.Extract(pixels)));
        var scene = new SceneSnapshot { Title = song.Title, Artist = song.Artist, Palette = palette, Cover = cover, Duration = song.Duration };
        var report = new StringBuilder();
        report.AppendLine("# Music video framing, offscreen (WARP)");
        report.AppendLine();
        report.AppendLine("| Render | Source | Surface | Framing | Picture rows | Bands | Band check | |");
        report.AppendLine("|---|---|---|---|---|---|---|---|");
        var failures = 0;
        (int W, int H)[] surfaces = [(1280, 720), (1280, 800)];
        foreach (var clip in args[1..].Select(Path.GetFullPath))
        {
            var aspect = ClipAspect(clip);
            var tag = Tag(aspect);
            foreach (var (w, h) in surfaces)
            {
                foreach (var framing in MusicVideoFramings.All)
                {
                    var name = $"file-{tag}-{w}x{h}-{framing.Value()}";
                    failures += FileRender(output, name, clip, aspect, framing, w, h, scene, report);
                    var still = Still(aspect);
                    failures += YouTubeRender(output, $"youtube-{tag}-{w}x{h}-{framing.Value()}-covered", still, aspect, framing, w, h, scene, false, report);
                    failures += YouTubeRender(output, $"youtube-{tag}-{w}x{h}-{framing.Value()}-playing", still, aspect, framing, w, h, scene, true, report);
                    // The smaller 4:3 still YouTube falls back to: cropped into the picture, never squashed.
                    failures += YouTubeRender(output, $"youtube-hq-{tag}-{w}x{h}-{framing.Value()}-covered", Still(aspect, hq: true), aspect, framing, w, h,
                        scene, false, report);
                }
            }
        }
        report.AppendLine();
        report.AppendLine(failures == 0 ? "Shown whole, every band is the backdrop (neither black nor see-through), and only the picture is see-through." : $"{failures} check(s) failed.");
        File.WriteAllText(Path.Combine(output, "framing.md"), report.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
        Console.WriteLine(failures == 0 ? "framing: all checks passed" : $"framing: {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    private static string Tag(double aspect) => aspect switch
    {
        > 2.2 => "21x9",
        < 1.5 => "4x3",
        _ => "16x9",
    };

    private static double ClipAspect(string clip)
    {
        using var reader = Lunalyr.Platform.Media.VideoReader.Open(clip, 0, out var error) ?? throw new InvalidDataException(error ?? clip);
        return (double)reader.Width / reader.Height;
    }

    private static int FileRender(string output, string name, string clip, double aspect, MusicVideoFraming framing, int w, int h,
        SceneSnapshot scene, StringBuilder report)
    {
        var feed = new Feed { Inputs = new MusicVideoInputs(clip, 2, true, 0, 0.2, 0, FileFraming: framing) };
        using var surface = new OffscreenSurface(w, h);
        using var layer = new MusicVideoLayerFactory(feed, Plain).Create();
        surface.Prepare(layer);
        var wall = 0.0;
        byte[] pixels = [];
        // The clip decodes on its own thread and the backdrop blurs on another: draw until both have arrived.
        for (var i = 0; i < 240; i++)
        {
            wall += 1 / 60.0;
            surface.Draw([layer], scene, 2, true, wall, 1 / 60.0);
            if (i > 60 && ((MusicVideoLayer)layer).ShownTime is not null) break;
            Thread.Sleep(10);
        }
        for (var i = 0; i < 40; i++)
        {
            wall += 1 / 60.0;
            surface.Draw([layer], scene, 2, true, wall, 1 / 60.0);
        }
        pixels = surface.ReadPixels();
        surface.SavePng(Path.Combine(output, name + ".png"));
        var rect = framing.Rect(aspect, new VideoSize(w, h));
        return Check(report, name, "video file", w, h, framing, rect, pixels, seeThroughInside: false);
    }

    private static int YouTubeRender(string output, string name, CoverArt still, double aspect, MusicVideoFraming framing, int w, int h,
        SceneSnapshot scene, bool playing, StringBuilder report)
    {
        var feed = new Feed
        {
            Inputs = new MusicVideoInputs(null, 2, true, 0, 0.2, 0, PlayerVisible: playing, Still: still, YouTubeFraming: framing,
                YouTubeAspect: aspect),
        };
        using var surface = new OffscreenSurface(w, h);
        using var layer = new YouTubeVideoLayerFactory(feed, Plain).Create();
        surface.Prepare(layer);
        byte[] pixels = [];
        for (var i = 0; i < 60; i++)
        {
            var dc = surface.Context;
            dc.BeginDraw();
            // Magenta stands for the player's window: wherever the layer is see-through, it shows.
            dc.Clear(new Color4(1, 0, 1, 1));
            var frame = new FrameContext
            {
                Dc = dc, DWrite = surface.DWrite, D2DFactory = surface.Factory, Size = surface.Size, Scale = 1, WallSeconds = 10 + i,
                SongSeconds = 2, IsPlaying = true, Scene = scene, DeltaSeconds = 1 / 60.0,
                AheadWork = new Lunalyr.Render.Pacing.AheadWork(), IsMainDisplay = true,
            };
            layer.Render(frame);
            dc.EndDraw().CheckError();
            Thread.Sleep(5);
        }
        pixels = surface.ReadPixels();
        surface.SavePng(Path.Combine(output, name + ".png"));
        var visible = framing == MusicVideoFraming.Whole
            ? MusicVideoFramings.WholeLayoutFor(aspect, new VideoSize(w, h)).VisibleRect(new VideoSize(w, h))
            : new VideoRect(0, 0, w, h);
        return Check(report, name, playing ? "YouTube, playing" : "YouTube, covered", w, h, framing, visible, pixels, seeThroughInside: playing);
    }

    /// <summary>Bands (rows outside the picture) must be neither black nor magenta; the picture is magenta exactly when see-through.</summary>
    private static int Check(StringBuilder report, string name, string source, int w, int h, MusicVideoFraming framing, VideoRect picture,
        byte[] pixels, bool seeThroughInside)
    {
        var top = (int)Math.Ceiling(Math.Max(0, picture.Y));
        var bottom = (int)Math.Floor(Math.Min(h, picture.Y + picture.Height));
        var bandRows = h - (bottom - top);
        var failures = 0;
        var notes = new List<string>();
        // Two rows into each band, and the band's middle.
        foreach (var y in new[] { top / 2, top - 3, bottom + 2, (bottom + h) / 2 }.Where(y => y >= 0 && y < h && (y < top - 1 || y > bottom)).Distinct())
        {
            var (black, magenta) = Row(pixels, w, y);
            if (black > w / 10) { failures++; notes.Add($"row {y}: black"); }
            if (magenta > 0) { failures++; notes.Add($"row {y}: see-through"); }
        }
        var middle = Row(pixels, w, h / 2);
        if (seeThroughInside && middle.Magenta < w * 9 / 10) { failures++; notes.Add("picture not see-through"); }
        if (!seeThroughInside && middle.Magenta > 0) { failures++; notes.Add("picture see-through while covered"); }
        report.AppendLine(CultureInfo.InvariantCulture,
            $"| `{name}.png` | {source} | {w}×{h} | {framing.Title()} | {bottom - top} | {bandRows} | {(notes.Count == 0 ? "ok" : string.Join("; ", notes))} | {(failures == 0 ? "" : "FAIL")} |");
        Console.WriteLine($"{name}: {(failures == 0 ? "ok" : string.Join("; ", notes))}");
        return failures;
    }

    private static (int Black, int Magenta) Row(byte[] pixels, int w, int y)
    {
        int black = 0, magenta = 0;
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            if (r + g + b < 12) black++;
            // Magenta under the dimming (0.2 black over it).
            if (r > 150 && b > 150 && g < 30 && Math.Abs(r - b) < 12) magenta++;
        }
        return (black, magenta);
    }

    /// <summary>
    /// A still as YouTube makes one: 1280×720, the picture at <paramref name="aspect"/> inside it with black bars, painted with
    /// hue bands and a grid so a crop or a stretch shows.
    /// </summary>
    private static CoverArt Still(double aspect, bool hq = false)
    {
        // maxresdefault: 1280×720, the 16:9 player. hqdefault: 480×360, the 16:9 player letterboxed again inside 4:3.
        int W = hq ? 480 : 1280, H = hq ? 360 : 720;
        var playerH = hq ? 270 : H;
        var pixels = new byte[W * H * 4];
        var pw = aspect < 16.0 / 9.0 ? (int)Math.Round(playerH * aspect) : W;
        var ph = aspect < 16.0 / 9.0 ? playerH : (int)Math.Round(W / aspect);
        var x0 = (W - pw) / 2;
        var y0 = (H - ph) / 2;
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                pixels[i + 3] = 255;
                if (x < x0 || x >= x0 + pw || y < y0 || y >= y0 + ph) continue; // YouTube's bar
                var u = (double)(x - x0) / pw;
                var v = (double)(y - y0) / ph;
                var grid = (x - x0) % 80 < 3 || (y - y0) % 80 < 3;
                pixels[i] = (byte)(grid ? 240 : 60 + 160 * v);
                pixels[i + 1] = (byte)(grid ? 240 : 60 + 120 * (1 - u));
                pixels[i + 2] = (byte)(grid ? 240 : 80 + 160 * u);
            }
        }
        return new CoverArt(W, H, pixels);
    }
}
