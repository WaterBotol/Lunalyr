// What the settings window says: its choices, titles, captions and the notes on rows Windows can't do yet. Wording is
// ported from the Mac's settings pages (Sources/Settings/Pages/*.swift, LunalyrWorkspace.swift's WorkspaceCopy,
// QuickControls.swift's TimingNudge, LyricRomanization.swift, LyricHighlightColour.swift; all Lunalyr's own) and
// adapted where Windows differs. Kept out of the window so tests can hold it to what the app does.

using System.Globalization;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Shell.Settings;

/// <summary>One choice in a menu or segmented control: the stored value, what it is called and an optional caption.</summary>
public sealed record SettingsChoice(string Value, string Title, string? Detail = null);

/// <summary>One background type: what it is called, what it does, and (when Windows can't show it yet) why not.</summary>
public sealed record WallpaperType(string Value, string Title, string Summary, string? NotYet = null)
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

    /// <summary>What Match the music does with the layout (the Mac's <c>WorkspaceCopy.automaticLayoutDetail</c>).</summary>
    public const string AutomaticLayoutDetail =
        "Picks Constellation, Echo, Editorial or Scattered to suit each track’s energy and feel.";

    public const string ReduceMotionEntrances = "Animation effects are off in Windows, so every entrance plays as a simple fade.";

    /// <summary>Every layout, "Match the music" first, in the settings' order.</summary>
    public static IReadOnlyList<SettingsChoice> Layouts { get; } =
    [
        new(AppSettings.AllLayoutsKey, "Match the music", AutomaticLayoutDetail),
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
        [new("off", "Off"), new("below", "Below lyrics"), new("replace", "Replace lyrics")];

    public static IReadOnlyList<SettingsChoice> ReadingLanguages { get; } =
    [
        new("automatic", "Detect language"),
        new("japanese", "Japanese · Romaji"),
        new("mandarin", "Mandarin · Pinyin"),
        new("cantonese", "Cantonese · Saved readings only"),
        new("korean", "Korean · Revised Romanization"),
        new("other", "Other scripts · Latin"),
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

    /// <summary>What "Treat long pauses as instrumental" changes (the Mac's <c>TimingPage.pauseCaption</c>).</summary>
    public static string PauseCaption(bool showInterludes)
    {
        const string caption = "Lyrics timed only by line don’t say when singing stops. With Show three dots during instrumental breaks on (Lyric style), a long gap after a line is shown as an instrumental break instead of one held line. Turn this off for songs with long held notes.";
        return showInterludes
            ? caption
            : caption + " The three dots are off, so this changes only QQ Music’s lyrics, whose held lines end early, and how long the words of a line before a long gap take to light up.";
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

    /// <summary>Every known source in the configured order (unknown names dropped, new sources at the end).</summary>
    public static IReadOnlyList<LyricSourceId> SourceOrder(AppSettings settings)
    {
        var order = new List<LyricSourceId>();
        foreach (var raw in (settings.LyricSourceOrder ?? []).Concat(LyricSourceSnapshot.RecommendedOrder.Select(s => s.RawValue())))
            if (LyricSourceInfo.TryParseRaw(raw, out var id) && !order.Contains(id)) order.Add(id);
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

    /// <summary>The Mac's background types, with the two Windows draws now first.</summary>
    public static IReadOnlyList<WallpaperType> WallpaperTypes { get; } =
    [
        new("gradient", "Album colours", "A slowly moving field of the cover’s colours, darkened so the lyrics stay readable."),
        new("colourOnly", "Colour only", "Blends the cover’s colours into a moving gradient."),
        new("auto", "Match the music", "GPU effects and line patterns picked to suit each song, with animated covers.", LaterBuild),
        new("shader", "GPU effects", "Flowing GPU-drawn effects in the album’s colours.", LaterBuild),
        new("pattern", "Line patterns", "Fine moving line patterns in the album’s colours.", LaterBuild),
        new("visualizer", "Visualisers", "Scenes that move with the music playing.", Milestone6),
        new("musicVideo", "Music video", "The song’s music video behind the lyrics.", Milestone6),
        new("desktop", "Your wallpaper", "Only the lyrics, over your own Windows wallpaper.", LaterBuild),
        new("custom", "Imported image or video", "A picture or video of your own behind the lyrics.", LaterBuild),
    ];

    /// <summary>The type the Wallpaper page shows as chosen for a stored <c>backgroundMode</c>.</summary>
    public static string WallpaperTypeFor(string? backgroundMode) => backgroundMode switch
    {
        "colourOnly" or "colour" or "colorOnly" => "colourOnly",
        _ => "gradient",
    };

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
