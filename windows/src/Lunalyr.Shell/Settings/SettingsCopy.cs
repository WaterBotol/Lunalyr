// What the settings window says: its choices, titles, captions and the notes on rows Windows can't do yet. Wording is
// ported from the Mac's settings pages (Sources/Settings/Pages/*.swift, LunalyrWorkspace.swift's WorkspaceCopy,
// QuickControls.swift's TimingNudge, LyricHighlightColour.swift; Lunalyr's own lines, checked with git blame) and
// adapted where Windows differs. The readings menus are worded afresh: their Mac titles sit on lines that trace back to
// the squashed Verso import (LyricRomanization.swift). Kept out of the window so tests can hold it to what the app does.

using System.Globalization;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Shell.Settings;

/// <summary>One choice in a menu or segmented control: the stored value, what it is called and an optional caption.</summary>
public sealed record SettingsChoice(string Value, string Title, string? Detail = null);

/// <summary>One background type: what it is called, what it does, and (when Windows can't show it yet) why not.</summary>
public sealed record WallpaperType(string Value, string Title, string ShortTitle, string Glyph, string Summary, string? NotYet = null)
{
    public bool Available => NotYet is null;
}

public static class SettingsCopy
{
    // ---- Milestones (from the port's plan) ----

    public const string Milestone6 = "Arrives in milestone 6.";
    public const string Milestone7 = "Arrives in milestone 7.";
    public const string Milestone8 = "Arrives in milestone 8.";
    public const string LaterBuild = "Arrives in a later build.";

    // ---- Lyric style ----

    /// <summary>What Custom Mix does with the layout (the Mac's <c>WorkspaceCopy.automaticLayoutDetail</c>).</summary>
    public const string AutomaticLayoutDetail =
        "Picks a layout to suit each track’s energy and feel.";

    public const string ReduceMotionEntrances = "Animation effects are off in Windows, so every entrance plays as a simple fade.";

    /// <summary>
    /// The layout in use, by name (the Mac's <c>ShowingLayout.title</c>, 68bac63): a chosen layout's own name; under
    /// Custom Mix, "Custom Mix · Hinge" once the song playing has taken a layout (<paramref name="showing"/>), else
    /// "Custom Mix".
    /// </summary>
    public static string ShowingLayoutTitle(string? chosen, string? showing)
    {
        if (chosen is not null && chosen != AppSettings.AllLayoutsKey)
            return Layouts.FirstOrDefault(l => l.Value == chosen)?.Title ?? "Custom Mix";
        if (showing is null || showing == AppSettings.AllLayoutsKey || Layouts.FirstOrDefault(l => l.Value == showing) is not { } shown)
            return "Custom Mix";
        return $"Custom Mix · {shown.Title}";
    }

    /// <summary>Every layout, "Custom Mix" first, in the settings' order.</summary>
    public static IReadOnlyList<SettingsChoice> Layouts { get; } =
    [
        new(AppSettings.AllLayoutsKey, "Custom Mix", AutomaticLayoutDetail),
        .. AppSettings.Compositions.Where(id => id != AppSettings.AllLayoutsKey)
            .Select(id => Kind(id) is { } kind ? new SettingsChoice(id, kind.Title(), kind.Detail()) : new SettingsChoice(id, id)),
    ];

    /// <summary>The composition a layout id names, or null ("automatic" and unknown ids).</summary>
    public static CompositionKind? Kind(string? id)
    {
        foreach (var kind in Enum.GetValues<CompositionKind>())
            if (kind.Id() == id) return kind;
        return null;
    }

    /// <summary>The entrances as the menu lists them for <paramref name="layout"/> (an entrance the layout leaves out is marked).</summary>
    public static IReadOnlyList<SettingsChoice> Entrances(string layout)
    {
        var kind = Kind(layout);
        return [.. Enum.GetValues<WordArrival>().Select(a => new SettingsChoice(a.Id(), EntranceSupport.MenuTitle(a, kind)))];
    }

    /// <summary>The entrances a custom mix can hold, with their titles.</summary>
    public static IReadOnlyList<SettingsChoice> MixChoices { get; } =
        [.. WordArrivals.MixChoices.Select(a => new SettingsChoice(a.Id(), a.Title()))];

    /// <summary>Songs in a row for a layout in the rotation: "Never", "1 song", "3 songs in a row".</summary>
    public static string RotationCount(int count) => count switch
    {
        <= 0 => "Never",
        1 => "1 song",
        _ => $"{count} songs in a row",
    };

    public static IReadOnlyList<SettingsChoice> HighlightModes { get; } =
    [
        new("complement", "Complement lyrics", "Follows the text colour and picks a complementary accent clear of the cover’s main hue. White text takes an album accent."),
        new("album", "Album accent", "A vivid cover accent that stands apart from the album’s main colour."),
        new("custom", "Custom", "Your chosen colour, adjusted only where the text needs more contrast."),
    ];

    public static IReadOnlyList<SettingsChoice> FontChoices { get; } =
    [
        new("system", "Segoe UI Variable", "Windows’ own typeface."),
        new("installed", "An installed font", "Any font family installed on this PC."),
    ];

    // ---- Readings ----

    public static IReadOnlyList<SettingsChoice> ReadingDisplays { get; } =
        [new("off", "Off"), new("below", "Under the lyrics"), new("replace", "Instead of the lyrics")];

    public static IReadOnlyList<SettingsChoice> ReadingLanguages { get; } =
    [
        new("automatic", "Detect automatically"),
        new("japanese", "Japanese (rōmaji)"),
        new("mandarin", "Mandarin (pinyin)"),
        new("cantonese", "Cantonese (saved readings only)"),
        new("korean", "Korean (Revised Romanization)"),
        new("other", "Other scripts (Latin letters)"),
    ];

    // ---- Timing ----

    /// <summary>"+0.25 s", "−0.30 s", "0.00 s" (the Mac's <c>TimingNudge.format</c>).</summary>
    public static string FormatOffset(double seconds) =>
        seconds == 0 ? "0.00 s" : seconds.ToString("+0.00;-0.00", CultureInfo.InvariantCulture).Replace('-', '−') + " s";

    /// <summary>"Lyrics 0.25 seconds earlier", "No offset" (the Mac's <c>TimingNudge.spoken</c>).</summary>
    public static string SpokenOffset(double seconds) =>
        seconds == 0 ? "No offset"
        : $"Lyrics {Math.Abs(seconds).ToString("0.00", CultureInfo.InvariantCulture)} seconds {(seconds > 0 ? "earlier" : "later")}";

    /// <summary>
    /// The song offset slider's range (the Mac's <c>TimingPage.offsetRange</c>): ±10 s, widened to ±30 s once an offset
    /// is beyond 10 s, so the thumb shows it and a drag can't snap it back.
    /// </summary>
    public static (double Min, double Max) SongOffsetRange(double offset) =>
        Math.Abs(offset) > 10 ? (-30, 30) : (-10, 10);

    /// <summary>What "Treat long pauses as breaks" (Lyrics › Instrumental breaks) changes (the Mac's <c>TimingPage.pauseCaption</c>).</summary>
    public static string PauseCaption(bool showInterludes)
    {
        const string caption = "Lyrics timed only by line don’t say when singing stops. With the glowing orb on, a long gap after a line is shown as a break instead of one held line. Turn this off for songs with long held notes.";
        return showInterludes
            ? caption
            : caption + " The orb is off, so this changes only QQ Music’s lyrics, whose held lines end early, and how long the words of a line before a long gap take to light up.";
    }

    public static IReadOnlyList<SettingsChoice> MatchModes { get; } =
        [.. LyricMatchModeInfo.All.Select(m => new SettingsChoice(m.RawValue(), m.Title(), m.Detail()))];

    public const string ArtistAliasesCaption =
        "When NetEase, QQ, Kugou or Kuwo find nothing under a name like “Kenshi Yonezu”, they search again as “米津玄師”. The other names come from MusicBrainz, which receives the artist’s name. A song listed under an English title, like “Racing into the Night”, is searched again under its Japanese title, “夜に駆ける”, from Apple’s catalogue, which receives the title and artist.";

    /// <summary>A source as the priority list shows it.</summary>
    public sealed record SourceRow(string Value, string Title, string Timing, bool Enabled, bool IsFirst, bool IsLast);

    /// <summary>Every source in the configured order, switched on or off.</summary>
    public static IReadOnlyList<SourceRow> Sources(AppSettings settings)
    {
        var snapshot = settings.SourceSnapshot();
        var order = SourceOrder(settings);
        var enabled = new HashSet<LyricSourceId>(snapshot.Enabled);
        return [.. order.Select((id, i) => new SourceRow(id.RawValue(), id.Title(), id.TimingDescription(), enabled.Contains(id), i == 0, i == order.Count - 1))];
    }

    /// <summary>
    /// Every known source in the configured order (unknown names dropped, a new source after the one it is placed after,
    /// others at the end).
    /// </summary>
    public static IReadOnlyList<LyricSourceId> SourceOrder(AppSettings settings)
    {
        var order = LyricSourceSnapshot.OrderFor(settings.LyricSourceOrder);
        foreach (var id in LyricSourceInfo.All)
            if (!order.Contains(id)) order.Add(id);
        return order;
    }

    /// <summary>The settings with <paramref name="source"/> moved <paramref name="step"/> places (the Mac's <c>move(_:by:)</c>: a swap, nothing past either end).</summary>
    public static AppSettings MoveSource(AppSettings settings, string source, int step)
    {
        var order = SourceOrder(settings).Select(s => s.RawValue()).ToList();
        var index = order.IndexOf(source);
        var next = index + step;
        if (index < 0 || next < 0 || next >= order.Count) return settings;
        (order[index], order[next]) = (order[next], order[index]);
        return settings with { LyricSourceOrder = order };
    }

    /// <summary>The settings with <paramref name="source"/> switched on or off.</summary>
    public static AppSettings SetSourceEnabled(AppSettings settings, string source, bool enabled)
    {
        var set = new SortedSet<string>(settings.LyricSources ?? [], StringComparer.Ordinal);
        if (enabled) set.Add(source);
        else set.Remove(source);
        return settings with { LyricSources = [.. set] };
    }

    /// <summary>"Restore Recommended Order": the recommended order and Accuracy first (the Mac's <c>resetOrder</c>).</summary>
    public static AppSettings RestoreRecommendedOrder(AppSettings settings) => settings with
    {
        LyricSourceOrder = [.. LyricSourceSnapshot.RecommendedOrder.Select(s => s.RawValue())],
        LyricMatchMode = LyricMatchMode.AccuracyFirst.RawValue(),
    };

    // ---- Wallpaper ----

    /// <summary>What isn't in this build of milestone 6 yet.</summary>
    public const string Milestone6Later = "Arrives in a later build of milestone 6.";

    /// <summary>
    /// The Mac's wallpaper types in its grid's order (QuickControls.swift: the five Lunalyr draws, then the three that
    /// start from something of yours), with its titles, tile names and summaries; the glyphs are Segoe Fluent's.
    /// </summary>
    public static IReadOnlyList<WallpaperType> WallpaperTypes { get; } =
    [
        new("auto", "Custom Mix", "Mix", "",
            "Each song picks a look to suit its energy and feel."),
        new("visualizer", "Visualisers", "Visualiser", "", "Scenes that move with the music itself, with System audio on."),
        new("shader", "GPU effects", "Effects", "", "Plasma, aurora, nebulae and more, drawn on the GPU in the album’s colours."),
        new("pattern", "Line patterns", "Patterns", "", "Line patterns in the album’s colours, the gentlest on power after Colour only."),
        new("colourOnly", "Colour only", "Colour", "", "The album’s colours alone, the lightest on power."),
        new("desktop", "Your wallpaper", "Your wallpaper", "", "Keep your own wallpaper and show lyrics and instrumental cues over it."),
        new("custom", "Imported image or video", "Image or video", "", "A picture or video of your own behind the lyrics."),
        new("musicVideo", "Music video", "Music video", "",
            "The song’s music video behind the lyrics, muted and in time with the song."),
    ];

    /// <summary>The type the Wallpaper page shows as chosen for a stored <c>backgroundMode</c> (normalised first).</summary>
    public static string WallpaperTypeFor(string? backgroundMode) =>
        new AppSettings { BackgroundMode = backgroundMode ?? "" }.Normalized().BackgroundMode;

    /// <summary>The Mac's placements (<c>MotionPlacement</c>): titles and captions.</summary>
    public static IReadOnlyList<SettingsChoice> Placements { get; } =
    [
        new("fill", "Fill", "Crops equally top and bottom."),
        new("fillTop", "Fill, top", "Crops from the bottom, keeping the top."),
        new("fillBottom", "Fill, bottom", "Crops from the top."),
        new("fit", "Fit whole frame", "Nothing cropped; the sides are left black."),
    ];

    public const string MatchTheMusicPicks = "Custom Mix picks one of each per song; choose one to pin it.";

    public const string EffectsNotYet = "GPU effects arrive in a later build of milestone 6. Until then this type shows the album’s colours.";

    public const string PatternsNotYet = "Line patterns arrive in a later build of milestone 6. Until then this type shows the album’s colours.";

    public const string VisualisersNotYet = "Visualisers arrive in a later build of milestone 6. Until then this type shows the album’s colours.";

    public const string YourWallpaper =
        "Only lyrics are drawn over your desktop. Keep using your current Windows wallpaper or wallpaper app; Lunalyr leaves it in place.";

    public const string YourWallpaperPreview =
        "Your wallpaper shows through behind the lyrics on each monitor. The live preview can’t show it, so it draws the lyrics over black.";

    public const string AlbumGradientCaption = "Blends the cover’s colours into a moving gradient.";

    public const string AlbumGradientOff = "Off: a slowly drifting field of the cover’s colours instead.";

    /// <summary>The Legibility section's caption (the Mac's <c>LyricBackingSection.caption</c>).</summary>
    public static string BackingCaption(bool desktop, bool automatic) => desktop
        ? automatic
            ? "While lyrics are on screen, a soft shadow sits behind them, strong enough for white text over a light desktop. With nothing playing, your desktop is left as it is."
            : "While lyrics are on screen, a soft shadow behind them keeps them readable over light parts of your desktop."
        : automatic
            ? "Light covers and images get a soft shadow behind the lyrics, just strong enough for white text to stay readable."
            : "The soft shadow behind the lyrics, on top of Darkening.";

    public const string ResetAdjustmentsHelp =
        "Motion speed, Colour richness, Darkening, Pattern visibility and the covers’ Softening go back to how Lunalyr comes.";

    /// <summary>"1.25×".</summary>
    public static string Multiplier(double value) => value.ToString("0.00", CultureInfo.InvariantCulture) + "×";

    /// <summary>"None" or "35%".</summary>
    public static string PercentOrNone(double value) => value <= 0 ? "None" : Percent(value);

    /// <summary>The menu for GPU effects or line patterns: "Custom Mix", then the catalogue's.</summary>
    public static IReadOnlyList<SettingsChoice> StyleChoices(IEnumerable<(string Id, string Title, string? Detail)> styles) =>
        [new("", "Custom Mix"), .. styles.Select(s => new SettingsChoice(s.Id, s.Title, s.Detail))];

    // ---- Covers ----

    public const string UseAnimatedCovers = "Apple Music’s moving album artwork behind the lyrics, with Custom Mix. Played muted.";

    /// <summary>What covers are doing for the playing song, or why they aren't (the Mac's CoversPage.coverStatus).</summary>
    public static string CoverStatus(MotionCoverStatus status, string? offReason = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State switch
        {
            MotionCoverState.Off => "Animated covers are off.",
            MotionCoverState.NotMatchTheMusic => "Animated covers play in Custom Mix wallpaper mode.",
            MotionCoverState.PausedForPower => $"{status.Power ?? "Power saving is on"}, so animated covers are paused.",
            MotionCoverState.Unavailable => $"Animated covers aren’t looked up in this run ({offReason ?? "lookups are off"}).",
            MotionCoverState.NothingPlaying => "Play an album in Apple Music to check its cover.",
            MotionCoverState.NotAppleMusic => "Animated covers come from Apple Music, so they play only for songs playing in Apple Music.",
            MotionCoverState.Stream => "Radio streams have no animated cover.",
            MotionCoverState.Excluded => "This album is excluded.",
            MotionCoverState.LookingUp => "Checking Apple Music for an animated cover…",
            MotionCoverState.Unreachable => "Apple Music couldn’t be reached for this album’s cover. It’s tried again with the next album.",
            MotionCoverState.Available => "Animated cover available.",
            _ => "No animated cover found for this album.",
        };
    }

    public const string CoversWithMatchTitle = "Covers play with Custom Mix";

    /// <summary>The banner while another wallpaper type is chosen.</summary>
    public static string CoversWithMatch(string? backgroundMode) =>
        $"The wallpaper is {WallpaperTypeOf(backgroundMode).Title} now, so animated covers don’t show.";

    public const string ReduceMotionCovers = "Animation effects are off in Windows, so animated covers hold their first frame.";

    public const string SofteningHelp = "A soft blur over the cover, so fine detail doesn’t fight the lyrics.";

    /// <summary>The type's entry, or Custom Mix's.</summary>
    public static WallpaperType WallpaperTypeOf(string? backgroundMode)
    {
        var value = WallpaperTypeFor(backgroundMode);
        return WallpaperTypes.FirstOrDefault(t => t.Value == value) ?? WallpaperTypes[0];
    }

    // ---- Performance ----

    public static IReadOnlyList<SettingsChoice> FrameRates { get; } =
    [
        new("15", "15 fps"), new("30", "30 fps"), new("60", "60 fps"), new("0", "Match the display"),
    ];

    // ---- General ----

    public static IReadOnlyList<SettingsChoice> AppThemes { get; } =
    [
        new("system", "Use system setting"), new("light", "Light"), new("dark", "Dark"),
    ];

    /// <summary>"85%".</summary>
    public static string Percent(double value) => (value * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
