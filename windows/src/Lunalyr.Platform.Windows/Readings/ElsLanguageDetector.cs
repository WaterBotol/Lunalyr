using System.Runtime.InteropServices;
using Lunalyr.Core.Lyrics.Readings;

namespace Lunalyr.Platform.Readings;

/// <summary>
/// Language detection with Windows' Extended Linguistic Services ("Microsoft Language Detection", elscore.dll),
/// which is built into Windows and works offline. It ranks candidate languages but gives no confidence, so unlike the
/// Mac's NLLanguageRecognizer (which needs 0.6) it cannot say "unsure": short texts of an alphabetic script (under 12
/// letters; Han, kana and Hangul are exempt, as a few of them already say a lot) are treated as unknown, and CJK text
/// with no ELS answer falls back to
/// <see cref="ScriptLanguageGuesser"/>. Accuracy limits are in docs/READINGS-WINDOWS.md (it took Ukrainian for
/// Bulgarian in testing).
/// </summary>
public sealed unsafe partial class ElsLanguageDetector : ILanguageGuesser, ILyricLanguageDetector
{
    private static readonly Guid DetectionService = new("CF7E00B1-909B-4D95-A8F4-611F7C377702");
    private const int ShortestSpacedSample = 12;

    private readonly Lock _lock = new();
    private readonly MappingServiceInfo* _services;
    private readonly MappingServiceInfo* _service;

    private ElsLanguageDetector(MappingServiceInfo* services, MappingServiceInfo* service)
    {
        _services = services;
        _service = service;
    }

    /// <summary>The detector, or null where ELS or its detection service is missing.</summary>
    public static ElsLanguageDetector? TryCreate()
    {
        try
        {
            var guid = DetectionService;
            var options = new MappingEnumOptions { Size = (nuint)sizeof(MappingEnumOptions), Guid = &guid };
            MappingServiceInfo* services;
            uint count;
            if (MappingGetServices(&options, &services, &count) != 0 || count == 0) return null;
            return new ElsLanguageDetector(services, &services[0]);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The ranked languages ELS recognises in <paramref name="text"/>, best first.</summary>
    public List<string> Languages(string text)
    {
        var languages = new List<string>();
        if (text.Length == 0) return languages;
        lock (_lock)
        {
            var bag = new MappingPropertyBag { Size = (nuint)sizeof(MappingPropertyBag) };
            fixed (char* pointer = text)
            {
                if (MappingRecognizeText(_service, pointer, (uint)text.Length, 0, null, &bag) != 0) return languages;
            }
            try
            {
                if (bag.RangesCount > 0 && bag.ResultRanges[0].Data != null)
                {
                    // A double-NUL-terminated list of language tags.
                    var cursor = (char*)bag.ResultRanges[0].Data;
                    while (*cursor != 0)
                    {
                        var tag = new string(cursor);
                        languages.Add(tag);
                        cursor += tag.Length + 1;
                    }
                }
            }
            finally
            {
                MappingFreePropertyBag(&bag);
            }
        }
        return languages;
    }

    public string? DominantLanguage(string text)
    {
        var letters = 0;
        var cjk = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!ReadingText.IsAlphabetic(rune)) continue;
            letters++;
            if (ReadingText.IsHanScript(rune) || ReadingText.IsHiraganaScript(rune) || ReadingText.IsKatakanaScript(rune)
                || rune.Value is >= 0xAC00 and <= 0xD7A3)
            {
                cjk = true;
            }
        }
        if (letters == 0) return null;
        if (!cjk && letters < ShortestSpacedSample) return null;
        var best = Languages(text).FirstOrDefault();
        return best ?? (cjk ? ScriptLanguageGuesser.Instance.DominantLanguage(text) : null);
    }

    public string? Language(IReadOnlyList<string> texts) => new GuessingLyricLanguageDetector(this).Language(texts);

    ~ElsLanguageDetector()
    {
        if (_services != null) MappingFreeServices(_services);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappingEnumOptions
    {
        public nuint Size;
        public char* Category, InputLanguage, OutputLanguage, InputScript, OutputScript, InputContentType, OutputContentType;
        public Guid* Guid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappingServiceInfo
    {
        public nuint Size;
        public char* Copyright;
        public ushort MajorVersion, MinorVersion, BuildVersion, StepVersion;
        public uint InputContentTypesCount; public char** InputContentTypes;
        public uint OutputContentTypesCount; public char** OutputContentTypes;
        public uint InputLanguagesCount; public char** InputLanguages;
        public uint OutputLanguagesCount; public char** OutputLanguages;
        public uint InputScriptsCount; public char** InputScripts;
        public uint OutputScriptsCount; public char** OutputScripts;
        public Guid Guid;
        public char* Category, Description;
        public uint PrivateDataSize;
        public void* PrivateData, Context;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappingDataRange
    {
        public uint StartIndex, EndIndex;
        public char* Description;
        public uint DescriptionLength;
        public void* Data;
        public uint DataSize;
        public char* ContentType;
        public char** ActionIds;
        public uint ActionsCount;
        public char** ActionDisplayNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappingPropertyBag
    {
        public nuint Size;
        public MappingDataRange* ResultRanges;
        public uint RangesCount;
        public void* ServiceData;
        public uint ServiceDataSize;
        public void* CallerData;
        public uint CallerDataSize;
        public void* Context;
    }

    [LibraryImport("elscore.dll")]
    private static partial int MappingGetServices(MappingEnumOptions* options, MappingServiceInfo** services, uint* count);

    [LibraryImport("elscore.dll")]
    private static partial int MappingFreeServices(MappingServiceInfo* services);

    [LibraryImport("elscore.dll")]
    private static partial int MappingRecognizeText(MappingServiceInfo* service, char* text, uint length, uint index, void* options, MappingPropertyBag* bag);

    [LibraryImport("elscore.dll")]
    private static partial int MappingFreePropertyBag(MappingPropertyBag* bag);
}
