// Word symbols and action words (the Mac's next/tester-fixes ad5a6a6 and cba108f, 15 Nov 2026, ported exactly): a small
// monochrome symbol beside a listed word ("love" a heart), and a short motion on a listed action word ("spin" turns,
// "fall" dips). English only, word-timed lines only. The symbol names are the Mac's SF Symbols; WordSymbolGlyphs maps
// each to the Segoe Fluent Icons glyph Windows draws.

using System.Text;

namespace Lunalyr.Core.Compositions;

/// <summary>What an action word does.</summary>
public enum WordAction { Spin, Float, Dip, Shake, Hop, Grow, Shrink, Dissolve, Flicker, Frost, Bleed, Dark, Shatter, Melt, Echo, Pulse, Wave, Sink }

/// <summary>
/// The accents one run takes: a symbol name (SF Symbol, as stored) and an action, either or both. <c>Gentle</c>: a
/// chorus repeat of an action word already heard (every second repeat) moves at <c>LivelyAction.GentleShare</c>.
/// </summary>
public readonly record struct RunAccent(string? Symbol, WordAction? Action, bool Gentle = false)
{
    public bool IsEmpty => Symbol is null && Action is null;
}

/// <summary>The two word lists, keyed by <see cref="WordAccents.EnglishKey"/>, in their display order.</summary>
public sealed class WordAccentLists : IEquatable<WordAccentLists>
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

    /// <summary>Lists are equal when they hold the same words with the same values, in the same order.</summary>
    public bool Equals(WordAccentLists? other) =>
        other is not null && (ReferenceEquals(this, other) || SymbolOrder.SequenceEqual(other.SymbolOrder) && ActionOrder.SequenceEqual(other.ActionOrder));

    public override bool Equals(object? obj) => Equals(obj as WordAccentLists);

    public override int GetHashCode() => HashCode.Combine(SymbolOrder.Count, ActionOrder.Count);
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

    /// <summary>
    /// The default actions, in the Mac's order (16 Nov 2026). Plurals, -ing and -ed fold through <see cref="EnglishForms"/>;
    /// irregular forms are listed. Vivid words the motion shows, not everyday ones ("love", "go", "night", "heart",
    /// "high", "rain" are left out: measured on a few hundred songs, they made action words constant).
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, WordAction>> DefaultActions = Pairs(
        ("spin", WordAction.Spin), ("twirl", WordAction.Spin), ("whirl", WordAction.Spin), ("whirlwind", WordAction.Spin),
        ("spiral", WordAction.Spin), ("tornado", WordAction.Spin), ("dizzy", WordAction.Spin), ("carousel", WordAction.Spin),
        ("revolve", WordAction.Spin),
        ("levitate", WordAction.Float), ("float", WordAction.Float), ("fly", WordAction.Float), ("flying", WordAction.Float),
        ("flew", WordAction.Float), ("soar", WordAction.Float), ("drift", WordAction.Float), ("heaven", WordAction.Float),
        ("clouds", WordAction.Float), ("weightless", WordAction.Float), ("wings", WordAction.Float), ("higher", WordAction.Float),
        ("fall", WordAction.Dip), ("falling", WordAction.Dip), ("fell", WordAction.Dip), ("drop", WordAction.Dip),
        ("dive", WordAction.Dip), ("tumble", WordAction.Dip), ("gravity", WordAction.Dip), ("plunge", WordAction.Dip),
        ("shake", WordAction.Shake), ("shook", WordAction.Shake), ("tremble", WordAction.Shake), ("shiver", WordAction.Shake),
        ("nervous", WordAction.Shake), ("quake", WordAction.Shake), ("earthquake", WordAction.Shake), ("scared", WordAction.Shake),
        ("rattle", WordAction.Shake),
        ("bounce", WordAction.Hop), ("jump", WordAction.Hop), ("leap", WordAction.Hop), ("hop", WordAction.Hop),
        ("dance", WordAction.Hop), ("dancing", WordAction.Hop),
        ("grow", WordAction.Grow), ("grew", WordAction.Grow), ("rise", WordAction.Grow), ("rising", WordAction.Grow),
        ("bigger", WordAction.Grow), ("bloom", WordAction.Grow), ("blossom", WordAction.Grow), ("swell", WordAction.Grow),
        ("shrink", WordAction.Shrink), ("smaller", WordAction.Shrink), ("tiny", WordAction.Shrink),
        ("fade", WordAction.Dissolve), ("disappear", WordAction.Dissolve), ("vanish", WordAction.Dissolve), ("gone", WordAction.Dissolve),
        ("dust", WordAction.Dissolve), ("ghost", WordAction.Dissolve), ("forget", WordAction.Dissolve), ("forgotten", WordAction.Dissolve),
        ("erase", WordAction.Dissolve), ("evaporate", WordAction.Dissolve), ("dissolve", WordAction.Dissolve),
        ("burn", WordAction.Flicker), ("burnt", WordAction.Flicker), ("fire", WordAction.Flicker), ("flame", WordAction.Flicker),
        ("blaze", WordAction.Flicker), ("ignite", WordAction.Flicker), ("spark", WordAction.Flicker), ("embers", WordAction.Flicker),
        ("smoke", WordAction.Flicker), ("heat", WordAction.Flicker),
        ("freeze", WordAction.Frost), ("frozen", WordAction.Frost), ("froze", WordAction.Frost), ("cold", WordAction.Frost),
        ("colder", WordAction.Frost), ("ice", WordAction.Frost), ("icy", WordAction.Frost), ("winter", WordAction.Frost),
        ("snow", WordAction.Frost), ("numb", WordAction.Frost), ("chill", WordAction.Frost), ("frost", WordAction.Frost),
        ("bleed", WordAction.Bleed), ("bled", WordAction.Bleed), ("blood", WordAction.Bleed), ("bloody", WordAction.Bleed),
        ("wound", WordAction.Bleed), ("scar", WordAction.Bleed), ("veins", WordAction.Bleed),
        ("dark", WordAction.Dark), ("darker", WordAction.Dark), ("darkest", WordAction.Dark), ("darkness", WordAction.Dark),
        ("shadow", WordAction.Dark), ("midnight", WordAction.Dark), ("black", WordAction.Dark), ("blackout", WordAction.Dark),
        ("void", WordAction.Dark),
        ("break", WordAction.Shatter), ("broke", WordAction.Shatter), ("broken", WordAction.Shatter), ("shatter", WordAction.Shatter),
        ("crack", WordAction.Shatter), ("pieces", WordAction.Shatter), ("crash", WordAction.Shatter), ("smash", WordAction.Shatter),
        ("heartbreak", WordAction.Shatter), ("apart", WordAction.Shatter),
        ("melt", WordAction.Melt), ("drip", WordAction.Melt), ("meltdown", WordAction.Melt), ("thaw", WordAction.Melt),
        ("liquid", WordAction.Melt), ("wax", WordAction.Melt),
        ("echo", WordAction.Echo), ("repeat", WordAction.Echo), ("resonate", WordAction.Echo), ("voices", WordAction.Echo),
        ("calling", WordAction.Echo),
        ("heartbeat", WordAction.Pulse), ("pulse", WordAction.Pulse), ("beat", WordAction.Pulse), ("alive", WordAction.Pulse),
        ("pounding", WordAction.Pulse), ("racing", WordAction.Pulse), ("drum", WordAction.Pulse), ("drums", WordAction.Pulse),
        ("rhythm", WordAction.Pulse),
        ("wave", WordAction.Wave), ("ocean", WordAction.Wave), ("sea", WordAction.Wave), ("tide", WordAction.Wave),
        ("river", WordAction.Wave), ("surf", WordAction.Wave), ("shore", WordAction.Wave), ("flood", WordAction.Wave),
        ("sway", WordAction.Wave), ("water", WordAction.Wave),
        ("sink", WordAction.Sink), ("sank", WordAction.Sink), ("sunk", WordAction.Sink), ("drown", WordAction.Sink),
        ("deep", WordAction.Sink), ("deeper", WordAction.Sink), ("underwater", WordAction.Sink), ("abyss", WordAction.Sink),
        ("anchor", WordAction.Sink), ("quicksand", WordAction.Sink));

    /// <summary>The stored name of an action (its raw value: "spin", "float", …).</summary>
    public static string Name(this WordAction action) => action switch
    {
        WordAction.Spin => "spin", WordAction.Float => "float", WordAction.Dip => "dip", WordAction.Shake => "shake",
        WordAction.Hop => "hop", WordAction.Grow => "grow", WordAction.Shrink => "shrink", WordAction.Dissolve => "dissolve",
        WordAction.Flicker => "flicker", WordAction.Frost => "frost", WordAction.Bleed => "bleed", WordAction.Dark => "dark",
        WordAction.Shatter => "shatter", WordAction.Melt => "melt", WordAction.Echo => "echo", WordAction.Pulse => "pulse",
        WordAction.Wave => "wave", _ => "sink",
    };

    /// <summary>The action's title in the list editor (the Mac's <c>WordAction.title</c>).</summary>
    public static string Title(this WordAction action) => action switch
    {
        WordAction.Spin => "Spin", WordAction.Float => "Float", WordAction.Dip => "Dip", WordAction.Shake => "Shake",
        WordAction.Hop => "Hop", WordAction.Grow => "Grow", WordAction.Shrink => "Shrink", WordAction.Dissolve => "Dissolve",
        WordAction.Flicker => "Burn", WordAction.Frost => "Freeze", WordAction.Bleed => "Bleed", WordAction.Dark => "Dark",
        WordAction.Shatter => "Shatter", WordAction.Melt => "Melt", WordAction.Echo => "Echo", WordAction.Pulse => "Heartbeat",
        WordAction.Wave => "Wave", _ => "Sink",
    };

    public static readonly IReadOnlyList<WordAction> AllActions = Enum.GetValues<WordAction>();

    /// <summary>
    /// An action as a word list stores it (the Mac's <c>WordAction(listValue:)</c>): its raw value, or, for a list saved by
    /// hand or by an older build, its title or a former name. Unknown names and actions no longer offered (explode,
    /// scream, whisper, ghost, glow, electric) give null, so the word is skipped rather than the list rewritten.
    /// </summary>
    public static WordAction? ParseAction(string? name)
    {
        var trimmed = name?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed)) return null;
        foreach (var action in AllActions)
            if (action.Name() == trimmed) return action;
        return trimmed switch
        {
            "freeze" or "ice" => WordAction.Frost,
            "warm flicker" or "burn" or "fire" => WordAction.Flicker,
            "heartbeat" => WordAction.Pulse,
            "fade" or "vanish" => WordAction.Dissolve,
            "fall" => WordAction.Dip,
            "jump" or "bounce" => WordAction.Hop,
            "drown" => WordAction.Sink,
            "break" => WordAction.Shatter,
            "blood" => WordAction.Bleed,
            "darkness" => WordAction.Dark,
            "turn" or "rotate" => WordAction.Spin,
            "fly" => WordAction.Float,
            "tremble" => WordAction.Shake,
            "ocean" => WordAction.Wave,
            _ => null,
        };
    }

    /// <summary>
    /// An action word as its list keeps it (the Mac's <c>WordAccentsSection.actionKey</c>): English as matched, any other
    /// language folded (accents and case), CJK as written. Null when nothing is left.
    /// </summary>
    public static string? ActionKey(string? word)
    {
        if (EnglishKey(word) is { } english) return english;
        var folded = ActionLexicon.Fold(word);
        return folded.Length == 0 ? null : folded;
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

    /// <summary>The action <paramref name="word"/> takes on its own (its line's script read from the word itself).</summary>
    public static WordAction? ActionFor(string word, WordAccentLists lists) =>
        ActionLexicon.Match(word, lists.Actions, new ActionLexicon.Script(word))?.Action;

    /// <summary>
    /// One line's accents, planned once in reading order: at most two symbols (a picture already in the line is not
    /// repeated) and one action (the first action word, from the user's list or the built-in lists for the line's
    /// script). A run may take both. <paramref name="runs"/> are the runs' texts, a glued run's tokens joined.
    /// <paramref name="gentle"/>: the line is a chorus repeat (<see cref="GentleLines"/>), so its action moves gently.
    /// </summary>
    public static RunAccent[] Plan(IReadOnlyList<string> runs, bool symbols, bool actions, WordAccentLists lists, bool gentle = false)
    {
        var plan = new RunAccent[runs.Count];
        if (!symbols && !actions) return plan;
        var shown = new List<string>(MaxSymbolsPerLine);
        var actionsLeft = MaxActionsPerLine;
        var script = actions ? new ActionLexicon.Script(string.Join(' ', runs)) : default;
        for (var i = 0; i < runs.Count; i++)
        {
            string? symbol = null;
            WordAction? action = null;
            // A picture already in the line is not shown again: on Windows by its glyph, since some symbol names share
            // one (moon and night, cloud and rain), so a line never shows the same picture twice.
            if (symbols && shown.Count < MaxSymbolsPerLine && SymbolFor(runs[i], lists) is { } found
                && !shown.Contains(WordSymbolGlyphs.Glyph(found) ?? found))
            {
                symbol = found;
                shown.Add(WordSymbolGlyphs.Glyph(found) ?? found);
            }
            if (actions && actionsLeft > 0 && ActionLexicon.Match(runs[i], lists.Actions, script) is { } matched)
            {
                action = matched.Action;
                actionsLeft--;
            }
            plan[i] = new RunAccent(symbol, action, action is not null && gentle);
        }
        return plan;
    }

    /// <summary>The list key of the line's action word, if any (the same matching as <see cref="Plan"/>).</summary>
    public static string? ActionKeyOf(IReadOnlyList<string> runs, WordAccentLists lists)
    {
        var script = new ActionLexicon.Script(string.Join(' ', runs));
        foreach (var run in runs)
        {
            if (string.IsNullOrWhiteSpace(run)) continue;
            if (ActionLexicon.Match(run, lists.Actions, script) is { } found) return found.Key;
        }
        return null;
    }

    /// <summary>
    /// Which lines of a song move gently (the Mac's <c>WordAccentPlan.gentleLines</c>): an action word acts in full the
    /// first time it is sung, and its later repeats alternate gentle and full (2nd gentle, 3rd full, 4th gentle…), so a
    /// chorus does not tire. A pure function of the song's lines (each line's lead runs, backing vocals already lifted
    /// out; null for a line without words) and the lists, so seeking and layout changes always agree.
    /// </summary>
    public static HashSet<int> GentleLines(IReadOnlyList<IReadOnlyList<string>?> lines, WordAccentLists lists)
    {
        var heard = new Dictionary<string, int>(StringComparer.Ordinal);
        var gentle = new HashSet<int>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index] is not { Count: > 0 } runs || ActionKeyOf(runs, lists) is not { } key) continue;
            heard.TryGetValue(key, out var count);
            if (count % 2 != 0) gentle.Add(index);
            heard[key] = count + 1;
        }
        return gentle;
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
        // Keys: English words as EnglishKey, others folded (ActionLexicon.Fold). Values by raw value, title or former name;
        // a word whose action is no longer offered is skipped, and the stored list itself is left alone.
        foreach (var (word, name) in Ordered(stored, defaults))
            if (ActionKey(word) is { } key && ParseAction(name) is { } action && result.All(pair => pair.Key != key))
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
