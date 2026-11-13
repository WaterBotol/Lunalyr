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
            "Your custom mix includes entrances that unveil each word as it’s sung, so this stays on.",
        _ => null,
    };

    /// <summary>How "Add Editorial typography" relates to the chosen layout (null for Match the music: the Mac's <c>editorialNote</c>).</summary>
    public static string? EditorialNote(CompositionKind? layout)
    {
        if (layout is not { } kind)
            return "Editorial picks already mix two typefaces; this adds the mix to the other layouts Match the music chooses.";
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
        "Bracketed backing words like (oh oh) are animated word by word like the lyrics, but small and soft behind the lead, easing in and lingering. Spoken, Cascade and Cutout can’t be used here; they follow Editorial.";

    public const string BackingLeadsLayout = "The lead’s layout";

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

    public const string ColourBacking =
        "Automatic text backing helps the pastels stay visible on bright wallpapers. Colours fade between shades and hold almost still with Reduce Motion on.";

    // ---- Remembered styles ----

    public const string MemoryNothingPlaying = "Play a song to remember a layout and colour for it or its artist.";

    public const string MemorySaves = "Saves the layout and text colour chosen above. A song’s own style wins over its artist’s.";

    public const string MemoryNoArtist = "This song names no artist, so its style can only be remembered for the song.";

    public const string MemoryInUse = "Changes above apply to other music until you update or forget this.";

    /// <summary>Under the Layout or Colour picker while a remembered style overrides it.</summary>
    public static string RememberedNote(StyleScope scope, string name) =>
        $"This {StyleMemory.ScopeName(scope)} uses {name}, remembered for it. The choice above applies to other music.";
}
