using Vortice.WIC;

namespace Lunalyr.Tools.WallpaperCost;

/// <summary>
/// The test media the imported-picture, imported-video, animated-cover and music-video runs draw, all invented and drawn
/// in code: a full-HD H.264 clip with no audio track (tools/WallpaperTestMedia's writer) as long as the demo's songs so
/// the music video never runs past its end, and a 4K JPEG. Made once into the media folder and reused.
/// </summary>
internal static class Media
{
    public const int ClipWidth = 1920, ClipHeight = 1080, ClipFps = 30, ClipSeconds = 50;
    public const int PictureWidth = 3840, PictureHeight = 2160;

    public static string ClipName => $"loop-{ClipWidth}x{ClipHeight}-{ClipFps}fps-{ClipSeconds}s.mp4";

    public static string PictureName => $"picture-{PictureWidth}x{PictureHeight}.jpg";

    /// <summary>The default media folder: the worktree's artifacts (ignored by git), else the temp folder.</summary>
    public static string DefaultFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lunalyr.slnx"))) return Path.Combine(dir.FullName, "artifacts", "wallpaper-cost-media");
        return Path.Combine(Path.GetTempPath(), "lunalyr-wallpaper-cost-media");
    }

    /// <summary>Makes whatever is missing; returns the clip's and the picture's paths.</summary>
    public static (string Clip, string Picture) Ensure(string folder)
    {
        Directory.CreateDirectory(folder);
        var clip = Path.Combine(folder, ClipName);
        if (!File.Exists(clip))
        {
            Console.WriteLine($"Making the test clip ({ClipWidth}x{ClipHeight}, {ClipFps} fps, {ClipSeconds} s, no audio track); once only...");
            var partial = clip + ".part.mp4";
            Video.Write(partial, ClipWidth, ClipHeight, ClipFps, ClipSeconds, fast: true, bitrate: 8_000_000);
            File.Move(partial, clip, overwrite: true);
        }
        var picture = Path.Combine(folder, PictureName);
        if (!File.Exists(picture))
        {
            Console.WriteLine($"Making the test picture ({PictureWidth}x{PictureHeight} JPEG)...");
            WritePicture(picture + ".part", PictureWidth, PictureHeight);
            File.Move(picture + ".part", picture, overwrite: true);
        }
        return (clip, picture);
    }

    // A photo-like sky: pale blue to warm sand, a soft sun, a dark hill line and fine noise (so JPEG has detail to keep).
    private static unsafe void WritePicture(string path, int w, int h)
    {
        var pixels = new byte[w * h * 3];
        var random = new Random(5);
        for (var y = 0; y < h; y++)
        {
            var t = y / (double)h;
            for (var x = 0; x < w; x++)
            {
                double dx = x - w * 0.68, dy = y - h * 0.4, sun = Math.Exp(-(dx * dx + dy * dy) / (2 * Math.Pow(h * 0.09, 2)));
                var hill = y > h * (0.72 + 0.06 * Math.Sin(x / (double)w * 7) + 0.03 * Math.Sin(x / (double)w * 23));
                var noise = random.Next(-6, 7);
                double r = hill ? 40 + 20 * t : 150 + 85 * t + sun * 90, g = hill ? 52 + 10 * t : 200 + 5 * t + sun * 70,
                    b = hill ? 48 : 245 - 85 * t + sun * 30;
                var i = (y * w + x) * 3;
                pixels[i] = (byte)Math.Clamp(b + noise, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(g + noise, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(r + noise, 0, 255);
            }
        }
        using var wic = new IWICImagingFactory();
        using var file = File.Create(path);
        using var encoder = wic.CreateEncoder(ContainerFormat.Jpeg, file);
        using var frame = encoder.CreateNewFrame(out var properties);
        frame.Initialize(properties);
        frame.SetSize((uint)w, (uint)h);
        var format = PixelFormat.Format24bppBGR;
        frame.SetPixelFormat(ref format);
        if (format != PixelFormat.Format24bppBGR) throw new InvalidOperationException("The JPEG encoder wants another pixel format.");
        fixed (byte* data = pixels) frame.WritePixels((uint)h, (uint)(w * 3), (uint)pixels.Length, data);
        frame.Commit();
        encoder.Commit();
        properties?.Dispose();
    }
}
