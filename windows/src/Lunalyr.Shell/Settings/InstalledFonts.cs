using Vortice.DirectWrite;

namespace Lunalyr.Shell.Settings;

/// <summary>The font families installed on this PC, as DirectWrite (and so the wallpaper) knows them, for Lyric style › Typeface.</summary>
public static class InstalledFonts
{
    /// <summary>Every installed family's English name (or its first name), sorted, without duplicates. Empty if DirectWrite fails.</summary>
    public static IReadOnlyList<string> Families()
    {
        try
        {
            using var factory = DWrite.DWriteCreateFactory<IDWriteFactory>();
            using var collection = factory.GetSystemFontCollection(false);
            var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            for (uint i = 0; i < collection.FontFamilyCount; i++)
            {
                using var family = collection.GetFontFamily(i);
                using var localized = family.FamilyNames;
                if (localized.Count == 0) continue;
                var index = localized.FindLocaleName("en-us", out var found) ? found : 0;
                var name = localized.GetString(index);
                // Families whose names start with '@' are the vertical forms of CJK fonts.
                if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith('@')) names.Add(name.Trim());
            }
            return [.. names];
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }
}
