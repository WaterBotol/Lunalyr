// Action words in other languages (the Mac's WordActionLanguages.swift, 16 Nov 2026, Lunalyr's own lines, ported
// exactly). Action words match the user's list first (English forms, and any word in another language they add), then
// built-in lists for the languages people most often sing in. A built-in list is only used where the line can be in
// that language: Korean on Hangul, Japanese on kana or kanji, Chinese on hanzi without kana, Vietnamese on its own
// letters; the Latin-script lists (Spanish, Portuguese, French, German, Italian, Indonesian, Tagalog) on any line, so
// they are kept to words and stems that are not English words. Missing a word is preferred over moving the wrong one.

using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Compositions;

public static class ActionLexicon
{
    /// <summary>What scripts a line is written in.</summary>
    public readonly record struct Script(bool Hangul, bool Kana, bool Han, bool Vietnamese)
    {
        public Script(string? text) : this(false, false, false, false)
        {
            foreach (var rune in (text ?? "").EnumerateRunes())
            {
                switch (rune.Value)
                {
                    case >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F: Hangul = true; break;
                    case >= 0x3040 and <= 0x30FF: Kana = true; break;
                    case >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0xF900 and <= 0xFAFF: Han = true; break;
                    case >= 0x1EA0 and <= 0x1EF9 or 0x01A0 or 0x01A1 or 0x01AF or 0x01B0 or 0x0110 or 0x0111 or 0x0102 or 0x0103:
                        Vietnamese = true; break;
                }
            }
        }

        public bool Cjk => Hangul || Kana || Han;
    }

    /// <summary>
    /// A word as the lists hold it (the Mac's <c>fold</c>, matched to Foundation's results): lowercased, Latin accents
    /// stripped (ß → ss), full-width forms folded, letters, marks and digits only. Other scripts are kept as written:
    /// kana keep their dakuten, Hangul is never split into jamo, and đ stays đ.
    /// </summary>
    public static string Fold(string? word)
    {
        if (string.IsNullOrEmpty(word)) return "";
        var kept = new StringBuilder(word.Length);
        foreach (var rune in word.EnumerateRunes())
        {
            var value = rune.Value;
            // Full-width ASCII forms (Ａ → a).
            if (value is >= 0xFF01 and <= 0xFF5E) value -= 0xFEE0;
            var r = new Rune(value);
            if (IsLatin(value))
            {
                // Strip the Latin letter's accents: its canonical decomposition without the combining marks.
                foreach (var part in r.ToString().Normalize(NormalizationForm.FormD).EnumerateRunes())
                    if (Rune.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark) Append(kept, part);
            }
            else
            {
                Append(kept, r);
            }
        }
        return kept.ToString();

        static void Append(StringBuilder kept, Rune rune)
        {
            if (rune.Value == 'ß' || rune.Value == 'ẞ')
            {
                kept.Append("ss");
                return;
            }
            var lower = Rune.ToLowerInvariant(rune);
            var category = Rune.GetUnicodeCategory(lower);
            if (Rune.IsLetterOrDigit(lower) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber)
                kept.Append(lower.ToString());
        }

        static bool IsLatin(int value) => value is < 0x0250 or >= 0x1E00 and <= 0x1EFF;
    }

    /// <summary>
    /// The action <paramref name="text"/> (a run as written) takes, and the list key it matched: the user's list first,
    /// then the built-in lists for the line's <paramref name="script"/>.
    /// </summary>
    public static (string Key, WordAction Action)? Match(string text, IReadOnlyDictionary<string, WordAction> user, Script script)
    {
        foreach (var form in WordAccents.EnglishForms(text))
            if (user.TryGetValue(form, out var english)) return (form, english);
        var folded = Fold(text);
        if (folded.Length == 0) return null;
        if (user.TryGetValue(folded, out var own)) return (folded, own);
        if (script.Cjk)
        {
            // The user's own CJK words: contained in the run, longest first.
            string? best = null;
            foreach (var key in user.Keys)
            {
                if (!new Script(key).Cjk || !folded.Contains(key, StringComparison.Ordinal)) continue;
                if (best is null || key.Length > best.Length || key.Length == best.Length && string.CompareOrdinal(key, best) < 0)
                    best = key;
            }
            if (best is not null) return (best, user[best]);
            if (script.Hangul && Korean.Match(text) is { } ko) return ko;
            if ((script.Kana || script.Han) && Japanese.Match(text) is { } ja) return ja;
            if (script.Han && !script.Kana && Chinese.Match(text) is { } zh) return zh;
        }
        if (script.Vietnamese)
        {
            var lowered = text.ToLowerInvariant();
            if (VietnameseWords.TryGetValue(TrimPunctuation(lowered), out var vi)) return ("vi:" + lowered, vi);
        }
        // French and Italian elide an article onto the word (l'obscurité, d'amore): the word after the apostrophe too.
        var after = "";
        var cut = Math.Max(text.LastIndexOf('\''), text.LastIndexOf('’'));
        if (cut >= 0)
        {
            after = Fold(text[(cut + 1)..]);
            if (after == folded) after = "";
        }
        foreach (var list in Latin)
            if (list.Match(folded) is { } found) return found;
        if (after.Length > 0)
            foreach (var list in Latin)
                if (list.Match(after) is { } found) return found;
        return null;
    }

    private static string TrimPunctuation(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsPunctuation(text[start])) start++;
        while (end > start && IsPunctuation(text[end - 1])) end--;
        return text[start..end];

        static bool IsPunctuation(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    }

    // ---- Latin-script lists ----

    /// <summary>Whole words (folded), and stems a word may run on from by at most four letters.</summary>
    public sealed class LatinList(string language, Dictionary<string, WordAction> words, (string Stem, WordAction Action)[] stems,
                                  HashSet<string> exclude)
    {
        public (string Key, WordAction Action)? Match(string folded)
        {
            if (exclude.Contains(folded)) return null;
            if (words.TryGetValue(folded, out var action)) return ($"{language}:{folded}", action);
            foreach (var (stem, stemAction) in stems)
                if (folded.StartsWith(stem, StringComparison.Ordinal) && folded.Length - stem.Length <= 4)
                    return ($"{language}:{stem}", stemAction);
            return null;
        }
    }

    private static (string, WordAction)[] W(WordAction action, params string[] list) => [.. list.Select(word => (word, action))];

    private static LatinList List(string language, (string, WordAction)[][] groups, (string, WordAction)[] stems, params string[] exclude)
    {
        var words = new Dictionary<string, WordAction>(StringComparer.Ordinal);
        foreach (var group in groups)
            foreach (var (word, action) in group) words[word] = action;
        return new LatinList(language, words, stems, new HashSet<string>(exclude, StringComparer.Ordinal));
    }

    private const WordAction Spin = WordAction.Spin, Float = WordAction.Float, Dip = WordAction.Dip, Shake = WordAction.Shake,
        Hop = WordAction.Hop, Grow = WordAction.Grow, Shrink = WordAction.Shrink, Dissolve = WordAction.Dissolve,
        Flicker = WordAction.Flicker, Frost = WordAction.Frost, Bleed = WordAction.Bleed, Dark = WordAction.Dark,
        Shatter = WordAction.Shatter, Melt = WordAction.Melt, Echo = WordAction.Echo, Pulse = WordAction.Pulse,
        Wave = WordAction.Wave, Sink = WordAction.Sink;

    public static readonly LatinList Spanish = List("es", [
        W(Spin, "gira", "giras", "giro", "girar", "girando", "giramos"),
        W(Float, "volar", "vuelo", "vuela", "vuelas", "volando", "flota", "flotar", "flotando"),
        W(Dip, "caer", "cae", "caes", "caigo", "cayo", "caida", "cayendo", "caemos", "caen"),
        W(Hop, "saltar", "salto", "salta", "saltas", "saltando", "brinca", "brincar", "brincando"),
        W(Flicker, "fuego", "arde", "ardo", "arder", "ardes", "ardiendo"),
        W(Frost, "hielo", "frio", "fria", "helado", "helada"),
        W(Dark, "sombra", "sombras", "tinieblas"),
        W(Shatter, "rompe", "romper", "rompes", "rompi", "rompio", "rompiendo", "rompo", "roto", "rota", "pedazos"),
        W(Echo, "eco", "ecos"),
        W(Pulse, "latido", "latidos", "latir"),
        W(Wave, "olas", "mar", "oceano", "marea"),
        W(Sink, "hundir", "hundo", "hunde", "hundiendo", "hundirme", "profundo", "profunda"),
        W(Hop, "bailar", "baila", "bailas", "bailo", "bailando", "bailamos", "baile", "perreo"),
        W(Float, "cielo", "alas"),
        W(Dissolve, "olvidar", "olvido", "olvida", "olvidame", "olvidarte"),
        W(Frost, "nieve"),
    ], [
        ("tembl", Shake), ("tiembl", Shake), ("sacud", Shake),
        ("crece", Grow), ("crezc", Grow), ("creci", Grow),
        ("encog", Shrink), ("desapare", Dissolve), ("desvanec", Dissolve),
        ("quema", Flicker), ("quemo", Flicker), ("congel", Frost),
        ("sangr", Bleed), ("oscur", Dark), ("quebr", Shatter),
        ("derrit", Melt), ("derret", Melt), ("ahog", Sink),
    ], "sangria", "sangrias");

    public static readonly LatinList Portuguese = List("pt", [
        W(Float, "voar", "voo", "voa", "voando", "voei"),
        W(Dip, "cair", "cai", "caio", "caiu", "caindo"),
        W(Shake, "tremer", "tremo", "treme", "tremendo"),
        W(Hop, "pular", "pulo", "pula", "pulando"),
        W(Flicker, "fogo", "fogueira"),
        W(Frost, "gelo", "gelado", "gelada"),
        W(Bleed, "sangue"),
        W(Echo, "eco"),
        W(Wave, "onda", "ondas", "mar", "oceano"),
        W(Grow, "crescer", "cresce", "cresceu", "cresco", "crescendo"),
        W(Hop, "dancar", "danca", "dancando", "danco", "rebola", "rebolar"),
        W(Float, "ceu", "asas"),
        W(Dissolve, "esquecer", "esqueci", "esquece", "sumir", "sumiu"),
        W(Frost, "neve"),
    ], [
        ("flutu", Float), ("encolh", Shrink), ("desapare", Dissolve),
        ("queim", Flicker), ("congel", Frost), ("sangr", Bleed), ("escur", Dark),
        ("quebr", Shatter), ("derret", Melt), ("batiment", Pulse), ("afund", Sink), ("afog", Sink),
    ], "tremendo", "sangria", "sangrias");

    public static readonly LatinList French = List("fr", [
        W(Spin, "tourne", "tourner", "tournent", "tournoie", "tournoyer"),
        W(Dip, "tomber", "tombe", "tombes", "tombent", "tombee", "tombait", "tombant", "tombons", "tombez"),
        W(Flicker, "feu", "flamme", "flammes"),
        W(Frost, "glace", "froid", "froide", "gele", "geler", "gelee", "glacee"),
        W(Bleed, "saigne", "saigner", "saignant", "saignent"),
        W(Dark, "obscurite", "tenebres", "ombre", "ombres"),
        W(Shatter, "casse", "casser", "cassee", "eclats", "brisee", "briser"),
        W(Melt, "fondre", "fondu", "fondent"),
        W(Grow, "grandir", "grandis", "grandit", "grandissent", "grandissant"),
        W(Hop, "danse", "danser", "dansons", "dansent", "dansez"),
        W(Float, "ciel", "ailes"),
        W(Dissolve, "oublier", "oublie", "oublies", "oubliee"),
        W(Frost, "neige"),
        W(Sink, "profond", "profonde"),
        W(Wave, "mer", "houle"),
        W(Sink, "noie", "noyer", "noyee", "sombrer", "sombrent"),
    ], [
        ("envol", Float), ("flott", Float), ("trembl", Shake), ("saut", Hop),
        ("retreci", Shrink), ("disparai", Dissolve), ("disparu", Dissolve),
        ("brul", Flicker), ("battement", Pulse),
    ], "grandiose", "sauterelle");

    public static readonly LatinList German = List("de", [
        W(Dip, "fallen", "fallt", "fiel", "fielen", "falle", "fallst"),
        W(Flicker, "feuer", "flamme", "flammen"),
        W(Frost, "eis", "kalt", "kalte", "friere", "frieren", "friert", "gefroren", "erfroren"),
        W(Hop, "springen", "springt", "sprang"),
        W(Dark, "schatten", "finster", "finsternis"),
        W(Shatter, "gebrochen", "zerbrochen"),
        W(Echo, "widerhall"),
        W(Shake, "beben", "bebt", "bebte", "bebten"),
        W(Hop, "tanzen", "tanz", "tanzt", "tanze", "tanzte"),
        W(Float, "himmel", "flugel", "luftballons", "luftballon"),
        W(Dissolve, "vergessen", "vergiss", "vergess", "vergesse"),
        W(Frost, "schnee"),
        W(Sink, "tief", "tiefer", "tiefe"),
        W(Wave, "welle", "wellen", "meer", "ozean"),
        W(Sink, "sinken", "sinkt", "versinken", "versinkt", "versank"),
    ], [
        ("flieg", Float), ("schweb", Float), ("dreh", Spin), ("zitter", Shake),
        ("hupf", Hop), ("wachs", Grow), ("schrumpf", Shrink), ("verschw", Dissolve), ("verblass", Dissolve),
        ("brenn", Flicker), ("brannt", Flicker), ("blut", Bleed), ("dunkel", Dark), ("dunkl", Dark),
        ("zerbr", Shatter), ("schmelz", Melt), ("schmolz", Melt), ("schmilz", Melt), ("herzschlag", Pulse),
        ("ertrink", Sink), ("ertrank", Sink),
    ], "gefallen", "drehbuch");

    public static readonly LatinList Italian = List("it", [
        W(Spin, "gira", "giro", "girare", "girando"),
        W(Float, "volare", "volo", "vola", "voli", "volando", "volato"),
        W(Dip, "cado", "cadi", "cade", "cadono", "caduto", "caduta", "cadendo", "cadere"),
        W(Shake, "tremo", "trema", "tremare", "tremando"),
        W(Hop, "salta", "salto", "saltare", "saltando"),
        W(Flicker, "fuoco", "fiamme", "fiamma"),
        W(Frost, "gelo", "ghiaccio", "freddo", "fredda", "gela"),
        W(Bleed, "sangue"),
        W(Dark, "buio", "ombra", "ombre"),
        W(Shatter, "rotto", "rotta", "frantumi"),
        W(Echo, "eco"),
        W(Pulse, "battito", "battiti"),
        W(Wave, "onda", "mare", "oceano"),
        W(Grow, "crescere", "cresce", "cresci", "cresco", "cresciuto"),
        W(Hop, "ballare", "balla", "ballo", "ballando", "balliamo"),
        W(Float, "cielo", "ali"),
        W(Dissolve, "dimenticare", "dimentico", "dimentica", "dimenticato"),
        W(Frost, "neve"),
        W(Sink, "profondo", "profonda"),
    ], [
        ("svani", Dissolve), ("scompar", Dissolve), ("bruci", Flicker), ("congel", Frost),
        ("oscur", Dark), ("spezz", Shatter), ("sciogl", Melt), ("sciolt", Melt),
        ("affond", Sink), ("annega", Sink),
    ]);

    public static readonly LatinList Indonesian = List("id", [
        W(Spin, "putar", "berputar", "memutar"),
        W(Float, "terbang", "melayang"),
        W(Dip, "jatuh", "terjatuh"),
        W(Shake, "gemetar", "bergetar"),
        W(Hop, "lompat", "melompat"),
        W(Grow, "tumbuh", "bertumbuh"),
        W(Dissolve, "hilang", "menghilang", "lenyap"),
        W(Flicker, "terbakar", "membakar", "bakar"),
        W(Frost, "beku", "membeku", "dingin", "salju"),
        W(Bleed, "darah", "berdarah"),
        W(Dark, "gelap", "kegelapan"),
        W(Shatter, "hancur", "patah", "retak"),
        W(Melt, "meleleh", "mencair"),
        W(Echo, "gema", "bergema"),
        W(Pulse, "detak", "berdetak"),
        W(Wave, "ombak", "lautan", "gelombang", "samudra"),
        W(Sink, "tenggelam"),
    ], []);

    public static readonly LatinList Tagalog = List("tl", [
        W(Spin, "ikot", "umiikot", "umikot"),
        W(Float, "lipad", "lumipad", "lumilipad"),
        W(Dip, "hulog", "nahulog", "mahulog", "bumagsak"),
        W(Shake, "nanginginig", "nanginig"),
        W(Hop, "tumalon", "tumatalon"),
        W(Grow, "lumaki", "lumalaki"),
        W(Dissolve, "nawala", "naglaho", "maglaho"),
        W(Flicker, "apoy", "nasusunog", "nagliliyab"),
        W(Frost, "yelo", "lamig", "malamig"),
        W(Bleed, "dugo", "duguan"),
        W(Dark, "dilim", "madilim", "kadiliman"),
        W(Shatter, "basag", "nabasag", "wasak", "durog"),
        W(Melt, "natunaw", "matunaw"),
        W(Echo, "alingawngaw"),
        W(Pulse, "tibok"),
        W(Wave, "alon", "dagat", "karagatan"),
        W(Sink, "nalunod", "lunod", "lumubog"),
    ], []);

    /// <summary>The Latin-script lists, in the Mac's order.</summary>
    public static readonly IReadOnlyList<LatinList> Latin = [Spanish, Portuguese, French, German, Italian, Indonesian, Tagalog];

    /// <summary>Vietnamese: whole syllables with their tone marks, only on a line with Vietnamese letters.</summary>
    public static readonly IReadOnlyDictionary<string, WordAction> VietnameseWords = new Dictionary<string, WordAction>(StringComparer.Ordinal)
    {
        ["xoay"] = Spin, ["bay"] = Float, ["rơi"] = Dip, ["run"] = Shake, ["nhảy"] = Hop,
        ["cháy"] = Flicker, ["lửa"] = Flicker, ["băng"] = Frost, ["lạnh"] = Frost, ["máu"] = Bleed,
        ["vỡ"] = Shatter, ["sóng"] = Wave, ["biển"] = Wave, ["chìm"] = Sink,
    };

    // ---- CJK lists ----

    /// <summary>Words a run contains (longest first), and longer words that contain one but mean something else.</summary>
    public sealed class ContainedList
    {
        private readonly string _language;
        private readonly (string Word, WordAction Action)[] _entries;
        private readonly string[] _exclude;

        public ContainedList(string language, (string, WordAction)[][] groups, params string[] exclude)
        {
            _language = language;
            _entries = [.. groups.SelectMany(group => group)
                .OrderByDescending(entry => entry.Item1.Length).ThenBy(entry => entry.Item1, StringComparer.Ordinal)];
            _exclude = exclude;
        }

        public (string Key, WordAction Action)? Match(string text)
        {
            var rest = text;
            foreach (var word in _exclude) rest = rest.Replace(word, "￼", StringComparison.Ordinal);
            foreach (var (word, action) in _entries)
                if (rest.Contains(word, StringComparison.Ordinal)) return ($"{_language}:{word}", action);
            return null;
        }
    }

    public static readonly ContainedList Japanese = new("ja", [
        W(Spin, "回る", "回って", "回り", "廻る", "くるくる", "回転"),
        W(Float, "飛ぶ", "飛ん", "飛べ", "飛び", "舞い", "浮か", "浮い"),
        W(Dip, "落ちる", "落ち", "堕ち"),
        W(Shake, "震え", "揺れ", "ふるえ"),
        W(Hop, "跳ね", "跳ん", "跳び", "跳ぶ", "ジャンプ"),
        W(Grow, "育っ", "育つ"),
        W(Shrink, "縮"),
        W(Dissolve, "消え", "薄れ"),
        W(Flicker, "燃え", "燃や", "炎", "焦が"),
        W(Frost, "凍え", "凍り", "凍る", "氷", "冷た"),
        W(Bleed, "血"),
        W(Dark, "闇", "暗"),
        W(Shatter, "壊れ", "砕け", "割れ", "崩れ"),
        W(Melt, "溶け", "溶か"),
        W(Echo, "響", "こだま", "木霊"),
        W(Pulse, "鼓動"),
        W(Wave, "波", "海"),
        W(Sink, "沈", "溺れ", "深い", "深く"),
        W(Hop, "踊", "ダンス"),
        W(Float, "翼"),
        W(Frost, "雪"),
        W(Dissolve, "忘れ"),
    ], "影響", "暗記", "暗号", "落ち着", "回答", "今回", "一回", "毎回", "前回", "海外", "暗恋", "暗戀", "沈黙");

    public static readonly ContainedList Chinese = new("zh", [
        W(Spin, "旋转", "旋轉", "转圈", "轉圈", "回旋"),
        W(Float, "飞", "飛", "飘", "飄", "漂浮"),
        W(Dip, "坠落", "墜落", "跌落", "掉落", "落下"),
        W(Shake, "颤抖", "顫抖", "发抖", "發抖", "摇晃", "搖晃"),
        W(Hop, "跳"),
        W(Grow, "成长", "成長", "长大", "長大"),
        W(Shrink, "缩小", "縮小"),
        W(Dissolve, "消失", "消散"),
        W(Flicker, "燃烧", "燃燒", "火焰", "燃"),
        W(Frost, "冰", "冻", "凍"),
        W(Bleed, "血"),
        W(Dark, "黑暗", "暗"),
        W(Shatter, "破碎", "碎", "崩溃", "崩潰"),
        W(Melt, "融化", "溶化"),
        W(Echo, "回声", "回聲", "回音"),
        W(Pulse, "心跳"),
        W(Wave, "海浪", "波浪", "浪花", "海"),
        W(Sink, "沉没", "沉沒", "下沉", "沉入", "淹没", "淹沒", "深海"),
        W(Hop, "跳舞", "舞"),
        W(Float, "翅膀", "天空"),
        W(Frost, "雪"),
        W(Dissolve, "忘记", "忘記"),
        W(Flicker, "火"),
    ], "暗恋", "暗戀", "飞机", "飛機", "海报", "海報", "火车", "火車", "雪碧");

    public static readonly ContainedList Korean = new("ko", [
        W(Spin, "빙빙", "회전"),
        W(Float, "날아", "떠올라", "떠다니", "떠다녀"),
        W(Dip, "떨어지", "떨어져", "떨어졌"),
        W(Shake, "떨려", "떨리", "흔들"),
        W(Hop, "뛰어올", "점프"),
        W(Grow, "자라나", "자라고", "자랐"),
        W(Shrink, "작아져", "작아지"),
        W(Dissolve, "사라지", "사라져", "사라졌"),
        W(Flicker, "불타", "타오르", "타올라", "불꽃"),
        W(Frost, "얼어", "얼음", "차가운"),
        W(Bleed, "피가", "피를", "피눈물"),
        W(Dark, "어둠", "어두"),
        W(Shatter, "부서지", "부서져", "깨져", "깨지"),
        W(Melt, "녹아", "녹는"),
        W(Echo, "메아리"),
        W(Pulse, "두근", "심장 소리"),
        W(Wave, "파도", "바다"),
        W(Sink, "가라앉", "잠겨", "깊은", "깊이"),
        W(Hop, "춤", "댄스"),
        W(Float, "날개", "하늘"),
        W(Dissolve, "잊어", "잊혀", "잊지"),
        W(Flicker, "뜨거운"),
    ], "커피", "멈춤");
}
