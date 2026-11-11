using System.Text;

namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>
/// Lines a romaji reading up with the Japanese line it reads. Kana are the anchors: each must be spelled as it is
/// written, allowing the usual variants (wa for は, o for を, Kunrei-shiki si, tu and syo, a long vowel doubled,
/// marked with a macron or left out). Kanji take what lies between the anchors, and where kanji meet with no kana
/// between them the system's own reading decides the split. A reading spelled by syllable (NetEase's
/// "yu me na ra ba"), by word or with no spaces lands on the characters it reads, and a reading of different words
/// does not fit at all. Ported from the Mac's JapaneseAlignment.swift. "Characters" are grapheme clusters, held as
/// strings; ranges are UTF-16.
/// </summary>
public static class JapaneseAlignment
{
    /// <summary>A stretch of the line a reading spells as one: a run of kanji, a kana with its small kana (and a っ
    /// before it), a lone っ, a ー, a Latin word, or punctuation, which is not spelled.</summary>
    public sealed class Unit
    {
        public enum UnitKind { Kanji, Kana, Sokuon, Latin, Mark }

        public UnitKind Kind { get; set; }
        public int Location { get; init; }
        public int Length { get; set; }
        public string Source { get; set; } = "";
        /// <summary>UTF-16 ranges of the characters, to share a reading out.</summary>
        public List<(int Location, int Length)> Characters { get; init; } = [];
        /// <summary>The generated piece the unit is part of.</summary>
        public int? Piece { get; init; }
        /// <summary>A っ comes first (った tta).</summary>
        public bool Geminated { get; set; }
        /// <summary>ー, which lengthens the vowel before it.</summary>
        public bool Lengthens { get; init; }
        public List<Spelling> Spellings { get; set; } = [];

        private List<string>? _hint;

        /// <summary>The system's reading of a kanji run, normalised, and with long vowels written short.</summary>
        public List<string>? Hint
        {
            get => _hint;
            set { _hint = value; ShortHint = value is null ? null : Collapsed(value); }
        }

        public string? ShortHint { get; private set; }
    }

    public readonly record struct Spelling(List<string> Letters, double Cost);

    /// <summary>A character's part of a reading, and the phrase it belongs to.</summary>
    public record struct CharacterReading(int Location, int Length, string Text, int Phrase);

    /// <summary>A line read by the system, laid out in units.</summary>
    public sealed class Analysis(List<JapaneseReading.Piece> pieces, List<Unit> units, List<string> generated, List<int> phrases)
    {
        public List<JapaneseReading.Piece> Pieces { get; } = pieces;
        public List<Unit> Units { get; } = units;
        /// <summary>Each unit's part of the generated reading.</summary>
        public List<string> Generated { get; } = generated;
        /// <summary>The phrase (pieces written together) each unit belongs to.</summary>
        public List<int> Phrases { get; } = phrases;

        /// <summary>Each unit's part of <paramref name="saved"/>, or null when it does not read this line. The parts
        /// are written in Hepburn, as the app's own readings are (<see cref="Hepburn"/>), whichever system the
        /// source used.</summary>
        public List<string>? Align(string saved)
        {
            // A reading in kana or kanji is not romaji.
            if (saved.EnumerateRunes().Any(JapaneseReading.IsJapanese)) return null;
            var reading = new Reading(saved);
            if (reading.Letters.Count == 0 || JapaneseAlignment.Align(reading, Units) is not { } ranges) return null;
            var parts = ranges.Select(reading.Raw).ToList();
            // A kanji read otherwise than the app reads it is respelled only when the reading shows elsewhere that it
            // writes Kunrei-shiki: an English word sung for a kanji (passion, vision, Asia) is shaped like romaji too,
            // and is kept as written.
            var kunrei = Enumerable.Range(0, Units.Count).Any(index =>
                Units[index].Kind is Unit.UnitKind.Kana or Unit.UnitKind.Kanji
                && WritesNonHepburn(parts[index], Generated[index]));
            return Enumerable.Range(0, Units.Count).Select(index => Units[index].Kind switch
            {
                Unit.UnitKind.Mark => Generated[index],
                Unit.UnitKind.Latin => ranges[index].Start == ranges[index].End ? "" : Units[index].Source,
                _ => Hepburn(parts[index], Generated[index], kunrei && Units[index].Kind == Unit.UnitKind.Kanji),
            }).ToList();
        }

        /// <summary>The generated pieces, respelled with <paramref name="readings"/> (one per unit).</summary>
        public List<JapaneseReading.Piece> PiecesReading(List<string> readings)
        {
            var result = Pieces.Select(piece => piece with { Text = "" }).ToList();
            for (var index = 0; index < Units.Count; index++)
            {
                if (readings[index].Length == 0) continue;
                var unit = Units[index];
                if (unit.Piece is { } piece)
                {
                    result[piece].Text += readings[index];
                }
                else
                {
                    result.Add(new JapaneseReading.Piece
                    {
                        Text = readings[index], Location = unit.Location, Length = unit.Length, BreakBefore = true,
                    });
                }
            }
            return result.OrderBy(piece => piece.Location).ToList();
        }

        /// <summary>Each character's share of <paramref name="readings"/> (one per unit), for lines timed one
        /// character at a time: a kana carries its own syllable, a run of kanji is shared out by syllable (今日 kyo,
        /// u), a っ goes with the kana after it and a ー with the one before.</summary>
        public List<CharacterReading> Characters(List<string> readings)
        {
            var result = new List<CharacterReading>();
            var carried = "";
            var previousEnd = -1;
            int? filled = null;
            for (var index = 0; index < Units.Count; index++)
            {
                var unit = Units[index];
                var text = readings[index];
                var phrase = Phrases[index];
                var adjacent = previousEnd == unit.Location;
                previousEnd = unit.Location + unit.Length;
                void Add((int Location, int Length) range, string part)
                {
                    if (part.Length > 0) filled = result.Count;
                    result.Add(new CharacterReading(range.Location, range.Length, part, phrase));
                }
                if (carried.Length > 0 && adjacent && unit.Kind is Unit.UnitKind.Kana or Unit.UnitKind.Kanji) text = carried + text;
                carried = "";
                switch (unit.Kind)
                {
                    case Unit.UnitKind.Mark:
                        if (text.Length > 0 && filled is { } markTarget)
                        {
                            result[markTarget] = result[markTarget] with { Text = result[markTarget].Text + text };
                        }
                        foreach (var range in unit.Characters) Add(range, "");
                        break;
                    case Unit.UnitKind.Sokuon:
                        carried = text;
                        foreach (var range in unit.Characters) Add(range, "");
                        break;
                    case Unit.UnitKind.Kana when unit.Lengthens:
                        if (adjacent && filled is { } target && result[target].Phrase == phrase)
                        {
                            result[target] = result[target] with { Text = result[target].Text + text };
                            text = "";
                        }
                        foreach (var range in unit.Characters) Add(range, text);
                        break;
                    case Unit.UnitKind.Kana:
                    {
                        var head = unit.Geminated ? 1 : 0;
                        for (var offset = 0; offset < unit.Characters.Count; offset++)
                        {
                            Add(unit.Characters[offset], offset == Math.Min(head, unit.Characters.Count - 1) ? text : "");
                        }
                        break;
                    }
                    case Unit.UnitKind.Kanji:
                    {
                        var shares = Share(text, unit.Characters.Count);
                        for (var offset = 0; offset < unit.Characters.Count && offset < shares.Count; offset++)
                        {
                            Add(unit.Characters[offset], shares[offset]);
                        }
                        break;
                    }
                    case Unit.UnitKind.Latin:
                        for (var offset = 0; offset < unit.Characters.Count; offset++)
                        {
                            Add(unit.Characters[offset], offset == 0 ? text : "");
                        }
                        break;
                }
            }
            return result;
        }
    }

    public static Analysis Analyse(string text, JapaneseReading reader)
    {
        var pieces = reader.Pieces(text);
        var units = MakeUnits(text, pieces);
        var generated = Enumerable.Repeat("", units.Count).ToList();
        // Each piece's reading laid on its own units gives every kanji run the system's reading of it.
        for (var index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            var members = Enumerable.Range(0, units.Count).Where(member => units[member].Piece == index).ToList();
            if (members.Count == 0) continue;
            var reading = new Reading(piece.Text);
            if (piece.IsJapanese && Align(reading, members.Select(member => units[member]).ToList()) is { } ranges)
            {
                for (var offset = 0; offset < members.Count; offset++)
                {
                    var member = members[offset];
                    generated[member] = reading.Raw(ranges[offset]);
                    if (units[member].Kind == Unit.UnitKind.Kanji)
                    {
                        units[member].Hint = reading.Letters.GetRange(ranges[offset].Start, ranges[offset].End - ranges[offset].Start);
                    }
                }
            }
            else
            {
                generated[members[0]] = piece.Text;
            }
        }
        var phrase = -1;
        var piecePhrases = pieces.Select(piece =>
        {
            if (!piece.JoinsPrevious || phrase < 0) phrase++;
            return phrase;
        }).ToList();
        // A kanji nothing read is a phrase of its own.
        var phrases = Enumerable.Range(0, units.Count)
            .Select(index => units[index].Piece is { } piece ? piecePhrases[piece] : phrase + 1 + index).ToList();
        return new Analysis(pieces, units, generated, phrases);
    }

    // MARK: - Units

    private enum Class { Kanji, Kana, Small, Sokuon, Long, Voicing, Digit, Latin, Mark, Space }

    private static Class Classify(ReadingText.Grapheme character)
    {
        var scalar = character.First.Value;
        if (character.IsWhitespace || scalar is 0x30FB or 0xFF65) return Class.Space;
        switch (scalar)
        {
            case 0x3063 or 0x30C3 or 0xFF6F: return Class.Sokuon;
            case 0x30FC or 0xFF70: return Class.Long;
            case >= 0x3099 and <= 0x309C or 0xFF9E or 0xFF9F: return Class.Voicing;
            case 0x3041 or 0x3043 or 0x3045 or 0x3047 or 0x3049 or 0x3083 or 0x3085 or 0x3087 or 0x308E or 0x3095 or 0x3096
                or 0x30A1 or 0x30A3 or 0x30A5 or 0x30A7 or 0x30A9 or 0x30E3 or 0x30E5 or 0x30E7 or 0x30EE or 0x30F5 or 0x30F6
                or >= 0x31F0 and <= 0x31FF or >= 0xFF67 and <= 0xFF6E:
                // ヵ and ヶ count as small only after kana; alone they are counters.
                return Class.Small;
            case >= 0x3005 and <= 0x3007 or 0x309D or 0x309E or 0x30FD or 0x30FE: return Class.Kanji; // 々 〆 〇 ゝ ゞ ヽ ヾ
            case >= 0x3041 and <= 0x3096 or >= 0x30A1 and <= 0x30FA or >= 0xFF66 and <= 0xFF9D: return Class.Kana;
        }
        if (ReadingText.IsIdeographic(character.First)) return Class.Kanji;
        if (character.IsNumber) return Class.Digit;
        if (character.IsLetter) return Class.Latin;
        return Class.Mark;
    }

    private static List<Unit> MakeUnits(string text, List<JapaneseReading.Piece> pieces)
    {
        var units = new List<Unit>();
        var pieceIndex = 0;
        foreach (var character in ReadingText.Graphemes(text))
        {
            var offset = character.Offset;
            var width = character.Length;
            while (pieceIndex < pieces.Count && pieces[pieceIndex].Location + pieces[pieceIndex].Length <= offset) pieceIndex++;
            int? piece = pieceIndex < pieces.Count && pieces[pieceIndex].Location <= offset ? pieceIndex : null;
            var range = (offset, width);
            var kind = Classify(character);
            if (kind == Class.Space) continue;
            if (units.Count > 0 && units[^1] is var last && last.Piece == piece && last.Location + last.Length == offset)
            {
                var extends = false;
                switch (last.Kind, kind)
                {
                    case (Unit.UnitKind.Kanji, Class.Kanji) or (Unit.UnitKind.Kanji, Class.Digit)
                        or (Unit.UnitKind.Latin, Class.Latin) or (Unit.UnitKind.Latin, Class.Digit):
                        extends = true;
                        break;
                    case (Unit.UnitKind.Kanji, Class.Small) when "ヵヶゕゖ".Contains(character.Text, StringComparison.Ordinal):
                        extends = true;                                                         // 一ヶ月
                        break;
                    case (Unit.UnitKind.Latin, Class.Kanji) when ReadingText.Graphemes(last.Source).All(g => g.IsNumber):
                        last.Kind = Unit.UnitKind.Kanji;                                        // 2人
                        extends = true;
                        break;
                    case (Unit.UnitKind.Kana, Class.Small) when !last.Lengthens:
                    case (Unit.UnitKind.Kana, Class.Voicing) when !last.Lengthens:
                        extends = true;
                        break;
                    case (Unit.UnitKind.Sokuon, Class.Kana):
                        last.Kind = Unit.UnitKind.Kana;                                         // った
                        last.Geminated = true;
                        extends = true;
                        break;
                }
                if (extends)
                {
                    last.Length += width;
                    last.Source += character.Text;
                    last.Characters.Add(range);
                    continue;
                }
            }
            var lengthens = false;
            switch (kind)
            {
                case Class.Small: kind = Class.Kana; break;
                case Class.Long: kind = Class.Kana; lengthens = true; break;
                case Class.Voicing: kind = Class.Mark; break;
                case Class.Digit: kind = Class.Latin; break;
            }
            var unitKind = kind switch
            {
                Class.Kanji => Unit.UnitKind.Kanji,
                Class.Kana => Unit.UnitKind.Kana,
                Class.Sokuon => Unit.UnitKind.Sokuon,
                Class.Latin => Unit.UnitKind.Latin,
                _ => Unit.UnitKind.Mark,
            };
            units.Add(new Unit
            {
                Kind = unitKind, Location = offset, Length = width, Source = character.Text, Characters = [range],
                Piece = piece, Lengthens = lengthens,
            });
        }
        for (var index = 0; index < units.Count; index++)
        {
            var previous = index > 0 && units[index - 1].Location + units[index - 1].Length == units[index].Location
                ? units[index - 1] : null;
            units[index].Spellings = Spellings(units[index], previous);
        }
        return units;
    }

    // MARK: - Spelling

    private static readonly Dictionary<string, string> Kana = new()
    {
        ["あ"] = "a", ["い"] = "i", ["う"] = "u", ["え"] = "e", ["お"] = "o",
        ["か"] = "ka", ["き"] = "ki", ["く"] = "ku", ["け"] = "ke", ["こ"] = "ko", ["が"] = "ga", ["ぎ"] = "gi", ["ぐ"] = "gu", ["げ"] = "ge", ["ご"] = "go",
        ["さ"] = "sa", ["し"] = "shi", ["す"] = "su", ["せ"] = "se", ["そ"] = "so", ["ざ"] = "za", ["じ"] = "ji", ["ず"] = "zu", ["ぜ"] = "ze", ["ぞ"] = "zo",
        ["た"] = "ta", ["ち"] = "chi", ["つ"] = "tsu", ["て"] = "te", ["と"] = "to", ["だ"] = "da", ["ぢ"] = "ji", ["づ"] = "zu", ["で"] = "de", ["ど"] = "do",
        ["な"] = "na", ["に"] = "ni", ["ぬ"] = "nu", ["ね"] = "ne", ["の"] = "no",
        ["は"] = "ha", ["ひ"] = "hi", ["ふ"] = "fu", ["へ"] = "he", ["ほ"] = "ho", ["ば"] = "ba", ["び"] = "bi", ["ぶ"] = "bu", ["べ"] = "be", ["ぼ"] = "bo",
        ["ぱ"] = "pa", ["ぴ"] = "pi", ["ぷ"] = "pu", ["ぺ"] = "pe", ["ぽ"] = "po",
        ["ま"] = "ma", ["み"] = "mi", ["む"] = "mu", ["め"] = "me", ["も"] = "mo", ["や"] = "ya", ["ゆ"] = "yu", ["よ"] = "yo",
        ["ら"] = "ra", ["り"] = "ri", ["る"] = "ru", ["れ"] = "re", ["ろ"] = "ro", ["わ"] = "wa", ["ゐ"] = "i", ["ゑ"] = "e", ["を"] = "o", ["ん"] = "n",
        ["ゔ"] = "vu", ["ぁ"] = "a", ["ぃ"] = "i", ["ぅ"] = "u", ["ぇ"] = "e", ["ぉ"] = "o", ["ゃ"] = "ya", ["ゅ"] = "yu", ["ょ"] = "yo", ["ゎ"] = "wa",
        ["ゕ"] = "ka", ["ゖ"] = "ke",
    };

    /// <summary>Other ways these kana are written in readings.</summary>
    private static readonly Dictionary<string, string[]> Variants = new()
    {
        ["は"] = ["wa"], ["へ"] = ["e"], ["を"] = ["wo"], ["ゐ"] = ["wi"], ["ゑ"] = ["we"], ["ゔ"] = ["bu"], ["ぢ"] = ["di"], ["づ"] = ["du"],
    };

    private static List<string> Hiragana(string text)
    {
        var result = new List<string>();
        foreach (var scalar in text.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            result.Add(scalar.Value switch
            {
                >= 0x30A1 and <= 0x30F6 => char.ConvertFromUtf32(scalar.Value - 0x60),
                >= 0x31F0 and <= 0x31FF => "ぅ", // small Ainu katakana: closest small kana
                _ => scalar.ToString(),
            });
        }
        return result;
    }

    private static List<Spelling> Spellings(Unit unit, Unit? previous)
    {
        switch (unit.Kind)
        {
            case Unit.UnitKind.Latin:
                return [new Spelling(new Reading(unit.Source).Letters, 0)];
            case Unit.UnitKind.Kana when unit.Lengthens:
            {
                // ー repeats the vowel before it; readings also write ou and ei, a macron (doubled here) or nothing.
                var vowel = previous?.Spellings.FirstOrDefault().Letters?.LastOrDefault(IsVowel);
                if (vowel is null)
                {
                    return [.. "aiueo".Select(letter => new Spelling([letter.ToString()], 0.5)), new Spelling([], 0.3)];
                }
                var result = Lengthenings(vowel, vowel);
                if (vowel == "o") result.Add(new Spelling(["u"], 0.1));
                if (vowel == "e") result.Add(new Spelling(["i"], 0.1));
                return result;
            }
            case Unit.UnitKind.Kana:
            {
                var characters = Hiragana(unit.Source).Where(character => character != "っ").ToList();
                if (characters.Count == 0 || !Kana.TryGetValue(characters[0], out var @base)) return [];
                var head = characters[0];
                var forms = new List<string> { @base };
                if (Variants.TryGetValue(head, out var variants)) forms.AddRange(variants);
                foreach (var small in characters.Skip(1))
                {
                    if (!Kana.TryGetValue(small, out var sound)) continue;
                    forms = forms.SelectMany(form => Combined(form, sound)).ToList();
                }
                var result = new List<Spelling>();
                foreach (var form in forms)
                {
                    var letters = new Reading(form).Letters;
                    if (letters.Count == 0) continue;
                    var first = letters[0];
                    if (!unit.Geminated || IsVowel(first))
                    {
                        result.Add(new Spelling(letters, 0));
                    }
                    else if (letters.Count >= 2 && letters[0] == "c" && letters[1] == "h")
                    {
                        result.Add(new Spelling(["t", .. letters], 0));
                        result.Add(new Spelling(["c", .. letters], 0));
                    }
                    else
                    {
                        result.Add(new Spelling([first, .. letters], 0));
                    }
                }
                // A vowel that lengthens the one before it (そう, ねえ, せい) may be written, doubled or left out.
                if (characters.Count == 1 && "あいうえお".Contains(head, StringComparison.Ordinal) && !unit.Geminated
                    && Kana.TryGetValue(head, out var spoken) && previous?.Kind == Unit.UnitKind.Kana
                    && previous.Spellings.Count > 0 && previous.Spellings[0].Letters.Count > 0
                    && previous.Spellings[0].Letters[^1] is var before && IsVowel(before))
                {
                    var sound = spoken[..1];
                    if (sound == before || (before == "o" && sound == "u") || (before == "e" && sound == "i"))
                    {
                        return Lengthenings(before, sound);
                    }
                }
                return result;
            }
            default:
                return [];
        }
    }

    /// <summary>A sound that lengthens <paramref name="vowel"/>: as written, as the vowel again (a macron,
    /// doubled), or left out.</summary>
    private static List<Spelling> Lengthenings(string vowel, string written)
    {
        var result = new List<Spelling> { new([written], 0) };
        if (written != vowel) result.Add(new Spelling([vowel], 0.1));
        result.Add(new Spelling([], 0.3));
        return result;
    }

    /// <summary>A kana followed by a small kana: きゃ kya, しゃ sha, ふぁ fa, ティ ti, ウィ wi, also written as the two
    /// kana (kiya, tei).</summary>
    private static List<string> Combined(string head, string small)
    {
        var result = new List<string>();
        var stem = head[..^1];
        if (small is "ya" or "yu" or "yo")
        {
            if (head.EndsWith('i'))
            {
                result.Add(stem is "sh" or "ch" or "j" ? stem + small[1..] : stem + small);
            }
            else if (stem.Length > 0)
            {
                result.Add(stem + small);
            }
        }
        else if (small == "wa")
        {
            if (head is "ku" or "gu") result.Add(head[..1] + "wa");
        }
        else
        {
            switch (head)
            {
                case "u": result.Add(small == "u" ? "wu" : "w" + small); break;
                case "i": result.Add("y" + small); break;
                case "ku" or "gu": result.Add(head[..1] + "w" + small); break;
                case "te" or "de" or "to" or "do" or "fu" or "vu" or "tsu" or "su" or "zu": result.Add(stem + small); break;
                default:
                    if (head.EndsWith('i')) result.Add(stem is "sh" or "ch" or "j" ? stem + small : stem + "y" + small);
                    else if (stem.Length > 0) result.Add(stem + small);
                    break;
            }
        }
        result.Add(head + small);
        return result;
    }

    // MARK: - Alignment

    /// <summary>A half-open range of a reading's letters.</summary>
    public readonly record struct LetterRange(int Start, int End)
    {
        public bool IsEmpty => Start == End;
        public bool Contains(int value) => value >= Start && value < End;
    }

    /// <summary>The cheapest way to lay <paramref name="reading"/> over <paramref name="units"/>, as a range of its
    /// letters for each unit, or null when the kana cannot all be matched.</summary>
    public static List<LetterRange>? Align(Reading reading, List<Unit> units)
    {
        var letters = reading.Letters;
        var count = letters.Count;
        var width = count + 1;
        if (units.Count == 0) return count == 0 ? [] : null;
        var cost = new double[(units.Count + 1) * width];
        Array.Fill(cost, double.PositiveInfinity);
        var back = new int[(units.Count + 1) * width];
        Array.Fill(back, -1);
        cost[0] = 0;
        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];
            for (var start = 0; start <= count; start++)
            {
                var @base = cost[index * width + start];
                if (double.IsPositiveInfinity(@base)) continue;
                void Relax(int end, double step)
                {
                    var slot = (index + 1) * width + end;
                    if (@base + step < cost[slot]) { cost[slot] = @base + step; back[slot] = start; }
                }
                switch (unit.Kind)
                {
                    case Unit.UnitKind.Mark:
                        Relax(start, 0);
                        break;
                    case Unit.UnitKind.Kana or Unit.UnitKind.Latin:
                        foreach (var spelling in unit.Spellings)
                        {
                            if (Matches(letters, start, spelling.Letters)) Relax(start + spelling.Letters.Count, spelling.Cost);
                        }
                        // A reading may leave out the Latin words of the line.
                        if (unit.Kind == Unit.UnitKind.Latin) Relax(start, 0.8);
                        break;
                    case Unit.UnitKind.Sokuon:
                        Relax(start, 0.2);
                        if (start < count && IsConsonant(letters[start]) && letters[start] != "n"
                            && (start + 1 == count || letters[start + 1] == letters[start]
                                || (letters[start + 1] == "c" && letters[start] is "t" or "c")))
                        {
                            Relax(start + 1, 0);
                        }
                        break;
                    case Unit.UnitKind.Kanji:
                        if (start >= count) continue;
                        var limit = Math.Min(count, start + 14 * unit.Characters.Count + 4);
                        for (var end = start + 1; end <= limit; end++)
                        {
                            if (reading.Boundary[end]) Relax(end, KanjiCost(letters.GetRange(start, end - start), unit));
                        }
                        break;
                }
            }
        }
        if (double.IsPositiveInfinity(cost[units.Count * width + count])) return null;
        var ranges = new LetterRange[units.Count];
        var last = count;
        for (var index = units.Count; index > 0; index--)
        {
            var start = back[index * width + last];
            ranges[index - 1] = new LetterRange(start, last);
            last = start;
        }
        return [.. ranges];
    }

    private static bool Matches(List<string> letters, int start, List<string> spelling)
    {
        if (start + spelling.Count > letters.Count) return false;
        for (var offset = 0; offset < spelling.Count; offset++)
        {
            if (letters[start + offset] != spelling[offset]) return false;
        }
        return true;
    }

    private static double KanjiCost(List<string> part, Unit unit)
    {
        var syllables = SyllableCount(part);
        if (unit.Hint is not { } hint || unit.ShortHint is not { } shortHint)
        {
            return 0.6 + 0.1 * Math.Abs(syllables - 2 * unit.Characters.Count);
        }
        if (part.SequenceEqual(hint)) return 0;
        if (Math.Abs(part.Count - hint.Count) <= 2 && Collapsed(part) == shortHint) return 0.05;
        return 1 + 0.2 * Math.Abs(syllables - SyllableCount(hint));
    }

    private static int SyllableCount(List<string> letters)
    {
        var count = 0;
        string? previous = null;
        for (var offset = 0; offset < letters.Count; offset++)
        {
            var letter = letters[offset];
            var next = offset + 1 < letters.Count ? letters[offset + 1] : null;
            if (IsVowel(letter)) count++;
            else if (letter == "n" && (next is null || (!IsVowel(next) && next != "y"))) count++;
            else if (next is not null && next == letter && (previous is null || IsVowel(previous))) count++;
            previous = letter;
        }
        return Math.Max(count, 1);
    }

    /// <summary>Letters with long vowels written short, so kyou, kyoo and kyo agree.</summary>
    private static string Collapsed(List<string> letters)
    {
        var result = new StringBuilder();
        string? last = null;
        foreach (var letter in letters)
        {
            if (last is not null && IsVowel(letter)
                && (letter == last || (last == "o" && letter == "u") || (last == "e" && letter == "i")))
            {
                continue;
            }
            result.Append(letter);
            last = letter;
        }
        return result.ToString();
    }

    public static bool IsVowel(string character) => character is "a" or "i" or "u" or "e" or "o";

    private static bool IsVowel(char character) => character is 'a' or 'i' or 'u' or 'e' or 'o';

    private static bool IsConsonant(string character) =>
        character.Length > 0 && ReadingText.IsAlphabetic(System.Text.Rune.GetRuneAt(character, 0)) && !IsVowel(character);

    // MARK: - Sharing a kanji reading

    /// <summary>Shares a reading of several kanji out by syllable, the way on'yomi fall: one or two syllables each,
    /// and no character starting with ん, a doubled consonant or a long vowel, and rarely with the second half of
    /// ai, ei or ui or with ku, ki, tsu or chi (以上 i·jou, 最後 sai·go, 唯一 yui·itsu). Readings of compounds like
    /// 今日 cannot be split; they are spread out evenly so each character keeps a short reading.</summary>
    public static List<string> Share(string reading, int count)
    {
        if (count <= 1) return [reading];
        var syllables = Morae(ReadingText.Graphemes(reading).Select(g => g.Text).ToList()).Select(syllable => string.Concat(syllable)).ToList();
        if (syllables.Count <= count)
        {
            return Enumerable.Range(0, count).Select(index => index < syllables.Count ? syllables[index] : "").ToList();
        }
        var total = syllables.Count;
        var even = (double)total / count;
        var best = new double[count + 1, total + 1];
        var cut = new int[count + 1, total + 1];
        for (var group = 0; group <= count; group++)
        {
            for (var end = 0; end <= total; end++) best[group, end] = double.PositiveInfinity;
        }
        best[0, 0] = 0;
        for (var group = 1; group <= count; group++)
        {
            for (var end = group; end <= total - (count - group); end++)
            {
                for (var start = group - 1; start < end; start++)
                {
                    if (double.IsPositiveInfinity(best[group - 1, start])) continue;
                    var size = end - start;
                    var step = 0.3 * Math.Abs(size - even) + 0.8 * Math.Max(0, size - 2);
                    if (group > 1) step += Penalty(start, syllables);
                    if (best[group - 1, start] + step < best[group, end])
                    {
                        best[group, end] = best[group - 1, start] + step;
                        cut[group, end] = start;
                    }
                }
            }
        }
        var parts = new string[count];
        var last = total;
        for (var group = count; group > 0; group--)
        {
            var start = cut[group, last];
            parts[group - 1] = string.Concat(syllables.Skip(start).Take(last - start));
            last = start;
        }
        return [.. parts];
    }

    private static double Penalty(int index, List<string> syllables)
    {
        var syllable = new string(syllables[index].ToLowerInvariant().Where(c => c is not ('\'' or '’')).ToArray());
        if (syllable.Length == 0) return 0;
        var letter = syllable[0];
        if (syllable == "n" || (syllable.Length == 1 && !IsVowel(letter))) return 3;
        var previous = syllables[index - 1].ToLowerInvariant();
        if (syllable.Length == 1 && previous.LastOrDefault(IsVowel) is var vowel and not '\0')
        {
            // A vowel lengthens a full syllable before it (kyo·u), but after a lone vowel it can start the next
            // reading (唯一 yu·i·i·tsu).
            if (previous.Length > 1 && (letter == vowel || (vowel == 'o' && letter == 'u') || (vowel == 'e' && letter == 'i'))) return 3;
            if (letter is 'i' or 'u') return 1;
        }
        if (syllable is "ku" or "ki" or "tsu" or "chi" or "tu" or "ti") return 1;
        return 0;
    }

    // MARK: - Readings

    /// <summary>A reading's letters, normalised to Hepburn one syllable at a time, with where a kanji's reading may
    /// end and the source spelling of each syllable.</summary>
    public sealed class Reading
    {
        public List<string> Letters { get; } = [];
        /// <summary><c>Boundary[i]</c>: a syllable can end before letter <c>i</c>.</summary>
        public List<bool> Boundary { get; } = [true];
        private readonly List<Segment> _segments = [];

        private readonly record struct Segment(List<string> Raw, int Start, int Count, bool Verbatim);

        public Reading(string text)
        {
            foreach (var token in SplitTokens(text))
            {
                var characters = ReadingText.Graphemes(token)
                    .Where(g => g.IsLetterOrNumber || g.Text is "'" or "’").Select(g => g.Text).ToList();
                foreach (var syllable in Morae(characters))
                {
                    var lower = ReadingText.Graphemes(string.Concat(syllable).ToLowerInvariant()).Select(g => g.Text).ToList();
                    var normal = Normalized(lower);
                    if (normal.Count == 0) continue;
                    _segments.Add(new Segment(syllable, Letters.Count, normal.Count,
                        normal.SequenceEqual(lower) && lower.Count == syllable.Count));
                    Letters.AddRange(normal);
                    for (var index = 0; index < normal.Count - 1; index++) Boundary.Add(false);
                    Boundary.Add(true);
                }
            }
            // ん before a vowel can end a reading inside a syllable (ren·ai).
            for (var index = 1; index < Letters.Count; index++)
            {
                if (Letters[index - 1] == "n" && IsVowel(Letters[index])) Boundary[index] = true;
            }
        }

        private static IEnumerable<string> SplitTokens(string text)
        {
            var current = new StringBuilder();
            foreach (var grapheme in ReadingText.Graphemes(text))
            {
                if (grapheme.IsWhitespace || grapheme.Text is "-" or "‐" or "・" or "/")
                {
                    if (current.Length > 0) yield return current.ToString();
                    current.Clear();
                }
                else
                {
                    current.Append(grapheme.Text);
                }
            }
            if (current.Length > 0) yield return current.ToString();
        }

        /// <summary>The source spelling of <paramref name="range"/> of the letters.</summary>
        public string Raw(LetterRange range)
        {
            var text = new StringBuilder();
            foreach (var segment in _segments)
            {
                if (segment.Start >= range.End || segment.Start + segment.Count <= range.Start) continue;
                if (segment.Verbatim)
                {
                    var lower = Math.Max(range.Start, segment.Start) - segment.Start;
                    var upper = Math.Min(range.End, segment.Start + segment.Count) - segment.Start;
                    for (var index = lower; index < upper; index++) text.Append(segment.Raw[index]);
                }
                else if (range.Contains(segment.Start))
                {
                    foreach (var character in segment.Raw) text.Append(character);
                }
            }
            return text.ToString();
        }
    }

    /// <summary>Splits romaji into syllables: consonants with their vowel, ん (with the apostrophe that may follow
    /// it, kan'i), the first of a doubled consonant, and runs of digits.</summary>
    public static List<List<string>> Morae(List<string> characters)
    {
        var lower = characters.Select(character => character.ToLowerInvariant()).ToList();
        var result = new List<List<string>>();
        var current = new List<string>();
        var index = 0;
        while (index < characters.Count)
        {
            var letter = Plain(lower[index]);
            var next = index + 1 < characters.Count ? Plain(lower[index + 1]) : null;
            if (letter is "'" or "’")
            {
                index++;
                continue;
            }
            if (current.Count == 0 && letter == "n" && next is "'" or "’")
            {
                result.Add([characters[index], characters[index + 1]]);
                index += 2;
                continue;
            }
            if (IsVowel(letter))
            {
                current.Add(characters[index]);
                result.Add(current);
                current = [];
            }
            else if (IsNumberCharacter(lower[index]))
            {
                if (current.Count > 0) { result.Add(current); current = []; }
                var end = index;
                while (end < characters.Count && IsNumberCharacter(lower[end])) end++;
                result.Add(characters.GetRange(index, end - index));
                index = end;
                continue;
            }
            else if (current.Count == 0 && (letter == "n" || (letter == "m" && next is "b" or "p" or "m"))
                     && (next is null || (!IsVowel(next) && next != "y")))
            {
                result.Add([characters[index]]);                       // ん
            }
            else if (current.Count == 0 && letter != "n" && IsLetterCharacter(letter) && next is not null
                     && (next == letter || (next == "c" && letter is "t" or "c")))
            {
                result.Add([characters[index]]);                       // っ
            }
            else
            {
                current.Add(characters[index]);
            }
            index++;
        }
        if (current.Count > 0) result.Add(current);
        return result;
    }

    private static bool IsNumberCharacter(string character) =>
        character.Length > 0 && ReadingText.IsNumber(System.Text.Rune.GetRuneAt(character, 0));

    private static bool IsLetterCharacter(string character) =>
        character.Length > 0 && ReadingText.IsAlphabetic(System.Text.Rune.GetRuneAt(character, 0));

    /// <summary>A vowel without its accent (ō o, é e).</summary>
    private static string Plain(string letter)
    {
        if (letter.Length == 0 || letter.All(char.IsAscii)) return letter;
        var stripped = ReadingText.StripDiacritics(letter);
        return stripped.Length == 0 ? letter : ReadingText.Graphemes(stripped)[0].Text;
    }

    private static readonly Dictionary<string, string> Onsets = new()
    {
        ["si"] = "shi", ["ti"] = "chi", ["tu"] = "tsu", ["hu"] = "fu", ["zi"] = "ji", ["di"] = "ji", ["du"] = "zu", ["dzu"] = "zu", ["dji"] = "ji",
        ["sya"] = "sha", ["syu"] = "shu", ["syo"] = "sho", ["sye"] = "she", ["tya"] = "cha", ["tyu"] = "chu", ["tyo"] = "cho", ["tye"] = "che",
        ["cya"] = "cha", ["cyu"] = "chu", ["cyo"] = "cho", ["zya"] = "ja", ["zyu"] = "ju", ["zyo"] = "jo", ["zye"] = "je",
        ["jya"] = "ja", ["jyu"] = "ju", ["jyo"] = "jo", ["jye"] = "je", ["dya"] = "ja", ["dyu"] = "ju", ["dyo"] = "jo",
    };

    /// <summary>One syllable in Hepburn with long vowels doubled: syo sho, tu tsu, hu fu, kyō kyoo, and m before b, p
    /// or m as n.</summary>
    public static List<string> Normalized(List<string> syllable)
    {
        var letters = new List<string>();
        foreach (var letter in syllable)
        {
            switch (letter)
            {
                case "ā" or "â": letters.Add("a"); letters.Add("a"); break;
                case "ī" or "î": letters.Add("i"); letters.Add("i"); break;
                case "ū" or "û": letters.Add("u"); letters.Add("u"); break;
                case "ē" or "ê": letters.Add("e"); letters.Add("e"); break;
                case "ō" or "ô": letters.Add("o"); letters.Add("o"); break;
                case "'" or "’": break;
                default: letters.Add(Plain(letter)); break;
            }
        }
        if (letters is ["m"]) return ["n"];
        foreach (var length in new[] { 3, 2 })
        {
            if (letters.Count < length) continue;
            if (Onsets.TryGetValue(string.Concat(letters.Take(length)), out var onset))
            {
                return [.. onset.Select(c => c.ToString()), .. letters.Skip(length)];
            }
        }
        return letters;
    }

    // MARK: - Showing a source's reading

    /// <summary>A unit's part of a source or saved reading, written in Hepburn like the app's own readings. Where it
    /// spells the same sounds as the generated reading (NetEase's de syo u, i jyo u, hu ri), the generated spelling
    /// is shown, in the source's capitals; that also keeps loanword spellings such as ティ ti. A reading that
    /// differs is the source's own, as written, so a source that writes は as ha still shows ha. With
    /// <paramref name="respelling"/> (a kanji's reading, in a reading that writes Kunrei-shiki elsewhere), its
    /// Kunrei-shiki onsets are respelled where that is unambiguous (<see cref="Respelled"/>): ma zi de syo u shows
    /// maji.</summary>
    public static string Hepburn(string source, string generated, bool respelling)
    {
        if (source.Length == 0) return source;
        if (generated.Length > 0 && new Reading(source).Letters.SequenceEqual(new Reading(generated).Letters))
        {
            return Cased(generated, source);
        }
        return respelling ? Respelled(source) : source;
    }

    /// <summary>Whether <paramref name="source"/> spells the sounds of <paramref name="generated"/>, the app's
    /// reading of the same characters, with a syllable Hepburn writes otherwise (<c>Onsets</c>): de syo u for でしょう,
    /// hu ri for 降り, hi to tu for 一つ. That shows the reading uses Kunrei-shiki or a spelling like it, so its other
    /// syllables can be read the same way. Loanword spellings both write (ティ ti) are not a sign of it.</summary>
    public static bool WritesNonHepburn(string source, string generated)
    {
        if (source.Length == 0 || generated.Length == 0
            || !new Reading(source).Letters.SequenceEqual(new Reading(generated).Letters))
        {
            return false;
        }
        return NonHepburnSyllables(source) > NonHepburnSyllables(generated);
    }

    /// <summary>How many syllables of <paramref name="text"/> start with an onset Hepburn writes otherwise.</summary>
    private static int NonHepburnSyllables(string text) =>
        Morae(ReadingText.Graphemes(text).Select(g => g.Text).ToList()).Count(syllable =>
        {
            var letters = string.Concat(syllable.Select(character => Plain(character.ToLowerInvariant())));
            return new[] { 3, 2 }.Any(length => letters.Length >= length && Onsets.ContainsKey(letters[..length]));
        });

    /// <summary><paramref name="text"/> with the Kunrei-shiki and Nihon-shiki onsets that Hepburn never writes
    /// respelled (si shi, zi ji, hu fu, syo sho, tya cha, zyo jo). ti, tu, di, du, tyu and dyu stay, because Hepburn
    /// writes loanwords that way (ティ ti, ディ di, デュ dyu), and so does anything that is not romaji at all, such as
    /// an English word sung for a kanji ("music"). Vowels, macrons and capitals are kept. Some English words are
    /// shaped like romaji (passion, vision, Asia), so this is only for a reading that shows it writes Kunrei-shiki
    /// (<see cref="WritesNonHepburn"/>).</summary>
    public static string Respelled(string text)
    {
        var syllables = Morae(ReadingText.Graphemes(text).Select(g => g.Text).ToList());
        if (!IsRomaji(syllables)) return text;
        var result = new StringBuilder();
        foreach (var syllable in syllables)
        {
            var letters = syllable.Select(character => Plain(character.ToLowerInvariant())).ToList();
            var respelled = false;
            foreach (var length in new[] { 3, 2 })
            {
                if (letters.Count < length) continue;
                if (!UnambiguousOnsets.TryGetValue(string.Concat(letters.Take(length)), out var onset)) continue;
                // Each entry ends with the vowel, which keeps its own spelling.
                result.Append(Cased(onset[..^1], string.Concat(syllable))).Append(string.Concat(syllable.Skip(length - 1)));
                respelled = true;
                break;
            }
            if (!respelled) result.Append(string.Concat(syllable));
        }
        return result.ToString();
    }

    private static readonly Dictionary<string, string> UnambiguousOnsets = Onsets
        .Where(pair => pair.Key is not ("ti" or "tu" or "di" or "du" or "tyu" or "dyu"))
        .ToDictionary(pair => pair.Key, pair => pair.Value);

    /// <summary>What comes before the vowel of a syllable in romaji, in any system.</summary>
    private static readonly HashSet<string> RomajiOnsets =
    [
        "", "k", "g", "s", "z", "t", "d", "n", "h", "b", "p", "m", "y", "r", "w", "f", "j", "v",
        "sh", "ch", "ts", "dz", "dj", "ky", "gy", "sy", "zy", "jy", "ty", "dy", "cy", "ny", "hy", "by", "py", "my", "ry",
        "fy", "kw", "gw",
    ];

    /// <summary>Whether every syllable is one romaji writes: a known onset and its vowel, ん, or the first of a
    /// doubled consonant.</summary>
    private static bool IsRomaji(List<List<string>> syllables)
    {
        var parts = syllables.Select(syllable => syllable.Select(character => Plain(character.ToLowerInvariant()))
            .Where(character => character is not ("'" or "’")).ToList()).ToList();
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part.Count == 0) continue;
            var last = part[^1];
            if (IsVowel(last))
            {
                if (!RomajiOnsets.Contains(string.Concat(part.Take(part.Count - 1)))) return false;
                continue;
            }
            if (part is ["n"]) continue;
            if (part.Count != 1 || index + 1 >= parts.Count || parts[index + 1].Count == 0) return false;
            var next = parts[index + 1][0];
            var fits = last == "m" ? next is "b" or "p" or "m" : next == last || (next == "c" && last == "t");
            if (!fits) return false;
        }
        return true;
    }

    /// <summary><paramref name="text"/> in the capitals of <paramref name="source"/>: all capitals, a capital first
    /// letter, or as it is.</summary>
    private static string Cased(string text, string source)
    {
        var letters = ReadingText.Graphemes(source).Where(g => g.IsLetter).Select(g => g.First).ToList();
        if (letters.Count == 0 || !System.Text.Rune.IsUpper(letters[0])) return text;
        if (letters.Count > 1 && letters.All(System.Text.Rune.IsUpper)) return text.ToUpperInvariant();
        return text.Length == 0 ? text : text[..1].ToUpperInvariant() + text[1..];
    }
}
