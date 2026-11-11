using System.Text;

namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>
/// Revised Romanization of Korean (2000), spelled from pronunciation the way the standard requires: inside a word,
/// liaison, ㅎ aspiration and elision, palatalisation, nasalisation and liquidisation are applied across syllable
/// boundaries. Tensification is not written, as in the standard, and no hyphens are added. A word is a run of Hangul
/// syllables; anything else (spaces, Latin, punctuation) passes through unchanged and ends the run.
/// Ported from the Mac's KoreanRomanizer.swift.
/// </summary>
public static class KoreanRomanizer
{
    public static string Romanize(string text) => string.Concat(Syllables(text));

    /// <summary>The reading of each character (grapheme) of <paramref name="text"/>: a Hangul syllable's sounds
    /// after the changes its word brings (같이 ga, chi; 국물 gung, mul), any other character itself.</summary>
    public static List<string> Syllables(string text)
    {
        var result = new List<string>();
        var word = new List<Syllable>();
        foreach (var grapheme in ReadingText.Graphemes(text))
        {
            var composed = grapheme.Text.Normalize(NormalizationForm.FormC);
            if (composed.Length == 1 && Syllable.From(composed[0]) is { } syllable)
            {
                word.Add(syllable);
            }
            else
            {
                result.AddRange(Spell(word));
                word.Clear();
                result.Add(grapheme.Text);
            }
        }
        result.AddRange(Spell(word));
        return result;
    }

    /// <summary>Hangul syllables, precomposed or written as conjoining jamo (NFD).</summary>
    public static bool ContainsHangul(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is >= 0xAC00 and <= 0xD7A3 or >= 0x1100 and <= 0x11FF or >= 0xA960 and <= 0xA97F
                or >= 0xD7B0 and <= 0xD7FF)
            {
                return true;
            }
        }
        return false;
    }

    // MARK: - Jamo

    private enum Consonant { G, KK, N, D, TT, R, M, B, PP, S, SS, J, JJ, CH, K, T, P, H, NG }

    private enum Coda { None, K, N, T, L, M, P, NG }

    private static string Letters(Consonant consonant) => consonant switch
    {
        Consonant.G => "g", Consonant.KK => "kk", Consonant.N => "n", Consonant.D => "d", Consonant.TT => "tt",
        Consonant.R => "r", Consonant.M => "m", Consonant.B => "b", Consonant.PP => "pp", Consonant.S => "s",
        Consonant.SS => "ss", Consonant.J => "j", Consonant.JJ => "jj", Consonant.CH => "ch", Consonant.K => "k",
        Consonant.T => "t", Consonant.P => "p", Consonant.H => "h", _ => "ng",
    };

    private static string Letters(Coda coda) => coda switch
    {
        Coda.None => "", Coda.K => "k", Coda.N => "n", Coda.T => "t", Coda.L => "l", Coda.M => "m", Coda.P => "p",
        _ => "ng",
    };

    /// <summary>The sound a final consonant settles on before another consonant or at the end of a word.</summary>
    private static Coda Neutral(Consonant consonant) => consonant switch
    {
        Consonant.G or Consonant.KK or Consonant.K => Coda.K,
        Consonant.N => Coda.N,
        Consonant.R => Coda.L,
        Consonant.M => Coda.M,
        Consonant.B or Consonant.PP or Consonant.P => Coda.P,
        Consonant.NG => Coda.NG,
        _ => Coda.T,
    };

    /// <summary>The aspirated sound an obstruent takes next to ㅎ.</summary>
    private static Consonant Aspirated(Consonant consonant) => consonant switch
    {
        Consonant.G or Consonant.KK or Consonant.K => Consonant.K,
        Consonant.B or Consonant.PP or Consonant.P => Consonant.P,
        Consonant.J or Consonant.JJ or Consonant.CH => Consonant.CH,
        _ => Consonant.T,
    };

    private static readonly Consonant?[] Initials =
    [
        Consonant.G, Consonant.KK, Consonant.N, Consonant.D, Consonant.TT, Consonant.R, Consonant.M, Consonant.B,
        Consonant.PP, Consonant.S, Consonant.SS, null, Consonant.J, Consonant.JJ, Consonant.CH, Consonant.K,
        Consonant.T, Consonant.P, Consonant.H,
    ];

    private static readonly string[] Vowels =
    [
        "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae",
        "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i",
    ];

    private const int VowelI = 20;

    /// <summary>ㅕ: 여 and 혀 are often 이 and 히 contracted with 어 (붙이어 붙여, 갇히어 갇혀), so ㅌ before 여, and
    /// ㄷ or ㅌ before 혀, palatalise as they do before 이 and 히.</summary>
    private const int VowelYeo = 6;

    private static bool Palatalises(int vowel) => vowel is VowelI or VowelYeo;

    /// <summary>Final consonants; double finals keep both of their consonants.</summary>
    private static readonly Consonant[][] Finals =
    [
        [], [Consonant.G], [Consonant.KK], [Consonant.G, Consonant.S], [Consonant.N], [Consonant.N, Consonant.J],
        [Consonant.N, Consonant.H], [Consonant.D], [Consonant.R], [Consonant.R, Consonant.G], [Consonant.R, Consonant.M],
        [Consonant.R, Consonant.B], [Consonant.R, Consonant.S], [Consonant.R, Consonant.T], [Consonant.R, Consonant.P],
        [Consonant.R, Consonant.H], [Consonant.M], [Consonant.B], [Consonant.B, Consonant.S], [Consonant.S],
        [Consonant.SS], [Consonant.NG], [Consonant.J], [Consonant.CH], [Consonant.K], [Consonant.T], [Consonant.P],
        [Consonant.H],
    ];

    private readonly record struct Syllable(int Scalar, int Initial, int Vowel, int Final)
    {
        public static Syllable? From(char character)
        {
            if (character is < '가' or > '힣') return null;
            var index = character - 0xAC00;
            return new Syllable(character, index / 588, index % 588 / 28, index % 28);
        }
    }

    // MARK: - Spelling

    private static List<string> Spell(List<Syllable> word)
    {
        var result = new List<string>(word.Count);
        if (word.Count == 0) return result;
        var onsets = word.Select(syllable => Initials[syllable.Initial]).ToArray();
        var codas = new Coda[word.Count];
        for (var index = 0; index < word.Count; index++)
        {
            var final = Finals[word[index].Final];
            if (index + 1 < word.Count)
            {
                var (coda, onset) = Join(final, word, index);
                codas[index] = coda;
                onsets[index + 1] = onset;
            }
            else
            {
                codas[index] = WordFinal(final, word[index]);
            }
        }
        for (var index = 0; index < word.Count; index++)
        {
            var text = new StringBuilder();
            if (onsets[index] is { } onset)
            {
                // ㄹ after ㄹ is written ll; elsewhere an initial ㄹ is r.
                text.Append(onset == Consonant.R && index > 0 && codas[index - 1] == Coda.L ? "l" : Letters(onset));
            }
            result.Add(text.Append(Vowels[word[index].Vowel]).Append(Letters(codas[index])).ToString());
        }
        return result;
    }

    private static Coda WordFinal(Consonant[] final, Syllable syllable)
    {
        if (final.Length == 0) return Coda.None;
        var head = final[0];
        if (final.Length != 2) return Neutral(head);
        return (head, final[1]) switch
        {
            (Consonant.R, Consonant.G) or (Consonant.R, Consonant.M) or (Consonant.R, Consonant.P) => Neutral(final[1]),
            (Consonant.R, Consonant.B) => IsBalp(syllable) ? Coda.P : Coda.L,
            _ => Neutral(head),
        };
    }

    /// <summary>밟 keeps ㅂ where other ㄼ finals keep ㄹ (밟다 bapda, 넓다 neolda).</summary>
    private static bool IsBalp(Syllable syllable) => syllable.Scalar == 0xBC1F;

    /// <summary>ㄴ is inserted before a few vowel-initial morphemes in compounds (꽃잎 kkonnip, 한여름 hannyeoreum).
    /// It is lexical, so only common lyric words are listed; elsewhere a final simply moves across.</summary>
    private static bool InsertsN(List<Syllable> word, int index)
    {
        var next = word[index + 1].Scalar;
        if (next == 0xC78E) return true; // 잎
        var current = word[index].Scalar;
        int? after = index + 2 < word.Count ? word[index + 2].Scalar : null;
        if (next == 0xC5EC && after == 0xB984 && current is 0xD55C or 0xB2A6 or 0xCCAB) return true; // 한/늦/첫 여름
        if (current == 0xC19C && next == 0xC774 && after == 0xBD88) return true; // 솜이불
        if (current == 0xB2F4 && next == 0xC694 && after is null) return word.Count == 2; // 담요
        return false;
    }

    /// <summary>Resolves a final consonant against the next syllable's initial.</summary>
    private static (Coda Coda, Consonant? Onset) Join(Consonant[] final, List<Syllable> word, int index)
    {
        var current = word[index];
        var next = word[index + 1];
        var initial = Initials[next.Initial];
        if (final.Length == 0) return (Coda.None, initial);
        var head = final[0];
        Consonant? tail = final.Length == 2 ? final[1] : null;
        if (initial is null && InsertsN(word, index)) initial = Consonant.N;

        if (initial is not { } onset)
        {
            // Liaison: the final, or the second consonant of a pair, opens the next syllable. ㄷ/ㅌ before 이
            // (ㅌ also before 여) palatalise; ㅎ falls silent.
            if (head == Consonant.NG && tail is null) return (Coda.NG, null);
            if (tail is { } pairTail)
            {
                if (pairTail == Consonant.H) return (Coda.None, head);                                     // 많아 mana, 싫어 sireo
                if (head == Consonant.R && pairTail == Consonant.T && Palatalises(next.Vowel)) return (Coda.L, Consonant.CH);
                return (Neutral(head), pairTail);                                                          // 없어 eopseo, 읽어 ilgeo
            }
            if (head == Consonant.H) return (Coda.None, null);                                             // 좋아 joa
            if (next.Vowel == VowelI && head == Consonant.D) return (Coda.None, Consonant.J);              // 굳이 guji
            if (Palatalises(next.Vowel) && head == Consonant.T) return (Coda.None, Consonant.CH);          // 같이 gachi, 붙여 buchyeo
            return (Coda.None, head);                                                                      // 한국어 hangugeo
        }

        if (final[^1] == Consonant.H)
        {
            // ㅎ, ㄶ, ㅀ: aspirate a following ㄱ/ㄷ/ㅈ, merge into ㅅ, and assimilate before ㄴ
            // (놓는 nonneun, 않는 anneun, 싫니 silli).
            var @base = tail is null ? Coda.None : Neutral(head);
            switch (onset)
            {
                case Consonant.G or Consonant.D or Consonant.J: return (@base, Aspirated(onset));       // 놓다 nota, 많고 manko
                case Consonant.S or Consonant.H: return (@base, onset);
                case Consonant.N: return @base == Coda.L ? (Coda.L, Consonant.R) : (Coda.N, Consonant.N);
                default: return Assimilate(@base == Coda.None ? Coda.T : @base, onset);
            }
        }

        var single = head;
        if (tail is { } second)
        {
            if (onset == Consonant.H)
            {
                // A pair before ㅎ keeps its first consonant and aspirates the second (밝히다 balkida, 앉히다 anchida).
                return second == Consonant.M ? (Coda.M, Consonant.H) : (Neutral(head), Aspirated(second));
            }
            single = (head, second) switch
            {
                (Consonant.R, Consonant.G) => onset == Consonant.G ? Consonant.R : Consonant.G,            // 읽다 ikda, 읽고 ilgo
                (Consonant.R, Consonant.M) or (Consonant.R, Consonant.P) => second,                       // 젊다 jeomda, 읊다 eupda
                (Consonant.R, Consonant.B) => IsBalp(current) ? Consonant.B : Consonant.R,                // 밟다 bapda, 넓다 neolda
                _ => head,                                                                                // ㄳ ㄵ ㄽ ㄾ ㅄ
            };
        }
        if (onset == Consonant.H)
        {
            switch (Neutral(single))
            {
                case Coda.K or Coda.P: return (Coda.None, Aspirated(single));                              // 축하 chuka, 입학 ipak
                case Coda.T:
                    if (single is Consonant.D or Consonant.T && Palatalises(next.Vowel)) return (Coda.None, Consonant.CH); // 굳히다 guchida
                    return (Coda.None, Aspirated(single));                                                 // 못해 motae
                default: return (Neutral(single), Consonant.H);                                            // 전화 jeonhwa
            }
        }
        return Assimilate(Neutral(single), onset);
    }

    /// <summary>Nasalisation and liquidisation between a settled final and an initial.</summary>
    private static (Coda Coda, Consonant? Onset) Assimilate(Coda coda, Consonant onset)
    {
        switch (onset)
        {
            case Consonant.N or Consonant.M:
                return coda switch
                {
                    Coda.K => (Coda.NG, onset),                                                            // 국물 gungmul
                    Coda.T => (Coda.N, onset),                                                             // 있는 inneun
                    Coda.P => (Coda.M, onset),                                                             // 합니다 hamnida
                    Coda.L when onset == Consonant.N => (Coda.L, Consonant.R),                             // 설날 seollal
                    _ => (coda, onset),
                };
            case Consonant.R:
                return coda switch
                {
                    Coda.N or Coda.L => (Coda.L, Consonant.R),                                             // 신라 silla, 몰라 molla
                    Coda.M or Coda.NG => (coda, Consonant.N),                                              // 종로 jongno
                    Coda.K => (Coda.NG, Consonant.N),                                                      // 독립 dongnip
                    Coda.P => (Coda.M, Consonant.N),                                                       // 법률 beomnyul
                    Coda.T => (Coda.N, Consonant.N),
                    _ => (Coda.None, Consonant.R),
                };
            default:
                return (coda, onset);
        }
    }
}
