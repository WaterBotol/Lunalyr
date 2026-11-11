using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Lunalyr.Core.Lyrics.Readings.Japanese;

/// <summary>
/// One dictionary word (or unknown-word template): its connection context ids, its cost, and its reading in katakana
/// as a slice of the reading pool (length 0: no reading). Laid out as stored, 12 bytes, so the file can be used in
/// place.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct DictionaryEntry(short Left, short Right, short Cost, ushort ReadingLength, int ReadingOffset);

/// <summary>A character category of the analyser (MeCab's char.def): whether its unknown words are always tried,
/// whether a run of it makes one word, how many 1..n character words are tried, and its unknown-word templates.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct CharacterCategory(int Invoke, int Group, int Length, int UnknownStart, int UnknownCount);

/// <summary>
/// The dictionary file: a little-endian header and sections that are used in place (memory-mapped or in one array),
/// so opening it costs no parsing. Sections hold the sorted surfaces, their entries and readings, the connection
/// costs, the character categories and the part-of-speech names of the context ids. Written by
/// <see cref="Write"/> (the dictionary builder tool), read by <see cref="JapaneseDictionary"/>.
/// </summary>
public static class JapaneseDictionaryFormat
{
    public static ReadOnlySpan<byte> Magic => "LUNAJDIC"u8;
    public const int Version = 1;

    internal enum Section
    {
        Meta, SurfaceChars, SurfaceOffsets, EntryStarts, Entries, ReadingChars, Matrix, CharTable, Categories,
        UnknownEntries, PartsOfSpeech, Count,
    }

    internal enum MetaField { LeftSize, RightSize, SpaceCategory, MaxSurfaceLength, Count }

    internal const int HeaderSize = 16 + (int)Section.Count * 8;

    /// <summary>The words and costs to store, as the builder reads them from a MeCab-format source dictionary.</summary>
    public sealed record Source(
        IReadOnlyList<(string Surface, short Left, short Right, short Cost, string? Reading)> Words,
        int LeftSize, int RightSize, short[] Matrix,
        IReadOnlyList<CategorySource> Categories, uint[] CharTable, int SpaceCategory,
        IReadOnlyList<string> PartsOfSpeech);

    /// <summary>A character category with its unknown-word templates (left, right, cost).</summary>
    public sealed record CategorySource(string Name, bool Invoke, bool Group, int Length,
        IReadOnlyList<(short Left, short Right, short Cost)> Unknown);

    /// <summary>A character's categories in <see cref="Source.CharTable"/>: its own category in the low 8 bits and
    /// the set of categories it belongs to as a mask above them.</summary>
    public static uint CharInfo(int category, uint mask) => (uint)category | (mask << 8);

    /// <summary>Writes the dictionary file.</summary>
    public static byte[] Write(Source source)
    {
        // Surfaces sorted ordinally; each surface's entries keep the source's order.
        var groups = source.Words
            .Select((word, index) => (word, index))
            .GroupBy(item => item.word.Surface, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
        var surfaceChars = new StringBuilder();
        var surfaceOffsets = new List<int> { 0 };
        var entryStarts = new List<int> { 0 };
        var entries = new List<DictionaryEntry>();
        var readings = new StringBuilder();
        var readingOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        var maxSurface = 0;
        foreach (var group in groups)
        {
            surfaceChars.Append(group.Key);
            surfaceOffsets.Add(surfaceChars.Length);
            maxSurface = Math.Max(maxSurface, group.Key.Length);
            foreach (var (word, _) in group.OrderBy(item => item.index))
            {
                var (offset, length) = (0, 0);
                if (!string.IsNullOrEmpty(word.Reading))
                {
                    if (!readingOffsets.TryGetValue(word.Reading, out offset))
                    {
                        offset = readings.Length;
                        readings.Append(word.Reading);
                        readingOffsets[word.Reading] = offset;
                    }
                    length = word.Reading.Length;
                }
                entries.Add(new DictionaryEntry(word.Left, word.Right, word.Cost, checked((ushort)length), offset));
            }
            entryStarts.Add(entries.Count);
        }
        var categories = new List<CharacterCategory>();
        var unknown = new List<DictionaryEntry>();
        foreach (var category in source.Categories)
        {
            categories.Add(new CharacterCategory(category.Invoke ? 1 : 0, category.Group ? 1 : 0, category.Length, unknown.Count, category.Unknown.Count));
            unknown.AddRange(category.Unknown.Select(template => new DictionaryEntry(template.Left, template.Right, template.Cost, 0, 0)));
        }
        var meta = new int[(int)MetaField.Count];
        meta[(int)MetaField.LeftSize] = source.LeftSize;
        meta[(int)MetaField.RightSize] = source.RightSize;
        meta[(int)MetaField.SpaceCategory] = source.SpaceCategory;
        meta[(int)MetaField.MaxSurfaceLength] = maxSurface;
        if (source.Matrix.Length != source.LeftSize * source.RightSize) throw new ArgumentException("matrix size");
        if (source.CharTable.Length != 0x10000) throw new ArgumentException("char table size");

        var sections = new byte[(int)Section.Count][];
        sections[(int)Section.Meta] = MemoryMarshal.AsBytes(meta.AsSpan()).ToArray();
        sections[(int)Section.SurfaceChars] = MemoryMarshal.AsBytes(surfaceChars.ToString().AsSpan()).ToArray();
        sections[(int)Section.SurfaceOffsets] = MemoryMarshal.AsBytes(surfaceOffsets.ToArray().AsSpan()).ToArray();
        sections[(int)Section.EntryStarts] = MemoryMarshal.AsBytes(entryStarts.ToArray().AsSpan()).ToArray();
        sections[(int)Section.Entries] = MemoryMarshal.AsBytes(entries.ToArray().AsSpan()).ToArray();
        sections[(int)Section.ReadingChars] = MemoryMarshal.AsBytes(readings.ToString().AsSpan()).ToArray();
        sections[(int)Section.Matrix] = MemoryMarshal.AsBytes(source.Matrix.AsSpan()).ToArray();
        sections[(int)Section.CharTable] = MemoryMarshal.AsBytes(source.CharTable.AsSpan()).ToArray();
        sections[(int)Section.Categories] = MemoryMarshal.AsBytes(categories.ToArray().AsSpan()).ToArray();
        sections[(int)Section.UnknownEntries] = MemoryMarshal.AsBytes(unknown.ToArray().AsSpan()).ToArray();
        sections[(int)Section.PartsOfSpeech] = MemoryMarshal.AsBytes(string.Join('\n', source.PartsOfSpeech).AsSpan()).ToArray();

        using var output = new MemoryStream();
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), Version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), (int)Section.Count);
        output.Write(header);
        for (var index = 0; index < sections.Length; index++)
        {
            while (output.Length % 8 != 0) output.WriteByte(0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16 + index * 8), (int)output.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20 + index * 8), sections[index].Length);
            output.Write(sections[index]);
        }
        output.Position = 0;
        output.Write(header);
        return output.ToArray();
    }
}
