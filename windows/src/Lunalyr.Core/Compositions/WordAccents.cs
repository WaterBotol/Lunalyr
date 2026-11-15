// Word symbols and action words (the Mac's next/tester-fixes ad5a6a6 and cba108f, 15 Nov 2026, ported exactly): a small
// monochrome symbol beside a listed word ("love" a heart), and a short motion on a listed action word ("spin" turns,
// "fall" dips). English only, word-timed lines only. The symbol names are the Mac's SF Symbols; WordSymbolGlyphs maps
// each to the Segoe Fluent Icons glyph Windows draws.

using System.Text;

namespace Lunalyr.Core.Compositions;

/// <summary>What an action word does.</summary>
public enum WordAction { Spin, Float, Dip, Shake, Hop, Grow, Shrink, Dissolve, Flicker, Frost }

/// <summary>The accents one run takes: a symbol name (SF Symbol, as stored) and an action, either or both.</summary>
public readonly record struct RunAccent(string? Symbol, WordAction? Action)
{
    public bool IsEmpty => Symbol is null && Action is null;
}

/// <summary>The two word lists, keyed by <see cref="WordAccents.EnglishKey"/>, in their display order.</summary>
public sealed class WordAccentLists
{
    public WordAccentLists(IReadOnlyList<KeyValuePair<string, string>> symbols, IReadOnlyList<KeyValuePair<string, WordAction>> actions)
    {
        SymbolOrder = symbols;
        ActionOrder = actions;
        foreach (var (word, symbol) in symbols) Symbols.TryAdd(word, symbol);
        foreach (var (word, action) in actions) Actions.TryAdd(word, action);
    }

    public Dictionary<string, string> Symbols { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, WordAction> Actions { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<KeyValuePair<string, string>> SymbolOrder { get; }
    public IReadOnlyList<KeyValuePair<string, WordAction>> ActionOrder { get; }

    public static WordAccentLists Defaults { get; } = new(WordAccents.DefaultSymbols, WordAccents.DefaultActions);
}

public static class WordAccents
{
    public const int MaxSymbolsPerLine = 2;
    public const int MaxActionsPerLine = 1;

    /// <summary>The default symbols, in the Mac's order.</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> DefaultSymbols = Pairs(
        ("love", "heart.fill"), ("loved", "heart.fill"), ("loving", "heart.fill"),
        ("heart", "heart"),
        ("broken", "heart.slash"), ("heartbreak", "heart.slash"),
        ("fire", "flame"), ("burn", "flame"), ("burning", "flame"),
        ("star", "star"), ("stars", "star"),
        ("moon", "moon"),
        ("sun", "sun.max"), ("sunshine", "sun.max"),
        ("rain", "cloud.rain"), ("raining", "cloud.rain"),
        ("night", "moon.stars"), ("midnight", "moon.stars"),
        ("dance", "figure.dance"), ("dancing", "figure.dance"),
        ("cry", "drop"), ("tears", "drop"), ("crying", "drop"),
        ("money", "dollarsign"), ("cash", "dollarsign"),
        ("crown", "crown"), ("king", "crown"), ("queen", "crown"),
        ("him", "person"), ("her", "person"),
        ("home", "house"),
        ("time", "clock"),
        ("music", "music.note"), ("song", "music.note"),
        ("sky", "cloud"),
        ("world", "globe"),
        ("call", "phone"), ("phone", "phone"));

    /// <summary>The default actions, in the Mac's order.</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, WordAction>> DefaultActions = Pairs(
        ("spin", WordAction.Spin),
        ("levitate", WordAction.Float), ("float", WordAction.Float), ("fly", WordAction.Float), ("flying", WordAction.Float),
        ("fall", WordAction.Dip), ("falling", WordAction.Dip), ("drop", WordAction.Dip),
        ("shake", WordAction.Shake), ("tremble", WordAction.Shake), ("shiver", WordAction.Shake),
        ("bounce", WordAction.Hop), ("jump", WordAction.Hop),
        ("grow", WordAction.Grow), ("rise", WordAction.Grow), ("rising", WordAction.Grow),
        ("shrink", WordAction.Shrink),
        ("fade", WordAction.Dissolve), ("disappear", WordAction.Dissolve), ("vanish", WordAction.Dissolve),
        ("burn", WordAction.Flicker), ("fire", WordAction.Flicker),
        ("freeze", WordAction.Frost), ("frozen", WordAction.Frost), ("cold", WordAction.Frost));

    /// <summary>The stored name of an action ("spin", "float", …).</summary>
    public static string Name(this WordAction action) => action switch
    {
        WordAction.Spin => "spin", WordAction.Float => "float", WordAction.Dip => "dip", WordAction.Shake => "shake",
        WordAction.Hop => "hop", WordAction.Grow => "grow", WordAction.Shrink => "shrink", WordAction.Dissolve => "dissolve",
        WordAction.Flicker => "flicker", _ => "frost",
    };

    public static readonly IReadOnlyList<WordAction> AllActions =
        [WordAction.Spin, WordAction.Float, WordAction.Dip, WordAction.Shake, WordAction.Hop, WordAction.Grow,
         WordAction.Shrink, WordAction.Dissolve, WordAction.Flicker, WordAction.Frost];

    public static WordAction? ParseAction(string? name)
    {
        var trimmed = name?.Trim().ToLowerInvariant();
        foreach (var action in AllActions)
            if (action.Name() == trimmed) return action;
        return null;
    }

    /// <summary>
    /// The word lowercased with only a–z and 0–9 kept; null when it has any other letter (accented Latin, CJK…) or
    /// nothing is left.
    /// </summary>
    public static string? EnglishKey(string? word)
    {
        if (string.IsNullOrEmpty(word)) return null;
        var kept = new StringBuilder(word.Length);
        foreach (var rune in word.EnumerateRunes())
        {
            if (rune.Value is >= 'A' and <= 'Z') kept.Append((char)(rune.Value + 32));
            else if (rune.Value is >= 'a' and <= 'z' or >= '0' and <= '9') kept.Append((char)rune.Value);
            else if (Rune.IsLetter(rune)) return null;
        }
        return kept.Length == 0 ? null : kept.ToString();
    }

    /// <summary>
    /// The forms <paramref name="word"/> is looked up by, most literal first (the Mac's englishForms): the key, then
    /// -ies/-ied → y, -es, -s, and the stems of -ing, -ed and -in'. Every added form is at least 3 letters.
    /// </summary>
    public static List<string> EnglishForms(string? word)
    {
        var forms = new List<string>(6);
        if (EnglishKey(word) is not { } key) return forms;
        forms.Add(key);
        void Add(string form)
        {
            if (form.Length >= 3 && !forms.Contains(form)) forms.Add(form);
        }
        var length = key.Length;
        if ((key.EndsWith("ies", StringComparison.Ordinal) || key.EndsWith("ied", StringComparison.Ordinal)) && length > 4)
            Add(key[..^3] + "y");
        if (key.EndsWith("es", StringComparison.Ordinal) && length > 4) Add(key[..^2]);
        if (key.EndsWith('s') && !key.EndsWith("ss", StringComparison.Ordinal) && length > 3) Add(key[..^1]);
        var raw = word!.TrimEnd();
        string? stem = key.EndsWith("ing", StringComparison.Ordinal) ? key[..^3]
            : key.EndsWith("ed", StringComparison.Ordinal) ? key[..^2]
            : (raw.EndsWith("in'", StringComparison.OrdinalIgnoreCase) || raw.EndsWith("in’", StringComparison.OrdinalIgnoreCase))
              && key.EndsWith("in", StringComparison.Ordinal) ? key[..^2]
            : null;
        if (stem is { Length: >= 3 })
        {
            if (stem[^1] == stem[^2] && IsConsonant(stem[^1]) && stem[^1] is not ('l' or 's' or 'z')) Add(stem[..^1]);
            if (!(Syllables(stem) == 1 && EndsConsonantVowelConsonant(stem) && stem[^1] is not ('w' or 'x' or 'y'))) Add(stem);
            Add(stem + "e");
        }
        return forms;
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';
    private static bool IsConsonant(char c) => c is >= 'a' and <= 'z' && !IsVowel(c);

    private static int Syllables(string text)
    {
        var count = 0;
        var inVowel = false;
        foreach (var c in text)
        {
            var vowel = IsVowel(c);
            if (vowel && !inVowel) count++;
            inVowel = vowel;
        }
        return count;
    }

    private static bool EndsConsonantVowelConsonant(string text) =>
        text.Length >= 3 && IsConsonant(text[^3]) && IsVowel(text[^2]) && IsConsonant(text[^1]);

    /// <summary>The symbol <paramref name="word"/> takes from <paramref name="lists"/>: its first form that is listed.</summary>
    public static string? SymbolFor(string word, WordAccentLists lists)
    {
        foreach (var form in EnglishForms(word))
            if (lists.Symbols.TryGetValue(form, out var symbol)) return symbol;
        return null;
    }

    public static WordAction? ActionFor(string word, WordAccentLists lists)
    {
        foreach (var form in EnglishForms(word))
            if (lists.Actions.TryGetValue(form, out var action)) return action;
        return null;
    }

    /// <summary>
    /// One line's accents, planned once in reading order: at most two symbols (a symbol already in the line is not
    /// repeated) and one action (the first listed action word). A run may take both. <paramref name="runs"/> are the
    /// runs' texts, a glued run's tokens joined.
    /// </summary>
    public static RunAccent[] Plan(IReadOnlyList<string> runs, bool symbols, bool actions, WordAccentLists lists)
    {
        var plan = new RunAccent[runs.Count];
        if (!symbols && !actions) return plan;
        var shown = new List<string>(MaxSymbolsPerLine);
        var actionsLeft = MaxActionsPerLine;
        for (var i = 0; i < runs.Count; i++)
        {
            string? symbol = null;
            WordAction? action = null;
            if (symbols && shown.Count < MaxSymbolsPerLine && SymbolFor(runs[i], lists) is { } found && !shown.Contains(found))
            {
                symbol = found;
                shown.Add(found);
            }
            if (actions && actionsLeft > 0 && ActionFor(runs[i], lists) is { } act)
            {
                action = act;
                actionsLeft--;
            }
            plan[i] = new RunAccent(symbol, action);
        }
        return plan;
    }

    /// <summary>
    /// A stored symbol list made usable: each key folded, entries whose key folds to nothing or whose symbol Windows
    /// cannot draw dropped. Null (no stored list) is the defaults.
    /// </summary>
    public static List<KeyValuePair<string, string>> CleanSymbols(IReadOnlyDictionary<string, string>? stored)
    {
        if (stored is null) return [.. DefaultSymbols];
        var result = new List<KeyValuePair<string, string>>();
        foreach (var (word, symbol) in Ordered(stored, DefaultSymbols))
            if (EnglishKey(word) is { } key && WordSymbolGlyphs.Glyph(symbol) is not null && result.All(pair => pair.Key != key))
                result.Add(new(key, symbol.Trim()));
        return result;
    }

    public static List<KeyValuePair<string, WordAction>> CleanActions(IReadOnlyDictionary<string, string>? stored)
    {
        if (stored is null) return [.. DefaultActions];
        var result = new List<KeyValuePair<string, WordAction>>();
        var defaults = DefaultActions.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value.Name())).ToList();
        foreach (var (word, name) in Ordered(stored, defaults))
            if (EnglishKey(word) is { } key && ParseAction(name) is { } action && result.All(pair => pair.Key != key))
                result.Add(new(key, action));
        return result;
    }

    /// <summary>A list's display order: the default list's words first (in its order), then the user's own A to Z.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Ordered(IReadOnlyDictionary<string, string> stored,
                                                                  IReadOnlyList<KeyValuePair<string, string>> defaults)
    {
        var defaultOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < defaults.Count; i++) defaultOrder.TryAdd(defaults[i].Key, i);
        return stored
            .OrderBy(pair => EnglishKey(pair.Key) is { } key && defaultOrder.TryGetValue(key, out var at) ? at : int.MaxValue)
            .ThenBy(pair => EnglishKey(pair.Key) ?? pair.Key, StringComparer.Ordinal);
    }

    private static KeyValuePair<string, T>[] Pairs<T>(params (string Word, T Value)[] pairs) =>
        pairs.Select(pair => new KeyValuePair<string, T>(pair.Word, pair.Value)).ToArray();
}

/// <summary>
/// The Segoe Fluent Icons glyph Windows draws for each word symbol (the Mac's SF Symbol names), from the Mac's hand-off
/// table. "dollarsign" is a "$" set in the lyric font. A name with no glyph draws nothing and takes no room.
/// </summary>
public static class WordSymbolGlyphs
{
    /// <summary>Set in the lyric font rather than the icon font.</summary>
    public const string Dollar = "$";

    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.Ordinal)
    {
        ["heart.fill"] = "",
        ["heart"] = "",
        ["heart.slash"] = "",
        // The Mac's flame: Segoe Fluent Icons has one (U+ECAD, as Portal uses), so no stand-in is needed.
        ["flame"] = "",
        ["star"] = "",
        ["moon"] = "",
        ["sun.max"] = "",
        ["cloud.rain"] = "",
        ["moon.stars"] = "",
        ["figure.dance"] = "",
        ["drop"] = "",
        ["dollarsign"] = Dollar,
        ["crown"] = "",
        ["person"] = "",
        ["house"] = "",
        ["clock"] = "",
        ["music.note"] = "",
        ["cloud"] = "",
        ["globe"] = "",
        ["phone"] = "",
    };

    /// <summary>The glyph for <paramref name="symbol"/>, or null when Windows has none for it.</summary>
    public static string? Glyph(string? symbol) => symbol is not null && Glyphs.TryGetValue(symbol.Trim(), out var glyph) ? glyph : null;

    /// <summary>The symbol names Windows can draw (for the list editor's suggestions).</summary>
    public static IEnumerable<string> Names => Glyphs.Keys;
}
