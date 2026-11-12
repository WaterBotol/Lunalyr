// GpuEffectsPreview: the GPU effects and line patterns, drawn offscreen.
//
//   GpuEffectsPreview sheet -out <folder> [-kind effects|patterns|all] [-only id,id] [-times 8,40] [-tile 400x225]
//                     [-palettes dark,bright,two] [-hardware]
//       One contact sheet per palette and time: every layer as a labelled tile.
//   GpuEffectsPreview one -layer shader.plasma -out <file.png> [-size 1280x720] [-time 8] [-palette dark] [-hardware]
//       One frame of one layer.
//   GpuEffectsPreview bench -adapter <name part> [-size 2560x1440] [-frames 240] [-kind all] [-only id,id] [-out file.md]
//                     [-budget pixels]  (every effect at this pixel budget instead of its own)
//       Per-layer GPU time (D3D11 timestamps around the whole frame) and render-thread CPU time on that adapter.
//
// Palettes are made up (no covers needed): "dark" (deep navy, wine and teal), "bright" (pale yellow, pink and sky) and
// "two" (a two-colour cover: red on the left, blue on the right).

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Backgrounds.Gpu;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using Double3 = Lunalyr.Core.Colour.Double3;

var arguments = Args.Parse(args);
if (arguments is null)
{
    Console.Error.WriteLine("usage: GpuEffectsPreview sheet|one|bench … (see Program.cs)");
    return 2;
}
return arguments.Command switch
{
    "sheet" => Preview.Sheets(arguments),
    "one" => Preview.One(arguments),
    "bench" => Bench.Run(arguments),
    _ => 2,
};

internal sealed record Args(string Command, string? Out, string Kind, string[]? Only, double[] Times, int Width, int Height,
                            string[] Palettes, bool Hardware, string? Layer, string? Adapter, int Frames, double? Budget)
{
    public static Args? Parse(string[] args)
    {
        if (args.Length == 0) return null;
        string? output = null, layer = null, adapter = null;
        var kind = "all";
        string[]? only = null;
        double[] times = [8, 40];
        int width = 0, height = 0, frames = 240;
        string[] palettes = ["dark", "bright", "two"];
        var hardware = false;
        double? budget = null;
        for (var i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i].ToLowerInvariant())
            {
                case "-out": output = Next(); break;
                case "-kind": kind = Next(); break;
                case "-only": only = Next().Split(','); break;
                case "-times": times = [.. Next().Split(',').Select(t => double.Parse(t, CultureInfo.InvariantCulture))]; break;
                case "-time": times = [double.Parse(Next(), CultureInfo.InvariantCulture)]; break;
                case "-tile":
                case "-size":
                    var parts = Next().Split('x');
                    width = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    break;
                case "-palettes":
                case "-palette": palettes = Next().Split(','); break;
                case "-hardware": hardware = true; break;
                case "-layer": layer = Next(); break;
                case "-adapter": adapter = Next(); break;
                case "-frames": frames = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "-budget": budget = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                default: return null;
            }
        }
        var command = args[0].ToLowerInvariant();
        if (width == 0) (width, height) = command switch { "sheet" => (400, 225), "bench" => (2560, 1440), _ => (1280, 720) };
        return new Args(command, output, kind, only, times, width, height, palettes, hardware, layer, adapter, frames, budget);
    }

    public IReadOnlyList<IRenderLayerFactory> Factories()
    {
        var options = new BackgroundOptions { PixelBudget = Budget };
        return [.. GpuBackgrounds.Factories(options).Where(f =>
            (Kind == "all" || (Kind == "effects" && f.Id.StartsWith("shader.", StringComparison.Ordinal))
                           || (Kind == "patterns" && f.Id.StartsWith("pattern.", StringComparison.Ordinal)))
            && (Only is null || Only.Any(o => f.Id == o || f.Id.EndsWith("." + o, StringComparison.Ordinal))))];
    }
}

internal static class Palettes
{
    private static Double3 Hsb(double h, double s, double b) => new(h, s, b);

    public static Palette Named(string name) => PaletteMapper.FromAlbum(new AlbumWallpaperPalette(name switch
    {
        "dark" => (Double3[])[Hsb(0.62, 0.75, 0.24), Hsb(0.95, 0.65, 0.28), Hsb(0.52, 0.7, 0.22), Hsb(0.66, 0.6, 0.18), Hsb(0.98, 0.7, 0.3),
                              Hsb(0.5, 0.6, 0.2), Hsb(0.6, 0.8, 0.15), Hsb(0.9, 0.5, 0.22), Hsb(0.55, 0.65, 0.26)],
        "bright" => [Hsb(0.14, 0.32, 0.98), Hsb(0.93, 0.28, 0.97), Hsb(0.55, 0.3, 0.97), Hsb(0.12, 0.25, 0.99), Hsb(0.85, 0.2, 0.98),
                     Hsb(0.5, 0.25, 0.96), Hsb(0.16, 0.3, 0.97), Hsb(0.95, 0.25, 0.96), Hsb(0.58, 0.28, 0.98)],
        "two" => [Hsb(0.0, 0.85, 0.85), Hsb(0.62, 0.85, 0.72), Hsb(0.62, 0.85, 0.72), Hsb(0.0, 0.85, 0.85), Hsb(0.62, 0.85, 0.72),
                  Hsb(0.62, 0.85, 0.72), Hsb(0.0, 0.85, 0.85), Hsb(0.0, 0.85, 0.85), Hsb(0.62, 0.85, 0.72)],
        _ => throw new ArgumentException($"Unknown palette {name} (dark, bright, two)."),
    }));
}

/// <summary>Draws a layer's id in the tile's top-left corner.</summary>
internal sealed class LabelLayer(string text) : IRenderLayer
{
    private IDWriteTextFormat? _format;
    private ID2D1SolidColorBrush? _white, _shadow;

    public bool IsAnimating => false;

    public void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        _format = dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 13);
        _white = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 0.95f));
        _shadow = dc.CreateSolidColorBrush(new Color4(0, 0, 0, 0.8f));
    }

    public void Resize(Vector2 size, float scale) { }

    public void Render(in FrameContext frame)
    {
        frame.Dc.DrawText(text, _format!, new Vortice.RawRectF(7, 5, 400, 30), _shadow!);
        frame.Dc.DrawText(text, _format!, new Vortice.RawRectF(6, 4, 400, 30), _white!);
    }

    public void ReleaseResources()
    {
        _format?.Dispose(); _white?.Dispose(); _shadow?.Dispose();
    }

    public void Dispose() => ReleaseResources();
}

internal static class Preview
{
    public static int Sheets(Args a)
    {
        if (a.Out is null) return 2;
        Directory.CreateDirectory(a.Out);
        var factories = a.Factories();
        const int columns = 5;
        var rows = (factories.Count + columns - 1) / columns;
        using var surface = new OffscreenSurface(a.Width, a.Height, 1, a.Hardware);
        foreach (var name in a.Palettes)
        {
            var palette = Palettes.Named(name);
            var scene = new SceneSnapshot { Palette = palette };
            foreach (var time in a.Times)
            {
                var sheet = new byte[a.Width * columns * a.Height * rows * 4];
                for (var i = 0; i < factories.Count; i++)
                {
                    using var layer = factories[i].Create();
                    using var label = new LabelLayer(factories[i].Id);
                    surface.Prepare(layer);
                    surface.Prepare(label);
                    // A frame a moment before, so fades and clocks start as in a running wallpaper.
                    surface.Draw([layer, label], scene, double.NaN, false, time - 0.5, 0.5);
                    surface.Draw([layer, label], scene, double.NaN, false, time, 0.5);
                    var pixels = surface.ReadPixels();
                    int x0 = i % columns * a.Width, y0 = i / columns * a.Height;
                    for (var y = 0; y < a.Height; y++)
                        Buffer.BlockCopy(pixels, y * a.Width * 4, sheet, ((y0 + y) * a.Width * columns + x0) * 4, a.Width * 4);
                }
                var file = Path.Combine(a.Out, string.Create(CultureInfo.InvariantCulture, $"{a.Kind}-{name}-t{time:0.##}.png"));
                Png.Save(file, sheet, a.Width * columns, a.Height * rows);
                Console.WriteLine(file);
            }
        }
        return 0;
    }

    public static int One(Args a)
    {
        if (a.Out is null || a.Layer is null) return 2;
        var factory = GpuBackgrounds.Factories(new BackgroundOptions()).FirstOrDefault(f => f.Id == a.Layer);
        if (factory is null)
        {
            Console.Error.WriteLine($"No layer {a.Layer}.");
            return 2;
        }
        using var surface = new OffscreenSurface(a.Width, a.Height, 1, a.Hardware);
        using var layer = factory.Create();
        surface.Prepare(layer);
        var scene = new SceneSnapshot { Palette = Palettes.Named(a.Palettes[0]) };
        surface.Draw(layer, scene, a.Times[0] - 0.5);
        surface.Draw(layer, scene, a.Times[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(a.Out))!);
        surface.SavePng(a.Out);
        Console.WriteLine(a.Out);
        return 0;
    }
}

internal static class Png
{
    public static unsafe void Save(string path, byte[] bgra, int width, int height)
    {
        using var wic = new IWICImagingFactory();
        using var file = File.Create(path);
        using var encoder = wic.CreateEncoder(ContainerFormat.Png, file);
        using var frame = encoder.CreateNewFrame(out var properties);
        frame.Initialize(properties);
        frame.SetSize((uint)width, (uint)height);
        var format = Vortice.WIC.PixelFormat.Format32bppBGRA;
        frame.SetPixelFormat(ref format);
        fixed (byte* data = bgra) frame.WritePixels((uint)height, (uint)(width * 4), (uint)bgra.Length, data);
        frame.Commit();
        encoder.Commit();
        properties?.Dispose();
    }
}

internal static class Bench
{
    public static int Run(Args a)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? chosen = null;
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            var description = adapter.Description1.Description;
            Console.WriteLine($"adapter {i}: {description}");
            if (chosen is null && a.Adapter is not null && description.Contains(a.Adapter, StringComparison.OrdinalIgnoreCase)) chosen = adapter;
            else adapter.Dispose();
        }
        if (chosen is null)
        {
            Console.Error.WriteLine($"No adapter matches \"{a.Adapter}\".");
            return 2;
        }
        var name = chosen.Description1.Description;
        using var surface = new OffscreenSurface(a.Width, a.Height, 1.25f, chosen);
        using var d2d = surface.Context.Device;
        using var d2d2 = d2d.QueryInterface<ID2D1Device2>();
        using var dxgi = d2d2.DxgiDevice;
        using var device = dxgi.QueryInterface<ID3D11Device>();
        using var context = device.ImmediateContext;
        using var disjoint = device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
        using var start = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        using var end = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        var palette = Palettes.Named("two");
        var scene = new SceneSnapshot { Palette = palette };
        var rows = new List<(string Id, double GpuMs, double GpuP95, double CpuMs)>();
        var list = new List<IRenderLayerFactory> { new GradientLayerFactory(new BackgroundOptions()) };
        list.AddRange(a.Factories());
        foreach (var f in list)
        {
            using var layer = f.Create();
            surface.Prepare(layer);
            var gpu = new List<double>();
            var cpu = new List<double>();
            for (var frame = 0; frame < a.Frames + 30; frame++)
            {
                var wall = 10 + frame / 60.0;
                context.Begin(disjoint);
                context.End(start);
                var t0 = Stopwatch.GetTimestamp();
                surface.Draw(layer, scene, wall);
                var t1 = Stopwatch.GetTimestamp();
                context.End(end);
                context.End(disjoint);
                var freq = Read<QueryDataTimestampDisjoint>(context, disjoint);
                var s = Read<ulong>(context, start);
                var e = Read<ulong>(context, end);
                if (frame < 30 || freq.Disjoint) continue;
                gpu.Add((e - s) * 1000.0 / freq.Frequency);
                cpu.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
            }
            gpu.Sort();
            var row = (f.Id, gpu.Average(), gpu[(int)(gpu.Count * 0.95)], cpu.Average());
            rows.Add(row);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{row.Item1,-26} gpu {row.Item2,6:0.000} ms (p95 {row.Item3,6:0.000})  cpu {row.Item4,6:0.000} ms"));
        }
        if (a.Out is not null)
        {
            using var w = new StreamWriter(a.Out);
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"# {name}, {a.Width}×{a.Height}, {a.Frames} frames each"));
            w.WriteLine();
            w.WriteLine("| Layer | GPU ms/frame (avg) | GPU p95 | CPU ms/frame (avg) |");
            w.WriteLine("|---|---|---|---|");
            foreach (var r in rows)
                w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {r.Id} | {r.GpuMs:0.000} | {r.GpuP95:0.000} | {r.CpuMs:0.000} |"));
        }
        chosen.Dispose();
        return 0;
    }

    private static unsafe T Read<T>(ID3D11DeviceContext context, ID3D11Query query) where T : unmanaged
    {
        T value;
        while (context.GetData(query, (IntPtr)(&value), (uint)sizeof(T), AsyncGetDataFlags.None).Code != 0) { }
        return value;
    }
}
