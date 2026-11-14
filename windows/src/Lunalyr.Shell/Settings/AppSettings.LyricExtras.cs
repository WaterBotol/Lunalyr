// The last of the Mac's Lyric style settings: backing vocals (BackingVocals.swift), Editorial typography and
// randomised fonts (Settings.swift's editorialTypography, editorialAmount, randomizeLyricFonts), the text colour
// (LyricExtrasOptions.swift's LyricColourOptions), "Reveal sung words" and the next-word preview (Settings.swift's
// revealsWords, previewsNextWord and wordRevealLock, Lunalyr's own lines; the two dictionaries they read are declared
// on lines that trace back to the squashed Verso import, and only their keys are kept), and remembered styles
// (StyleMemory, in StyleMemory.cs). Keys are the Mac's without "custom." and with dots folded into camelCase.

using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Render.Scene;
using Arrival = Lunalyr.Core.Compositions.WordArrival;
using Arrivals = Lunalyr.Core.Compositions.WordArrivals;

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    // ---- Backing vocals ----

    /// <summary>
    /// "Backing vocals in the background" (Mac <c>custom.backingVocalsLayer</c>): bracketed backing words of word-timed
    /// lines are lifted out of the lead and animated small and soft behind it. Off, they stay in the line.
    /// </summary>
    public bool BackingVocalsLayer { get; init; } = true;

    /// <summary>
    /// "Backing vocals animate like" (Mac <c>custom.backingVocalStyle</c>): a layout of <see cref="BackingVocalChoices"/>,
    /// or "" for the lead's own layout (the rolling three then take Editorial).
    /// </summary>
    public string BackingVocalStyle { get; init; } = "";

    /// <summary>The layouts backing words can animate in (the Mac's <c>BackingVocals.choices</c>).</summary>
    public static IReadOnlyList<string> BackingVocalChoices { get; } = ["scattered", "constellation", "constellationEditorial", "echo", "editorial"];

    // ---- Typography ----

    /// <summary>"Add Editorial typography" (Mac <c>custom.editorialTypography</c>): a second, contrasting face in every layout.</summary>
    public bool EditorialTypography { get; init; }

    /// <summary>"Editorial amount", 0–1 (Mac <c>custom.editorialAmount</c>): how many words take the second face.</summary>
    public double EditorialAmount { get; init; } = 1;

    /// <summary>"Randomise lyric fonts" (Mac <c>custom.randomizeLyricFonts</c>): synced words take a mix of typefaces.</summary>
    public bool RandomizeLyricFonts { get; init; }

    // ---- Colour ----

    /// <summary>
    /// The text colour (Mac <c>custom.lyricColourMode</c>): "albumDrift", "sungSweep", "musicReactive" or
    /// "customGradient"; the old "static" reads as Album drift.
    /// </summary>
    public string LyricColourMode { get; init; } = LyricColourModes.DefaultMode.RawValue();

    /// <summary>"Colour speed", 0.25–2 (Mac <c>custom.lyricColourSpeed</c>): how fast the drifting modes move.</summary>
    public double LyricColourSpeed { get; init; } = 1;

    /// <summary>"Colour strength", 0–2 (Mac <c>custom.lyricColourStrength</c>): neutral to vivid.</summary>
    public double LyricColourStrength { get; init; } = 1;

    /// <summary>The custom gradient's two to four colours, <c>#RRGGBB</c> (Mac <c>custom.lyricColourCustom</c>).</summary>
    public IReadOnlyList<string> LyricColourCustom { get; init; } = [.. LyricColourOptions.DefaultCustom];

    // ---- Word entrances ----

    /// <summary>
    /// "Reveal sung words" per layout (Mac <c>custom.wordRevealByStyle</c>): composition id → on, and "automatic" for
    /// every layout Custom Mix picks. A layout without an entry reveals its words.
    /// </summary>
    public IReadOnlyDictionary<string, bool> WordRevealByStyle { get; init; } = new Dictionary<string, bool>();

    /// <summary>"Faint next-word preview" per layout (Mac <c>custom.nextWordPreviewByStyle</c>), keyed as <see cref="WordRevealByStyle"/>.</summary>
    public IReadOnlyDictionary<string, bool> NextWordPreviewByStyle { get; init; } = new Dictionary<string, bool>();

    /// <summary>"Preview lead": seconds before a word its preview appears, 0.15–1.25 (Mac <c>custom.nextWordPreviewLead</c>).</summary>
    public double NextWordPreviewLead { get; init; } = DefaultPreviewLead;

    public const double DefaultPreviewLead = 0.45, PreviewLeadMin = 0.15, PreviewLeadMax = 1.25;

    /// <summary>The colour mode as a choice.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public LyricColourMode ColourMode => LyricColourModes.SavedChoice(LyricColourMode) ?? LyricColourModes.DefaultMode;

    /// <summary>The layout the word controls stand for when reading back: the chosen one, or Scattered for Custom Mix.</summary>
    private CompositionKind ControlsLayout => SettingsCopy.Kind(LyricComposition) ?? CompositionKind.Scattered;

    /// <summary>The entrance <paramref name="layout"/> draws its words with (the Mac's <c>arrival(in:)</c>).</summary>
    public Arrival ArrivalIn(CompositionKind layout)
    {
        var byStyle = WordArrivalByStyle ?? new Dictionary<string, string>();
        if (LyricComposition == AllLayoutsKey && byStyle.TryGetValue(AllLayoutsKey, out var all)) return Arrivals.Parse(all);
        return Arrivals.Parse(byStyle.TryGetValue(layout.Id(), out var own) ? own : WordArrival);
    }

    /// <summary>
    /// Why sung words must be revealed one by one in <paramref name="layout"/>, if they must (the Mac's
    /// <c>wordRevealLock(in:)</c>): with a layout chosen, only an entrance it shows on sung words locks it; Match the
    /// music's choice is one for every layout it picks, so it locks wherever it unveils words.
    /// </summary>
    public WordRevealLock? RevealLock(CompositionKind layout)
    {
        if (layout.ArrivalOnly()) return WordRevealLock.ForLayout(layout);
        var selected = ArrivalIn(layout);
        var chosenLayout = LyricComposition != AllLayoutsKey;
        bool Unveils(Arrival arrival) =>
            arrival.RequiresReveal() && (!chosenLayout || EntranceSupport.AppliesToSungWords(arrival, layout));
        if (Unveils(selected)) return WordRevealLock.ForEntrance(selected);
        if (selected == Arrival.Mix
            && (WordEntranceMix ?? []).Any(id => KnownArrival(id) is { } arrival && Unveils(arrival)))
            return WordRevealLock.Mix;
        return null;
    }

    public bool RequiresWordEntrances(CompositionKind layout) => RevealLock(layout) is not null;

    /// <summary>Whether <paramref name="layout"/> reveals sung words one by one (the Mac's <c>revealsWords(in:)</c>).</summary>
    public bool RevealsWords(CompositionKind layout)
    {
        if (RequiresWordEntrances(layout)) return true;
        var map = WordRevealByStyle ?? new Dictionary<string, bool>();
        if (LyricComposition == AllLayoutsKey && map.TryGetValue(AllLayoutsKey, out var all)) return all;
        return !map.TryGetValue(layout.Id(), out var own) || own;
    }

    /// <summary>Whether <paramref name="layout"/> shows the faint next-word preview (the Mac's <c>previewsNextWord(in:)</c>).</summary>
    public bool PreviewsNextWord(CompositionKind layout)
    {
        var map = NextWordPreviewByStyle ?? new Dictionary<string, bool>();
        if (LyricComposition == AllLayoutsKey && map.TryGetValue(AllLayoutsKey, out var all)) return all;
        return !map.TryGetValue(layout.Id(), out var own) || own;
    }

    /// <summary>What the page's "Reveal sung words" shows for the controls' layout.</summary>
    public bool RevealsWordsForControls() => RevealsWords(ControlsLayout);

    /// <summary>What the page's "Faint next-word preview" shows for the controls' layout.</summary>
    public bool PreviewsNextWordForControls() => PreviewsNextWord(ControlsLayout);

    /// <summary>Why the page's "Reveal sung words" can't be switched off, if it can't.</summary>
    public WordRevealLock? RevealLockForControls() => RevealLock(ControlsLayout);

    public AppSettings WithRevealsWordsForControls(bool on) => this with
    {
        WordRevealByStyle = new Dictionary<string, bool>(WordRevealByStyle ?? new Dictionary<string, bool>(), StringComparer.Ordinal) { [EntranceKey] = on },
    };

    public AppSettings WithPreviewsNextWordForControls(bool on) => this with
    {
        NextWordPreviewByStyle = new Dictionary<string, bool>(NextWordPreviewByStyle ?? new Dictionary<string, bool>(), StringComparer.Ordinal) { [EntranceKey] = on },
    };

    /// <summary>The custom colours with one changed, added or removed (two to four kept, as the Mac's options keep them).</summary>
    public AppSettings WithCustomColour(int index, string hex)
    {
        var colours = LyricColourOptions.Sanitised(LyricColourCustom ?? []);
        if (index < 0 || index >= colours.Count || ColourHex.ToOkLab(hex) is null) return this;
        colours[index] = hex.Trim().ToUpperInvariant();
        return this with { LyricColourCustom = colours };
    }

    public AppSettings WithCustomColourAdded()
    {
        var colours = LyricColourOptions.Sanitised(LyricColourCustom ?? []);
        if (colours.Count >= 4) return this;
        colours.Add(LyricColourOptions.DefaultCustom[colours.Count % LyricColourOptions.DefaultCustom.Count]);
        return this with { LyricColourCustom = colours };
    }

    public AppSettings WithCustomColourRemoved(int index)
    {
        var colours = LyricColourOptions.Sanitised(LyricColourCustom ?? []);
        if (colours.Count <= 2 || index < 0 || index >= colours.Count) return this;
        colours.RemoveAt(index);
        return this with { LyricColourCustom = colours };
    }

    private static Arrival? KnownArrival(string? id) =>
        id is not null && WordArrivals.Contains(id) ? Arrivals.Parse(id) : null;

    private AppSettings NormalizedLyricExtras()
    {
        var defaults = new AppSettings();
        var speed = LyricColourSpeed;
        var amount = EditorialAmount;
        var lead = NextWordPreviewLead;
        return this with
        {
            BackingVocalStyle = BackingVocalChoices.Contains(BackingVocalStyle ?? "") ? BackingVocalStyle! : "",
            EditorialAmount = double.IsFinite(amount) ? Math.Clamp(amount, 0, 1) : defaults.EditorialAmount,
            LyricColourMode = (LyricColourModes.SavedChoice(LyricColourMode) ?? LyricColourModes.DefaultMode).RawValue(),
            LyricColourSpeed = double.IsFinite(speed) ? Math.Clamp(speed, LyricColourOptions.SpeedMin, LyricColourOptions.SpeedMax) : 1,
            LyricColourStrength = LyricColourOptions.ClampedStrength(LyricColourStrength),
            LyricColourCustom = LyricColourOptions.Sanitised(LyricColourCustom ?? []),
            WordRevealByStyle = KnownLayouts(WordRevealByStyle),
            NextWordPreviewByStyle = KnownLayouts(NextWordPreviewByStyle),
            NextWordPreviewLead = double.IsFinite(lead) ? Math.Clamp(lead, PreviewLeadMin, PreviewLeadMax) : DefaultPreviewLead,
            StyleMemorySongs = Stored(StyleMemorySongs),
            StyleMemoryArtists = Stored(StyleMemoryArtists),
        };

        static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Stored(
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? map)
        {
            var kept = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            foreach (var (key, style) in map ?? new Dictionary<string, IReadOnlyDictionary<string, string>>())
                if (key is not null && style is not null) kept[key] = style;
            return kept;
        }

        static IReadOnlyDictionary<string, bool> KnownLayouts(IReadOnlyDictionary<string, bool>? map)
        {
            var known = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var (layout, on) in map ?? new Dictionary<string, bool>())
                if (layout is not null && Compositions.Contains(layout)) known[layout] = on;
            return known;
        }
    }

    /// <summary>These settings' typography, colour, reveal and backing choices in the renderer's style.</summary>
    private LyricStyle WithLyricExtrasStyle(LyricStyle style)
    {
        var noReveal = new List<string>();
        var noPreview = new List<string>();
        foreach (var id in Compositions)
        {
            if (SettingsCopy.Kind(id) is not { } kind) continue;
            if (!RevealsWords(kind)) noReveal.Add(id);
            if (!PreviewsNextWord(kind)) noPreview.Add(id);
        }
        var lead = double.IsFinite(NextWordPreviewLead) ? Math.Clamp(NextWordPreviewLead, PreviewLeadMin, PreviewLeadMax) : DefaultPreviewLead;
        return style with
        {
            BackingVocals = BackingVocalsLayer,
            BackingStyle = BackingVocalChoices.Contains(BackingVocalStyle ?? "") ? BackingVocalStyle : null,
            EditorialTypography = EditorialTypography,
            EditorialAmount = double.IsFinite(EditorialAmount) ? Math.Clamp(EditorialAmount, 0, 1) : 1,
            RandomFonts = RandomizeLyricFonts,
            ColourMode = ColourMode,
            ColourSpeed = double.IsFinite(LyricColourSpeed) ? Math.Clamp(LyricColourSpeed, LyricColourOptions.SpeedMin, LyricColourOptions.SpeedMax) : 1,
            ColourStrength = LyricColourOptions.ClampedStrength(LyricColourStrength),
            ColourCustom = new ValueList(LyricColourOptions.Sanitised(LyricColourCustom ?? [])),
            NoWordReveal = new ValueList(noReveal),
            NoNextWordPreview = new ValueList(noPreview),
            PreviewLead = lead,
        };
    }
}

/// <summary>Why "Reveal sung words" is locked on (the Mac's <c>Settings.WordRevealLock</c>).</summary>
public readonly record struct WordRevealLock(WordRevealLockKind Kind, CompositionKind? Layout, Arrival? Entrance)
{
    public static WordRevealLock ForLayout(CompositionKind layout) => new(WordRevealLockKind.Layout, layout, null);
    public static WordRevealLock ForEntrance(Arrival arrival) => new(WordRevealLockKind.Entrance, null, arrival);
    public static WordRevealLock Mix => new(WordRevealLockKind.Mix, null, null);
}

public enum WordRevealLockKind
{
    /// <summary>Spoken, Cascade and Cutout build every line from the sung words.</summary>
    Layout,
    /// <summary>The chosen entrance unveils each word as it is sung.</summary>
    Entrance,
    /// <summary>The custom mix includes such an entrance.</summary>
    Mix,
}
