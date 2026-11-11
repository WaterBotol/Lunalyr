using Lunalyr.Core.Lyrics.Readings;

namespace Lunalyr.Platform.Readings;

/// <summary>
/// <see cref="IScriptTransliterator"/> on Windows' ICU: "Any-Latin" (Apple's <c>.toLatin</c>) and "Han-Latin"
/// (Apple's <c>.mandarinToLatin</c>). Both are ICU on the Mac too, so readings match up to the ICU data versions.
/// Thread-safe: each transliterator is used under its own lock.
/// </summary>
public sealed unsafe class IcuTransliterator : IScriptTransliterator, IDisposable
{
    private readonly Transform? _toLatin;
    private readonly Transform? _mandarin;

    private IcuTransliterator(Transform? toLatin, Transform? mandarin)
    {
        _toLatin = toLatin;
        _mandarin = mandarin;
    }

    /// <summary>The transliterator, or null where icu.dll or its transliteration data is missing.</summary>
    public static IcuTransliterator? TryCreate()
    {
        if (!IcuNative.IsAvailable) return null;
        try
        {
            var toLatin = Transform.Open("Any-Latin");
            var mandarin = Transform.Open("Han-Latin");
            return toLatin is null && mandarin is null ? null : new IcuTransliterator(toLatin, mandarin);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public string? ToLatin(string text) => _toLatin?.Apply(text);

    public string? MandarinToLatin(string text) => _mandarin?.Apply(text);

    /// <summary>Any ICU transliterator by ID, for tests and tools.</summary>
    internal static string? Apply(string id, string text)
    {
        using var transform = Transform.Open(id);
        return transform?.Apply(text);
    }

    public void Dispose()
    {
        _toLatin?.Dispose();
        _mandarin?.Dispose();
    }

    private sealed class Transform : IDisposable
    {
        private readonly Lock _lock = new();
        private nint _handle;

        private Transform(nint handle) => _handle = handle;

        public static Transform? Open(string id)
        {
            var status = 0;
            nint handle;
            fixed (char* idPointer = id)
            {
                handle = IcuNative.utrans_openU(idPointer, id.Length, 0, null, 0, null, &status);
            }
            return status > 0 || handle == 0 ? null : new Transform(handle);
        }

        public string? Apply(string text)
        {
            if (text.Length == 0) return text;
            lock (_lock)
            {
                if (_handle == 0) return null;
                var capacity = text.Length * 4 + 32;
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var buffer = new char[capacity];
                    text.CopyTo(0, buffer, 0, text.Length);
                    int length = text.Length, limit = text.Length, status = 0;
                    fixed (char* pointer = buffer)
                    {
                        IcuNative.utrans_transUChars(_handle, pointer, &length, capacity, 0, &limit, &status);
                    }
                    if (status == IcuNative.BufferOverflowError)
                    {
                        capacity = Math.Max(capacity * 2, length + 32);
                        continue;
                    }
                    return status > 0 ? null : new string(buffer, 0, length);
                }
                return null;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_handle != 0) IcuNative.utrans_close(_handle);
                _handle = 0;
            }
        }
    }
}

/// <summary>
/// <see cref="IWordBreaker"/> on ICU's word break iterator, which splits Chinese and Japanese with its dictionary
/// (我们|一起|走过, 窓辺|に|座|って) and Thai by its dictionary too. It parts some Japanese differently from what the
/// Mac's tests expect of Apple's segmentation (見|てい|た); see docs/READINGS-WINDOWS.md.
/// </summary>
public sealed unsafe class IcuWordBreaker : IWordBreaker
{
    private static readonly byte[] Locale = "ja\0"u8.ToArray();

    public static IcuWordBreaker? TryCreate()
    {
        if (!IcuNative.IsAvailable) return null;
        try
        {
            var breaker = new IcuWordBreaker();
            breaker.Words("probe");
            return breaker;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public IReadOnlyList<(int Start, int Length)> Words(string text)
    {
        var words = new List<(int Start, int Length)>();
        if (text.Length == 0) return words;
        fixed (char* pointer = text)
        fixed (byte* locale = Locale)
        {
            var status = 0;
            var iterator = IcuNative.ubrk_open(IcuNative.WordBreak, locale, pointer, text.Length, &status);
            if (status > 0 || iterator == 0) return SimpleWordBreaker.Instance.Words(text);
            try
            {
                var start = IcuNative.ubrk_first(iterator);
                for (var end = IcuNative.ubrk_next(iterator); end != IcuNative.Done; start = end, end = IcuNative.ubrk_next(iterator))
                {
                    if (IcuNative.ubrk_getRuleStatus(iterator) >= IcuNative.WordNoneLimit) words.Add((start, end - start));
                }
            }
            finally
            {
                IcuNative.ubrk_close(iterator);
            }
        }
        return words;
    }
}
