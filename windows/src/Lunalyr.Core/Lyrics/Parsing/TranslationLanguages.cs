// Ported from the Mac app's TranslationLanguages.swift (Lunalyr's own code).

using System.Text;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>
/// Which translations a listener can read. A lyric source may carry only a translation into a language the listener
/// doesn't know (an AMLL TTML with just a Chinese translation of a Japanese song), and showing that under every lyric
/// for an English listener only crowds the stage. A translation counts as readable when most of its lines are written
/// only in Latin or scripts their preferred languages use.
/// </summary>
public static class TranslationLanguages
{
    [Flags]
    public enum Script
    {
        None = 0,
        Latin = 1 << 0,
        Han = 1 << 1,
        Kana = 1 << 2,
        Hangul = 1 << 3,
        Cyrillic = 1 << 4,
        Greek = 1 << 5,
        Arabic = 1 << 6,
        Hebrew = 1 << 7,
        Thai = 1 << 8,
        Devanagari = 1 << 9,
    }

    /// <summary>The scripts a language tag ("en-AU", "zh-Hans-CN", "sr-Latn") is written in. An explicit script subtag wins.</summary>
    public static Script ScriptsOfLanguage(string tag)
    {
        var parts = tag.ToLowerInvariant().Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Script.None;
        foreach (var part in parts.Skip(1))
        {
            if (part.Length != 4) continue;
            switch (part)
            {
                case "latn": return Script.Latin;
                case "cyrl": return Script.Cyrillic;
                case "hans" or "hant" or "hani": return Script.Han;
                case "jpan": return Script.Han | Script.Kana;
                case "kore" or "hang": return Script.Hangul;
                case "arab": return Script.Arabic;
            }
        }
        return parts[0] switch
        {
            "zh" or "yue" or "wuu" or "nan" or "hak" => Script.Han,
            "ja" => Script.Han | Script.Kana,
            "ko" => Script.Hangul,
            "ru" or "uk" or "be" or "bg" or "mk" or "sr" or "kk" or "ky" or "mn" or "tg" => Script.Cyrillic,
            "el" => Script.Greek,
            "ar" or "fa" or "ur" or "ps" => Script.Arabic,
            "he" or "yi" => Script.Hebrew,
            "th" => Script.Thai,
            "hi" or "mr" or "ne" or "sa" => Script.Devanagari,
            _ => Script.Latin,
        };
    }

    /// <summary>The scripts of a text's letters. Letters in scripts not listed here are left out, so such a translation is never held back.</summary>
    public static Script ScriptsOf(string text)
    {
        var found = Script.None;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsAlphabetic(rune)) continue;
            var v = rune.Value;
            if (v is >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A or >= 0xC0 and <= 0x24F or >= 0x1E00 and <= 0x1EFF) found |= Script.Latin;
            else if (v is 0x3005 or 0x3007 or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
                     or >= 0x20000 and <= 0x323AF) found |= Script.Han;
            else if (v is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F) found |= Script.Kana;
            else if (v is >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7AF) found |= Script.Hangul;
            else if (v is >= 0x400 and <= 0x52F) found |= Script.Cyrillic;
            else if (v is >= 0x370 and <= 0x3FF) found |= Script.Greek;
            else if (v is >= 0x600 and <= 0x6FF or >= 0x750 and <= 0x77F or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF) found |= Script.Arabic;
            else if (v is >= 0x590 and <= 0x5FF) found |= Script.Hebrew;
            else if (v is >= 0xE00 and <= 0xE7F) found |= Script.Thai;
            else if (v is >= 0x900 and <= 0x97F) found |= Script.Devanagari;
        }
        return found;
    }

    /// <summary>
    /// Unicode's Alphabetic property, approximated: letters, letter numbers and the combining vowel signs of the
    /// Indic and Southeast Asian scripts (spacing and non-spacing marks), as Swift's <c>isAlphabetic</c> counts them.
    /// </summary>
    private static bool IsAlphabetic(Rune rune)
    {
        if (Rune.IsLetter(rune)) return true;
        var category = Rune.GetUnicodeCategory(rune);
        if (category == System.Globalization.UnicodeCategory.LetterNumber) return true;
        var v = rune.Value;
        return category is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark
            && v is >= 0x900 and <= 0x97F or >= 0xE00 and <= 0xE7F or >= 0x5B0 and <= 0x5C7 or >= 0x610 and <= 0x65F;
    }

    /// <summary>
    /// Whether a listener with these preferred languages can read a translation whose lines are
    /// <paramref name="texts"/>: at least as many of them as not are written only in scripts the listener reads. Latin
    /// always counts. Judged on the whole translation, because a line or two may be a name or a loanword in Latin
    /// letters; lines with no letters at all don't count either way.
    /// </summary>
    public static bool Readable(IEnumerable<string> texts, IEnumerable<string> languages)
    {
        var known = languages.Aggregate(Script.Latin, (scripts, language) => scripts | ScriptsOfLanguage(language));
        int readable = 0, unreadable = 0;
        foreach (var text in texts)
        {
            var used = ScriptsOf(text);
            if (used == Script.None) continue;
            if ((used & ~known) == 0) readable++;
            else unreadable++;
        }
        return readable >= unreadable;
    }
}
