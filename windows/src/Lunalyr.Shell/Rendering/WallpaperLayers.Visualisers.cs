using Lunalyr.Core.Logging;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Scene;
using Lunalyr.Render.Visualisers;
using Lunalyr.Shell.Visualisers;

namespace Lunalyr.Shell.Rendering;

// The wallpaper-types hook (WallpaperLayers.cs): the visualiser layer. Its settings, sound and power state come from
// VisualiserServices, which the app sets up; until then it draws the default scene's quiet ambient spectrum.
public static partial class WallpaperLayers
{
    static partial void AddVisualiserLayers(List<IRenderLayerFactory> into, BackgroundOptions options, Logger? logger) =>
        into.Add(new VisualiserLayerFactory(VisualiserServices.CreateHost(), options, logger));
}
