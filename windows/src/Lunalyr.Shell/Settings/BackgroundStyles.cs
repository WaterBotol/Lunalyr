// What a saved shaderStyle / patternStyle means (Windows' own). The Mac saves the pattern as an integer raw value; that
// reads as the pattern's id. Anything else is kept as it is: a style this build doesn't draw already reads as "Match
// the music" in the menus and in WallpaperMatch.

using Lunalyr.Core.Backgrounds;

namespace Lunalyr.Shell.Settings;

public static class BackgroundStyles
{
    /// <summary>A saved effect, trimmed.</summary>
    public static string Effect(string? value) => value?.Trim() ?? "";

    /// <summary>A saved pattern: the Mac's raw value 0...17 as its id ("3" is "bubbles"), anything else as it is.</summary>
    public static string Pattern(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        return trimmed.Length > 0 && char.IsAsciiDigit(trimmed[0]) && LinePatternCatalog.Find(trimmed) is { } pattern ? pattern.Id : trimmed;
    }
}
