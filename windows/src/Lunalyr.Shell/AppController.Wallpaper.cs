using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Wallpaper;

namespace Lunalyr.Shell;

// The wallpaper type (backgroundMode) as a background layer id: docs/APP-SHELL.md, "Wallpaper types (milestone 6)".
public sealed partial class AppController
{
    /// <summary>The layer every build has: the album colour field, drawn wherever a type's own layer is missing.</summary>
    public const string FallbackBackground = "gradient";

    /// <summary>The background layer id <paramref name="settings"/> draw for the song on screen.</summary>
    private string BackgroundLayerFor(AppSettings settings) => settings.BackgroundMode switch
    {
        "colourOnly" => settings.AlbumGradientWallpaper ? "colour" : FallbackBackground,
        "desktop" => "desktop",
        "custom" => string.IsNullOrEmpty(settings.WallpaperPath) ? FallbackBackground : "custom",
        "visualizer" => WallpaperCatalog.VisualiserLayerFor(settings),
        _ => FallbackBackground,
    };
}
