// Every GPU effect and line pattern as a layer factory, for the app to register and the tools to draw. Windows' own.

using Lunalyr.Core.Logging;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Backgrounds.Gpu;

public static class GpuBackgrounds
{
    /// <summary>
    /// A factory for every GPU effect (<c>shader.&lt;id&gt;</c>) and every line pattern (<c>pattern.&lt;id&gt;</c>), drawn
    /// with <paramref name="options"/>; the patterns show as strongly as each frame's scene says ("Pattern visibility").
    /// </summary>
    public static IReadOnlyList<IRenderLayerFactory> Factories(BackgroundOptions? options = null, Logger? log = null)
    {
        var all = new List<IRenderLayerFactory>();
        all.AddRange(GpuEffectLayerFactory.All(options, log));
        all.AddRange(LinePatternLayerFactory.All(options, log));
        return all;
    }
}
