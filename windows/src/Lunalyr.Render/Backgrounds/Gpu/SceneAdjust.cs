// Where the GPU effects and line patterns read the Wallpaper page's adjustments: the sliders reach every background
// through SceneSnapshot.Adjustments (docs/APP-SHELL.md, "What a background layer receives"), multiplied into the
// options' own values as the colour field does.

using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Backgrounds.Gpu;

internal static class SceneAdjust
{
    /// <summary>"Motion speed": the options' times the scene's.</summary>
    public static double Speed(BackgroundOptions options, SceneSnapshot scene) => options.Speed * scene.Adjustments.Speed;

    /// <summary>"Colour richness": the options' times the scene's.</summary>
    public static double Vividness(BackgroundOptions options, SceneSnapshot scene) => options.Vividness * scene.Adjustments.Vividness;

    /// <summary>"Pattern visibility", 0...1, from the scene.</summary>
    public static double PatternOpacity(SceneSnapshot scene) =>
        double.IsFinite(scene.Adjustments.PatternOpacity) ? Math.Clamp(scene.Adjustments.PatternOpacity, 0, 1) : LinePatternLayer.DefaultOpacity;
}
