namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>
/// Which translations a listener can read. A lyric source may carry only a translation into a language the listener
/// doesn't know -- an AMLL TTML with just a Chinese translation of a Japanese song -- and showing that under every
/// lyric for an English listener only crowds the stage. A translation counts as readable when most of its lines are
/// written only in Latin or scripts their preferred languages use. Ported from the Mac's TranslationLanguages.swift.
/// </summary>
public static class TranslationLanguages
{
    public enum Script { Latin, Han, Kana, Hangul, Cyrillic, Greek, Arabic, Hebrew, Thai, Devanagari }

    /// <summary>The scripts a language tag ("en-AU", "zh-Hans-CN", "sr-Latn") is written in. An explicit script
    /// subtag wins.</summary>
    public static HashSet<Script> ScriptsOfLanguage(string tag)
    {
        var parts = tag.ToLowerInvariant().Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return [];
        foreach (var part in parts.Skip(1))
        {
            if (part.Length != 4) continue;
            switch (part)
            {
                case "latn": return [Script.Latin];
                case "cyrl": return [Script.Cyrillic];
                case "hans" or "hant" or "hani": return [Script.Han];
                case "jpan": return [Script.Han, Script.Kana];
                case "kore" or "hang": return [Script.Hangul];
                case "arab": return [Script.Arabic];
            }
        }
        return parts[0] switch
        {
            "zh" or "yue" or "wuu" or "nan" or "hak" => [Script.Han],
            "ja" => [Script.Han, Script.Kana],
            "ko" => [Script.Hangul],
            "ru" or "uk" or "be" or "bg" or "mk" or "sr" or "kk" or "ky" or "mn" or "tg" => [Script.Cyrillic],
            "el" => [Script.Greek],
            "ar" or "fa" or "ur" or "ps" => [Script.Arabic],
            "he" or "yi" => [Script.Hebrew],
            "th" => [Script.Thai],
            "hi" or "mr" or "ne" or "sa" => [Script.Devanagari],
            _ => [Script.Latin],
        };
    }

    /// <summary>The scripts of a text's letters. Letters in scripts not listed here are left out, so such a
    /// translation is never held back.</summary>
    public static HashSet<Script> ScriptsOf(string text)
    {
        var found = new HashSet<Script>();
        foreach (var scalar in text.EnumerateRunes())
        {
            if (!ReadingText.IsAlphabetic(scalar)) continue;
            switch (scalar.Value)
            {
                case >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A or >= 0xC0 and <= 0x24F or >= 0x1E00 and <= 0x1EFF: found.Add(Script.Latin); break;
                case 0x3005 or 0x3007 or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF:
                    found.Add(Script.Han); break;
                case >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F: found.Add(Script.Kana); break;
                case >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7AF: found.Add(Script.Hangul); break;
                case >= 0x400 and <= 0x52F: found.Add(Script.Cyrillic); break;
                case >= 0x370 and <= 0x3FF: found.Add(Script.Greek); break;
                case >= 0x600 and <= 0x6FF or >= 0x750 and <= 0x77F or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF: found.Add(Script.Arabic); break;
                case >= 0x590 and <= 0x5FF: found.Add(Script.Hebrew); break;
                case >= 0xE00 and <= 0xE7F: found.Add(Script.Thai); break;
                case >= 0x900 and <= 0x97F: found.Add(Script.Devanagari); break;
            }
        }
        return found;
    }

    /// <summary>Whether a listener with these preferred languages can read a translation whose lines are
    /// <paramref name="texts"/>: at least as many of them as not are written only in scripts the listener reads. Latin
    /// always counts: it is the one script nearly every reader shares. Lines with no letters don't count either way.</summary>
    public static bool Readable(IEnumerable<string> texts, IEnumerable<string> languages)
    {
        var known = new HashSet<Script> { Script.Latin };
        foreach (var language in languages) known.UnionWith(ScriptsOfLanguage(language));
        int readable = 0, unreadable = 0;
        foreach (var text in texts)
        {
            var used = ScriptsOf(text);
            if (used.Count == 0) continue;
            if (used.IsSubsetOf(known)) readable++;
            else unreadable++;
        }
        return readable >= unreadable;
    }
}
