using System.Text;

namespace Lunalyr.Core.Lyrics.Readings.Japanese;

/// <summary>
/// <see cref="IJapaneseTokenizer"/> on the bundled morphological analyser: its morphemes are the tokens, and each
/// reading (katakana) is turned into Latin letters by <see cref="IScriptTransliterator.ToLatin"/>, which gives the
/// same style as Apple's tokenizer (<c>~tsu</c> for a final っ, <c>~ya</c> for small kana, macrons for ー) that
/// <see cref="JapaneseReading"/> tidies. A few things are shaped the way the Mac's tokenizer gives them, by rules
/// about the language rather than about particular words:
/// <list type="bullet">
/// <item>A ー drawn out after hiragana (すごーい) is left out while analysing, so the word around it is still found;
/// the token keeps it, and is read from what is written.</item>
/// <item>A morpheme never begins with a small kana: where an unknown word does (はくし|ゅして), the small kana goes
/// to the word before it.</item>
/// <item>An interjection or adverb ending in っ takes a particle's と (はっ|として is はっと|して).</item>
/// <item>は or へ beginning a phrase before an adverb, a particle or a pronoun is the particle (は|もう, not はも|う);
/// see <see cref="LeadingParticle"/>.</item>
/// <item>The volitional う and よう stay on their verb (行こ|う is one token, ikou; でしょ|う deshou).</item>
/// <item>1 or 2 people (1人, ２人, 一人, 二人) is one token: hitori, futari. The dictionary reads 2|人 as ni nin; for
/// digits the token has no reading of its own and <see cref="JapaneseReading"/> reads it, as it does on the Mac.</item>
/// </list>
/// Words the dictionary does not know get a reading from what is written when that is kana, and none otherwise (a
/// kanji it lacks, such as simplified 梦, is never given a Mandarin reading). Thread-safe.
/// </summary>
public sealed class DictionaryJapaneseTokenizer(MorphologicalAnalyzer analyzer, IScriptTransliterator? transliterator) : IJapaneseTokenizer
{
    public bool ReadsKanji => true;

    public MorphologicalAnalyzer Analyzer => analyzer;

    /// <summary>A morpheme of the original text: its range there, its reading in kana (null when it has none: an
    /// unknown word that is not kana), its part of speech (context id) and whether the dictionary knows it.</summary>
    public readonly record struct Word(int Start, int Length, string? Reading, int ContextId, bool IsKnown);

    public IReadOnlyList<JapaneseToken> Tokenize(string text, int start, int length)
    {
        var slice = text.AsSpan(start, length);
        var words = Analyze(slice);
        var dictionary = analyzer.Dictionary;
        var tokens = new List<JapaneseToken>(words.Count);
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            var surface = slice.Slice(word.Start, word.Length);
            var next = index + 1 < words.Count && words[index + 1].Start == word.Start + word.Length ? words[index + 1] : (Word?)null;
            if (next is { } person && slice.Slice(person.Start, person.Length) is "人" && People(surface) is var (count, digits))
            {
                tokens.Add(new JapaneseToken(start + word.Start, word.Length + person.Length, digits ? null : Latin(count == 1 ? "ひとり" : "ふたり")));
                index++;
                continue;
            }
            var reading = word.Reading;
            var end = word.Start + word.Length;
            if (next is { } volitional && slice.Slice(volitional.Start, volitional.Length) is "う" or "よう"
                && dictionary.PartOfSpeech(volitional.ContextId).StartsWith("助動詞", StringComparison.Ordinal)
                && dictionary.PartOfSpeech(word.ContextId) is var part
                && (part.StartsWith("動詞", StringComparison.Ordinal) || part.StartsWith("助動詞", StringComparison.Ordinal)))
            {
                reading = reading is null ? null : reading + volitional.Reading;
                end = volitional.Start + volitional.Length;
                index++;
            }
            tokens.Add(new JapaneseToken(start + word.Start, end - word.Start, reading is null ? null : Latin(reading)));
        }
        return tokens;
    }

    /// <summary>1 or 2 (digits, full-width digits or kanji) counting people, and whether it is written in digits.</summary>
    private static (int Count, bool Digits)? People(ReadOnlySpan<char> number) => number switch
    {
        "1" or "１" => (1, true),
        "2" or "２" => (2, true),
        "一" => (1, false),
        "二" => (2, false),
        _ => null,
    };

    /// <summary>The morphemes of <paramref name="text"/>, with ranges in it. Each ー that follows hiragana is left
    /// out of the analysis and stays on the morpheme before it; a small kana that would begin a morpheme goes to the
    /// one before it. Such morphemes are read as they are written.</summary>
    public List<Word> Analyze(ReadOnlySpan<char> text)
    {
        var kept = new StringBuilder(text.Length);
        var origin = new List<int>(text.Length + 1);
        for (var index = 0; index < text.Length; index++)
        {
            if (IsDrawnOut(text, index)) continue;
            kept.Append(text[index]);
            origin.Add(index);
        }
        origin.Add(text.Length);
        var result = new List<Word>();
        foreach (var morpheme in LeadingParticle(kept.ToString()))
        {
            var from = origin[morpheme.Start];
            var to = origin[morpheme.Start + morpheme.Length - 1] + 1;
            while (to < text.Length && IsDrawnOut(text, to)) to++;
            var reading = morpheme.Reading;
            if (to - from != morpheme.Length) reading = IsKana(text[from..to]) ? text[from..to].ToString() : reading;
            if (reading is null && IsKana(text[from..to])) reading = text[from..to].ToString();
            var word = new Word(from, to - from, reading, morpheme.ContextId, morpheme.IsKnown);
            // A small kana cannot begin a word: it carries on the syllable before it.
            if (result.Count > 0 && result[^1] is var previous && previous.Start + previous.Length == from
                && IsSmallKana(text[from]) && IsKana(text.Slice(previous.Start, previous.Length)) && IsKana(text[from..to]))
            {
                var moved = 1;
                while (moved < word.Length && IsSmallKana(text[from + moved])) moved++;
                result[^1] = previous with
                {
                    Length = previous.Length + moved, Reading = text.Slice(previous.Start, previous.Length + moved).ToString(),
                };
                if (moved == word.Length) continue;
                word = word with { Start = from + moved, Length = word.Length - moved, Reading = text[(from + moved)..to].ToString() };
            }
            // An interjection or adverb ending in っ takes the と after it (はっ|として is はっと|して, グッ|と グッと).
            if (result.Count > 0 && result[^1] is var sound && sound.Start + sound.Length == word.Start
                && text[sound.Start + sound.Length - 1] is 'っ' or 'ッ' && text[word.Start] == 'と'
                && analyzer.Dictionary.PartOfSpeech(sound.ContextId) is var soundPart
                && (soundPart.StartsWith("感動詞", StringComparison.Ordinal) || soundPart.StartsWith("副詞", StringComparison.Ordinal))
                && analyzer.Dictionary.PartOfSpeech(word.ContextId).StartsWith("助詞", StringComparison.Ordinal))
            {
                result[^1] = sound with { Length = sound.Length + 1, Reading = sound.Reading is null ? null : sound.Reading + "ト" };
                if (word.Length == 1) continue;
                word = word with
                {
                    Start = word.Start + 1, Length = word.Length - 1,
                    Reading = word.Reading is { Length: > 1 } rest && rest[0] is 'ト' or 'と' ? rest[1..] : word.Reading,
                };
            }
            result.Add(word);
        }
        return result;
    }

    /// <summary>
    /// The analysis of <paramref name="text"/>, with a は or へ that begins it before hiragana read as the particle
    /// where what follows reads as a word that comes after one. A lyric line often goes on from the one before with
    /// its particle (は もう, は から), and the dictionary would otherwise take the two together for a rare verb
    /// written in kana (はも|う, from hamoru; はから, from hakaru). What follows the particle is then analysed as a phrase
    /// of its own (しあわせ, not し|あわ|せ). Where what follows is a verb, an adjective or a noun the word stays
    /// whole (はしれ, はなれて, はかない); <see cref="JapaneseReading"/> then decides, as it does on the Mac, whether a
    /// lone は before a one-kana particle begins a word (は|ね hane).
    /// </summary>
    private List<Morpheme> LeadingParticle(string text)
    {
        var morphemes = analyzer.Analyze(text);
        // Small kana and っ carry on the syllable before them (はっ, はぁ), so they never follow the particle.
        if (text.Length < 2 || text[0] is not ('は' or 'へ') || text[1] is not (>= 'ぁ' and <= 'ゟ')
            || text[1] == 'っ' || IsSmallKana(text[1]) || morphemes.Count == 0)
        {
            return morphemes;
        }
        var dictionary = analyzer.Dictionary;
        var first = morphemes[0];
        var rest = analyzer.Analyze(text.AsSpan(1));
        if (rest.Count == 0) return morphemes;
        Morpheme particle;
        if (first.Length == 1)
        {
            if (!dictionary.PartOfSpeech(first.ContextId).StartsWith("助詞", StringComparison.Ordinal)) return morphemes;
            particle = first;
        }
        else
        {
            var part = dictionary.PartOfSpeech(rest[0].ContextId);
            if (!rest[0].IsKnown || !FollowsAParticle.Any(prefix => part.StartsWith(prefix, StringComparison.Ordinal))) return morphemes;
            if (analyzer.Analyze(text, out _, 1, "助詞") is not [var forced, ..]) return morphemes;
            particle = forced;
        }
        return [particle, .. rest.Select(morpheme => morpheme with { Start = morpheme.Start + 1 })];
    }

    /// <summary>Parts of speech that begin what comes after a topic or direction particle: adverbs, particles,
    /// conjunctions, pronouns and adnominals (もう, から, でも, どこ, この).</summary>
    private static readonly string[] FollowsAParticle = ["副詞", "助詞", "接続詞", "名詞,代名詞", "連体詞"];

    /// <summary>A ー (or one of a run of them) straight after hiragana: a sung vowel drawn out (すごーい, ねーえ).</summary>
    private static bool IsDrawnOut(ReadOnlySpan<char> text, int index)
    {
        if (text[index] is not ('ー' or 'ｰ')) return false;
        var before = index - 1;
        while (before >= 0 && text[before] is 'ー' or 'ｰ') before--;
        return before >= 0 && text[before] is >= 'ぁ' and <= 'ゟ';
    }

    private static bool IsSmallKana(char character) => "ぁぃぅぇぉゃゅょゎァィゥェォャュョヮ".Contains(character);

    private static bool IsKana(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return false;
        foreach (var character in text)
        {
            if (character is not (>= 'ぁ' and <= 'ゟ' or >= 'ァ' and <= 'ヺ' or 'ー' or 'ｰ' or >= 'ｦ' and <= 'ﾟ')) return false;
        }
        return true;
    }

    /// <summary>A reading in kana as Latin letters; a lone ー stays as it is (the Mac's tokenizer gives it so).</summary>
    private string? Latin(string reading)
    {
        if (transliterator is null || reading.Length == 0) return null;
        if (reading.All(character => character is 'ー' or 'ｰ')) return reading;
        return transliterator.ToLatin(reading);
    }
}
