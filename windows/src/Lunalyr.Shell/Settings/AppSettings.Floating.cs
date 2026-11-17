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

    /// <summary>
    /// Each layout's own floating window size and text size, [width, height, text size] in points, by layout id (Windows'
    /// own; <see cref="FloatingLayoutSize"/>). The window's place stays shared by every layout.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<double>> FloatingLayoutSizes { get; init; } = new Dictionary<string, IReadOnlyList<double>>();

    /// <summary>The size <paramref name="layout"/> was last resized to, or null.</summary>
    public FloatingLayoutSize? LayoutSize(string? layout) =>
        layout is not null && (FloatingLayoutSizes ?? new Dictionary<string, IReadOnlyList<double>>()).TryGetValue(layout, out var values)
            ? FloatingLayoutSize.Decode(values) : null;

    /// <summary>
    /// The Text size (or a size preset) set by hand: every layout takes it again, their own sizes forgotten. The same size
    /// changes nothing.
    /// </summary>
    public AppSettings WithFloatingTextSize(double points) =>
        Math.Abs(points - FloatingTextSize) < 0.01 ? this
            : this with { FloatingTextSize = ClampedFloatingTextSize(points), FloatingLayoutSizes = new Dictionary<string, IReadOnlyList<double>>() };

    /// <summary>The floating text size for <paramref name="layout"/>: its own once resized, else the Text size setting.</summary>
    public double FloatingTextSizeFor(string? layout) => LayoutSize(layout)?.TextSize ?? FloatingTextSize;

    /// <summary>
    /// The window was resized in Edit position while <paramref name="layout"/> showed: that layout keeps the new size, its
    /// text scaled by the height's change (<paramref name="heightRatio"/>).
    /// </summary>
    /// <param name="baseText">The text size the resize started from (a live drag sends many steps; each scales the
    /// start, never the step before); null for the layout's text now.</param>
    public AppSettings WithLayoutResized(string layout, Core.Compositions.SizeD size, double heightRatio, double? baseText = null)
    {
        var text = FloatingLayoutSize.ScaledText(baseText ?? FloatingTextSizeFor(layout), 1, heightRatio);
        var sizes = new Dictionary<string, IReadOnlyList<double>>(FloatingLayoutSizes ?? new Dictionary<string, IReadOnlyList<double>>())
        {
            [layout] = new FloatingLayoutSize(size, text).Encode(),
        };
        return this with { FloatingLayoutSizes = sizes };
    }

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
        FloatingLayoutSizes = new Dictionary<string, IReadOnlyList<double>>(),
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
            FloatingLayoutSizes = ValidLayoutSizes(FloatingLayoutSizes),
        };

        static IReadOnlyDictionary<string, IReadOnlyList<double>> ValidLayoutSizes(IReadOnlyDictionary<string, IReadOnlyList<double>>? sizes)
        {
            var valid = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
            foreach (var (key, values) in sizes ?? new Dictionary<string, IReadOnlyList<double>>())
                if (!string.IsNullOrEmpty(key) && FloatingLayoutSize.Decode(values) is { } size) valid[key] = size.Encode();
            return valid;
        }

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
    /// the setting (translations have no Windows setting yet), plus Windows' one-for-all <c>wordArrival</c>.
    /// </summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        // Settings
        "fontChoice", "fontFamily", "lyricComposition", "constellationSpread", "constellationLinkOpacity",
        "wordRevealByStyle", "wordArrival", "wordArrivalByStyle", "nextWordPreviewByStyle", "nextWordPreviewLead",
        "wordEntranceMix", "echoStrength", "lyricScale", "showInterludes", "thinkingOrbStyle", "wordSymbolsEnabled", "actionWordsEnabled",
        "actionWordsStrength", "editorialTypography", "editorialAmount", "randomizeLyricFonts",
        // Layout rotation, backing vocals, credits, the opening title
        "rotateLayouts", "backingVocalsLayer", "backingVocalStyle", "creditsLayer", "openingTitle", "openingTitleCover",
        // Readings and translations shown (the Mac's FloatingLyrics.swift lists Show translations right after them)
        "romanizationDisplay", "showTranslations",
        // Text and highlight colours
        "lyricColourMode", "lyricColourSpeed", "lyricColourStrength", "lyricColourCustom", "lyricColourPastel",
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
    public static IReadOnlyDictionary<string, JsonElement> Current(AppSettings settings) => Values(settings, Keys);

    /// <summary><paramref name="keys"/> as the settings hold them, as stored.</summary>
    public static Dictionary<string, JsonElement> Values(AppSettings settings, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var node = JsonSerializer.SerializeToNode(settings, AppSettingsJsonContext.Default.AppSettings)!.AsObject();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in keys)
            if (node[key] is { } value) values[key] = JsonSerializer.SerializeToElement(value, AppSettingsJsonContext.Default.JsonNode);
        return values;
    }

    /// <summary>Today's kept style for <paramref name="surface"/> from before monitors had looks, read for one release.</summary>
    public static IReadOnlyDictionary<string, JsonElement>? Saved(AppSettings settings, LyricsSurface surface) =>
        surface == LyricsSurface.Floating ? settings.LyricStyleFloating : settings.LyricStyleDesktop;

    /// <summary>
    /// Switches the style Settings edits on the monitor it edits (<see cref="MonitorLooks.SwitchEditing"/>): the style in
    /// use is kept as its slot's and <paramref name="surface"/>'s is loaded. <c>Seeded</c> is true when it had no style yet
    /// and was worked out now (a first Floating style starts from the desktop's, less its size).
    /// </summary>
    public static (AppSettings Settings, bool Seeded) Switch(AppSettings settings, LyricsSurface surface)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Surface == surface) return (settings, false);
        var editing = settings.EditingSlot(null);
        return MonitorLooks.SwitchEditing(settings, editing, editing with { Surface = surface }, settings.LookMonitor ?? editing.Display);
    }

    /// <summary>
    /// Puts <paramref name="values"/> in place for each of <paramref name="keys"/>; a key it lacks goes back to its
    /// default. Every other setting comes back as it was.
    /// </summary>
    public static AppSettings Applied(AppSettings settings, IReadOnlyDictionary<string, JsonElement> values, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(values);
        var node = JsonSerializer.SerializeToNode(settings, AppSettingsJsonContext.Default.AppSettings)!.AsObject();
        // A key the values lack takes the setting's own default, written out: left missing, the generated reader would
        // set it to the type's zero (false for a switch that defaults on).
        var defaults = JsonSerializer.SerializeToNode(new AppSettings(), AppSettingsJsonContext.Default.AppSettings)!.AsObject();
        foreach (var key in keys)
        {
            node.Remove(key);
            if (values.TryGetValue(key, out var value)) node[key] = JsonSerializer.SerializeToNode(value, AppSettingsJsonContext.Default.JsonElement);
            else if (defaults[key] is { } fallback) node[key] = fallback.DeepClone();
        }
        var applied = node.Deserialize(AppSettingsJsonContext.Default.AppSettings) ?? settings;
        // The kept styles and looks are not part of a style: they come back as they were.
        return applied.Normalized() with
        {
            LyricStyleDesktop = settings.LyricStyleDesktop, LyricStyleFloating = settings.LyricStyleFloating, Looks = settings.Looks,
            DisplayLyrics = settings.DisplayLyrics, LookMonitor = settings.LookMonitor,
        };
    }
}
