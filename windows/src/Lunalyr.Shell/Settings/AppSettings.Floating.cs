// Floating lyrics' settings (milestone 7), ported from the Mac's FloatingLyricsOptions and LyricStyleProfiles
// (FloatingLyrics.swift, Lunalyr's own lines). The keys are the Mac's without "custom." and with the dots folded into
// camelCase, as the other Windows keys are (custom.floating.textSize → floatingTextSize).

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lunalyr.Core.Floating;

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    /// <summary>Small, Medium and Large, as text sizes in points (the Mac's <c>SizePreset</c>).</summary>
    public static IReadOnlyList<(string Title, double Points)> FloatingSizePresets { get; } = [("Small", 30), ("Medium", 44), ("Large", 64)];

    public const double FloatingTextSizeMin = 22, FloatingTextSizeMax = 110, DefaultFloatingTextSize = 44, DefaultFloatingBackdrop = 0.6;

    /// <summary>
    /// Where the lyrics show: "desktop" (on the wallpaper, the default, so nothing changes until someone picks
    /// Floating) or "floating" (over every app). Never both (Mac <c>custom.lyricsSurface</c>). Change it with
    /// <see cref="WithLyricsSurface"/>, which swaps the lyric style too.
    /// </summary>
    public string LyricsSurface { get; init; } = "desktop";

    /// <summary>The floating lyrics' text size in points, 22–110 (Mac <c>custom.floating.textSize</c>).</summary>
    public double FloatingTextSize { get; init; } = DefaultFloatingTextSize;

    /// <summary>How strong the soft shade behind the floating lyrics is, 0–1 (Mac <c>custom.floating.backdrop</c>).</summary>
    public double FloatingBackdrop { get; init; } = DefaultFloatingBackdrop;

    /// <summary>Fade the floating lyrics down while the pointer is over them (Mac <c>custom.floating.fadeUnderPointer</c>).</summary>
    public bool FloatingFadeUnderPointer { get; init; } = true;

    /// <summary>Where the floating window sits: a preset's raw value or "custom" (Mac <c>custom.floating.placement</c>).</summary>
    public string FloatingPlacement { get; init; } = "bottom";

    /// <summary>The display's lasting key, or empty for the main display (Mac <c>custom.floating.display</c>).</summary>
    public string FloatingDisplay { get; init; } = "";

    /// <summary>Frames placed by hand per display key, relative to that display, in points, y up (Mac <c>custom.floating.frames</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<double>> FloatingFrames { get; init; } = new Dictionary<string, IReadOnlyList<double>>();

    /// <summary>Window sizes per display key, used by the presets (Mac <c>custom.floating.sizes</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<double>> FloatingSizes { get; init; } = new Dictionary<string, IReadOnlyList<double>>();

    /// <summary>The desktop's lyric style, kept while the floating lyrics are in use (Mac <c>custom.lyricStyle.desktop</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? LyricStyleDesktop { get; init; }

    /// <summary>The floating lyrics' style, kept while the desktop is in use (Mac <c>custom.lyricStyle.floating</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? LyricStyleFloating { get; init; }

    [JsonIgnore]
    public LyricsSurface Surface => LyricsSurfaces.Parse(LyricsSurface) ?? Core.Floating.LyricsSurface.Desktop;

    [JsonIgnore]
    public bool IsFloating => Surface == Core.Floating.LyricsSurface.Floating;

    [JsonIgnore]
    public FloatingPlacement Placement => FloatingPlacements.Parse(FloatingPlacement) ?? Core.Floating.FloatingPlacement.Bottom;

    public static double ClampedFloatingTextSize(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, FloatingTextSizeMin, FloatingTextSizeMax) : DefaultFloatingTextSize;

    /// <summary>The other surface, its style swapped in (the Mac's <c>toggleSurface</c>).</summary>
    public AppSettings ToggleSurface() =>
        WithLyricsSurface(IsFloating ? Core.Floating.LyricsSurface.Desktop : Core.Floating.LyricsSurface.Floating);

    /// <summary>
    /// These settings showing the lyrics on <paramref name="surface"/>: the style in use is kept as the outgoing
    /// surface's and the incoming one's is put in its place (<see cref="LyricStyleProfiles.Switch"/>).
    /// </summary>
    public AppSettings WithLyricsSurface(LyricsSurface surface) => LyricStyleProfiles.Switch(this, surface).Settings;

    /// <summary>A frame placed by hand on <paramref name="display"/>, relative to it.</summary>
    public FloatingRect? CustomFrame(string display) =>
        (FloatingFrames ?? new Dictionary<string, IReadOnlyList<double>>()).TryGetValue(display, out var values) ? FloatingFrame.Decode(values) : null;

    /// <summary>The size last given on <paramref name="display"/>, for the presets.</summary>
    public Core.Compositions.SizeD? SavedSize(string display) =>
        (FloatingSizes ?? new Dictionary<string, IReadOnlyList<double>>()).TryGetValue(display, out var values) ? FloatingFrame.Decode(values)?.Size : null;

    /// <summary>Keeps a frame dragged into place on <paramref name="display"/> and switches to Custom.</summary>
    public AppSettings WithCustomFrame(FloatingRect relative, string display)
    {
        var frames = new Dictionary<string, IReadOnlyList<double>>(FloatingFrames ?? new Dictionary<string, IReadOnlyList<double>>())
        {
            [display] = FloatingFrame.Encode(relative),
        };
        var sizes = new Dictionary<string, IReadOnlyList<double>>(FloatingSizes ?? new Dictionary<string, IReadOnlyList<double>>())
        {
            [display] = FloatingFrame.Encode(new FloatingRect(0, 0, relative.Width, relative.Height)),
        };
        return this with { FloatingFrames = frames, FloatingSizes = sizes, FloatingPlacement = Core.Floating.FloatingPlacement.Custom.Raw() };
    }

    /// <summary>Forgets the sizes and frames set by hand; the presets go back to the size that follows the text size.</summary>
    public AppSettings WithFloatingFramesReset() => this with
    {
        FloatingFrames = new Dictionary<string, IReadOnlyList<double>>(),
        FloatingSizes = new Dictionary<string, IReadOnlyList<double>>(),
        FloatingPlacement = Placement == Core.Floating.FloatingPlacement.Custom ? Core.Floating.FloatingPlacement.Bottom.Raw() : FloatingPlacement,
    };

    private AppSettings NormalizedFloating()
    {
        var defaults = new AppSettings();
        var backdrop = FloatingBackdrop;
        return this with
        {
            LyricsSurface = LyricsSurfaces.Parse(LyricsSurface) is { } surface ? surface.Raw() : defaults.LyricsSurface,
            FloatingTextSize = ClampedFloatingTextSize(FloatingTextSize),
            FloatingBackdrop = double.IsFinite(backdrop) ? Math.Clamp(backdrop, 0, 1) : DefaultFloatingBackdrop,
            FloatingPlacement = FloatingPlacements.Parse(FloatingPlacement) is { } placement ? placement.Raw() : defaults.FloatingPlacement,
            FloatingDisplay = FloatingDisplay ?? "",
            FloatingFrames = ValidFrames(FloatingFrames),
            FloatingSizes = ValidFrames(FloatingSizes),
        };

        static IReadOnlyDictionary<string, IReadOnlyList<double>> ValidFrames(IReadOnlyDictionary<string, IReadOnlyList<double>>? frames)
        {
            var valid = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
            foreach (var (key, values) in frames ?? new Dictionary<string, IReadOnlyList<double>>())
                if (key is not null && FloatingFrame.Decode(values) is { } rect) valid[key] = FloatingFrame.Encode(rect);
            return valid;
        }
    }
}

/// <summary>
/// The lyric style is kept twice, once per surface, so changing the floating lyrics never changes the desktop's and
/// the reverse (the Mac's <c>LyricStyleProfiles</c>). The app's lyric settings stay the one place the renderer and the
/// settings pages read, and always hold the style of the surface in use. Switching surface stores the outgoing style
/// under its own key and loads the incoming one. The first switch to Floating starts it from the desktop's style (with
/// the size reset, since Floating has a size of its own); from then on the two never meet.
/// </summary>
public static class LyricStyleProfiles
{
    /// <summary>
    /// Every setting that belongs to a lyric style, as <c>settings.json</c> names it: the Mac's list where Windows has
    /// the setting (its colour modes, backing-vocal switches, translations, Editorial typography, randomised fonts and
    /// next-word previews have no Windows setting yet), plus Windows' one-for-all <c>wordArrival</c>.
    /// </summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        // Settings
        "fontChoice", "fontFamily", "lyricComposition", "constellationSpread", "constellationLinkOpacity",
        "wordArrival", "wordArrivalByStyle", "wordEntranceMix", "echoStrength", "lyricScale", "showInterludes",
        // Layout rotation, credits, the opening title
        "rotateLayouts", "creditsLayer", "openingTitle", "openingTitleCover",
        // Readings shown
        "romanizationDisplay",
        // Highlight colours
        "lyricHighlightMode", "lyricHighlightCustom",
    ];

    /// <summary>Where each surface's style is kept while the other is in use (its <c>settings.json</c> key).</summary>
    public static string StoreKey(LyricsSurface surface) => surface == LyricsSurface.Floating ? "lyricStyleFloating" : "lyricStyleDesktop";

    /// <summary>
    /// What the floating style starts with that is not the desktop's: its size is <see cref="AppSettings.FloatingTextSize"/>,
    /// so the wallpaper's Lyric size multiplier starts neutral.
    /// </summary>
    public static IReadOnlyDictionary<string, double> FloatingSeed { get; } = new Dictionary<string, double> { ["lyricScale"] = 1.0 };

    /// <summary>The style in use, as stored.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Current(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, AppSettingsJsonContext.Default.AppSettings)!.AsObject();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in Keys)
            if (node[key] is { } value) values[key] = JsonSerializer.SerializeToElement(value, AppSettingsJsonContext.Default.JsonNode);
        return values;
    }

    public static IReadOnlyDictionary<string, JsonElement>? Saved(AppSettings settings, LyricsSurface surface) =>
        surface == LyricsSurface.Floating ? settings.LyricStyleFloating : settings.LyricStyleDesktop;

    /// <summary>
    /// Stores the style in use as the current surface's and puts <paramref name="surface"/>'s in its place.
    /// <c>Seeded</c> is true when it had no style yet and was started from the outgoing one.
    /// </summary>
    public static (AppSettings Settings, bool Seeded) Switch(AppSettings settings, LyricsSurface surface)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var old = settings.Surface;
        if (old == surface) return (settings, false);
        var current = Current(settings);
        var next = With(settings with { LyricsSurface = surface.Raw() }, old, current);
        if (Saved(settings, surface) is { } incoming)
            return (With(Apply(next, incoming), surface, incoming), false);

        // The first time: start from the style just stored.
        if (surface == LyricsSurface.Floating)
        {
            var seeded = new Dictionary<string, JsonElement>(current, StringComparer.Ordinal);
            foreach (var (key, value) in FloatingSeed) seeded[key] = JsonSerializer.SerializeToElement(value, AppSettingsJsonContext.Default.Double);
            next = Apply(next, seeded);
        }
        return (With(next, surface, Current(next)), true);
    }

    private static AppSettings With(AppSettings settings, LyricsSurface surface, IReadOnlyDictionary<string, JsonElement> style) =>
        surface == LyricsSurface.Floating ? settings with { LyricStyleFloating = style } : settings with { LyricStyleDesktop = style };

    /// <summary>Puts <paramref name="style"/>'s values in place; a style key it lacks goes back to its default.</summary>
    private static AppSettings Apply(AppSettings settings, IReadOnlyDictionary<string, JsonElement> style)
    {
        var node = JsonSerializer.SerializeToNode(settings, AppSettingsJsonContext.Default.AppSettings)!.AsObject();
        foreach (var key in Keys)
        {
            node.Remove(key);
            if (style.TryGetValue(key, out var value)) node[key] = JsonSerializer.SerializeToNode(value, AppSettingsJsonContext.Default.JsonElement);
        }
        var applied = node.Deserialize(AppSettingsJsonContext.Default.AppSettings) ?? settings;
        // The kept styles are not part of a style: they come back as they were.
        return applied.Normalized() with { LyricStyleDesktop = settings.LyricStyleDesktop, LyricStyleFloating = settings.LyricStyleFloating };
    }
}
