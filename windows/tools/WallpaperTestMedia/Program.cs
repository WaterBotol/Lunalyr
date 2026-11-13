// WallpaperTestMedia <folder>: writes gradient.mp4 (1280x720, 30 fps, 6 s, H.264, no audio track), landscape.png,
// portrait.jpg (taller than wide) and turned.jpg (stored sideways, EXIF orientation 6 = shown upright) into <folder>.
// "WallpaperTestMedia <folder> clip" writes only clip.mp4 (160x96, 15 fps, 1 s), the unit tests' fixture.
// "WallpaperTestMedia <folder> timecode [width height fps seconds]" writes timecode.mp4: every frame carries its own frame
// number in a row of black and white blocks (bit 0 on the left), with a bar sweeping across under it, so a render can be
// read back to the exact frame (the music video sync checks). Defaults: 160x96, 15 fps, 8 s (the unit tests' fixture).
// "WallpaperTestMedia <folder> loop [width height fps seconds]" writes loop-<w>x<h>-<fps>fps-<seconds>s.mp4: the gradient
// clip's picture (hues sweeping, a disc going round) at any size, painted from a colour table so a full-HD clip as long as a
// song is quick to make, at 8 Mbit/s (tools/WallpaperCost's video). Defaults: 1920x1080, 30 fps, 50 s.
// Everything is drawn in code.

using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

var folder = Path.GetFullPath(args.Length > 0 ? args[0] : "test-media");
Directory.CreateDirectory(folder);
if (args.Length > 1 && args[1] == "clip")
{
    // The unit tests' fixture: small and short, checked in under tests/Lunalyr.Platform.Windows.Tests/Fixtures/Wallpaper.
    Video.Write(Path.Combine(folder, "clip.mp4"), 160, 96, 15, 1);
    Console.WriteLine($"Wrote clip.mp4 to {folder}");
    return;
}
if (args.Length > 1 && args[1] == "timecode")
{
    int Arg(int i, int fallback) => args.Length > i && int.TryParse(args[i], out var v) ? v : fallback;
    int tw = Arg(2, 160), th = Arg(3, 96), tfps = Arg(4, 15), tseconds = Arg(5, 8);
    Video.Write(Path.Combine(folder, "timecode.mp4"), tw, th, tfps, tseconds, timecode: true);
    Console.WriteLine($"Wrote timecode.mp4 ({tw}x{th}, {tfps} fps, {tseconds} s) to {folder}");
    return;
}
if (args.Length > 1 && args[1] == "loop")
{
    int Arg(int i, int fallback) => args.Length > i && int.TryParse(args[i], out var v) ? v : fallback;
    int lw = Arg(2, 1920), lh = Arg(3, 1080), lfps = Arg(4, 30), lseconds = Arg(5, 50);
    var name = $"loop-{lw}x{lh}-{lfps}fps-{lseconds}s.mp4";
    Video.Write(Path.Combine(folder, name), lw, lh, lfps, lseconds, fast: true, bitrate: 8_000_000);
    Console.WriteLine($"Wrote {name} to {folder}");
    return;
}
Video.Write(Path.Combine(folder, "gradient.mp4"), 1280, 720, 30, 6);
await Pictures.WriteAsync(Path.Combine(folder, "landscape.png"), 1600, 900, BitmapEncoder.PngEncoderId, null, Pictures.Landscape);
await Pictures.WriteAsync(Path.Combine(folder, "portrait.jpg"), 900, 1600, BitmapEncoder.JpegEncoderId, null, Pictures.Portrait);
// Stored 1600x900 sideways; orientation 6 asks viewers to turn it a quarter clockwise: upright 900x1600.
await Pictures.WriteAsync(Path.Combine(folder, "turned.jpg"), 1600, 900, BitmapEncoder.JpegEncoderId, 6, Pictures.Sideways);
Console.WriteLine($"Wrote test media to {folder}");

internal static class Pictures
{
    // A sky: pale blue at the top to warm sand at the bottom, a sun disc right of centre.
    public static (byte R, byte G, byte B) Landscape(int x, int y, int w, int h)
    {
        double t = y / (double)h;
        double dx = x - w * 0.68, dy = y - h * 0.42, sun = Math.Exp(-(dx * dx + dy * dy) / (2 * Math.Pow(h * 0.08, 2)));
        return (Mix(150, 235, t, sun), Mix(200, 205, t, sun), Mix(245, 160, t, sun));
    }

    // Tall: a dark teal top half and a bright coral bottom half, so Fill, top and Fill, bottom look different.
    public static (byte R, byte G, byte B) Portrait(int x, int y, int w, int h) =>
        y < h / 2 ? ((byte)20, (byte)(70 + 60 * x / w), (byte)90) : ((byte)240, (byte)(120 + 60 * x / w), (byte)100);

    // Stored sideways: red on the left, blue on the right; shown upright that is red at the top, blue at the bottom.
    public static (byte R, byte G, byte B) Sideways(int x, int y, int w, int h) =>
        x < w / 2 ? ((byte)220, (byte)40, (byte)40) : ((byte)40, (byte)60, (byte)220);

    private static byte Mix(double a, double b, double t, double sun) => (byte)Math.Clamp(a + (b - a) * t + sun * 80, 0, 255);

    public static async Task WriteAsync(string path, int w, int h, Guid encoderId, ushort? orientation,
        Func<int, int, int, int, (byte R, byte G, byte B)> colour)
    {
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = colour(x, y, w, h);
                var i = (y * w + x) * 4;
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
            }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, pixels);
        if (orientation is { } o)
        {
            var properties = new BitmapPropertySet { ["System.Photo.Orientation"] = new BitmapTypedValue(o, Windows.Foundation.PropertyType.UInt16) };
            await encoder.BitmapProperties.SetPropertiesAsync(properties);
        }
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }
        await File.WriteAllBytesAsync(path, bytes);
    }
}
