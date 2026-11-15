// Remembered lyric styles, ported from the Mac's StyleMemory (Sources/Settings/LyricExtrasOptions.swift) and the names
// its Lyric style page gives them (LyricExtrasInspector.swift's LyricStyleMemorySection and RememberedStyleNote):
// Lunalyr's own code. The Mac keeps the memory in its own UserDefaults store; here it is three settings
// (styleMemoryEnabled, styleMemorySongs, styleMemoryArtists) changed through AppSettings, so the store saves it.

using System.Security.Cryptography;
using System.Text;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;

namespace Lunalyr.Shell.Settings;

/// <summary>
/// A lyric style remembered for one recording or for everything by one artist: the layout and the text colour. A
/// song's own memory wins over its artist's; the general settings apply where neither exists.
/// </summary>
/// <param name="Composition">A composition id, or "automatic" for Custom Mix; null leaves the layout to the settings.</param>
/// <param name="Colour">A colour mode's raw value; null leaves the colour to the settings.</param>
/// <param name="Font">The typeface choice ("system" or "installed"), as the Mac now remembers it (next/tester-fixes); null
/// leaves it to the settings, as in styles saved before.</param>
/// <param name="FontFamily">The installed family with <paramref name="Font"/> "installed".</param>
public sealed record RememberedStyle(string? Composition, string? Colour, string? Font = null, string? FontFamily = null)
{
    /// <summary>The layout remembered, or null for none or Custom Mix.</summary>
    public CompositionKind? Layout => SettingsCopy.Kind(Composition);

    /// <summary>The colour remembered ("static" reads as Album drift), or null.</summary>
    public LyricColourMode? ColourMode => LyricColourModes.SavedChoice(Colour);

    /// <summary>As stored: only the parts remembered.</summary>
    public IReadOnlyDictionary<string, string> Stored()
    {
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Composition is not null) stored["composition"] = Composition;
        if (Colour is not null) stored["colour"] = Colour;
        if (Font is not null) stored["font"] = Font;
        if (FontFamily is not null) stored["fontFamily"] = FontFamily;
        return stored;
    }

    /// <summary>A stored style; a layout since cut reads as the one that replaced it (Editorial as Scattered).</summary>
    public static RememberedStyle? FromStored(IReadOnlyDictionary<string, string>? stored) =>
        stored is null ? null
            : new(stored.TryGetValue("composition", out var composition) ? AppSettings.MigratedLayout(composition) : null,
                  stored.TryGetValue("colour", out var colour) ? colour : null,
                  stored.TryGetValue("font", out var font) && font is "system" or "installed" ? font : null,
                  stored.TryGetValue("fontFamily", out var family) ? family : null);
}

public enum StyleScope { Song, Artist }

/// <summary>Where a style is remembered for the song playing, and which part of it the page's notes are about.</summary>
public enum StyleAspect { Layout, Colour }

public static class StyleMemory
{
    /// <summary>
    /// Keyed like the song offsets: a digest of the exact recording, so song and artist names are not written to the
    /// settings in the clear.
    /// </summary>
    public static string SongKey(LyricQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Digest("song|" + query.RecordingKey);
    }

    public static string ArtistKey(string artist) => Digest("artist|" + (artist ?? "").ToLowerInvariant().Trim());

    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// The style remembered for a song, its own first, then its artist's (by <paramref name="artist"/>, so it applies
    /// before the song's length is known); null when the memory is off or holds nothing for it.
    /// </summary>
    public static (RememberedStyle Style, StyleScope Scope)? Remembered(AppSettings settings, LyricQuery? song, string? artist)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.StyleMemoryEnabled) return null;
        if (song is not null && FromStored(settings.StyleMemorySongs, SongKey(song)) is { } own) return (own, StyleScope.Song);
        var name = artist ?? song?.Artist;
        if (!string.IsNullOrWhiteSpace(name) && FromStored(settings.StyleMemoryArtists, ArtistKey(name)) is { } theirs)
            return (theirs, StyleScope.Artist);
        return null;
    }

    private static RememberedStyle? FromStored(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? map, string key) =>
        map is not null && map.TryGetValue(key, out var stored) ? RememberedStyle.FromStored(stored) : null;

    /// <summary>
    /// A layout taken out of Custom Mix (its count set to 0 with the rotation on) is not brought back by a remembered
    /// style: the rotation picks instead, and the rest of the style still applies. The memory itself is kept. The Mac's
    /// rule (LyricComposition.selected, next/tester-fixes, 14 Nov 2026; tester feedback on 0.9.0).
    /// </summary>
    public static bool LeftOutOfMix(AppSettings settings, string layout) =>
        settings.LyricComposition == AppSettings.AllLayoutsKey && settings.RotateLayouts
        && settings.RotateLayoutCounts is { } counts && counts.TryGetValue(layout, out var count) && count == 0;

    /// <summary>
    /// The settings as the music playing sees them: the layout and colour remembered for it in place of the ones chosen
    /// (a remembered "automatic" means Custom Mix even where a layout is chosen), the rest as chosen.
    /// </summary>
    public static AppSettings Applied(AppSettings settings, RememberedStyle? style)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (style is null) return settings;
        var composition = style.Composition is { } id && AppSettings.Compositions.Contains(id) && !LeftOutOfMix(settings, id)
            ? id : settings.LyricComposition;
        var colour = style.ColourMode is { } mode ? mode.RawValue() : settings.LyricColourMode;
        var font = style.Font ?? settings.FontChoice;
        var family = style.Font is null ? settings.FontFamily : style.FontFamily ?? "";
        return composition == settings.LyricComposition && colour == settings.LyricColourMode && font == settings.FontChoice
               && family == settings.FontFamily
            ? settings
            : settings with { LyricComposition = composition, LyricColourMode = colour, FontChoice = font, FontFamily = family };
    }

    /// <summary>
    /// "Next layout" with a style remembered for the music playing: the remembered layout steps on in place, so the
    /// change shows and stays with the music (the Mac's <c>cycleLayout(settings:memory:track:)</c>); null when nothing is
    /// remembered and the chosen layout should step on instead.
    /// </summary>
    public static (AppSettings Settings, string Layout, StyleScope Scope)? CycledRemembered(AppSettings settings, LyricQuery? song,
                                                                                           Func<string?, string> cycled)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(cycled);
        if (song is null || Remembered(settings, song, song.Artist) is not { } active) return null;
        var next = cycled(active.Style.Composition);
        return (Remember(settings, active.Style with { Composition = next }, song, active.Scope), next, active.Scope);
    }

    /// <summary>The settings with <paramref name="style"/> remembered for the song or its artist.</summary>
    public static AppSettings Remember(AppSettings settings, RememberedStyle style, LyricQuery song, StyleScope scope)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(song);
        return scope == StyleScope.Song
            ? settings with { StyleMemorySongs = With(settings.StyleMemorySongs, SongKey(song), style) }
            : settings with { StyleMemoryArtists = With(settings.StyleMemoryArtists, ArtistKey(song.Artist), style) };
    }

    /// <summary>The settings without the style remembered for the song or its artist.</summary>
    public static AppSettings Forget(AppSettings settings, LyricQuery song, StyleScope scope)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(song);
        return scope == StyleScope.Song
            ? settings with { StyleMemorySongs = With(settings.StyleMemorySongs, SongKey(song), null) }
            : settings with { StyleMemoryArtists = With(settings.StyleMemoryArtists, ArtistKey(song.Artist), null) };
    }

    /// <summary>
    /// Forgets one part of a remembered style (the page's Forget under the Layout or Colour picker): the rest stays, and
    /// a style with nothing left is forgotten.
    /// </summary>
    public static AppSettings ForgetPart(AppSettings settings, LyricQuery song, StyleScope scope, RememberedStyle style, StyleAspect aspect)
    {
        var left = aspect == StyleAspect.Layout ? style with { Composition = null } : style with { Colour = null };
        return left.Composition is null && left.Colour is null && left.Font is null ? Forget(settings, song, scope) : Remember(settings, left, song, scope);
    }

    /// <summary>The settings with nothing remembered (the switch keeps its place).</summary>
    public static AppSettings ForgetAll(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            StyleMemorySongs = new Dictionary<string, IReadOnlyDictionary<string, string>>(),
            StyleMemoryArtists = new Dictionary<string, IReadOnlyDictionary<string, string>>(),
        };
    }

    /// <summary>The layout, colour and typeface chosen in the settings, as a style to remember.</summary>
    public static RememberedStyle Chosen(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(settings.LyricComposition, settings.ColourMode.RawValue(), settings.FontChoice,
            settings.FontChoice == "installed" ? settings.FontFamily : null);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> With(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? map, string key, RememberedStyle? style)
    {
        var next = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            map ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(), StringComparer.Ordinal);
        if (style is null) next.Remove(key);
        else next[key] = style.Stored();
        return next;
    }

    /// <summary>"1 song · 2 artists remembered".</summary>
    public static string RememberedCount(int songs, int artists) =>
        $"{songs} {(songs == 1 ? "song" : "songs")} · {artists} {(artists == 1 ? "artist" : "artists")} remembered";

    /// <summary>"Custom Mix · Album drift": what a remembered style holds.</summary>
    public static string Describe(RememberedStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var layout = style.Composition == AppSettings.AllLayoutsKey ? "Custom Mix" : style.Layout?.Title() ?? "Custom Mix";
        return style.ColourMode is { } colour ? $"{layout} · {colour.Title()}" : layout;
    }

    /// <summary>
    /// The remembered part a note under the Layout or Colour picker is about, by name, or null when the memory leaves it
    /// to the setting (the Mac's <c>RememberedStyleNote.rememberedName</c>).
    /// </summary>
    public static string? RememberedName(StyleAspect aspect, RememberedStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (aspect == StyleAspect.Colour) return style.ColourMode?.Title();
        if (style.Composition is not { } raw) return null;
        return raw == AppSettings.AllLayoutsKey ? "Custom Mix" : style.Layout?.Title() ?? "Custom Mix";
    }

    public static string ScopeName(StyleScope scope) => scope == StyleScope.Song ? "song" : "artist";
}

public sealed partial record AppSettings
{
    /// <summary>"Use remembered styles" (Mac <c>custom.styleMemory.enabled</c>).</summary>
    public bool StyleMemoryEnabled { get; init; } = true;

    /// <summary>Styles remembered per recording, by hashed key: {"composition", "colour"} (Mac <c>custom.styleMemory.songs</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> StyleMemorySongs { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// <summary>Styles remembered per artist, by hashed key (Mac <c>custom.styleMemory.artists</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> StyleMemoryArtists { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();
}
