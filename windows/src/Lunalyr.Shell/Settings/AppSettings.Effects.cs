// The GPU effects' and line patterns' part of the settings: a pinned shaderStyle / patternStyle this build doesn't
// draw reads as "" (Match the music), and a pattern saved as the Mac's integer raw value ("3") as its id ("bubbles").
// Implements the m6/wallpaper-types hook; compiled once that branch is merged and LUNALYR_WALLPAPER_TYPES is defined
// (docs/milestones/M6.md, "Merging").
#if LUNALYR_WALLPAPER_TYPES
using Lunalyr.Core.Backgrounds;

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    static partial void NormalizeEffects(ref AppSettings settings) =>
        settings = settings with
        {
            ShaderStyle = BackgroundStyles.Effect(settings.ShaderStyle),
            PatternStyle = BackgroundStyles.Pattern(settings.PatternStyle),
        };
}
#endif
