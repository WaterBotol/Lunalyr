using System.Numerics;
using Lunalyr.Core.Backgrounds;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Backgrounds.Gpu;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Tests.Backgrounds;

/// <summary>The GPU effects, drawn offscreen on WARP (Direct3D 11 pixel shaders inside a Direct2D frame) and read back.</summary>
public sealed class GpuEffectLayerTests : IDisposable
{
    private const int Width = 160, Height = 90;
    private static readonly BackgroundOptions Plain = new() { Grain = false, ScrimStrength = 0 };
    private readonly OffscreenSurface _surface = new(Width, Height);

    public void Dispose() => _surface.Dispose();

    internal static Palette TwoColour { get; } = Cover(new(0.85f, 0.18f, 0.22f), new(0.10f, 0.30f, 0.80f));

    internal static Palette Cover(Vector3 a, Vector3 b)
    {
        Vector4 A = new(a, 1), B = new(b, 1);
        return new Palette(new Vector4((a + b) * 0.2f, 1), [A, A, B, B, B], Vector4.One, Vector4.One);
    }

    public static TheoryData<string> Effects => [.. GpuEffectCatalog.All.Select(e => e.Id)];

    private byte[] Frame(IRenderLayer layer, Palette palette, double wall)
    {
        _surface.Draw(layer, new SceneSnapshot { Palette = palette }, wall);
        return _surface.ReadPixels();
    }

    private static double Mean(byte[] pixels)
    {
        double sum = 0;
        for (var i = 0; i < pixels.Length; i += 4) sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
        return sum / (pixels.Length / 4 * 3);
    }

    private static double Difference(byte[] a, byte[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    [Fact]
    public void EveryEffectHasALayerFactoryWithItsMacId()
    {
        var factories = GpuEffectLayerFactory.All(Plain);
        Assert.Equal(GpuEffectCatalog.All.Count, factories.Count);
        Assert.Contains(factories, f => f.Id == "shader.lightTrails");
        Assert.Equal(factories.Count, factories.Select(f => f.Id).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Effects))]
    public void EachEffectDrawsSomethingThatMoves(string id)
    {
        using var layer = new GpuEffectLayerFactory(id, Plain).Create();
        _surface.Prepare(layer);
        var first = Frame(layer, TwoColour, 20);
        var later = Frame(layer, TwoColour, 26);
        Assert.InRange(Mean(first), 2, 250);
        Assert.True(Difference(first, later) > 0.3, $"{id} did not move: {Difference(first, later):0.###}");
    }

    // Windows' analogue of WallpaperEnergyTests.swift: testEveryEffectLinksIntoABackgroundPipeline. The shaders are
    // compiled at build time, so what can fail at run time is a missing or unloadable bytecode.
    [Fact]
    public void EveryEffectHasItsOwnBytecodeAndEveryEmbeddedShaderLoads()
    {
        foreach (var effect in GpuEffectCatalog.All)
            Assert.True(GpuShaderLibrary.Bytecode("Effects." + effect.Id) is not null, $"{effect.Id} has no shader");
        using var d2d = _surface.Context.Device;
        using var d2d2 = d2d.QueryInterface<Vortice.Direct2D1.ID2D1Device2>();
        using var dxgi = d2d2.DxgiDevice;
        using var device = dxgi.QueryInterface<Vortice.Direct3D11.ID3D11Device>();
        var names = GpuShaderLibrary.Names.ToList();
        Assert.Equal(GpuEffectCatalog.All.Count + 1, names.Count(n => n.StartsWith("Effects.", StringComparison.Ordinal) || n.EndsWith(".vs", StringComparison.Ordinal)));
        foreach (var name in names)
        {
            var bytecode = GpuShaderLibrary.Bytecode(name)!;
            if (name.EndsWith(".vs", StringComparison.Ordinal)) device.CreateVertexShader(bytecode).Dispose();
            else device.CreatePixelShader(bytecode).Dispose();
            if (name.StartsWith("Patterns.", StringComparison.Ordinal)) Assert.NotNull(LinePatternCatalog.Find(name["Patterns.".Length..]));
        }
    }

    [Fact]
    public void ResourcesComeBackAfterBeingReleased()
    {
        using var layer = new GpuEffectLayerFactory("plasma", Plain).Create();
        _surface.Prepare(layer);
        var before = Frame(layer, TwoColour, 3);
        layer.ReleaseResources();
        _surface.Prepare(layer);
        var after = Frame(layer, TwoColour, 3);
        Assert.True(Mean(after) > 2);
        Assert.True(Difference(before, after) < 1, $"{Difference(before, after)}");
    }

    [Fact]
    public void AFrameAllocatesNothing()
    {
        using var layer = new GpuEffectLayerFactory("plasma", Plain).Create();
        _surface.Prepare(layer);
        var scene = new SceneSnapshot { Palette = TwoColour };
        for (var i = 0; i < 5; i++) _surface.Draw(layer, scene, i / 60.0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 5; i < 65; i++) _surface.Draw(layer, scene, i / 60.0);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // OffscreenSurface.Draw itself makes a FrameContext (a struct); anything here is the layer's.
        Assert.True(allocated == 0, $"{allocated} bytes over 60 frames");
    }
}
