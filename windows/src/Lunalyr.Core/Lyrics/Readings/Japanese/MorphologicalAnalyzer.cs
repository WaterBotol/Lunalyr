namespace Lunalyr.Core.Lyrics.Readings.Japanese;

/// <summary>One morpheme of an analysis: its UTF-16 range in the text given, its reading in katakana (null when
/// the dictionary has none, as for unknown words), its left context id (its part of speech) and whether the
/// dictionary knows it.</summary>
public readonly record struct Morpheme(int Start, int Length, string? Reading, int ContextId, bool IsKnown);

/// <summary>
/// A MeCab-style Japanese morphological analyser over a <see cref="JapaneseDictionary"/>: every dictionary word and
/// unknown-word candidate starting at each position forms a lattice, and the path of least word and connection cost
/// (Viterbi) is the analysis. Unknown words come from character categories as in MeCab's char.def: a category that
/// always invokes them, or a position no dictionary word starts at, adds a word of the whole run of that category
/// (when it groups) and words of 1..n characters. Spaces are skipped. Written for Lunalyr from the published
/// description of the model and its file formats; stateless and thread-safe.
/// </summary>
public sealed class MorphologicalAnalyzer(JapaneseDictionary dictionary)
{
    /// <summary>Unknown words longer than this many characters are not made from a run (MeCab's default).</summary>
    private const int MaxGrouping = 24;

    public JapaneseDictionary Dictionary => dictionary;

    private struct Node
    {
        public int Start;
        public int Length;
        public short Left;
        public short Right;
        public int Total;
        public int Previous;
        /// <summary>The dictionary entry, or for an unknown word -1 - its template index.</summary>
        public int Entry;
    }

    /// <summary>The morphemes of <paramref name="text"/>, in order. Spaces belong to none.</summary>
    public List<Morpheme> Analyze(ReadOnlySpan<char> text) => Analyze(text, out _);

    /// <summary>The morphemes of <paramref name="text"/> and the cost of the analysis. With
    /// <paramref name="firstLength"/>, the first morpheme must be that long and its part of speech begin with
    /// <paramref name="firstPart"/>; when no analysis does that, the result is empty and the cost
    /// <see cref="int.MaxValue"/>.</summary>
    public List<Morpheme> Analyze(ReadOnlySpan<char> text, out int cost, int firstLength = 0, string firstPart = "")
    {
        var result = new List<Morpheme>();
        cost = 0;
        if (text.IsEmpty) return result;
        var length = text.Length;
        var nodes = new List<Node> { new() { Previous = -1 } };
        var ends = new List<int>?[length + 1];
        ends[0] = [0];
        var matches = new List<(int Length, int First, int Count)>();
        var entries = dictionary.Entries;
        var categories = dictionary.Categories;
        var finals = new List<int>();

        for (var position = 0; position < length; position++)
        {
            if (ends[position] is not { } before) continue;
            var begin = position;
            while (begin < length && Info(text, begin).Category == dictionary.SpaceCategory) begin += Info(text, begin).Width;
            if (begin >= length)
            {
                finals.AddRange(before);
                continue;
            }

            var constrained = position == 0 && firstLength > 0;
            void Add(int wordLength, short left, short right, short wordCost, int entry)
            {
                if (constrained && (wordLength != firstLength || !dictionary.PartOfSpeech(left).StartsWith(firstPart, StringComparison.Ordinal))) return;
                var best = -1;
                var bestTotal = int.MaxValue;
                foreach (var index in before)
                {
                    var total = nodes[index].Total + dictionary.Connection(nodes[index].Right, left);
                    if (total < bestTotal) { bestTotal = total; best = index; }
                }
                nodes.Add(new Node
                {
                    Start = begin, Length = wordLength, Left = left, Right = right, Total = bestTotal + wordCost,
                    Previous = best, Entry = entry,
                });
                (ends[begin + wordLength] ??= []).Add(nodes.Count - 1);
            }

            matches.Clear();
            dictionary.CommonPrefixes(text[begin..], matches);
            foreach (var (wordLength, first, count) in matches)
            {
                for (var entry = first; entry < first + count; entry++)
                {
                    ref readonly var word = ref entries[entry];
                    Add(wordLength, word.Left, word.Right, word.Cost, entry);
                }
            }

            var info = Info(text, begin);
            var category = categories[info.Category];
            if (matches.Count > 0 && category.Invoke == 0) continue;
            void AddUnknown(int wordLength, in CharacterCategory kind)
            {
                for (var template = kind.UnknownStart; template < kind.UnknownStart + kind.UnknownCount; template++)
                {
                    var word = dictionary.UnknownEntries[template];
                    Add(wordLength, word.Left, word.Right, word.Cost, -1 - template);
                }
            }
            var grouped = 0;
            if (category.Group != 0)
            {
                var end = begin + info.Width;
                var characters = 1;
                while (end < length && (Info(text, end).Mask & info.Mask) != 0)
                {
                    end += Info(text, end).Width;
                    characters++;
                }
                if (characters <= MaxGrouping)
                {
                    grouped = end - begin;
                    AddUnknown(grouped, category);
                }
            }
            var cursor = begin;
            var added = grouped > 0;
            for (var characters = 1; characters <= category.Length && cursor < length; characters++)
            {
                var next = Info(text, cursor);
                if (characters > 1 && (next.Mask & info.Mask) == 0) break;
                cursor += next.Width;
                if (cursor - begin == grouped) continue;
                AddUnknown(cursor - begin, category);
                added = true;
            }
            if (!added && matches.Count == 0) AddUnknown(info.Width, category);
        }

        finals.AddRange(ends[length] ?? []);
        var last = -1;
        var lastTotal = int.MaxValue;
        foreach (var index in finals)
        {
            var total = nodes[index].Total + dictionary.Connection(nodes[index].Right, 0);
            if (total < lastTotal) { lastTotal = total; last = index; }
        }
        cost = lastTotal;
        for (var index = last; index > 0; index = nodes[index].Previous)
        {
            var node = nodes[index];
            var known = node.Entry >= 0;
            var reading = known ? dictionary.Reading(entries[node.Entry]) : null;
            result.Add(new Morpheme(node.Start, node.Length, reading, node.Left, known));
        }
        result.Reverse();
        return result;
    }

    private readonly record struct CharInfo(int Category, uint Mask, int Width);

    /// <summary>The character at <paramref name="index"/>: its category, its category set and its UTF-16 width.
    /// Characters beyond the Basic Multilingual Plane take the categories of a common kanji when they are
    /// ideographs and of U+FFFF (the default) otherwise.</summary>
    private CharInfo Info(ReadOnlySpan<char> text, int index)
    {
        var table = dictionary.CharTable;
        uint value;
        var width = 1;
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            width = 2;
            var scalar = char.ConvertToUtf32(text[index], text[index + 1]);
            value = scalar is >= 0x20000 and <= 0x3FFFF ? table['亜'] : table[0xFFFF];
        }
        else
        {
            value = table[text[index]];
        }
        return new CharInfo((int)(value & 0xFF), value >> 8, width);
    }
}
