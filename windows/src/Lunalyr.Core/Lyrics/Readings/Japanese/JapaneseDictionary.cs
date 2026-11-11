using System.Buffers.Binary;
using System.Runtime.InteropServices;
using static Lunalyr.Core.Lyrics.Readings.Japanese.JapaneseDictionaryFormat;

namespace Lunalyr.Core.Lyrics.Readings.Japanese;

/// <summary>
/// A Japanese morphological dictionary in <see cref="JapaneseDictionaryFormat"/>, used in place from memory (an
/// array, or a memory-mapped file behind a <see cref="System.Buffers.MemoryManager{T}"/>). Immutable and thread-safe.
/// Disposing it releases the memory's owner, if it was given one; the app keeps its dictionary for the whole run.
/// </summary>
public sealed class JapaneseDictionary : IDisposable
{
    private readonly ReadOnlyMemory<byte> _memory;
    private readonly IDisposable? _owner;
    private readonly (int Offset, int Length)[] _sections;
    private readonly string[] _partsOfSpeech;

    public int LeftSize { get; }
    public int RightSize { get; }
    internal int SpaceCategory { get; }
    internal int MaxSurfaceLength { get; }
    public int SurfaceCount { get; }
    public int EntryCount { get; }

    private JapaneseDictionary(ReadOnlyMemory<byte> memory, (int, int)[] sections, IDisposable? owner)
    {
        _memory = memory;
        _owner = owner;
        _sections = sections;
        var meta = Ints(Section.Meta);
        LeftSize = meta[(int)MetaField.LeftSize];
        RightSize = meta[(int)MetaField.RightSize];
        SpaceCategory = meta[(int)MetaField.SpaceCategory];
        MaxSurfaceLength = meta[(int)MetaField.MaxSurfaceLength];
        SurfaceCount = Ints(Section.SurfaceOffsets).Length - 1;
        EntryCount = Entries.Length;
        _partsOfSpeech = new string(Chars(Section.PartsOfSpeech)).Split('\n');
        if (Matrix.Length != LeftSize * RightSize || CharTable.Length != 0x10000 || Ints(Section.EntryStarts).Length != SurfaceCount + 1)
        {
            throw new InvalidDataException("The Japanese dictionary's sections do not fit together.");
        }
    }

    /// <summary>Opens a dictionary file held in <paramref name="memory"/>, which must stay valid while the dictionary
    /// is used (<paramref name="owner"/>, if given, is disposed with the dictionary). Throws
    /// <see cref="InvalidDataException"/> when it is not one.</summary>
    public static JapaneseDictionary Open(ReadOnlyMemory<byte> memory, IDisposable? owner = null)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("The Japanese dictionary is little-endian.");
        var bytes = memory.Span;
        if (bytes.Length < HeaderSize || !bytes[..8].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) != JapaneseDictionaryFormat.Version
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]) != (int)Section.Count)
        {
            throw new InvalidDataException("Not a Japanese dictionary of this version.");
        }
        var sections = new (int, int)[(int)Section.Count];
        for (var index = 0; index < sections.Length; index++)
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes[(16 + index * 8)..]);
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[(20 + index * 8)..]);
            if (offset < HeaderSize || length < 0 || offset % 8 != 0 || (long)offset + length > bytes.Length)
            {
                throw new InvalidDataException("The Japanese dictionary is truncated.");
            }
            sections[index] = (offset, length);
        }
        return new JapaneseDictionary(memory, sections, owner);
    }

    public void Dispose() => _owner?.Dispose();

    private ReadOnlySpan<byte> Bytes(Section section)
    {
        var (offset, length) = _sections[(int)section];
        return _memory.Span.Slice(offset, length);
    }

    private ReadOnlySpan<int> Ints(Section section) => MemoryMarshal.Cast<byte, int>(Bytes(section));
    private ReadOnlySpan<char> Chars(Section section) => MemoryMarshal.Cast<byte, char>(Bytes(section));

    internal ReadOnlySpan<DictionaryEntry> Entries => MemoryMarshal.Cast<byte, DictionaryEntry>(Bytes(Section.Entries));
    internal ReadOnlySpan<DictionaryEntry> UnknownEntries => MemoryMarshal.Cast<byte, DictionaryEntry>(Bytes(Section.UnknownEntries));
    internal ReadOnlySpan<short> Matrix => MemoryMarshal.Cast<byte, short>(Bytes(Section.Matrix));
    internal ReadOnlySpan<uint> CharTable => MemoryMarshal.Cast<byte, uint>(Bytes(Section.CharTable));
    internal ReadOnlySpan<CharacterCategory> Categories => MemoryMarshal.Cast<byte, CharacterCategory>(Bytes(Section.Categories));

    /// <summary>The cost of <paramref name="nextLeft"/> following <paramref name="previousRight"/>.</summary>
    internal int Connection(int previousRight, int nextLeft) => Matrix[previousRight + LeftSize * nextLeft];

    /// <summary>The part of speech of a context id, as the source dictionary names it (IPAdic: "名詞,数,*,*,*,*,*").</summary>
    public string PartOfSpeech(int contextId) =>
        (uint)contextId < (uint)_partsOfSpeech.Length ? _partsOfSpeech[contextId] : "";

    /// <summary>An entry's reading in katakana, or null when it has none.</summary>
    internal string? Reading(in DictionaryEntry entry) =>
        entry.ReadingLength == 0 ? null : new string(Chars(Section.ReadingChars).Slice(entry.ReadingOffset, entry.ReadingLength));

    /// <summary>Adds (length, first entry, entry count) to <paramref name="found"/> for every surface that
    /// <paramref name="text"/> starts with, shortest first.</summary>
    internal void CommonPrefixes(ReadOnlySpan<char> text, List<(int Length, int First, int Count)> found)
    {
        var surfaceChars = Chars(Section.SurfaceChars);
        var offsets = Ints(Section.SurfaceOffsets);
        var starts = Ints(Section.EntryStarts);
        int low = 0, high = SurfaceCount;
        var limit = Math.Min(text.Length, MaxSurfaceLength);
        for (var length = 1; length <= limit && low < high; length++)
        {
            // Every surface in [low, high) begins with text[..length-1]; those that end there sort first.
            var character = text[length - 1];
            var index = length - 1;
            low = Bound(surfaceChars, offsets, low, high, index, character, upper: false);
            high = Bound(surfaceChars, offsets, low, high, index, character, upper: true);
            if (low >= high) break;
            if (offsets[low + 1] - offsets[low] == length) found.Add((length, starts[low], starts[low + 1] - starts[low]));
        }
    }

    /// <summary>The first surface in [low, high) whose character at <paramref name="index"/> is at least (or, for
    /// <paramref name="upper"/>, greater than) <paramref name="character"/>; a surface too short to have one is less.</summary>
    private static int Bound(ReadOnlySpan<char> chars, ReadOnlySpan<int> offsets, int low, int high, int index, char character, bool upper)
    {
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            var start = offsets[middle];
            var length = offsets[middle + 1] - start;
            var less = length <= index || (upper ? chars[start + index] <= character : chars[start + index] < character);
            if (less) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
