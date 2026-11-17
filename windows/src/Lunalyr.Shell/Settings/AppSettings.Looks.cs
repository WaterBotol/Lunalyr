// Per-display lyrics and each monitor's look: the Mac's FloatingLyrics.swift (DisplayLyrics, 0da0eef7), MonitorLooks.swift
// and LyricStyles.swift (66f51d76, e849a460, 3452d8cc; Lunalyr's own code), on Windows' settings record.
//
// Every connected display has a look of its own: its wallpaper and two lyric styles, Desktop for the lyrics on its
// wallpaper and Floating for its floating window. A slot is one display's style for one surface; the wallpaper is kept
// with the display's Desktop slot. Settings edits one slot at a time, in the record's top-level values; every other slot
// is kept in Looks and drawn from a read-only copy (LookFor). Picking another monitor or style stores the edited slot and
// loads the picked one (SwitchEditing).

using System.Text.Json;
using System.Text.Json.Serialization;
using Lunalyr.Core.Floating;

namespace Lunalyr.Shell.Settings;

/// <summary>One display's lyric style for one surface.</summary>
public readonly record struct LookSlot(string Display, LyricsSurface Surface)
{
    public override string ToString() => $"{Display}.{Surface.Raw()}";
}

public sealed partial record AppSettings
{
    /// <summary>What each display shows, by display key (Mac <c>custom.displayLyrics</c>); null until first migrated.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? DisplayLyrics { get; init; }

    /// <summary>Each slot's look while Settings is not editing it, by <see cref="LookSlot"/> (Mac <c>custom.look.&lt;display&gt;.&lt;surface&gt;</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? Looks { get; init; }

    /// <summary>The monitor Settings edits, its display key (Mac <c>custom.lookMonitor</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LookMonitor { get; init; }

    /// <summary>Every monitor draws the edited monitor's look (Mac <c>custom.lookSameEverywhere</c>).</summary>
    public bool LookSameEverywhere { get; init; }

    // ---- What each display shows ----

    /// <summary>What <paramref name="display"/> shows; a display never seen shows the lyrics on its desktop.</summary>
    public DisplayLyrics DisplayChoice(string display) =>
        DisplayLyrics?.TryGetValue(display, out var raw) == true && DisplayLyricsChoices.Parse(raw) is { } choice ? choice : DisplayLyricsChoices.Fallback;

    public AppSettings WithDisplayChoice(string display, DisplayLyrics choice)
    {
        ArgumentNullException.ThrowIfNull(display);
        var map = new Dictionary<string, string>(DisplayLyrics ?? new Dictionary<string, string>(), StringComparer.Ordinal) { [display] = choice.Raw() };
        return this with { DisplayLyrics = map };
    }

    /// <summary>
    /// What <paramref name="display"/> shows, picked in Settings (the Mac's <c>setChoice</c>): a display whose lyrics float
    /// from now on keeps a Floating style of its own.
    /// </summary>
    public AppSettings WithDisplayChosen(string display, DisplayLyrics choice, string? main)
    {
        var settings = WithDisplayChoice(display, choice);
        return choice.Floats() ? MonitorLooks.KeepFloatingStyle(settings, [display], main) : settings;
    }

    /// <summary>
    /// The style Settings edits follows what the edited monitor shows (the Mac's <c>MonitorBar.followShows</c>): only a
    /// monitor showing both has two styles to choose between.
    /// </summary>
    public AppSettings FollowingShows(string? main) =>
        DisplayChoice(EditingSlot(main).Display).ImpliedStyle() is { } implied && implied != Surface ? WithLyricsSurface(implied) : this;

    /// <summary>Settings edits <paramref name="display"/>'s look from now on (the monitor bar's Monitor menu).</summary>
    public AppSettings WithEditedMonitor(string display, string? main) =>
        display == EditingSlot(main).Display ? this : MonitorLooks.Edit(this, display, main).FollowingShows(main);

    /// <summary>
    /// What <paramref name="display"/> shows, picked in Settings (its menu under Displays, or Shows on the monitor bar); on
    /// the edited monitor the style edited follows it.
    /// </summary>
    public AppSettings WithDisplayShows(string display, DisplayLyrics choice, string? main)
    {
        var settings = WithDisplayChosen(display, choice, main);
        return display == settings.EditingSlot(main).Display ? settings.FollowingShows(main) : settings;
    }

    /// <summary>The displays among <paramref name="connected"/> that get a floating window.</summary>
    public List<string> FloatingDisplays(IEnumerable<string> connected) => [.. connected.Where(d => DisplayChoice(d).Floats())];

    /// <summary>Floating lyrics are on somewhere among <paramref name="connected"/>.</summary>
    public bool FloatsOn(IEnumerable<string> connected) => FloatingDisplays(connected).Count > 0;

    /// <summary>
    /// The Floating quick toggle: off everywhere when any display floats (their lyrics go back on the desktop), else on
    /// for <paramref name="target"/> (the primary display), its desktop lyrics moving into the window.
    /// </summary>
    public AppSettings WithFloatingToggled(IReadOnlyList<string> connected, string? target)
    {
        ArgumentNullException.ThrowIfNull(connected);
        var settings = this;
        if (FloatsOn(connected))
        {
            foreach (var key in connected.Where(k => DisplayChoice(k).Floats()))
                settings = settings.WithDisplayChoice(key, settings.DisplayChoice(key).WithFloating(false));
            return settings;
        }
        if ((target ?? connected.FirstOrDefault()) is { } display)
        {
            settings = settings.WithDisplayChoice(display, DisplayChoice(display).WithFloating(true));
            settings = MonitorLooks.KeepFloatingStyle(settings, [display], connected.FirstOrDefault());
        }
        return settings;
    }

    /// <summary>
    /// The first time displays are known: each connected display gets the choice that keeps the look it had
    /// (<see cref="DisplayLyricsChoices.Migrated"/>). Once; a later display shows its lyrics on the desktop.
    /// </summary>
    public AppSettings WithDisplaysMigrated(IReadOnlyList<string> connected)
    {
        ArgumentNullException.ThrowIfNull(connected);
        if (DisplayLyrics is not null || connected.Count == 0) return this;
        var migrated = DisplayLyricsChoices.Migrated(LyricsSurface, FloatingDisplay, connected);
        return this with { DisplayLyrics = migrated.ToDictionary(p => p.Key, p => p.Value.Raw(), StringComparer.Ordinal) };
    }

    // ---- Each monitor's look ----

    /// <summary>The slot Settings edits; <paramref name="main"/> when no monitor was picked yet.</summary>
    public LookSlot EditingSlot(string? main) => new(LookMonitor ?? main ?? MonitorLooks.FallbackDisplay, Surface);

    /// <summary>
    /// The settings <paramref name="surface"/>'s lyrics draw with on <paramref name="display"/> (null surface: the style being
    /// edited; null display: the monitor being edited): these settings for the edited slot, else a read-only copy with
    /// the slot's look (its style and its display's wallpaper) laid over them. Every setting no look keeps is these.
    /// </summary>
    public AppSettings LookFor(string? display, LyricsSurface? surface, string? main)
    {
        var editing = EditingSlot(main);
        var slot = new LookSlot(LookSameEverywhere ? editing.Display : display ?? editing.Display, surface ?? editing.Surface);
        if (slot == editing) return this;
        return MonitorLooks.Applied(this, MonitorLooks.Drawn(this, slot, editing, main), MonitorLookKeys.All);
    }

    /// <summary>The settings <paramref name="display"/>'s wallpaper draws with: these on the monitor being edited, whichever of its styles is.</summary>
    public AppSettings WallpaperLookFor(string? display, string? main)
    {
        var editing = EditingSlot(main);
        var target = LookSameEverywhere ? editing.Display : display ?? editing.Display;
        return target == editing.Display ? this : LookFor(target, Core.Floating.LyricsSurface.Desktop, main);
    }
}

/// <summary>Which settings belong to a monitor's look.</summary>
public static class MonitorLookKeys
{
    /// <summary>Kept per slot: the lyric style (layout, typeface, colours, highlights, readings and translations shown, entrances, the orb, word symbols and action words, the title card).</summary>
    public static IReadOnlyList<string> Style => LyricStyleProfiles.Keys;

    /// <summary>
    /// Kept per display, with its Desktop slot: the wallpaper and its options, the animated covers, and how the desktop
    /// lyrics stay readable over it (the Mac's list where Windows has the setting; it has no Wavefield preset or
    /// projection, Light 3D colours or menu-bar shade).
    /// </summary>
    public static IReadOnlyList<string> Wallpaper { get; } =
    [
        "backgroundMode", "musicVideoFallback", "shaderStyle", "patternStyle", "albumGradientWallpaper", "visualizerStyle",
        "wallpaperPath", "wallpaperName", "wallpaperPlacement", "musicVideoFraming", "libraryVideoFraming", "musicVideoDim", "musicVideoBlur",
        "effectSpeed", "vividness", "scrimStrength", "patternOpacity", "motionBlur", "useMotionArtwork", "motionPlacement",
        "wavefieldColours", "wavecoreColours", "textBackingAutomatic", "textBacking",
    ];

    /// <summary>Every key a slot can hold.</summary>
    public static IReadOnlyList<string> All { get; } = [.. LyricStyleProfiles.Keys, .. Wallpaper];

    /// <summary>The keys <paramref name="surface"/>'s slot holds.</summary>
    public static IReadOnlyList<string> For(LyricsSurface surface) => surface == LyricsSurface.Desktop ? All : Style;
}

/// <summary>Where each monitor's look is kept, and the swap that moves Settings from one slot to another. Pure: settings in, settings out.</summary>
public static class MonitorLooks
{
    /// <summary>A display key while no display is known at all (a headless start).</summary>
    public const string FallbackDisplay = "main";

    public static string StoreKey(LookSlot slot) => slot.ToString();

    private static Dictionary<string, JsonElement> Only(IEnumerable<string> keys, IReadOnlyDictionary<string, JsonElement> values)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in keys) if (values.TryGetValue(key, out var value)) result[key] = value;
        return result;
    }

    private static Dictionary<string, JsonElement> Merged(IReadOnlyDictionary<string, JsonElement> first, IReadOnlyDictionary<string, JsonElement> over)
    {
        var result = new Dictionary<string, JsonElement>(first, StringComparer.Ordinal);
        foreach (var (key, value) in over) result[key] = value;
        return result;
    }

    /// <summary><paramref name="keys"/> as the settings hold them now.</summary>
    public static Dictionary<string, JsonElement> Top(AppSettings settings, IEnumerable<string> keys) => LyricStyleProfiles.Values(settings, keys);

    public static IReadOnlyDictionary<string, JsonElement>? Saved(AppSettings settings, LookSlot slot) =>
        settings.Looks?.TryGetValue(StoreKey(slot), out var values) == true ? values : null;

    public static AppSettings Store(AppSettings settings, IReadOnlyDictionary<string, JsonElement> values, LookSlot slot)
    {
        var looks = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(
            settings.Looks ?? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(), StringComparer.Ordinal)
        {
            [StoreKey(slot)] = Only(MonitorLookKeys.For(slot.Surface), values),
        };
        return settings with { Looks = looks };
    }

    /// <summary>
    /// A slot's lyric style. The edited slot's is the settings' own; a kept slot's is its own; a display never seen before
    /// copies the main display's; the main display, before monitors had looks, has today's styles (lyricStyleDesktop and
    /// lyricStyleFloating, read for one release). A Floating style never kept starts from its display's Desktop style,
    /// less its size, and is kept only once floating lyrics are used there.
    /// </summary>
    public static Dictionary<string, JsonElement> Style(AppSettings settings, LookSlot slot, LookSlot? editing, string? main)
    {
        var keys = MonitorLookKeys.Style;
        if (slot == editing) return Top(settings, keys);
        if (Saved(settings, slot) is { } saved) return Only(keys, saved);
        var home = main ?? slot.Display;
        var mainSlot = new LookSlot(home, slot.Surface);
        if (slot.Surface == LyricsSurface.Floating)
        {
            if (slot.Display != home && (mainSlot == editing || Saved(settings, mainSlot) is not null)) return Style(settings, mainSlot, editing, main);
            if (slot.Display == home && settings.LyricStyleFloating is { } legacy) return Only(keys, legacy);
            var seeded = Style(settings, slot with { Surface = LyricsSurface.Desktop }, editing, main);
            foreach (var (key, value) in LyricStyleProfiles.FloatingSeed) seeded[key] = JsonSerializer.SerializeToElement(value, AppSettingsJsonContext.Default.Double);
            return seeded;
        }
        if (slot.Display != home) return Style(settings, mainSlot, editing, main);
        if (settings.LyricStyleDesktop is { } desktop) return Only(keys, desktop);
        return Top(settings, keys);
    }

    /// <summary>
    /// A display's wallpaper: the settings' own for the edited monitor, else kept with its Desktop slot; a display never
    /// seen before copies the main display's, and before monitors had looks every display has today's.
    /// </summary>
    public static Dictionary<string, JsonElement> Wallpaper(AppSettings settings, string display, LookSlot? editing, string? main)
    {
        var keys = MonitorLookKeys.Wallpaper;
        if (display == editing?.Display) return Top(settings, keys);
        if (Saved(settings, new LookSlot(display, LyricsSurface.Desktop)) is { } saved) return Only(keys, saved);
        if (main is not null && display != main) return Wallpaper(settings, main, editing, main);
        return Top(settings, keys);
    }

    /// <summary>Everything <paramref name="slot"/> holds.</summary>
    public static Dictionary<string, JsonElement> Values(AppSettings settings, LookSlot slot, LookSlot? editing, string? main)
    {
        var values = Style(settings, slot, editing, main);
        return slot.Surface == LyricsSurface.Desktop ? Merged(values, Wallpaper(settings, slot.Display, editing, main)) : values;
    }

    /// <summary>Everything a read-only copy of <paramref name="slot"/> draws with: its style and its display's wallpaper.</summary>
    public static Dictionary<string, JsonElement> Drawn(AppSettings settings, LookSlot slot, LookSlot? editing, string? main) =>
        Merged(Style(settings, slot, editing, main), Wallpaper(settings, slot.Display, editing, main));

    /// <summary><paramref name="values"/> laid over <paramref name="settings"/>: each of <paramref name="keys"/> it lacks goes back to its default.</summary>
    public static AppSettings Applied(AppSettings settings, IReadOnlyDictionary<string, JsonElement> values, IEnumerable<string> keys) =>
        LyricStyleProfiles.Applied(settings, values, keys);

    /// <summary>
    /// Keeps the edited slot (and, while its Floating style is edited, the wallpaper with its Desktop slot), then loads
    /// <paramref name="next"/> into the settings and remembers its display as edited. <c>Seeded</c>: <paramref name="next"/>
    /// had never been kept and was worked out and kept now.
    /// </summary>
    public static (AppSettings Settings, bool Seeded) SwitchEditing(AppSettings settings, LookSlot old, LookSlot next, string? main)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (old == next) return (settings, false);
        settings = KeepEdited(settings, old, main);
        var seeded = Saved(settings, next) is null;
        var style = Style(settings, next, null, main);
        var wallpaper = Wallpaper(settings, next.Display, null, main);
        settings = Applied(settings, style, MonitorLookKeys.Style);
        settings = Applied(settings, wallpaper, MonitorLookKeys.Wallpaper);
        if (seeded) settings = Store(settings, Merged(style, wallpaper), next);
        return (settings with { LookMonitor = next.Display, LyricsSurface = next.Surface.Raw() }, seeded);
    }

    /// <summary>Stores the edited slot as it is now, and the edited monitor's wallpaper with its Desktop slot.</summary>
    public static AppSettings KeepEdited(AppSettings settings, LookSlot edited, string? main)
    {
        settings = Store(settings, Values(settings, edited, edited, main), edited);
        if (edited.Surface == LyricsSurface.Floating)
        {
            var desktop = edited with { Surface = LyricsSurface.Desktop };
            settings = Store(settings, Values(settings, desktop, edited, main), desktop);
        }
        return settings;
    }

    /// <summary>
    /// The first time monitors are known, and whenever a display is first seen: each connected display's Desktop slot is
    /// kept as worked out (today's look, or the main display's), and so is its Floating slot where there is one to copy.
    /// The edited slot stays in the settings' own values.
    /// </summary>
    public static AppSettings KeepConnected(AppSettings settings, IReadOnlyList<string> connected)
    {
        ArgumentNullException.ThrowIfNull(connected);
        var main = connected.FirstOrDefault();
        var editing = settings.EditingSlot(main);
        if (settings.LookMonitor is null && main is not null) settings = settings with { LookMonitor = editing.Display };
        foreach (var display in connected)
        {
            var desktop = new LookSlot(display, LyricsSurface.Desktop);
            if (desktop != editing && Saved(settings, desktop) is null) settings = Store(settings, Values(settings, desktop, editing, main), desktop);
            var floating = new LookSlot(display, LyricsSurface.Floating);
            if (floating == editing || Saved(settings, floating) is not null) continue;
            var mainFloating = main is null ? (LookSlot?)null : new LookSlot(main, LyricsSurface.Floating);
            var hasSource = display == main
                ? settings.LyricStyleFloating is not null
                : mainFloating == editing || (mainFloating is { } m && Saved(settings, m) is not null);
            if (hasSource) settings = Store(settings, Values(settings, floating, editing, main), floating);
        }
        return settings;
    }

    /// <summary>
    /// Floating lyrics are on for <paramref name="displays"/>: from now on each one's Floating style is its own, kept apart
    /// from its Desktop style (worked out as <see cref="Style"/> says, from that day's desktop look).
    /// </summary>
    public static AppSettings KeepFloatingStyle(AppSettings settings, IEnumerable<string> displays, string? main)
    {
        var editing = settings.EditingSlot(main);
        foreach (var display in displays)
        {
            var slot = new LookSlot(display, LyricsSurface.Floating);
            if (slot == editing || Saved(settings, slot) is not null) continue;
            settings = Store(settings, Style(settings, slot, editing, main), slot);
        }
        return settings;
    }

    /// <summary>
    /// Gives every display in <paramref name="displays"/> the edited monitor's look: its Desktop slot and wallpaper, and
    /// its Floating style once that is its own (kept or being edited).
    /// </summary>
    public static AppSettings CopyEdited(AppSettings settings, IEnumerable<string> displays, string? main)
    {
        var edited = settings.EditingSlot(main);
        var desktop = Values(settings, edited with { Surface = LyricsSurface.Desktop }, edited, main);
        var floatingSlot = edited with { Surface = LyricsSurface.Floating };
        var floating = floatingSlot == edited || Saved(settings, floatingSlot) is not null ? Style(settings, floatingSlot, edited, main) : null;
        foreach (var display in displays.Where(d => d != edited.Display))
        {
            settings = Store(settings, desktop, new LookSlot(display, LyricsSurface.Desktop));
            if (floating is not null) settings = Store(settings, floating, new LookSlot(display, LyricsSurface.Floating));
        }
        return settings;
    }

    /// <summary>Settings edits <paramref name="display"/>'s look from now on, the same style as before.</summary>
    public static AppSettings Edit(AppSettings settings, string display, string? main)
    {
        var current = settings.EditingSlot(main);
        return SwitchEditing(settings, current, current with { Display = display }, main).Settings;
    }

    /// <summary>
    /// After displays came or went: a display seen for the first time starts as a copy of the main display's look, and
    /// when the edited monitor is gone Settings edits the main display's look (with Same on every monitor, the main
    /// display takes the edited look with it rather than going back to what it kept). The per-display choices are
    /// migrated the first time any display is known.
    /// </summary>
    public static AppSettings DisplaysChanged(AppSettings settings, IReadOnlyList<string> connected)
    {
        ArgumentNullException.ThrowIfNull(connected);
        if (connected.Count == 0) return settings;
        var main = connected[0];
        settings = settings.WithDisplaysMigrated(connected);
        settings = KeepConnected(settings, connected);
        var editing = settings.EditingSlot(main);
        if (!connected.Contains(editing.Display))
        {
            if (settings.LookSameEverywhere) settings = CopyEdited(settings, connected, main);
            settings = Edit(settings, main, main);
            // Kept again from the slot now edited, so the same displays next time change nothing.
            settings = KeepConnected(settings, connected);
        }
        // The first floating display's Floating style is its own from now on (lazily, as on the Mac).
        settings = KeepFloatingStyle(settings, settings.FloatingDisplays(connected), main);
        return settings;
    }

    /// <summary>Turns "Same on every monitor" on or off. Off, every monitor keeps the look it was showing, the edited one's.</summary>
    public static AppSettings WithSameEverywhere(AppSettings settings, bool on, IReadOnlyList<string> connected)
    {
        if (on == settings.LookSameEverywhere) return settings;
        if (!on) settings = CopyEdited(settings, connected, connected.FirstOrDefault());
        return settings with { LookSameEverywhere = on };
    }

    /// <summary>Whether a monitor other than the one being edited keeps <paramref name="path"/> as its imported wallpaper, so a new import must not delete it.</summary>
    public static bool KeepsWallpaperFile(AppSettings settings, string path, string? main)
    {
        if (string.IsNullOrEmpty(path) || settings.Looks is null) return false;
        var edited = StoreKey(new LookSlot(settings.EditingSlot(main).Display, LyricsSurface.Desktop));
        return settings.Looks.Any(p => p.Key != edited && p.Value.TryGetValue("wallpaperPath", out var value)
                                       && value.ValueKind == JsonValueKind.String && value.GetString() == path);
    }
}
