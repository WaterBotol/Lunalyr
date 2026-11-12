// The GPU effects in the Wallpaper page's menus and "Match the music": Windows' own names, characters and cost tiers
// from Lunalyr.Core.Backgrounds.GpuEffectCatalog (the Mac's tables are Verso). Implements the m6/wallpaper-types hook;
// compiled once that branch is merged and LUNALYR_WALLPAPER_TYPES is defined (docs/milestones/M6.md, "Merging").
#if LUNALYR_WALLPAPER_TYPES
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
                effect.Heavy ? "Heavier on the graphics card; left out of Match the music while power is being saved." : null));
        }
    }
}
#endif
