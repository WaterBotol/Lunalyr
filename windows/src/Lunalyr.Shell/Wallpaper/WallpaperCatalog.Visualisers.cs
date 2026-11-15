using Lunalyr.Render.Visualisers;

namespace Lunalyr.Shell.Wallpaper;

// The wallpaper-types hooks (WallpaperCatalog.cs): the Visualisers type exists, and draws one layer that follows the
// chosen scene itself (crossfading between scenes), so a scene change never rebuilds the layers.
public static partial class WallpaperCatalog
{
    static partial void ProvidesVisualisers(ref bool has) => has = true;

    static partial void VisualiserLayer(Settings.AppSettings settings, ref string layerId) => layerId = VisualiserLayerFactory.LayerId;
}
