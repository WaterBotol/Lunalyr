// The line patterns in the Wallpaper page's menus and "Match the music": Windows' own names and characters from
// Lunalyr.Core.Backgrounds.LinePatternCatalog (the Mac's tables are Verso). Implements the m6/wallpaper-types hook;
// compiled once that branch is merged and LUNALYR_WALLPAPER_TYPES is defined (docs/milestones/M6.md, "Merging").
#if LUNALYR_WALLPAPER_TYPES
using Lunalyr.Core.Backgrounds;
using Lunalyr.Core.Compositions;

namespace Lunalyr.Shell.Wallpaper;

public static partial class WallpaperCatalog
{
    static partial void AddPatterns(List<WallpaperStyle> into)
    {
        foreach (var pattern in LinePatternCatalog.All)
        {
            var c = pattern.Character;
            into.Add(new WallpaperStyle(pattern.Id, pattern.Title, new WallpaperCharacter(c.Energy, c.Density, c.Organic)));
        }
    }
}
#endif
