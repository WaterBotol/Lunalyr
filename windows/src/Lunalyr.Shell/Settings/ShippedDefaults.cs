// What a new install starts with, ported from the Mac's ShippedDefaults.swift (2fe4dd7b, 2ed3445d; Lunalyr's own
// code): the owner's own look from November 2026 (the Star Trails visualiser, Custom Mix layouts, sung-sweep colour,
// a large floating text with no shade, and so on), and the owner's Floating style.
//
// A new install, with no settings file yet, starts from these (AppSettingsStore's CreateDefaults). An existing install
// keeps its look: the settings file stores every setting on each save, and a key a file lacks reads the record's own
// (older) default, never these (the store's MissingKeyDefaults, Windows' counterpart of the Mac's DefaultsMigration and
// its ownerDefaults2026 flag). A kept style missing a key also reads the old default (LyricStyleProfiles.Applied lays it
// over the record's own).
//
// Left out, as on the Mac: fonts (Windows' system face, Segoe UI Variable, stays the default and plays SF Pro's part),
// the music video, Discord, the lyric sources and anything per song. Windows has no Wavefield projection, Light 3D
// colours, Heavy scenes on the main display or machine translation, and already draws at 60 fps.

using System.Text.Json;

namespace Lunalyr.Shell.Settings;

public static class ShippedDefaults
{
    // Wallpaper
    public const string BackgroundMode = "visualizer";
    public const string VisualizerStyle = "starTrails";
    /// <summary>Honeycomb (the Mac's Hex Grid), within Custom Mix's patterns.</summary>
    public const string PatternStyle = "hexGrid";
    /// <summary>Streaks (the Mac's Light Trails).</summary>
    public const string ShaderStyle = "lightTrails";
    public const double ScrimStrength = 0.77;
    public const string MotionPlacement = "fit";
    public const bool UseMotionArtwork = false;
    public const string WavefieldColours = "original";
    public const double VisualizerPunch = 2;
    /// <summary>Windows' loopback capture reads only what is already playing; it starts once the welcome is done.</summary>
    public const bool AudioReactiveEnabled = true;
    public const double AudioSensitivity = 3;

    // Behaviour
    public const string NoLyrics = "trackName";
    public const string OnBattery = "colourOnly";

    // Readability
    public const bool TextBackingAutomatic = false;
    public const double TextBacking = 0;
    public const double FloatingBackdrop = 0;
    public const double FloatingTextSize = 93;

    // The song's opening title
    public const bool OpeningTitleAlways = true;
    public const double OpeningTitleStrength = 1;
    public const double OpeningTitleLeft = 0.03317977241847826;
    public const double OpeningTitleLift = 0.045021890096618396;
    public const double OpeningTitleScale = 0.8931764417736521;

    // Lyric cards
    public const string LyricCardAspect = "landscape";
    public static IReadOnlyList<string> LyricCardFormats { get; } = ["gif"];
    public const double LyricCardTextScale = 1.1;
    public const string LyricCardComposition = "fable";
    public const int LyricCardLines = 1;

    /// <summary>Songs in a row per layout in the rotation; a layout missing gets 1.</summary>
    public static IReadOnlyDictionary<string, int> RotateLayoutCounts { get; } = new Dictionary<string, int>
    {
        ["cutout"] = 1, ["spoken"] = 2, ["torque"] = 2, ["constellation"] = 1, ["portal"] = 0,
        ["hinge"] = 1, ["scattered"] = 1, ["cascade"] = 1, ["fable"] = 2, ["echo"] = 0,
    };

    // ---- The lyric style (the Desktop style's values) ----

    /// <summary>Custom Mix: a layout chosen per song.</summary>
    public const string LyricComposition = "automatic";
    public const double LyricScale = 1.5;
    public const double ConstellationSpread = 0.7000000000000001;
    public const double ConstellationLinkOpacity = 0;
    public const double EchoStrength = 1.8;
    public const string ThinkingOrbStyle = "composing";
    public const string BackingVocalStyle = "scattered";
    public const string LyricColourMode = "sungSweep";
    public const double LyricColourSpeed = 0.8259117625584113;
    public const double LyricColourStrength = 2;
    public const bool LyricColourPastel = false;
    public const string LyricHighlightMode = "album";
    public static IReadOnlyList<string> WordEntranceMix { get; } = ["cutoutHighlight", "orbit", "lift", "fade"];

    public static IReadOnlyDictionary<string, string> WordArrivalByStyle { get; } = new Dictionary<string, string>
    {
        ["cutout"] = "cutoutSlide", ["mosaic"] = "cutoutHighlight", ["automatic"] = "automatic",
        ["constellation"] = "lift", ["torque"] = "mix", ["broadside"] = "pop", ["scattered"] = "mix",
        ["cascade"] = "glide", ["editorial"] = "unfold", ["constellationEditorial"] = "mix", ["spoken"] = "fade",
    };

    public static IReadOnlyDictionary<string, bool> WordRevealByStyle { get; } = new Dictionary<string, bool>
    {
        ["editorial"] = true, ["scattered"] = true, ["echo"] = true, ["constellationEditorial"] = true, ["constellation"] = true,
    };

    public static IReadOnlyDictionary<string, bool> NextWordPreviewByStyle { get; } = new Dictionary<string, bool>
    {
        ["cutout"] = true, ["scattered"] = true, ["echo"] = true, ["constellationEditorial"] = true, ["constellation"] = true,
    };

    /// <summary>
    /// The Floating style a new install starts with (the Mac's <c>ShippedDefaults.floatingStyle</c>): Hinge at its own
    /// size, pastel sung-sweep colour, Editorial type and the Listening orb. The keys it does not name take the Desktop
    /// style's new values.
    /// </summary>
    public static AppSettings FloatingLook(AppSettings desktop) => desktop with
    {
        ShowTranslations = true,
        LyricScale = 1.0,
        NextWordPreviewLead = 0.45,
        LyricColourSpeed = 2.0,
        BackingVocalStyle = "scattered",
        LyricColourMode = "sungSweep",
        EditorialAmount = 1.0,
        RomanizationDisplay = "below",
        ThinkingOrbStyle = "listening",
        ConstellationLinkOpacity = 0.6,
        EchoStrength = 1.8,
        WordArrivalByStyle = new Dictionary<string, string>
        {
            ["cutout"] = "cutoutSlide", ["editorial"] = "unfold", ["scattered"] = "mix", ["spoken"] = "mix",
            ["constellationEditorial"] = "mix", ["constellation"] = "mix",
        },
        LyricComposition = "hinge",
        WordRevealByStyle = WordRevealByStyle,
        LyricHighlightMode = "album",
        LyricColourPastel = true,
        EditorialTypography = true,
        ShowInterludes = true,
        ConstellationSpread = 0.7000000000000001,
        WordEntranceMix = WordEntranceMix,
        LyricColourStrength = 2.0,
        RandomizeLyricFonts = false,
        NextWordPreviewByStyle = NextWordPreviewByStyle,
    };

    /// <summary>A new install's settings: the record's own defaults with the owner's in their place, and the owner's Floating style kept.</summary>
    public static AppSettings NewInstall()
    {
        var settings = new AppSettings
        {
            BackgroundMode = BackgroundMode,
            VisualizerStyle = VisualizerStyle,
            PatternStyle = PatternStyle,
            ShaderStyle = ShaderStyle,
            ScrimStrength = ScrimStrength,
            MotionPlacement = MotionPlacement,
            UseMotionArtwork = UseMotionArtwork,
            WavefieldColours = WavefieldColours,
            VisualizerPunch = VisualizerPunch,
            AudioReactiveEnabled = AudioReactiveEnabled,
            AudioSensitivity = AudioSensitivity,
            NoLyrics = NoLyrics,
            OnBattery = OnBattery,
            TextBackingAutomatic = TextBackingAutomatic,
            TextBacking = TextBacking,
            FloatingBackdrop = FloatingBackdrop,
            FloatingTextSize = FloatingTextSize,
            OpeningTitleAlways = OpeningTitleAlways,
            OpeningTitleStrength = OpeningTitleStrength,
            OpeningTitleLeft = OpeningTitleLeft,
            OpeningTitleLift = OpeningTitleLift,
            OpeningTitleScale = OpeningTitleScale,
            LyricCardAspect = LyricCardAspect,
            LyricCardFormats = LyricCardFormats,
            LyricCardTextScale = LyricCardTextScale,
            LyricCardComposition = LyricCardComposition,
            LyricCardLinesBefore = LyricCardLines,
            LyricCardLinesAfter = LyricCardLines,
            RotateLayoutCounts = RotateLayoutCounts,
            LyricComposition = LyricComposition,
            LyricScale = LyricScale,
            ConstellationSpread = ConstellationSpread,
            ConstellationLinkOpacity = ConstellationLinkOpacity,
            EchoStrength = EchoStrength,
            ThinkingOrbStyle = ThinkingOrbStyle,
            BackingVocalStyle = BackingVocalStyle,
            LyricColourMode = LyricColourMode,
            LyricColourSpeed = LyricColourSpeed,
            LyricColourStrength = LyricColourStrength,
            LyricColourPastel = LyricColourPastel,
            LyricHighlightMode = LyricHighlightMode,
            WordEntranceMix = WordEntranceMix,
            WordArrivalByStyle = WordArrivalByStyle,
            WordRevealByStyle = WordRevealByStyle,
            NextWordPreviewByStyle = NextWordPreviewByStyle,
        };
        return settings with { LyricStyleFloating = LyricStyleProfiles.Values(FloatingLook(settings), LyricStyleProfiles.Keys) };
    }

    /// <summary>The settings a fresh store starts with (no file yet): <see cref="NewInstall"/>, normalised.</summary>
    public static AppSettings ForNewInstall() => NewInstall().Normalized();
}
