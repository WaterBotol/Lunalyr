// One layer factory per line pattern (ids pattern.<id>), for the desktop and the settings preview; "Pattern
// visibility" reaches them through the scene's adjustments. Implements the wallpaper-types hook
// (docs/APP-SHELL.md, "Hooks for other layers").
using Lunalyr.Core.Logging;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Backgrounds.Gpu;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell.Rendering;

public static partial class WallpaperLayers
{
    static partial void AddPatternLayers(List<IRenderLayerFactory> into, BackgroundOptions options, Logger? logger) =>
        into.AddRange(LinePatternLayerFactory.All(options, log: logger));
}
