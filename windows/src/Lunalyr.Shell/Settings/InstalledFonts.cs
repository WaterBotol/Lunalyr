// The font families installed on this PC for Lyric style › Typeface › Installed font, ported from the Mac's
// InstalledFontPicker.swift (InstalledFontLibrary: 7fa2f68, 86af9d7, eda0f37; Lunalyr's own code). DirectWrite stands in
// for AppKit and Core Text: a family is listed when it can set lyrics, so not a symbol font, and it has Latin letters or
// another script's (Arabic, Hebrew, Cyrillic, Greek, Devanagari, Thai, Chinese, Japanese, Korean). Read once when first
// shown and again on "Find new fonts", which says how many families are new since it was last pressed. Unlike the Mac,
// the list is not reread by itself when a font is installed (Windows' font-change broadcast is not listened for).

using System.Globalization;
using Vortice.DirectWrite;

namespace Lunalyr.Shell.Settings;

/// <summary>The font families installed on this PC, as DirectWrite (and so the wallpaper) knows them.</summary>
public static class InstalledFonts
{
    /// <summary>Every family that can set lyrics, by its English name (or its first), sorted, once each. Empty if DirectWrite fails.</summary>
    public static IReadOnlyList<string> Families() => Read(checkForUpdates: false);

    /// <summary>
    /// The families as DirectWrite lists them now; <paramref name="checkForUpdates"/> asks it to look again for fonts
    /// installed since the app opened ("Find new fonts").
    /// </summary>
    public static IReadOnlyList<string> Read(bool checkForUpdates)
    {
        try
        {
            using var factory = DWrite.DWriteCreateFactory<IDWriteFactory>();
            using var collection = factory.GetSystemFontCollection(checkForUpdates);
            var raw = new List<string>();
            var usable = new HashSet<string>(StringComparer.Ordinal);
            for (uint i = 0; i < collection.FontFamilyCount; i++)
            {
                using var family = collection.GetFontFamily(i);
                using var localized = family.FamilyNames;
                if (localized.Count == 0) continue;
                var index = localized.FindLocaleName("en-us", out var found) ? found : 0;
                var name = localized.GetString(index)?.Trim() ?? "";
                raw.Add(name);
                if (name.Length > 0 && SetsLyrics(family)) usable.Add(name);
            }
            if (checkForUpdates) Forget();
            return InstalledFontLibrary.UsableFamilies(raw, usable.Contains);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    /// <summary>A few letters of each script lyrics may be in, beside Latin (the Mac's otherScripts, eda0f37).</summary>
    private static readonly string[] OtherScripts = ["ابتث", "אבגד", "абвг", "αβγδ", "कखगघ", "กขคง", "的一是不", "あいうえ", "가나다라"];

    /// <summary>
    /// Whether the family can set lyrics (the Mac's drawsLatin): not a symbol face (Wingdings, Webdings, Symbol), and it
    /// has Latin letters or another script's.
    /// </summary>
    private static bool SetsLyrics(IDWriteFontFamily family)
    {
        using var font = family.GetFirstMatchingFont(FontWeight.Normal, FontStretch.Normal, FontStyle.Normal);
        if (font.IsSymbolFont) return false;
        if (Covers(font, "AaBbEeGgRrSs")) return true;
        return OtherScripts.Any(script => Covers(font, script));
    }

    private static bool Covers(IDWriteFont font, string letters)
    {
        foreach (var rune in letters.EnumerateRunes())
            if (!font.HasCharacter((uint)rune.Value)) return false;
        return true;
    }

    private static readonly object Gate = new();
    private static IDWriteFactory? _factory;
    private static IDWriteFontCollection? _collection;
    private static readonly Dictionary<string, bool> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="family"/> is installed (any of its names, case ignored). Answers are kept until "Find new
    /// fonts" looks again. True when DirectWrite can't be asked, so a family is never dropped for want of an answer.
    /// </summary>
    public static bool IsInstalled(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return false;
        var name = family.Trim();
        lock (Gate)
        {
            if (Known.TryGetValue(name, out var known)) return known;
            try
            {
                _factory ??= DWrite.DWriteCreateFactory<IDWriteFactory>();
                _collection ??= _factory.GetSystemFontCollection(false);
                var found = _collection.FindFamilyName(name, out _);
                Known[name] = found;
                return found;
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException)
            {
                return true;
            }
        }
    }

    /// <summary>Forgets what <see cref="IsInstalled"/> knew, after fonts were looked for again.</summary>
    public static void Forget()
    {
        lock (Gate)
        {
            Known.Clear();
            _collection?.Dispose();
            _collection = null;
        }
    }
}

/// <summary>
/// The installed families Settings lists (the Mac's InstalledFontLibrary): read once when first needed, again on "Find new
/// fonts". The pure parts are static, so they are tested without the PC's fonts.
/// </summary>
public sealed class InstalledFontLibrary(Func<bool, IReadOnlyList<string>>? source = null)
{
    /// <summary>The app's one list (Settings and the welcome share it).</summary>
    public static InstalledFontLibrary Shared { get; } = new();

    private readonly Func<bool, IReadOnlyList<string>> _source = source ?? InstalledFonts.Read;
    private IReadOnlyList<string>? _announced;

    /// <summary>The families, once read (empty before).</summary>
    public IReadOnlyList<string> Families { get; private set; } = [];

    /// <summary>The list has been read at least once.</summary>
    public bool HasLoaded { get; private set; }

    /// <summary>Reads the list the first time it is needed.</summary>
    public void LoadIfNeeded()
    {
        if (!HasLoaded) Refresh();
    }

    /// <summary>Rereads the installed families and returns how many are new since the last reading (0 on the first).</summary>
    public int Refresh(bool checkForUpdates = false)
    {
        var usable = _source(checkForUpdates);
        var added = HasLoaded ? NewFamilies(Families, usable).Count : 0;
        Families = usable;
        HasLoaded = true;
        _announced ??= usable;
        return added;
    }

    /// <summary>"Find new fonts": rereads the list and returns how many families are new since it was last pressed (or first read).</summary>
    public int FindNewFonts()
    {
        var before = _announced;
        Refresh(checkForUpdates: true);
        var added = before is null ? 0 : NewFamilies(before, Families).Count;
        _announced = Families;
        return added;
    }

    // ---- Pure parts ----

    /// <summary>Visible, usable families, once each, in the system's order of names (hidden "." and vertical "@" ones left out).</summary>
    public static IReadOnlyList<string> UsableFamilies(IEnumerable<string> raw, Func<string, bool> isUsable)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. raw.Where(family => family.Length > 0 && !family.StartsWith('.') && !family.StartsWith('@') && seen.Add(family) && isUsable(family))
            .OrderBy(family => family, StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering))];
    }

    /// <summary>Families in <paramref name="now"/> that <paramref name="old"/> didn't have.</summary>
    public static IReadOnlyList<string> NewFamilies(IEnumerable<string> old, IEnumerable<string> now)
    {
        var before = new HashSet<string>(old, StringComparer.Ordinal);
        return [.. now.Where(family => !before.Contains(family))];
    }

    /// <summary>The families whose names contain <paramref name="query"/>, ignoring case and accents.</summary>
    public static IReadOnlyList<string> Filter(IReadOnlyList<string> families, string query)
    {
        var text = query.Trim();
        if (text.Length == 0) return families;
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        return [.. families.Where(family => compare.IndexOf(family, text, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)];
    }

    /// <summary>What "Find new fonts" says afterwards.</summary>
    public static string FoundMessage(int count) => count switch
    {
        0 => "No new fonts found.",
        1 => "Found 1 new family.",
        _ => $"Found {count} new families.",
    };
}
