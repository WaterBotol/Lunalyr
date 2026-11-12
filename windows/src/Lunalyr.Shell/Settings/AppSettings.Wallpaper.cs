// The Wallpaper and Covers pages' settings (milestone 6). Keys and defaults are the Mac's UserDefaults names and values
// (Settings.swift, DesktopOptions.swift), without "custom.". The Mac's Settings.swift carries lines from the squashed
// Verso import; the values and ranges below are read from its behaviour (what each key holds, its default and range),
// not translated.

using System.Text.Json.Serialization;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    // ---- The type's own options ----

    /// <summary>Colour only › "Album gradient": the cover's colours as a moving gradient (on), or the album colour field (off). Mac <c>custom.albumGradientWallpaper</c>.</summary>
    public bool AlbumGradientWallpaper { get; init; } = true;

    /// <summary>The GPU effect chosen in its menu (an id from <c>WallpaperCatalog.Effects</c>), or "" for Match the music. Mac <c>shaderStyle</c>.</summary>
    public string ShaderStyle { get; init; } = "";

    /// <summary>The line pattern chosen in its menu (an id from <c>WallpaperCatalog.Patterns</c>), or "" for Match the music. Mac <c>patternStyle</c> (an Int there).</summary>
    public string PatternStyle { get; init; } = "";

    /// <summary>Imported image or video: Lunalyr's own copy of the file, or "". Mac <c>custom.wallpaperPath</c>.</summary>
    public string WallpaperPath { get; init; } = "";

    /// <summary>The imported file's original name, for the page. Mac <c>custom.wallpaperName</c>.</summary>
    public string WallpaperName { get; init; } = "";

    /// <summary>How the imported picture or video fits the screen: "fill", "fillTop", "fillBottom", "fit". Mac <c>custom.wallpaperPlacement</c>.</summary>
    public string WallpaperPlacement { get; init; } = "fill";

    // ---- Adjustments (Reset Adjustments puts these back) ----

    /// <summary>"Motion speed", 0.2...2. Mac <c>effectSpeed</c>.</summary>
    public double EffectSpeed { get; init; } = 1;

    /// <summary>"Colour richness", 0.4...1.8. Mac <c>vividness</c>.</summary>
    public double Vividness { get; init; } = 1;

    /// <summary>"Darkening", 0...2: the amount chosen. Mac <c>scrimStrength</c>.</summary>
    public double ScrimStrength { get; init; } = 1;

    /// <summary>"Pattern visibility", 0...1 (Line patterns). Mac <c>patternOpacity</c>.</summary>
    public double PatternOpacity { get; init; } = 0.85;

    /// <summary>Covers › "Softening": blur over animated covers, 0...25 points. Mac <c>motionBlur</c>.</summary>
    public double MotionBlur { get; init; } = 5;

    // ---- Legibility ----

    /// <summary>"Automatic text backing": the backdrop decides how much shade sits behind the lyrics. Mac <c>custom.textBackingAutomatic</c>.</summary>
    public bool TextBackingAutomatic { get; init; } = true;

    /// <summary>"Text backing" when it isn't automatic, 0...1. Mac <c>custom.textBacking</c>.</summary>
    public double TextBacking { get; init; } = 0.35;

    // ---- Covers ----

    /// <summary>"Use animated covers" (Match the music). Mac <c>useMotionArtwork</c>.</summary>
    public bool UseMotionArtwork { get; init; } = true;

    /// <summary>How an animated cover fits the screen: "fill", "fillTop", "fillBottom", "fit". Mac <c>motionPlacement</c>.</summary>
    public string MotionPlacement { get; init; } = "fill";

    // ---- Values ----

    /// <summary>The Mac's wallpaper types, by <c>backgroundMode</c> raw value, in its order.</summary>
    public static IReadOnlyList<string> BackgroundModes { get; } =
        ["auto", "pattern", "shader", "colourOnly", "visualizer", "desktop", "custom", "musicVideo"];

    public static IReadOnlyList<string> Placements { get; } = ["fill", "fillTop", "fillBottom", "fit"];

    public const double SpeedMin = 0.2, SpeedMax = 2, VividnessMin = 0.4, VividnessMax = 1.8, ScrimMin = 0, ScrimMax = 2,
        MotionBlurMax = 25;

    /// <summary>The adjustments at the values Lunalyr comes with (Reset Adjustments).</summary>
    [JsonIgnore]
    public bool AdjustmentsAreDefault =>
        EffectSpeed == 1 && Vividness == 1 && ScrimStrength == 1 && PatternOpacity == 0.85 && MotionBlur == 5;

    /// <summary>These settings with every adjustment back as Lunalyr comes (the Mac's <c>resetAdvanced</c>, Softening included).</summary>
    public AppSettings WithDefaultAdjustments() =>
        this with { EffectSpeed = 1, Vividness = 1, ScrimStrength = 1, PatternOpacity = 0.85, MotionBlur = 5 };

    /// <summary>The adjustments as the backgrounds read them (<see cref="SceneSnapshot.Adjustments"/>).</summary>
    public WallpaperAdjustments Adjustments() => new()
    {
        Speed = EffectSpeed,
        Vividness = Vividness,
        Darkening = ScrimStrength,
        ManualBacking = TextBackingAutomatic ? null : TextBacking,
        PatternOpacity = PatternOpacity,
    };

    /// <summary>
    /// The wallpaper settings in range. <c>backgroundMode</c> "gradient" (Windows' own value until milestone 6: the album
    /// colour field) becomes the Mac's Colour only with Album gradient off, which draws the same; "colour" and
    /// "colorOnly" become Colour only; anything unknown is Match the music, the Mac's default.
    /// </summary>
    private AppSettings NormalizedWallpaper()
    {
        var mode = BackgroundMode?.Trim() ?? "";
        var gradient = AlbumGradientWallpaper;
        switch (mode)
        {
            case "gradient":
                mode = "colourOnly";
                gradient = false;
                break;
            case "colour" or "colorOnly":
                mode = "colourOnly";
                break;
            case "visualiser":
                mode = "visualizer";
                break;
        }
        if (!BackgroundModes.Contains(mode)) mode = "auto";
        var settings = this with
        {
            BackgroundMode = mode,
            AlbumGradientWallpaper = gradient,
            ShaderStyle = ShaderStyle?.Trim() ?? "",
            PatternStyle = PatternStyle?.Trim() ?? "",
            WallpaperPath = WallpaperPath?.Trim() ?? "",
            WallpaperName = WallpaperName ?? "",
            WallpaperPlacement = Placements.Contains(WallpaperPlacement) ? WallpaperPlacement : "fill",
            MotionPlacement = Placements.Contains(MotionPlacement) ? MotionPlacement : "fill",
            EffectSpeed = InRange(EffectSpeed, SpeedMin, SpeedMax, 1),
            Vividness = InRange(Vividness, VividnessMin, VividnessMax, 1),
            ScrimStrength = InRange(ScrimStrength, ScrimMin, ScrimMax, 1),
            PatternOpacity = InRange(PatternOpacity, 0, 1, 0.85),
            MotionBlur = InRange(MotionBlur, 0, MotionBlurMax, 5),
            TextBacking = InRange(TextBacking, 0, 1, 0.35),
        };
        NormalizeEffects(ref settings);
        NormalizeVisualiser(ref settings);
        return settings;
    }

    private static double InRange(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    // ---- Hooks: settings of other wallpaper layers, kept in their own partial files ----

    /// <summary>GPU effects / line patterns: put their own keys in range (implement in <c>AppSettings.Effects.cs</c>).</summary>
    static partial void NormalizeEffects(ref AppSettings settings);

    /// <summary>Visualisers: put their own keys in range (implement in <c>AppSettings.Visualiser.cs</c>).</summary>
    static partial void NormalizeVisualiser(ref AppSettings settings);
}
