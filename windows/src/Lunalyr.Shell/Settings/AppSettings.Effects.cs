// The GPU effects' and line patterns' part of the settings: a pattern saved as the Mac's integer raw value ("3") reads
// as its id ("bubbles").
// Implements the wallpaper-types hook (docs/APP-SHELL.md, "Hooks for other layers").
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
