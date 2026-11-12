// One layer factory per line pattern (ids pattern.<id>), for the desktop and the settings preview; "Pattern
// visibility" reaches them through the scene's adjustments. Implements the m6/wallpaper-types hook; compiled once that
// branch is merged and LUNALYR_WALLPAPER_TYPES is defined (docs/milestones/M6.md, "Merging").
#if LUNALYR_WALLPAPER_TYPES
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
#endif
