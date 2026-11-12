// Where the GPU effects and line patterns read the Wallpaper page's adjustments. On the m6/wallpaper-types branch the
// sliders reach every background through SceneSnapshot.Adjustments (docs/APP-SHELL.md there, "What a background layer
// receives"); this branch predates it, so the scene's part is compiled in only once that branch is merged and
// LUNALYR_WALLPAPER_TYPES is defined (docs/milestones/M6.md, "Merging"). Until then the options alone apply.

using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Backgrounds.Gpu;

internal static class SceneAdjust
{
    /// <summary>"Motion speed": the options' times the scene's.</summary>
    public static double Speed(BackgroundOptions options, SceneSnapshot scene) =>
#if LUNALYR_WALLPAPER_TYPES
        options.Speed * scene.Adjustments.Speed;
#else
        options.Speed;
#endif

    /// <summary>"Colour richness": the options' times the scene's.</summary>
    public static double Vividness(BackgroundOptions options, SceneSnapshot scene) =>
#if LUNALYR_WALLPAPER_TYPES
        options.Vividness * scene.Adjustments.Vividness;
#else
        options.Vividness;
#endif

    /// <summary>"Pattern visibility", 0...1: the scene's when it carries one, otherwise the factory's.</summary>
    public static double PatternOpacity(double factoryOpacity, SceneSnapshot scene) =>
#if LUNALYR_WALLPAPER_TYPES
        Math.Clamp(scene.Adjustments.PatternOpacity, 0, 1);
#else
        factoryOpacity;
#endif
}
