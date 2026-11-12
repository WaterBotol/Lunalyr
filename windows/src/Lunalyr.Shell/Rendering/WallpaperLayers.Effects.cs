// One layer factory per GPU effect (ids shader.<id>), for the desktop and the settings preview. Implements the
// wallpaper-types hook (docs/APP-SHELL.md, "Hooks for other layers").
using Lunalyr.Core.Logging;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Backgrounds.Gpu;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell.Rendering;

public static partial class WallpaperLayers
{
    static partial void AddEffectLayers(List<IRenderLayerFactory> into, BackgroundOptions options, Logger? logger) =>
        into.AddRange(GpuEffectLayerFactory.All(options, logger));
}
