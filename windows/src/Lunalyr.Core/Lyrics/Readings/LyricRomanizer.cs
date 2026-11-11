using System.Text;

namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>Where readings are shown.</summary>
public enum RomanizationDisplay { Off, Below, Replace }

/// <summary>Which language Han characters are read in, or none forced.</summary>
public enum RomanizationLanguage { Automatic, Japanese, Mandarin, Cantonese, Korean, Other }

/// <summary>
/// How readings are made and shown: under the lyric or instead of it, which language Han is read as, pinyin tone
/// marks, and whether a reading that came with the lyrics wins over a generated one. Stored values are the Mac's
/// ("off", "below", "replace"; "automatic" … "other") so settings compare across the apps. (The Mac's options class
/// carries Verso-attributed lines; this is written from its behaviour, not translated.)
/// </summary>
public sealed record RomanizationOptions(
    RomanizationDisplay Display = RomanizationDisplay.Below,
    RomanizationLanguage Language = RomanizationLanguage.Automatic,
    bool ToneMarks = true,
    bool PreferSaved = true)
{
    public static RomanizationOptions Default { get; } = new();

    public static string StoredValue(RomanizationDisplay display) => display switch
    {
        RomanizationDisplay.Off => "off",
        RomanizationDisplay.Replace => "replace",
        _ => "below",
    };

    public static string StoredValue(RomanizationLanguage language) => language switch
    {
        RomanizationLanguage.Japanese => "japanese",
        RomanizationLanguage.Mandarin => "mandarin",
        RomanizationLanguage.Cantonese => "cantonese",
        RomanizationLanguage.Korean => "korean",
        RomanizationLanguage.Other => "other",
        _ => "automatic",
    };

    /// <summary>The display a stored value names, or null for an unknown one.</summary>
    public static RomanizationDisplay? ParseDisplay(string? value) =>
        Enum.GetValues<RomanizationDisplay>().Select(d => (RomanizationDisplay?)d).FirstOrDefault(d => StoredValue(d!.Value) == value);

    /// <summary>The language a stored value names, or null for an unknown one.</summary>
    public static RomanizationLanguage? ParseLanguage(string? value) =>
        Enum.GetValues<RomanizationLanguage>().Select(l => (RomanizationLanguage?)l).FirstOrDefault(l => StoredValue(l!.Value) == value);
}

/// <summary>
/// Readings (romanisation) of lyric lines, made on the device with no online calls. Saved readings (from the
/// lyric source or a .roma.lrc file) can supply Jyutping too.
///
/// Readings are worked out per line, never per frame: Japanese and Korean need the whole line (readings and sound
/// changes cross word boundaries), so the line is read once and the result is laid back onto its words by range.
/// Ported from the Mac's LyricRomanization.swift; its outer loop and dispatch are written from their behaviour
/// (Verso-attributed lines), the rest translated.
/// </summary>
public sealed class LyricRomanizer(ReadingServices services)
{
    public ReadingServices Services { get; } = services;

    /// <summary>
    /// Readings by line index. An entry holds one reading per word of <c>line.Words</c>. A reading's time is that of
    /// the phrase it reads: a phrase written across several words (今日 kyou) gives them all its time, and on a line
    /// timed one character at a time each character carries its share of it (kyo, u); otherwise the first word
    /// carries the phrase and the rest an empty text. When a saved reading cannot be split by word, the entry is a
    /// single reading spanning the line, drawn once under the whole line (see <see cref="LineReading"/>). With
    /// "Replace lyrics" an entry is the words to show in place of the lyric.
    /// </summary>
    public Dictionary<int, List<LyricWord>> Readings(IReadOnlyList<LyricLine> lines, RomanizationOptions options,
        CancellationToken cancellation = default)
    {
        var result = new Dictionary<int, List<LyricWord>>();
        if (options.Display == RomanizationDisplay.Off) return result;
        var song = SongLanguage(lines);
        var memo = new Memo(this);
        for (var index = 0; index < lines.Count && !cancellation.IsCancellationRequested; index++)
        {
            var line = lines[index];
            if (line.IsInstrumental || line.Words.Count == 0) continue;
            var placement = PlacementFor(line, LanguageOf(line.Text, song, options.Language), options, memo);
            if (placement?.Words(line, options.Display, options.ToneMarks, Services.WordBreaker) is { } words) result[index] = words;
        }
        return result;
    }

    /// <summary>A saved reading comes first when preferred; one that does not fit gives way to the generated
    /// reading; and a saved reading is the only one for lines nothing can be generated for (Cantonese), kept whole
    /// when it cannot be split by word.</summary>
    private static Placement? PlacementFor(LyricLine line, RomanizationLanguage language, RomanizationOptions options, Memo memo)
    {
        var text = new LineText(line);
        var saved = line.Romanization?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        var placement = saved is not null && options.PreferSaved ? memo.Saved(saved, text, language) : null;
        placement ??= memo.Generated(text, language);
        if (placement is null && saved is not null) placement = memo.Saved(saved, text, language) ?? new Placement.Whole(saved);
        return placement;
    }

    /// <summary>For "Below lyrics": the line's reading written whole, or null when its words' readings already say
    /// it. That is a saved reading that could not be split by word, which is drawn once under the whole line
    /// (<see cref="LyricReadingPairs.Line"/>), and a line timed one character at a time whose phrases are shared out
    /// over their characters (kyo, u, mo becomes "kyou mo").</summary>
    public static string? LineReading(IReadOnlyList<LyricWord>? readings, LyricLine line)
    {
        if (readings is null || readings.Count == 0 || line.Words.Count == 0) return null;
        if (readings.Count != line.Words.Count)
        {
            var whole = string.Join(" ", readings.Select(reading => reading.Text).Where(text => text.Length > 0));
            return whole.Length == 0 || whole == line.Text ? null : whole;
        }
        if (!IsCharacterTimed(line)) return null;
        // The parts of one phrase share its time.
        var phrases = new List<string>();
        var spread = false;
        (double Start, double End)? previous = null;
        foreach (var reading in readings)
        {
            if (previous is { } last && last.Start == reading.Start && last.End == reading.End)
            {
                phrases[^1] += reading.Text;
                spread = true;
            }
            else
            {
                phrases.Add(reading.Text);
            }
            previous = (reading.Start, reading.End);
        }
        var text = string.Join(" ", phrases.Where(phrase => phrase.Length > 0));
        return spread && text.Length > 0 ? text : null;
    }

    /// <summary>Most sung words of the line are a single character (今, 日, も).</summary>
    public static bool IsCharacterTimed(LyricLine line)
    {
        var sung = line.Words.Select(word => ReadingText.LettersAndNumbers(word.Text)).Where(text => text.Length > 0).ToList();
        if (sung.Count < 2) return false;
        return sung.Count(text => ReadingText.GraphemeCount(text) == 1) * 4 >= sung.Count * 3;
    }

    /// <summary>
    /// <paramref name="text"/> read aloud in Latin letters. Text with no letters outside the Latin script is
    /// returned as it is. Automatic and Other read by script (kana Japanese, Hangul Korean, Han by its look); a
    /// chosen language decides how Han is read. Cantonese has no generated reading, so the text comes back
    /// unchanged. (Written from the Mac's behaviour: Verso-attributed lines.)
    /// </summary>
    public string Romanize(string text, RomanizationLanguage language, bool toneMarks)
    {
        if (!ContainsNonLatin(text)) return text;
        var resolved = language is RomanizationLanguage.Automatic or RomanizationLanguage.Other
            ? LanguageOf(text, RomanizationLanguage.Automatic, language)
            : language;
        string? reading = resolved switch
        {
            RomanizationLanguage.Japanese => new JapaneseReading(Services).Romanize(text),
            RomanizationLanguage.Korean => KoreanRomanizer.Romanize(text),
            RomanizationLanguage.Mandarin => Pinyin(text),
            RomanizationLanguage.Cantonese => null,
            _ => Services.ToLatin(text) ?? text,
        };
        return reading is null ? text : Normalized(reading, toneMarks);
    }

    /// <summary>Any letter outside the Latin script (the regex class <c>[\p{L}&amp;&amp;\P{Latin}]</c>), so every
    /// script gets at least the system's romanisation.</summary>
    public static bool ContainsNonLatin(string text) => LyricScript.HasNonLatinLetters(text);

    // MARK: - Language

    /// <summary>The song's language, from how many of its lines use each script, so a Japanese phrase in a Mandarin
    /// song does not make the song Japanese. Automatic means a mixed song, whose Han lines are judged one by one.</summary>
    public RomanizationLanguage SongLanguage(IReadOnlyList<LyricLine> lines)
    {
        int kana = 0, hangul = 0, han = 0;
        var hanText = new List<string>();
        foreach (var line in lines)
        {
            if (line.IsInstrumental) continue;
            if (ReadingText.HasKana(line.Text)) kana++;
            else if (KoreanRomanizer.ContainsHangul(line.Text)) hangul++;
            else if (ReadingText.HasHan(line.Text)) { han++; hanText.Add(line.Text); }
        }
        var total = kana + hangul + han;
        if (total == 0) return RomanizationLanguage.Other;
        if (hangul >= kana && hangul * 2 >= total) return RomanizationLanguage.Korean;
        if (kana * 2 >= total) return RomanizationLanguage.Japanese;
        if (han * 4 < total * 3) return RomanizationLanguage.Automatic;
        var text = string.Join("\n", hanText);
        // Written Cantonese through the song, not one Cantonese line in a Mandarin song, which is judged on its own.
        var marked = hanText.Count(line => CantoneseScore(line) >= 1);
        if (CantoneseScore(text) >= 2 && (marked >= 2 || marked * 4 >= hanText.Count)) return RomanizationLanguage.Cantonese;
        return IsJapanese(Prefix(text, 1200)) ? RomanizationLanguage.Japanese : RomanizationLanguage.Mandarin;
    }

    /// <summary>A line's language. Kana and Hangul settle it; the chosen language, else the song's, decides how Han
    /// characters are read.</summary>
    public RomanizationLanguage LanguageOf(string text, RomanizationLanguage song, RomanizationLanguage chosen)
    {
        if (chosen == RomanizationLanguage.Other) return RomanizationLanguage.Other;
        if (ReadingText.HasKana(text)) return RomanizationLanguage.Japanese;
        if (KoreanRomanizer.ContainsHangul(text)) return RomanizationLanguage.Korean;
        if (!ReadingText.HasHan(text)) return RomanizationLanguage.Other;
        if (chosen != RomanizationLanguage.Automatic) return chosen;
        switch (song)
        {
            case RomanizationLanguage.Japanese or RomanizationLanguage.Korean or RomanizationLanguage.Cantonese:
                return song;
            default:
                // Written Cantonese in an otherwise Mandarin song still never gets Mandarin pinyin.
                if (CantoneseScore(text) >= 1) return RomanizationLanguage.Cantonese;
                if (song == RomanizationLanguage.Mandarin) return RomanizationLanguage.Mandarin;
                return IsJapanese(text) ? RomanizationLanguage.Japanese : RomanizationLanguage.Mandarin;
        }
    }

    private bool IsJapanese(string text) =>
        Services.LanguageGuesser.DominantLanguage(text) is { } language
        && (language == "ja" || language.StartsWith("ja-", StringComparison.OrdinalIgnoreCase));

    private static string Prefix(string text, int length)
    {
        if (text.Length <= length) return text;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    /// <summary>Characters only written Cantonese uses.</summary>
    private static readonly HashSet<string> CantoneseCharacters =
        [.. ReadingText.Graphemes("佢哋嘅咗喺嘢冇啲嚟嗰噉攞啱搵揾諗谂冚嘥瞓㗎喎嚿攰冧啩噃咁").Select(g => g.Text)];

    /// <summary>Also found in Mandarin text (關係, 俾斯麥, 咩咩, a humming 唔), so they only add to other evidence.</summary>
    private static readonly HashSet<string> CantoneseHints = [.. ReadingText.Graphemes("係乜睇俾咩唔").Select(g => g.Text)];

    /// <summary>Colloquial words, which count only beside a Cantonese-only character: on their own they also turn up
    /// across Mandarin words (一點解釋, 身邊度過, 美好彩虹, 鋼琴日記).</summary>
    private static readonly HashSet<string> CantoneseWords =
    [
        "係咪", "點解", "点解", "乜嘢", "邊個", "边个", "而家", "依家", "鍾意", "钟意",
        "琴日", "聽日", "听日", "屋企", "點樣", "点样", "邊度", "边度", "呢度", "嗰度", "咁樣", "好彩",
    ];

    /// <summary>Evidence of written Cantonese: one point for each distinct Cantonese-only character, for 唔 before a
    /// word (唔知, 唔好), and for each colloquial word beside them, and half a point for each hint. Without a
    /// Cantonese-only character the score stays below 1. One point marks a line; two, spread over the song, mark the
    /// whole song.</summary>
    public static double CantoneseScore(string text)
    {
        var characters = ReadingText.Graphemes(text).Select(g => g.Text).ToList();
        var strong = new HashSet<string>();
        var hints = new HashSet<string>();
        var words = new HashSet<string>();
        for (var index = 0; index < characters.Count; index++)
        {
            var character = characters[index];
            var next = index + 1 < characters.Count ? characters[index + 1] : null;
            if (CantoneseCharacters.Contains(character))
            {
                strong.Add(character);
            }
            else if (character == "唔" && next is not null && next != "唔" && ReadingText.IsIdeographic(System.Text.Rune.GetRuneAt(next, 0)))
            {
                strong.Add(character + next);
            }
            else if (CantoneseHints.Contains(character))
            {
                hints.Add(character);
            }
            if (next is not null && CantoneseWords.Contains(character + next)) words.Add(character + next);
        }
        if (strong.Count == 0) return Math.Min(0.5, hints.Count / 2.0);
        return strong.Count + words.Count + hints.Count / 2.0;
    }

    // MARK: - Generated readings

    /// <summary>Analyses and placements worked out once per distinct line in a pass, which also keeps one tokenizer
    /// for all its lines.</summary>
    private sealed class Memo(LyricRomanizer romanizer)
    {
        private JapaneseReading? _reader;
        private readonly Dictionary<string, JapaneseAlignment.Analysis> _analyses = [];
        private readonly Dictionary<string, Placement?> _generated = [];
        private readonly Dictionary<string, Placement?> _saved = [];

        public JapaneseAlignment.Analysis Analysis(LineText text)
        {
            if (_analyses.TryGetValue(text.Key, out var known)) return known;
            _reader ??= new JapaneseReading(romanizer.Services);
            var analysis = JapaneseAlignment.Analyse(text.String, _reader);
            _analyses[text.Key] = analysis;
            return analysis;
        }

        public Placement? Generated(LineText text, RomanizationLanguage language)
        {
            var key = language + "\u0001" + text.Key;
            if (_generated.TryGetValue(key, out var known)) return known;
            var placement = romanizer.Generate(text, language, this);
            _generated[key] = placement;
            return placement;
        }

        public Placement? Saved(string reading, LineText text, RomanizationLanguage language)
        {
            var key = language + "\u0001" + reading + "\u0001" + text.Key;
            if (_saved.TryGetValue(key, out var known)) return known;
            var placement = romanizer.SavedPlacement(reading, text, language, this);
            _saved[key] = placement;
            return placement;
        }
    }

    private Placement? Generate(LineText text, RomanizationLanguage language, Memo memo)
    {
        if (!ContainsNonLatin(text.String)) return null;
        Placement placement;
        switch (language)
        {
            case RomanizationLanguage.Japanese:
            {
                var analysis = memo.Analysis(text);
                placement = text.Place(analysis.Pieces.Select(Piece.From).ToList(),
                    text.Fragments(analysis.Characters(analysis.Generated)));
                break;
            }
            case RomanizationLanguage.Korean:
            {
                // Each word is romanised whole, because its sounds change across syllables; each syllable keeps what
                // it sounds like (같이 ga·chi).
                var words = text.Segments(grapheme => grapheme.IsWhitespace);
                var characters = new List<JapaneseAlignment.CharacterReading>();
                for (var phrase = 0; phrase < words.Count; phrase++)
                {
                    var word = words[phrase];
                    var offset = word.Location;
                    var graphemes = ReadingText.Graphemes(word.Text);
                    var syllables = KoreanRomanizer.Syllables(word.Text);
                    for (var index = 0; index < Math.Min(graphemes.Count, syllables.Count); index++)
                    {
                        characters.Add(new JapaneseAlignment.CharacterReading(offset, graphemes[index].Length, syllables[index], phrase));
                        offset += graphemes[index].Length;
                    }
                }
                placement = text.Place(
                    words.Select(word => new Piece(KoreanRomanizer.Romanize(word.Text), word.Location, word.Length)).ToList(),
                    text.Fragments(characters));
                break;
            }
            case RomanizationLanguage.Mandarin:
                // Pinyin is read one character at a time, so each word stands alone.
                placement = new Placement.ByWord(text.Words.Select(Pinyin).ToList(), Enumerable.Range(0, text.Words.Count).ToList(), null);
                break;
            case RomanizationLanguage.Cantonese:
                // There is no on-device Jyutping, and Mandarin pinyin would be wrong.
                return null;
            default:
                placement = new Placement.ByWord(text.Words.Select(word => Services.ToLatin(word) ?? word).ToList(),
                    Enumerable.Range(0, text.Words.Count).ToList(), null);
                break;
        }
        return placement.Reads(text.Words) ? placement : null;
    }

    private string Pinyin(string text)
    {
        if (!ContainsNonLatin(text)) return text;
        return Punctuation(Services.MandarinToLatin(text) ?? text);
    }

    /// <summary>Full-width punctuation as it is written in Latin text.</summary>
    private static string Punctuation(string text)
    {
        var result = new StringBuilder();
        foreach (var grapheme in ReadingText.Graphemes(text))
        {
            switch (grapheme.Text)
            {
                case "，" or "、": result.Append(','); break;
                case "。" or "．": result.Append('.'); break;
                case "！": result.Append('!'); break;
                case "？": result.Append('?'); break;
                case "：": result.Append(':'); break;
                case "；": result.Append(';'); break;
                case "～" or "〜": result.Append('~'); break;
                case "「" or "」" or "『" or "』" or "《" or "》" or "〈" or "〉" or "（" or "）" or "【" or "】" or "“" or "”" or "‘" or "’":
                    break;
                default: result.Append(grapheme.Text); break;
            }
        }
        return result.ToString();
    }

    public static string Normalized(string text, bool toneMarks) =>
        (toneMarks ? text : ReadingText.StripDiacritics(text)).Trim();

    // MARK: - Saved readings

    /// <summary>Places a saved line reading on the words it reads. A Japanese reading is matched against the line's
    /// kana and placed on the characters it spells, however it is spaced; null means it reads other words. Other
    /// readings are split by character (Han), syllable (Hangul), word or space where the counts agree, and otherwise
    /// kept whole for the line.</summary>
    private Placement? SavedPlacement(string saved, LineText text, RomanizationLanguage language, Memo memo)
    {
        if (language == RomanizationLanguage.Japanese)
        {
            var analysis = memo.Analysis(text);
            if (analysis.Align(saved) is not { } readings) return null;
            var aligned = text.Place(analysis.PiecesReading(readings).Select(Piece.From).ToList(),
                text.Fragments(analysis.Characters(readings)));
            return aligned.Reads(text.Words) ? aligned : null;
        }
        var tokens = new List<string>();
        foreach (var part in ReadingText.SplitAtWhitespace(saved))
        {
            // Punctuation written on its own stays with the word before it.
            if (tokens.Count == 0 || ReadingText.HasLetterOrNumber(part)) tokens.Add(part);
            else tokens[^1] += part;
        }
        // One syllable for each character or Hangul syllable.
        if (text.ScriptUnits(language) is { } units && units.Count == tokens.Count && units.Count > 1)
        {
            var phrase = 0;
            var characters = new List<JapaneseAlignment.CharacterReading>();
            var pieces = new List<Piece>();
            for (var index = 0; index < units.Count; index++)
            {
                var unit = units[index];
                if (!unit.Joins) phrase++;
                characters.Add(new JapaneseAlignment.CharacterReading(unit.Location, unit.Length, tokens[index], phrase));
                pieces.Add(new Piece(tokens[index], unit.Location, unit.Length, unit.Joins));
            }
            return text.Place(pieces, language == RomanizationLanguage.Korean ? text.Fragments(characters) : null);
        }
        var split = EstimatedWords.Distribute(saved, 0, 1, Services.WordBreaker).Select(word => word.Text).ToList();
        if (split.Count == text.Words.Count) return new Placement.ByWord(split, Enumerable.Range(0, text.Words.Count).ToList(), null);
        var spaced = text.Segments(grapheme => grapheme.IsWhitespace).Where(segment => ReadingText.HasLetterOrNumber(segment.Text)).ToList();
        if (spaced.Count > 1 && spaced.Count == tokens.Count)
        {
            // A Korean word's reading is shared by syllable when it spells what the syllables sound like.
            var characters = new List<JapaneseAlignment.CharacterReading>();
            if (language == RomanizationLanguage.Korean)
            {
                for (var phrase = 0; phrase < tokens.Count; phrase++)
                {
                    var word = spaced[phrase];
                    var syllables = KoreanRomanizer.Syllables(word.Text);
                    if (!string.Concat(syllables).Equals(tokens[phrase], StringComparison.OrdinalIgnoreCase)) continue;
                    var offset = word.Location;
                    var graphemes = ReadingText.Graphemes(word.Text);
                    for (var index = 0; index < Math.Min(graphemes.Count, syllables.Count); index++)
                    {
                        characters.Add(new JapaneseAlignment.CharacterReading(offset, graphemes[index].Length, syllables[index], phrase));
                        offset += graphemes[index].Length;
                    }
                }
            }
            return text.Place(tokens.Select((token, index) => new Piece(token, spaced[index].Location, spaced[index].Length)).ToList(),
                characters.Count == 0 ? null : text.Fragments(characters));
        }
        return new Placement.Whole(saved);
    }

    // MARK: - Placement

    private readonly record struct Piece(string Text, int Location, int Length, bool JoinsPrevious = false)
    {
        public static Piece From(JapaneseReading.Piece piece) => new(piece.Text, piece.Location, piece.Length, piece.JoinsPrevious);
    }

    private abstract record Placement
    {
        /// <summary>One reading per word, each phrase on the word it starts in, with the last word each phrase
        /// covers. <paramref name="Fragments"/> share the phrases out over the words they cover, for lines timed by
        /// character.</summary>
        public sealed record ByWord(List<string> Readings, List<int> Spans, List<string>? Fragments) : Placement;

        /// <summary>A reading that cannot be split by word.</summary>
        public sealed record Whole(string Text) : Placement;

        public bool Reads(IReadOnlyList<string> words) => this switch
        {
            ByWord byWord => byWord.Readings.Zip(words).Any(pair => pair.First.Length > 0 && pair.First != pair.Second),
            Whole whole => whole.Text.Length > 0,
            _ => false,
        };

        public List<LyricWord>? Words(LyricLine line, RomanizationDisplay display, bool toneMarks, IWordBreaker breaker)
        {
            var words = line.Words;
            if (words.Count == 0) return null;
            if (this is Whole wholeReading)
            {
                var text = Normalized(wholeReading.Text, toneMarks);
                if (text.Length == 0 || text == line.Text) return null;
                if (display == RomanizationDisplay.Replace)
                {
                    var estimated = line.WordTimed
                        ? EstimatedWords.Distribute(text, words[0].Start, words[^1].End, breaker)
                        : EstimatedWords.Distribute(text, line.Start, EstimatedWords.SweepEnd(line), breaker);
                    return estimated.Count == 0 ? null : estimated;
                }
                return [new LyricWord(text, words[0].Start, words[^1].End)];
            }
            var (raw, spans, fragments) = (ByWord)this;
            if (raw.Count != words.Count || spans.Count != words.Count) return null;
            var texts = raw.Select(reading => Normalized(reading, toneMarks)).ToList();
            if (!texts.Zip(words).Any(pair => pair.First.Length > 0 && pair.First != pair.Second.Text)) return null;
            // The phrase each word belongs to, by the word it starts in.
            var owner = Enumerable.Range(0, words.Count).ToArray();
            int reach = -1, current = -1;
            for (var index = 0; index < words.Count; index++)
            {
                if (texts[index].Length > 0)
                {
                    current = index;
                    reach = Math.Max(index, Math.Min(spans[index], words.Count - 1));
                }
                else if (index <= reach)
                {
                    owner[index] = current;
                }
            }
            int End(int start) => start == owner[start] ? Math.Max(start, Math.Min(spans[start], words.Count - 1)) : start;
            if (display == RomanizationDisplay.Replace)
            {
                // A phrase spanning several words is shown across their time; a word no reading covers is shown as
                // it is.
                var replaced = new List<LyricWord>();
                for (var index = 0; index < words.Count; index++)
                {
                    if (texts[index].Length > 0) replaced.Add(new LyricWord(texts[index], words[index].Start, words[End(index)].End));
                    else if (owner[index] == index) replaced.Add(words[index]);
                }
                return replaced;
            }
            var shares = fragments is { } all && all.Count == words.Count
                ? all.Select(fragment => Normalized(fragment, toneMarks)).ToList()
                : null;
            var shown = texts.ToList();
            if (shares is not null && IsCharacterTimed(line))
            {
                shown = shares;
            }
            else if (shares is not null)
            {
                // A phrase sung across several words (笑っ てい た) is shared out over them (warat, tei, ta), so each
                // word reads itself as it is sung, rather than the first carrying the whole phrase and the rest
                // nothing. Only where the shares spell the phrase exactly; otherwise the first keeps it.
                for (var start = 0; start < words.Count; start++)
                {
                    if (owner[start] != start || texts[start].Length == 0) continue;
                    var last = End(start);
                    if (last <= start) continue;
                    var span = shares.GetRange(start, last - start + 1);
                    if (span.Any(share => share.Length == 0)
                        || ReadingText.WithoutWhitespace(string.Concat(span)) != ReadingText.WithoutWhitespace(texts[start]))
                    {
                        continue;
                    }
                    for (var index = start; index <= last; index++) shown[index] = span[index - start];
                }
            }
            return Enumerable.Range(0, words.Count).Select(index =>
            {
                var start = owner[index];
                return new LyricWord(shown[index], words[start].Start, words[End(start)].End);
            }).ToList();
        }
    }

    /// <summary>A line's words laid end to end in one string, with each word's UTF-16 range, so a reading of the whole
    /// line can be mapped back onto words.</summary>
    private sealed class LineText
    {
        public string String { get; }
        public List<string> Words { get; }
        public List<(int Location, int Length)> Ranges { get; }

        public LineText(LyricLine line)
        {
            var words = line.Words;
            var cursor = 0;
            var gaps = new List<bool?>(words.Count);
            foreach (var word in words)
            {
                var found = word.Text.Length == 0 ? -1 : line.Text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
                gaps.Add(found < 0 ? null : line.Text[cursor..found].Any(char.IsWhiteSpace));
                if (found >= 0) cursor = found + word.Text.Length;
            }
            // Timed Japanese words may be spaced by their markup rather than the language (今 日 も, 思っ た より),
            // where a space would split a word (omo ta) or part a particle from it (世界 は sekai ha). But a source
            // that cuts words only at spaces (KPoe, eLRC stamped by phrase) spaces every break between the lyric's own
            // phrases too, where reading across a space misreads hiragana (あいたい あいたい is not a itai, まって まって
            // not ma tte). So on a line spaced at every break between Japanese words, a space is taken as markup only
            // where a new phrase cannot start (GoesOn): on a line timed one character at a time, between single
            // characters or syllables (きょ う も), and before a word that only goes on from the one before. A space
            // at only some breaks is a pause (あっ 君だ) and stays.
            //
            // Spacing alone cannot settle the rest, which reads as spaced (the cases are pinned in RomanizationTests):
            // a word split after a kanji (明 日が is not asu ga, 大 好きだよ not daisuki, あの 時 not ano toki, 一人
            // ぼっち not hitoribotchi, 来 ない not ko nai), a っ before a word in kanji (引っ 越し is not hikkoshi) or,
            // after hiragana, before more than a syllable that is no ending (がっ こう is not gakkou, as あっ きみだ is
            // a kimi), and hiragana split after hiragana (だき しめ is daki shime).
            var japanese = new HashSet<int>();
            for (var index = 1; index < words.Count; index++)
            {
                if (ReadingText.LastScalar(words[index - 1].Text) is { } before && ReadingText.FirstScalar(words[index].Text) is { } after
                    && JapaneseReading.IsJapanese(before) && JapaneseReading.IsJapanese(after))
                {
                    japanese.Add(index);
                }
            }
            var markup = line.WordTimed && japanese.Count >= 2 && japanese.All(index => gaps[index] == true);
            var byCharacter = markup && IsCharacterTimed(line);
            string Sung(int index) => ReadingText.LettersAndNumbers(words[index].Text);
            bool Single(int index) => IsSyllable(Sung(index));
            // Whether the word at `index` goes on from the one before it, so a markup space between them is inside a
            // phrase.
            bool GoesOn(int index)
            {
                var previous = Sung(index - 1);
                var word = Sung(index);
                if (ReadingText.FirstScalar(word) is not { } first || ReadingText.LastScalar(previous) is not { } last) return false;
                // Small kana and ー never start a word (すご ー い), and the language never sets a lone particle apart
                // (は 笑った wa waratta, 空 いっぱい の 星).
                if (JapaneseReading.CarriesOn(first) || Particles.Contains(previous)) return true;
                // Before kanji or katakana a new word starts (今 日差しが is ima).
                if (!JapaneseReading.IsHiragana(first)) return false;
                // Particles and endings only follow a word (世界 は sekai wa, 会い たい aitai, ちょっ と chotto, はっ
                // として hatto shite).
                if (JapaneseReading.FollowsAWord(word)) return true;
                // An interjection's っ is not doubled across a pause (あっ きみだ, えっ どうして): without kanji before
                // it, it goes on only into a single syllable (さっ き sakki).
                var sokuon = last.Value is 'っ' or 'ッ';
                if (Single(index) && (!sokuon || IsSyllable(DropLastGrapheme(previous)))) return true;
                // After a noun the tokenizer splits a particle's kana off a word of hiragana (君 はなれて is not wa
                // narete, 夢 のこして not no koshite).
                if (Particles.Contains(first.ToString())) return false;
                // Hiragana goes on from kanji with its okurigana written, or from katakana, as the rest of the word (抱き
                // しめ dakishime, 引っ こし hikkoshi, アイ して ai shite). Right after a kanji the tokenizer would read it
                // as the kanji's okurigana (傷 まだ is not itama da, 愛 ささやく not mana), and after hiragana across
                // phrases (ならんだ あか, もしもし かめよ): there it stands apart.
                var kanji = previous.EnumerateRunes().Any(ReadingText.IsIdeographic);
                var katakana = previous.EnumerateRunes().Any(rune => rune.Value is >= 0x30A1 and <= 0x30FA);
                return (kanji && JapaneseReading.IsHiragana(last)) || (katakana && !sokuon);
            }
            var builder = new StringBuilder();
            var ranges = new List<(int Location, int Length)>(words.Count);
            for (var index = 0; index < words.Count; index++)
            {
                var word = words[index];
                if (index > 0)
                {
                    var before = ReadingText.LastScalar(words[index - 1].Text);
                    var after = ReadingText.FirstScalar(word.Text);
                    var spaced = gaps[index] ?? !(IsCJK(before) && IsCJK(after));
                    if (spaced && markup && japanese.Contains(index)
                        && (byCharacter || (Single(index - 1) && Single(index)) || GoesOn(index)))
                    {
                        spaced = false;
                    }
                    if (spaced) builder.Append(' ');
                }
                ranges.Add((builder.Length, word.Text.Length));
                builder.Append(word.Text);
            }
            String = builder.ToString();
            Words = words.Select(word => word.Text).ToList();
            Ranges = ranges;
        }

        public string Key => String + "\u0001" + string.Join(",", Ranges.Select(range => $"{range.Location}+{range.Length}"));

        /// <summary>Small kana written into the syllable before them (きょ, ティ).</summary>
        private const string SmallKana = "ぁぃぅぇぉゃゅょゎァィゥェォャュョヮ";

        /// <summary>Particles of one kana, which a markup space never parts from the words around them.</summary>
        private static readonly HashSet<string> Particles = ["は", "へ", "が", "を", "に", "の", "も", "と", "で", "や"];

        /// <summary>One syllable: a character with the small kana written into it.</summary>
        private static bool IsSyllable(string sung)
        {
            var graphemes = ReadingText.Graphemes(sung);
            return graphemes.Count > 0 && graphemes.Skip(1).All(g => SmallKana.Contains(g.Text, StringComparison.Ordinal));
        }

        private static string DropLastGrapheme(string text)
        {
            var graphemes = ReadingText.Graphemes(text);
            return graphemes.Count == 0 ? text : text[..graphemes[^1].Offset];
        }

        private static bool IsCJK(System.Text.Rune? scalar) =>
            scalar is { } rune && (JapaneseReading.IsJapanese(rune) || rune.Value is >= 0xAC00 and <= 0xD7A3
                or >= 0x1100 and <= 0x11FF or >= 0x3131 and <= 0x318E);

        /// <summary>The first word that ends after <paramref name="location"/>.</summary>
        public int WordIndex(int location)
        {
            for (var index = 0; index < Ranges.Count; index++)
            {
                if (location < Ranges[index].Location + Ranges[index].Length) return index;
            }
            return Math.Max(0, Ranges.Count - 1);
        }

        /// <summary>Lays readings onto the words they start in. Pieces that join form one phrase, so a word reads whole
        /// phrases (思った omotta).</summary>
        public Placement Place(List<Piece> pieces, List<string>? fragments)
        {
            var readings = Enumerable.Repeat("", Ranges.Count).ToList();
            var spans = Enumerable.Range(0, Ranges.Count).ToList();
            Piece? phrase = null;
            void Flush()
            {
                if (phrase is not { } finished || finished.Text.Length == 0 || Ranges.Count == 0) return;
                var first = WordIndex(finished.Location);
                var last = Math.Max(first, WordIndex(finished.Location + Math.Max(finished.Length, 1) - 1));
                readings[first] += (readings[first].Length == 0 ? "" : " ") + finished.Text;
                spans[first] = Math.Max(spans[first], last);
            }
            foreach (var piece in pieces)
            {
                if (piece.Text.Length == 0) continue;
                if (piece.JoinsPrevious && phrase is { } joined)
                {
                    phrase = joined with { Text = joined.Text + piece.Text, Length = piece.Location + piece.Length - joined.Location };
                }
                else
                {
                    Flush();
                    phrase = piece;
                }
            }
            Flush();
            return new Placement.ByWord(readings, spans, fragments);
        }

        /// <summary>Each word's share of readings given character by character: its characters' parts, with a space
        /// where a new phrase starts.</summary>
        public List<string> Fragments(List<JapaneseAlignment.CharacterReading> characters)
        {
            var result = Enumerable.Repeat("", Ranges.Count).ToList();
            var phrases = new int?[Ranges.Count];
            if (Ranges.Count == 0) return result;
            foreach (var character in characters.OrderBy(character => character.Location))
            {
                if (character.Text.Length == 0) continue;
                var index = WordIndex(character.Location);
                if (phrases[index] is { } phrase && phrase != character.Phrase) result[index] += " ";
                result[index] += character.Text;
                phrases[index] = character.Phrase;
            }
            return result;
        }

        /// <summary>Runs of characters between separators, with their UTF-16 ranges.</summary>
        public List<(string Text, int Location, int Length)> Segments(Func<ReadingText.Grapheme, bool> isSeparator)
        {
            var result = new List<(string Text, int Location, int Length)>();
            var current = new StringBuilder();
            var start = 0;
            foreach (var grapheme in ReadingText.Graphemes(String))
            {
                if (isSeparator(grapheme))
                {
                    if (current.Length > 0) result.Add((current.ToString(), start, grapheme.Offset - start));
                    current.Clear();
                }
                else
                {
                    if (current.Length == 0) start = grapheme.Offset;
                    current.Append(grapheme.Text);
                }
            }
            if (current.Length > 0) result.Add((current.ToString(), start, String.Length - start));
            return result;
        }

        /// <summary>The parts a Chinese or Korean reading spells one by one: each Han character or Hangul syllable,
        /// and each run of Latin letters or digits. Syllables of one Korean word join.</summary>
        public List<(int Location, int Length, bool Joins)>? ScriptUnits(RomanizationLanguage language)
        {
            var korean = language == RomanizationLanguage.Korean;
            if (language == RomanizationLanguage.Japanese
                || !(korean ? KoreanRomanizer.ContainsHangul(String) : ReadingText.HasHan(String)))
            {
                return null;
            }
            var units = new List<(int Location, int Length, bool Joins)>();
            var previousEnd = -1;
            (int Location, int Length)? latin = null;
            foreach (var grapheme in ReadingText.Graphemes(String))
            {
                var offset = grapheme.Offset;
                var width = grapheme.Length;
                var spelled = korean ? KoreanRomanizer.ContainsHangul(grapheme.Text) : ReadingText.IsIdeographic(grapheme.First);
                if (!spelled && grapheme.IsLetterOrNumber)
                {
                    latin = latin is { } run ? (run.Location, run.Length + width) : (offset, width);
                }
                else
                {
                    if (latin is { } run) { units.Add((run.Location, run.Length, false)); previousEnd = -1; latin = null; }
                    if (spelled)
                    {
                        units.Add((offset, width, korean && previousEnd == offset));
                        previousEnd = offset + width;
                    }
                    else
                    {
                        previousEnd = -1;
                    }
                }
            }
            if (latin is { } tail) units.Add((tail.Location, tail.Length, false));
            return units;
        }
    }
}
