// The line patterns in the Wallpaper page's menus and "Match the music": Windows' own names and characters from
// Lunalyr.Core.Backgrounds.LinePatternCatalog (the Mac's tables are Verso). Implements the wallpaper-types hook
// (docs/APP-SHELL.md, "Hooks for other layers").
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
