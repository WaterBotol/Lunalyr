// LayerPreview: draws a background layer offscreen and saves PNGs, so a layer can be judged without a desktop.
//
//   LayerPreview -covers <folder> -out <folder> [-layer gradient|colour] [-times 0,5,10] [-size 1920x1080]
//                [-scale 1] [-hardware] [-quiet] [-fade a.jpg,b.png]
//
// Each cover image (JPEG/PNG) in -covers is decoded as the app decodes artwork, turned into a palette, and the layer is
// drawn for it at each time. -quiet draws an empty stage (no text backing). -fade draws the palette change from the
// first cover to the second at 0, 0.2, 0.4, 0.6 and 0.8 s after the change.

using System.Globalization;
using Lunalyr.Core.Colour;
using Lunalyr.Platform.Imaging;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;

var options = Arguments.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("usage: LayerPreview -covers <folder> -out <folder> [-layer gradient|colour] [-times 0,5,10] "
                            + "[-size 1920x1080] [-scale 1] [-hardware] [-quiet] [-fade a.jpg,b.png]");
    return 2;
}
Directory.CreateDirectory(options.Out);
using var surface = new OffscreenSurface(options.Width, options.Height, options.Scale, options.Hardware);
IRenderLayerFactory factory = options.Layer == "colour" ? new ColourLayerFactory() : new GradientLayerFactory();
var scene = new SceneSnapshot { Title = options.Quiet ? null : "Preview" };

Palette PaletteOf(string path)
{
    var pixels = ArtworkDecoder.Decode(File.ReadAllBytes(path))
                 ?? throw new InvalidDataException($"{path} is not an image WIC can read.");
    return PaletteMapper.FromAlbum(new AlbumWallpaperPalette(AlbumColourGrid.Extract(pixels)));
}

string Name(string path) => Path.GetFileNameWithoutExtension(path);

if (options.Fade is { } fade)
{
    var (from, to) = (PaletteOf(fade.From), PaletteOf(fade.To));
    using var layer = factory.Create();
    surface.Prepare(layer);
    surface.Draw(layer, scene with { Palette = from }, 10);
    foreach (var after in new[] { 0.0, 0.2, 0.4, 0.6, 0.8 })
    {
        surface.Draw(layer, scene with { Palette = to }, 10 + after);
        var file = Path.Combine(options.Out, string.Create(CultureInfo.InvariantCulture,
            $"{factory.Id}-fade-{Name(fade.From)}-{Name(fade.To)}-{after:0.0}s.png"));
        surface.SavePng(file);
        Console.WriteLine(file);
    }
    return 0;
}

var covers = Directory.EnumerateFiles(options.Covers)
    .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
    .Order(StringComparer.Ordinal).ToList();
foreach (var cover in covers)
{
    Palette palette;
    try { palette = PaletteOf(cover); }
    catch (InvalidDataException ex) { Console.Error.WriteLine(ex.Message); continue; }
    using var layer = factory.Create();
    surface.Prepare(layer);
    foreach (var time in options.Times)
    {
        surface.Draw(layer, scene with { Palette = palette }, time);
        var file = Path.Combine(options.Out, string.Create(CultureInfo.InvariantCulture, $"{factory.Id}-{Name(cover)}-t{time:0.##}.png"));
        surface.SavePng(file);
        Console.WriteLine(file);
    }
}
return 0;

internal sealed record Arguments(string Covers, string Out, string Layer, double[] Times, int Width, int Height, float Scale,
                                 bool Hardware, bool Quiet, (string From, string To)? Fade)
{
    public static Arguments? Parse(string[] args)
    {
        string? covers = null, output = null, layer = "gradient";
        double[] times = [0, 5, 10];
        int width = 1920, height = 1080;
        float scale = 1;
        bool hardware = false, quiet = false;
        (string, string)? fade = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i].ToLowerInvariant())
            {
                case "-covers": covers = Next(); break;
                case "-out": output = Next(); break;
                case "-layer": layer = Next(); break;
                case "-times": times = Next().Split(',').Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray(); break;
                case "-size":
                    var parts = Next().Split('x');
                    width = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    break;
                case "-scale": scale = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "-hardware": hardware = true; break;
                case "-quiet": quiet = true; break;
                case "-fade":
                    var pair = Next().Split(',');
                    fade = (pair[0], pair[1]);
                    break;
                default: return null;
            }
        }
        if (output is null || (covers is null && fade is null)) return null;
        return new Arguments(covers ?? ".", output, layer, times, width, height, scale, hardware, quiet, fade);
    }
}
