// The Lyric style page's words for its last rows, from the Mac's WorkspaceCopy (LunalyrWorkspace.swift), the colour
// modes' details (LyricColour.swift) and the style memory section (LyricExtrasInspector.swift): Lunalyr's own lines,
// worded for Windows where the Mac names something Windows doesn't have.

using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;

namespace Lunalyr.Shell.Settings;

public static class LyricStyleCopy
{
    /// <summary>Why "Reveal sung words" can't be switched off, if it can't (the Mac's <c>revealLockReason</c>).</summary>
    public static string? RevealLockReason(WordRevealLock? revealLock) => revealLock switch
    {
        { Kind: WordRevealLockKind.Layout, Layout: { } layout } =>
            $"{layout.Title()} builds each line from the words as they’re sung, so they’re always revealed.",
        { Kind: WordRevealLockKind.Entrance, Entrance: { } arrival } =>
            $"{arrival.Title()} unveils each word as it’s sung, so this stays on.",
        { Kind: WordRevealLockKind.Mix } =>
            "Your mixed entrances include ones that unveil each word as it’s sung, so this stays on.",
        _ => null,
    };

    /// <summary>How "Add Editorial typography" relates to the chosen layout (null for Custom Mix: the Mac's <c>editorialNote</c>).</summary>
    public static string? EditorialNote(CompositionKind? layout)
    {
        if (layout is not { } kind)
            return "Editorial picks already mix two typefaces; this adds the mix to the other layouts Custom Mix chooses.";
        return kind.HasEditorialTypography()
            ? $"{kind.Title()} already mixes two typefaces. Switch this on to set how many words take the second one."
            : null;
    }

    public const string EditorialTypography = "Mixes in a second, contrasting typeface.";

    public const string Randomise = "Words use a mix of typefaces that come with Windows.";

    /// <summary>Under the typeface while Randomise is on (the Mac's <c>randomisedTypeface</c>).</summary>
    public const string RandomisedTypeface =
        "While Randomise is on, synced lyrics use the mix; this typeface still sets titles, unsynced lyrics and Echo’s repeats.";

    public const string BackingVocals =
        "Bracketed backing words like (oh oh) are animated word by word like the lyrics, but small and soft behind the lead, easing in and lingering. Layouts that can’t carry them (Spoken, Cascade, Cutout and the stage layouts) set them as Scattered.";

    /// <summary>Under "Keep the song title on screen" (the Mac's help text, d849c49).</summary>
    public const string KeepTitle =
        "The title stays for the whole song, quieter once the singing starts, so you always know what’s playing. Low on the left beside the lyrics; in the floating lyrics, on its card beside them.";

    /// <summary>Under "Show the song title card" (the Mac's b11a1ab3).</summary>
    public const string TitleCard =
        "With the floating lyrics, the song title floats on a small card of its own just above them. Move or resize it in Edit Position.";

    /// <summary>Under "Title strength" (the Mac's help text, e30fbb2).</summary>
    public const string TitleStrength = "How strong the song title stays once the singing has started. 100% keeps it fully solid.";

    /// <summary>Under "Reset Title Position" on the desktop and in the floating lyrics (the Mac's help texts, 3276613 and dece022).</summary>
    public const string ResetTitleDesktop =
        "Drag the song title on the picture to move it, or its corner to resize it. This puts it back low on the left at its usual size.";

    public const string ResetTitleFloating =
        "Move or resize the song title card in Edit Position. This puts it back just above the floating lyrics, centred, at its usual size.";

    public const string BackingLeadsLayout = "The lead’s layout";

    /// <summary>"Backing vocals animate like": the lead's layout, then the layouts backing words can take.</summary>
    public static IReadOnlyList<SettingsChoice> BackingChoices { get; } =
    [
        new("", BackingLeadsLayout),
        .. AppSettings.BackingVocalChoices.Select(id => new SettingsChoice(id, SettingsCopy.Kind(id)?.Title() ?? id)),
    ];

    /// <summary>The text colours, with their details.</summary>
    public static IReadOnlyList<SettingsChoice> ColourChoices { get; } =
        [.. LyricColourModes.Choices.Select(mode => new SettingsChoice(mode.RawValue(), mode.Title(), ColourDetail(mode)))];

    /// <summary>"Preview lead" as the page writes it: "0.45 s".</summary>
    public static string Seconds(double seconds) =>
        seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s";

    /// <summary>"Preview lead" as Narrator says it.</summary>
    public static string SpokenPreviewLead(double seconds) =>
        seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " seconds before the next word";

    public const string RevealSungWords = "Words appear one by one as they are sung.";

    public const string NextWordPreview = "A faint preview of each word just before it is sung.";

    /// <summary>A colour mode's detail (the Mac's <c>LyricColourMode.detail</c>), with what Windows draws differently.</summary>
    public static string ColourDetail(LyricColourMode mode) => mode switch
    {
        LyricColourMode.SungSweep => "White lyrics fill with a contrasting colour from the cover as each word or line is sung. Unsynced lyrics use Album drift.",
        LyricColourMode.MusicReactive => "Each line takes the cover’s colours brighter and deeper as the singing grows. Follows the vocal timing; live audio isn’t used.",
        LyricColourMode.CustomGradient => "Your own two to four colours, drifting through the line.",
        LyricColourMode.Standard => "White type, as before.",
        _ => "Colours from the cover drift slowly through the words.",
    };

    /// <summary>The Mac's help for "Make the colours pastel" (cab0212).</summary>
    public const string Pastel =
        "Eases your colours to soft pastels. Off keeps them as you picked them, lightened only as far as they need to stay readable.";

    public const string ColourBacking =
        "Automatic text backing helps the pastels stay visible on bright wallpapers. Colours fade between shades and hold almost still while Windows’ animation effects are off.";

    // ---- Remembered styles ----

    public const string MemoryNothingPlaying = "Play a song to remember a layout and colour for it or its artist.";

    public const string MemorySaves = "Saves the layout, text colour and typeface chosen above. A song’s own style wins over its artist’s.";

    public const string MemoryNoArtist = "This song names no artist, so its style can only be remembered for the song.";

    public const string MemoryInUse = "Changes above apply to other music until you update or forget this.";

    /// <summary>
    /// Under the Layout or Colour picker while a remembered style overrides it (the Mac's one line, split into the row's
    /// title and its description).
    /// </summary>
    public static string RememberedTitle(StyleScope scope, string name) => $"This {StyleMemory.ScopeName(scope)} uses {name}";

    public const string RememberedDetail = "Remembered for it. The choice above applies to other music.";
}
