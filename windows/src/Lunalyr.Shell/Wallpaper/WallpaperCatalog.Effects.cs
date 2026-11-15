// The GPU effects in the Wallpaper page's menus and "Custom Mix": Windows' own names, characters and cost tiers
// from Lunalyr.Core.Backgrounds.GpuEffectCatalog (the Mac's tables are Verso). Implements the wallpaper-types hook
// (docs/APP-SHELL.md, "Hooks for other layers").
using Lunalyr.Core.Backgrounds;
using Lunalyr.Core.Compositions;

namespace Lunalyr.Shell.Wallpaper;

public static partial class WallpaperCatalog
{
    static partial void AddEffects(List<WallpaperStyle> into)
    {
        foreach (var effect in GpuEffectCatalog.All)
        {
            var c = effect.Character;
            into.Add(new WallpaperStyle(effect.Id, effect.Title, new WallpaperCharacter(c.Energy, c.Density, c.Organic), effect.Heavy,
                effect.Heavy ? "Heavier on the graphics card; left out of Custom Mix while power is being saved." : null));
        }
    }
}
