// What a saved shaderStyle / patternStyle means (Windows' own): an effect or pattern this build draws, or "" for
// "Match the music". The Mac saves the pattern as an integer raw value; that reads as the pattern's id.

using Lunalyr.Core.Backgrounds;

namespace Lunalyr.Shell.Settings;

public static class BackgroundStyles
{
    /// <summary>The effect id <paramref name="value"/> pins, or "" when it pins none this build draws.</summary>
    public static string Effect(string? value) => GpuEffectCatalog.Find(value?.Trim())?.Id ?? "";

    /// <summary>The pattern id <paramref name="value"/> pins (an id, or the Mac's raw value 0...17), or "".</summary>
    public static string Pattern(string? value) => LinePatternCatalog.Find(value?.Trim())?.Id ?? "";
}
