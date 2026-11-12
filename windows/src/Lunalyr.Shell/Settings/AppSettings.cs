using System.Text.Json.Serialization;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell.Settings;

/// <summary>
/// The app's settings, version 1: <c>{data}\settings.json</c> through <see cref="Lunalyr.Platform.Settings.SettingsStore{T}"/>.
/// Keys and stored values follow the Mac's <c>UserDefaults</c> keys without the <c>custom.</c> prefix where the
/// setting means the same thing, so settings can be compared across the apps; Windows-only settings say so.
/// <c>docs/APP-SHELL.md</c> lists every key.
/// </summary>
/// <remarks>Immutable: change it with <c>store.Update(s =&gt; s with { … })</c>. <see cref="Normalized"/> repairs
/// out-of-range or unknown values read from disk.</remarks>
public sealed record AppSettings
{
    /// <summary>The schema version this build writes.</summary>
    public const int SchemaVersion = 1;

    // ---- Lyric style ----

    /// <summary>The lyric layout (Mac <c>lyricComposition</c>; its raw values). Default "editorial".</summary>
    public string LyricComposition { get; init; } = "editorial";

    /// <summary>
    /// How each word appears as it is sung: the Mac's <c>WordArrival</c> raw values ("automatic" = match the layout,
    /// "fade" = Soft focus, "lift" = Float up, "pop", "orbit", "glide"…). The Mac keeps one per layout
    /// (<c>custom.wordArrivalByStyle</c>); version 1 has one for all.
    /// </summary>
    public string WordArrival { get; init; } = "automatic";

    /// <summary>Lyric size multiplier (Mac <c>lyricScale</c>, 1 = the layout's own size), 0.5–2.</summary>
    public double LyricScale { get; init; } = 1.0;

    /// <summary>
    /// "system" (Segoe UI Variable) or "installed" (<see cref="FontFamily"/>). Windows values for the Mac's
    /// <c>fontChoice</c>, whose fonts (Manrope, SF Pro, Avenir Next…) aren't on Windows.
    /// </summary>
    public string FontChoice { get; init; } = "system";

    /// <summary>An installed font family, used when <see cref="FontChoice"/> is "installed" (Mac <c>googleFontFamily</c>).</summary>
    public string FontFamily { get; init; } = "";

    /// <summary>Show the song's production credits on the wallpaper (Mac <c>custom.creditsLayer</c>).</summary>
    public bool CreditsLayer { get; init; } = true;

    /// <summary>"Show the song title at the start": the opening title card as a song begins (Mac <c>custom.openingTitle</c>).</summary>
    public bool OpeningTitle { get; init; } = true;

    /// <summary>"Show the album cover" beside the opening title (Mac <c>custom.openingTitleCover</c>).</summary>
    public bool OpeningTitleCover { get; init; } = true;

    /// <summary>Three dots during instrumental breaks (Mac <c>custom.showInterludes</c>).</summary>
    public bool ShowInterludes { get; init; } = true;

    /// <summary>Where highlight tiles take their colour from: "complement", "album" or "custom" (Mac <c>custom.lyricHighlightMode</c>).</summary>
    public string LyricHighlightMode { get; init; } = "complement";

    /// <summary>The custom highlight colour, <c>#RRGGBB</c> (Mac <c>custom.lyricHighlightCustom</c>).</summary>
    public string LyricHighlightCustom { get; init; } = Core.Colour.LyricHighlight.DefaultCustomHex;

    /// <summary>With the layout on "automatic" (Match the music), each new song takes the next layout in a rotation (Mac <c>custom.rotateLayouts</c>); off chooses by the song's character alone.</summary>
    public bool RotateLayouts { get; init; } = true;

    /// <summary>Songs in a row per layout in the rotation, 0–5 (0 leaves it out); a layout missing here gets 1 (Mac <c>custom.rotateLayoutCounts</c>).</summary>
    public IReadOnlyDictionary<string, int> RotateLayoutCounts { get; init; } = new Dictionary<string, int>();

    /// <summary>Where the rotation has got to (Mac <c>custom.rotateLayoutsCursor</c>).</summary>
    public int RotateLayoutsCursor { get; init; }

    // ---- Readings ----

    /// <summary>"off", "below" or "replace" (Mac <c>custom.romanizationDisplay</c>).</summary>
    public string RomanizationDisplay { get; init; } = "below";

    /// <summary>"automatic", "japanese", "mandarin", "cantonese", "korean" or "other" (Mac <c>custom.romanizationLanguage</c>).</summary>
    public string RomanizationLanguage { get; init; } = "automatic";

    /// <summary>Pinyin tone marks (Mac <c>custom.romanizationTones</c>).</summary>
    public bool RomanizationTones { get; init; } = true;

    /// <summary>Prefer a reading that came with the lyrics (Mac <c>custom.romanizationSaved</c>).</summary>
    public bool RomanizationSaved { get; init; } = true;

    // ---- Wallpaper ----

    /// <summary>
    /// The background: "gradient" (the animated album colour field; Windows' default) or the Mac's "colourOnly" (the
    /// straight album gradient), then the Mac's other <c>backgroundMode</c> values as they are ported ("pattern",
    /// "shader", "visualizer", "desktop", "custom", "musicVideo"). EngineWallpaperRenderer.BackgroundLayerFor maps a
    /// value to its layer.
    /// </summary>
    public string BackgroundMode { get; init; } = "gradient";

    /// <summary>The wallpaper is paused from the tray (Mac <c>custom.wallpaperPaused</c>).</summary>
    public bool WallpaperPaused { get; init; }

    // ---- Performance ----

    /// <summary>Frame-rate cap: 15, 30 or 60 fps (Mac <c>custom.animationRate</c>), or 0 to follow the display. Default 60.</summary>
    public int AnimationRate { get; init; } = 60;

    /// <summary>Pause while windows cover the desktop (Mac <c>custom.pauseWhenCovered</c>).</summary>
    public bool PauseWhenCovered { get; init; } = true;

    /// <summary>Pause while Windows' battery saver (energy saver) is on. The Mac's nearest is <c>respectLowPower</c>.</summary>
    public bool PauseOnBatterySaver { get; init; } = true;

    /// <summary>Pause while a full-screen app or game is in front. Windows only.</summary>
    public bool PauseWhenFullScreen { get; init; } = true;

    // ---- Timing ----

    /// <summary>Global lyric offset in seconds, ±3 (Mac <c>custom.lyricOffset</c>; positive shows lyrics earlier).</summary>
    public double LyricOffset { get; init; }

    /// <summary>"Treat long pauses as instrumental" (Mac <c>custom.repairQQGaps</c>).</summary>
    public bool RepairQQGaps { get; init; } = true;

    /// <summary>Per-song offsets by hashed key (Mac <c>custom.songOffsets</c>; see <c>SongOffsets</c>).</summary>
    public IReadOnlyDictionary<string, double> SongOffsets { get; init; } = new Dictionary<string, double>();

    // ---- Lyric sources ----

    /// <summary>Every source in the configured order, by the Mac's raw values (Mac <c>custom.lyricSourceOrder</c>).</summary>
    public IReadOnlyList<string> LyricSourceOrder { get; init; } = [.. LyricSourceSnapshot.RecommendedOrder.Select(s => s.RawValue())];

    /// <summary>
    /// The switched-on sources (Mac <c>custom.lyricSources</c>). Like the Mac, a fresh install has everything on
    /// except the local folder and Apple Music's own lyrics.
    /// </summary>
    public IReadOnlyList<string> LyricSources { get; init; } =
        [.. LyricSourceInfo.All.Where(s => s is not (LyricSourceId.Lyrimuse or LyricSourceId.AppleMusicCache)).Select(s => s.RawValue()).Order(StringComparer.Ordinal)];

    /// <summary>"smart" (Accuracy first) or "priority" (Source order) (Mac <c>custom.lyricMatchMode</c>).</summary>
    public string LyricMatchMode { get; init; } = "smart";

    /// <summary>The local lyric folder (Mac <c>custom.lyrimuseFolder</c>), or empty.</summary>
    public string LyrimuseFolder { get; init; } = "";

    /// <summary>
    /// "Search under other names" (Mac <c>custom.artistAliases</c>): when a service finds nothing under the listed
    /// names, NetEase, QQ, Kugou and Kuwo search again under the artist's native name (from MusicBrainz), and a song
    /// listed under an English title is searched again under its original one (from Apple's catalogue). On by default.
    /// No settings window yet (milestone 5).
    /// </summary>
    public bool ArtistAliases { get; init; } = true;

    // ---- Desktop player ----

    /// <summary>
    /// "Show the player when the pointer rests on the desktop" (Mac <c>custom.showControlsOnHover</c>). On by default.
    /// </summary>
    public bool ShowControlsOnHover { get; init; } = true;

    /// <summary>
    /// The seek bar's right-hand label: "remaining" (time left, the default) or "total" (the song's length); any other
    /// value reads as "remaining" (Mac <c>custom.desktopPlayer.timeMode</c>).
    /// </summary>
    public string DesktopPlayerTimeMode { get; init; } = "remaining";

    // ---- Derived values ----

    public static IReadOnlyList<string> Compositions { get; } =
        ["automatic", "scattered", "constellation", "constellationEditorial", "echo", "editorial", "spoken", "cascade", "cutout", "hinge",
         "broadside", "portal", "mosaic", "torque", "fable"];

    public static IReadOnlyList<string> WordArrivals { get; } =
        ["automatic", "fade", "lift", "pop", "orbit", "glide", "unfold", "cutoutFold", "cutoutSlide", "cutoutHighlight",
         "satinRibbon", "splitInk", "mix"];

    public static IReadOnlyList<int> AnimationRates { get; } = [0, 15, 30, 60];

    /// <summary>The settings with every value in range and every choice a known one (defaults otherwise).</summary>
    public AppSettings Normalized()
    {
        var defaults = new AppSettings();
        return this with
        {
            LyricComposition = Compositions.Contains(LyricComposition) ? LyricComposition : defaults.LyricComposition,
            WordArrival = WordArrivals.Contains(WordArrival) ? WordArrival : defaults.WordArrival,
            LyricScale = double.IsFinite(LyricScale) ? Math.Clamp(LyricScale, 0.5, 2) : defaults.LyricScale,
            FontChoice = FontChoice is "system" or "installed" ? FontChoice : defaults.FontChoice,
            FontFamily = FontFamily?.Trim() ?? "",
            RomanizationDisplay = RomanizationOptions.ParseDisplay(RomanizationDisplay) is null ? defaults.RomanizationDisplay : RomanizationDisplay,
            RomanizationLanguage = RomanizationOptions.ParseLanguage(RomanizationLanguage) is null ? defaults.RomanizationLanguage : RomanizationLanguage,
            BackgroundMode = string.IsNullOrWhiteSpace(BackgroundMode) ? defaults.BackgroundMode : BackgroundMode,
            AnimationRate = AnimationRates.Contains(AnimationRate) ? AnimationRate : defaults.AnimationRate,
            LyricOffset = double.IsFinite(LyricOffset)
                ? Math.Clamp(LyricOffset, -Core.Lyrics.Timing.SongOffsets.GlobalOffsetLimit, Core.Lyrics.Timing.SongOffsets.GlobalOffsetLimit)
                : 0,
            SongOffsets = SongOffsets ?? new Dictionary<string, double>(),
            LyricSourceOrder = LyricSourceOrder ?? defaults.LyricSourceOrder,
            LyricSources = LyricSources ?? defaults.LyricSources,
            LyricMatchMode = LyricMatchModeInfo.TryParseRaw(LyricMatchMode, out _) ? LyricMatchMode : defaults.LyricMatchMode,
            LyrimuseFolder = LyrimuseFolder ?? "",
            LyricHighlightMode = LyricHighlightMode is "complement" or "album" or "custom" ? LyricHighlightMode : defaults.LyricHighlightMode,
            LyricHighlightCustom = Core.Colour.ColourHex.ToOkLab(LyricHighlightCustom) is null ? defaults.LyricHighlightCustom : LyricHighlightCustom.Trim().ToUpperInvariant(),
            RotateLayoutCounts = (RotateLayoutCounts ?? new Dictionary<string, int>()).ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, 0, Core.Compositions.LayoutRotation.MaximumCount)),
            RotateLayoutsCursor = Math.Max(0, RotateLayoutsCursor),
        };
    }

    /// <summary>What the lyric lookup asks: the configured order (unknown names dropped, new sources appended) and the switched-on set.</summary>
    public LyricSourceSnapshot SourceSnapshot()
    {
        var order = new List<LyricSourceId>();
        foreach (var raw in (LyricSourceOrder ?? []).Concat(LyricSourceSnapshot.RecommendedOrder.Select(s => s.RawValue())))
        {
            if (LyricSourceInfo.TryParseRaw(raw, out var id) && !order.Contains(id)) order.Add(id);
        }

        var enabled = new HashSet<LyricSourceId>();
        foreach (var raw in LyricSources ?? [])
        {
            if (LyricSourceInfo.TryParseRaw(raw, out var id)) enabled.Add(id);
        }

        var mode = LyricMatchModeInfo.TryParseRaw(LyricMatchMode, out var parsed) ? parsed : Core.Lyrics.Ranking.LyricMatchMode.AccuracyFirst;
        return LyricSourceSnapshot.From(order, enabled, mode, LyrimuseFolder ?? "");
    }

    /// <summary>How readings are made and shown.</summary>
    public RomanizationOptions Readings() => new(
        RomanizationOptions.ParseDisplay(RomanizationDisplay) ?? Core.Lyrics.Readings.RomanizationDisplay.Below,
        RomanizationOptions.ParseLanguage(RomanizationLanguage) ?? Core.Lyrics.Readings.RomanizationLanguage.Automatic,
        RomanizationTones,
        RomanizationSaved);

    /// <summary>The renderer's view of the lyric settings.</summary>
    public LyricStyle Style() => new()
    {
        FontFamily = FontChoice == "installed" && !string.IsNullOrWhiteSpace(FontFamily) ? FontFamily.Trim() : LyricStyle.Default.FontFamily,
        SizeFraction = (float)(LyricStyle.Default.SizeFraction * (double.IsFinite(LyricScale) ? Math.Clamp(LyricScale, 0.5, 2) : 1)),
        Reveal = SceneReveal(WordArrival),
        Credits = CreditsLayer,
        OpeningTitle = OpeningTitle,
        OpeningTitleCover = OpeningTitleCover,
        Interludes = ShowInterludes,
        HighlightMode = LyricHighlightMode,
        HighlightCustom = LyricHighlightCustom,
        Readings = (RomanizationOptions.ParseDisplay(RomanizationDisplay) ?? Core.Lyrics.Readings.RomanizationDisplay.Below) switch
        {
            Core.Lyrics.Readings.RomanizationDisplay.Off => ReadingPlacement.Off,
            Core.Lyrics.Readings.RomanizationDisplay.Replace => ReadingPlacement.Instead,
            _ => ReadingPlacement.Below,
        },
    };

    /// <summary>
    /// The scene contract names reveals "softFocus", "floatUp"…, but the Mac's stored values are "fade", "lift"…:
    /// the settings keep the Mac's, and the renderer gets the scene's names (others pass through unchanged).
    /// </summary>
    public static string SceneReveal(string? wordArrival) => wordArrival switch
    {
        "fade" => "softFocus",
        "lift" => "floatUp",
        null or "" => "automatic",
        _ => wordArrival,
    };
}

/// <summary>Source-generated JSON metadata for <see cref="AppSettings"/> (Native AOT safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class AppSettingsJsonContext : JsonSerializerContext;
