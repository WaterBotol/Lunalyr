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
public sealed partial record AppSettings
{
    /// <summary>The schema version this build writes.</summary>
    public const int SchemaVersion = 1;

    // ---- Lyric style ----

    /// <summary>The lyric layout (Mac <c>lyricComposition</c>; its raw values). Default "constellation", as on the Mac.</summary>
    public string LyricComposition { get; init; } = "constellation";

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

    /// <summary>
    /// "Keep the song title on screen": the opening title stays for the whole song, quieter once the singing starts (Mac
    /// <c>custom.openingTitleAlways</c>, d849c49). Shared by both surfaces, as on the Mac.
    /// </summary>
    public bool OpeningTitleAlways { get; init; }

    /// <summary>"Title strength": how strong the kept title stays once it has rested, 30–100% (Mac <c>custom.openingTitleStrength</c>, e30fbb2).</summary>
    public double OpeningTitleStrength { get; init; } = Core.Compositions.OpeningTitle.RestOpacity;

    /// <summary>
    /// Where the title card sits on the desktop, as dragged on the Settings picture (Mac <c>custom.openingTitleLeft</c>,
    /// <c>custom.openingTitleLift</c>, <c>custom.openingTitleScale</c>, 3276613): its lower-left corner as shares of the
    /// screen (unset: low on the left) and its scale, 60–160%.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OpeningTitleLeft { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OpeningTitleLift { get; init; }

    public double OpeningTitleScale { get; init; } = 1;

    /// <summary>
    /// Where the floating window's title row sits, as dragged on the Settings picture (Mac <c>custom.floatingTitleCentre</c>,
    /// <c>custom.floatingTitleEdge</c>, <c>custom.floatingTitleScale</c>, dece022): its centre across the window, "bottom"
    /// for the window's foot (unset: the top), and its scale, 60–160%.
    /// </summary>
    public double FloatingTitleCentre { get; init; } = 0.5;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FloatingTitleEdge { get; init; }

    public double FloatingTitleScale { get; init; } = 1;

    /// <summary>The title card's place on the desktop, as stored.</summary>
    public Core.Compositions.OpeningTitlePlacement TitlePlacement() =>
        Core.Compositions.OpeningTitlePlacement.From(OpeningTitleLeft, OpeningTitleLift, OpeningTitleScale);

    /// <summary>The floating title row's place, as stored.</summary>
    public Core.Compositions.OpeningTitleRow TitleRow() =>
        Core.Compositions.OpeningTitleRow.From(FloatingTitleCentre, FloatingTitleEdge, FloatingTitleScale);

    /// <summary>With the title card's place set (the standard place clears it, as the Mac removes its keys).</summary>
    public AppSettings WithTitlePlacement(Core.Compositions.OpeningTitlePlacement placement) => this with
    {
        OpeningTitleLeft = placement.Corner?.Left,
        OpeningTitleLift = placement.Corner?.Lift,
        OpeningTitleScale = Core.Compositions.OpeningTitle.CardScale(placement.Scale),
    };

    /// <summary>With the floating title row's place set.</summary>
    public AppSettings WithTitleRow(Core.Compositions.OpeningTitleRow row) => this with
    {
        FloatingTitleCentre = Math.Clamp(row.Centre, 0, 1),
        FloatingTitleEdge = row.Bottom ? "bottom" : null,
        FloatingTitleScale = Core.Compositions.OpeningTitle.RowScale(row.Scale),
    };

    /// <summary>Three dots during instrumental breaks (Mac <c>custom.showInterludes</c>).</summary>
    public bool ShowInterludes { get; init; } = true;

    /// <summary>
    /// The thinking orb's style (Mac <c>custom.thinkingOrb.style</c>, 13b8ed6): one of thinking-orbs' nine states
    /// ("listening", "searching", …) or "mix". Unknown values read as listening. Per surface.
    /// </summary>
    public string ThinkingOrbStyle { get; init; } = "listening";

    /// <summary>Where highlight tiles take their colour from: "complement", "album" or "custom" (Mac <c>custom.lyricHighlightMode</c>).</summary>
    public string LyricHighlightMode { get; init; } = "complement";

    /// <summary>The custom highlight colour, <c>#RRGGBB</c> (Mac <c>custom.lyricHighlightCustom</c>).</summary>
    public string LyricHighlightCustom { get; init; } = Core.Colour.LyricHighlight.DefaultCustomHex;

    /// <summary>With the layout on "automatic" (Custom Mix), each new song takes the next layout in a rotation (Mac <c>custom.rotateLayouts</c>); off chooses by the song's character alone.</summary>
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
    /// The wallpaper type, the Mac's <c>backgroundMode</c> raw values: "auto" (Custom Mix, the default), "pattern",
    /// "shader", "colourOnly", "visualizer", "desktop" (Your wallpaper), "custom" (Imported image or video),
    /// "musicVideo". Windows' earlier "gradient" is read as Colour only with Album gradient off (see
    /// AppSettings.Wallpaper.cs); AppController.Wallpaper.cs turns the type into a background layer.
    /// </summary>
    public string BackgroundMode { get; init; } = "auto";

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
    /// Settings › Timing › Sources.
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

    /// <summary>
    /// The layouts offered. Editorial and Constellation + Editorial were cut on the Mac (next/tester-fixes, 14 Nov 2026)
    /// and here: their layers stay registered, but nothing offers or picks them, and saved values move on
    /// (<see cref="MigratedLayout"/>). "Add Editorial typography" stays. Broadside went the same way (the owner, 15 Nov
    /// 2026: its little words sat above the big ones), to Hinge.
    /// </summary>
    public static IReadOnlyList<string> Compositions { get; } =
        ["automatic", "scattered", "constellation", "echo", "spoken", "cascade", "cutout", "hinge",
         "portal", "mosaic", "torque", "fable"];

    /// <summary>A saved layout id as it is now: Editorial is Scattered, Constellation + Editorial is Constellation, Broadside is Hinge.</summary>
    public static string MigratedLayout(string? id) => id switch
    {
        "editorial" => "scattered",
        "constellationEditorial" => "constellation",
        "broadside" => "hinge",
        _ => id ?? "",
    };

    /// <summary>Layouts no longer offered, for dropping their saved rotation counts.</summary>
    public static bool IsRetiredLayout(string? id) => id is "editorial" or "constellationEditorial" or "broadside";

    public static IReadOnlyList<string> WordArrivals { get; } =
        ["automatic", "fade", "lift", "pop", "orbit", "glide", "unfold", "cutoutFold", "cutoutSlide", "cutoutHighlight",
         "satinRibbon", "splitInk", "mix"];

    public static IReadOnlyList<int> AnimationRates { get; } = [0, 15, 30, 60];

    /// <summary>The settings with every value in range and every choice a known one (defaults otherwise).</summary>
    public AppSettings Normalized()
    {
        var defaults = new AppSettings();
        return (this with
        {
            LyricComposition = Compositions.Contains(MigratedLayout(LyricComposition)) ? MigratedLayout(LyricComposition) : defaults.LyricComposition,
            WordArrival = WordArrivals.Contains(WordArrival) ? WordArrival : defaults.WordArrival,
            LyricScale = double.IsFinite(LyricScale) ? Math.Clamp(LyricScale, 0.5, 2) : defaults.LyricScale,
            FontChoice = FontChoice is "system" or "installed" ? FontChoice : defaults.FontChoice,
            FontFamily = FontFamily?.Trim() ?? "",
            RomanizationDisplay = RomanizationOptions.ParseDisplay(RomanizationDisplay) is null ? defaults.RomanizationDisplay : RomanizationDisplay,
            RomanizationLanguage = RomanizationOptions.ParseLanguage(RomanizationLanguage) is null ? defaults.RomanizationLanguage : RomanizationLanguage,
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
            RotateLayoutCounts = (RotateLayoutCounts ?? new Dictionary<string, int>()).Where(pair => !IsRetiredLayout(pair.Key))
                .ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, 0, Core.Compositions.LayoutRotation.MaximumCount)),
            RotateLayoutsCursor = Math.Max(0, RotateLayoutsCursor),
            Player = Core.NowPlaying.PlayerChoice.Normalize(Player),
            OpeningTitleStrength = Core.Compositions.OpeningTitle.KeptStrength(OpeningTitleStrength),
        }).WithTitlePlacement(TitlePlacement()).WithTitleRow(TitleRow()).NormalizedWindowSettings().NormalizedLyricTools().NormalizedWallpaper().NormalizedFloating()
            .NormalizedDiscord().NormalizedLyricExtras();
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
    public LyricStyle Style() => WithLyricExtrasStyle(WithLyricToolsStyle(WithWindowStyle(new()
    {
        FontFamily = FontChoice == "installed" && !string.IsNullOrWhiteSpace(FontFamily) ? FontFamily.Trim() : LyricStyle.Default.FontFamily,
        SizeFraction = (float)(LyricStyle.Default.SizeFraction * (double.IsFinite(LyricScale) ? Math.Clamp(LyricScale, 0.5, 2) : 1)),
        Reveal = SceneReveal(WordArrival),
        Credits = CreditsLayer,
        OpeningTitle = OpeningTitle,
        OpeningTitleCover = OpeningTitleCover,
        OpeningTitleKeep = OpeningTitleAlways,
        OpeningTitleStrength = Core.Compositions.OpeningTitle.KeptStrength(OpeningTitleStrength),
        OpeningTitlePlacement = TitlePlacement(),
        OpeningTitleRow = TitleRow(),
        Interludes = ShowInterludes,
        OrbStyle = ThinkingOrbStyle ?? "listening",
        HighlightMode = LyricHighlightMode,
        HighlightCustom = LyricHighlightCustom,
        Readings = (RomanizationOptions.ParseDisplay(RomanizationDisplay) ?? Core.Lyrics.Readings.RomanizationDisplay.Below) switch
        {
            Core.Lyrics.Readings.RomanizationDisplay.Off => ReadingPlacement.Off,
            Core.Lyrics.Readings.RomanizationDisplay.Replace => ReadingPlacement.Instead,
            _ => ReadingPlacement.Below,
        },
    })));

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
// The lyric style profiles (AppSettings.Floating.cs) move values through these.
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonNode))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
[JsonSerializable(typeof(double))]
public sealed partial class AppSettingsJsonContext : JsonSerializerContext;
