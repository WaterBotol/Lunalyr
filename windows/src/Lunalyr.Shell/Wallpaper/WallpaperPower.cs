using Lunalyr.Render.Pacing;

namespace Lunalyr.Shell.Wallpaper;

/// <summary>
/// What power saving draws instead of the chosen wallpaper type. Re-implemented from the behaviour of the Mac's
/// <c>Settings.effectiveMode(_:under:)</c> (its lines come from the squashed Verso import, so none is translated):
/// <list type="bullet">
/// <item>Your wallpaper is never replaced: it draws no generated background to save.</item>
/// <item>No constraint, or "inexpensive effects only": the chosen type stays (heavy GPU effects are swapped for light
/// ones where the effect is picked, <see cref="AppController"/>'s <c>Pick</c>).</item>
/// <item>Line patterns (Low power visuals, Energy saver): every type but Colour only shows Line patterns.</item>
/// <item>Colour only: every type shows Colour only.</item>
/// </list>
/// </summary>
public static class WallpaperPower
{
    /// <summary>The <c>backgroundMode</c> drawn for <paramref name="chosen"/> under <paramref name="constraint"/>.</summary>
    public static string EffectiveMode(string chosen, PowerConstraint? constraint)
    {
        if (chosen == "desktop") return chosen;
        return (constraint?.Fallback ?? PowerFallback.Full) switch
        {
            PowerFallback.Patterns => chosen == "colourOnly" ? chosen : "pattern",
            PowerFallback.ColourOnly => "colourOnly",
            _ => chosen,
        };
    }

    /// <summary>Whether power saving spares the graphics card at all (heavy effects give way to light ones).</summary>
    public static bool SparesGpu(PowerConstraint? constraint) => constraint is not null;

    /// <summary>The type's title ("Line patterns"), for sentences about what power saving shows.</summary>
    public static string Title(string mode) =>
        Settings.SettingsCopy.WallpaperTypes.FirstOrDefault(t => t.Value == mode)?.Title ?? mode;
}
