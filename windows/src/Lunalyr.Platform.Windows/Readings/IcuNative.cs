using System.Runtime.InteropServices;

namespace Lunalyr.Platform.Readings;

/// <summary>
/// The parts of Windows' own ICU (<c>icu.dll</c>, Windows 10 1903 and later) the readings use: transliterators
/// (<c>utrans_*</c>, with the Han-Latin and Any-Latin data) and word break iterators (<c>ubrk_*</c>, with the
/// Chinese/Japanese and Thai dictionaries). Blittable signatures only, so the calls are Native-AOT safe.
/// </summary>
internal static unsafe partial class IcuNative
{
    private const string Library = "icu.dll";

    public const int BufferOverflowError = 15;
    public const int WordBreak = 1;
    public const int Done = -1;

    /// <summary>Rule status of a break: 0–99 none (spaces, punctuation), 100+ numbers, letters, kana, ideographs.</summary>
    public const int WordNoneLimit = 100;

    private static readonly Lazy<bool> Loaded = new(() => NativeLibrary.TryLoad(Library, out _));

    /// <summary>Whether icu.dll is present (it is on every supported Windows; older builds lack it).</summary>
    public static bool IsAvailable => Loaded.Value;

    [LibraryImport(Library)]
    public static partial nint utrans_openU(char* id, int idLength, int direction, char* rules, int rulesLength, void* parseError, int* status);

    [LibraryImport(Library)]
    public static partial void utrans_transUChars(nint transliterator, char* text, int* textLength, int textCapacity, int start, int* limit, int* status);

    [LibraryImport(Library)]
    public static partial void utrans_close(nint transliterator);

    [LibraryImport(Library)]
    public static partial nint ubrk_open(int type, byte* locale, char* text, int textLength, int* status);

    [LibraryImport(Library)]
    public static partial int ubrk_first(nint iterator);

    [LibraryImport(Library)]
    public static partial int ubrk_next(nint iterator);

    [LibraryImport(Library)]
    public static partial int ubrk_getRuleStatus(nint iterator);

    [LibraryImport(Library)]
    public static partial void ubrk_close(nint iterator);
}
