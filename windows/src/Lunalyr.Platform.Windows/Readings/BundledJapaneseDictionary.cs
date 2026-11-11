using System.Buffers;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using Lunalyr.Core.Lyrics.Readings.Japanese;

namespace Lunalyr.Platform.Readings;

/// <summary>
/// The Japanese dictionary bundled in this assembly (IPAdic, compressed with Brotli: about 4.4 MB in the app, 15.7 MB
/// expanded). At first use it is expanded once into a cache folder (the app's is
/// <c>%LOCALAPPDATA%\Lunalyr\Dictionaries</c>) and memory-mapped read-only from there, so its pages are shared and
/// loaded only as the analyser touches them. The file is named after the bundled data's hash, written to a temporary
/// name and then moved into place, so a partly written or stale file is never used; older versions are deleted.
/// Where the folder cannot be written, the dictionary is expanded into memory instead.
/// </summary>
public static class BundledJapaneseDictionary
{
    private const string ResourceName = "Lunalyr.Platform.Readings.Data.ipadic.lunadic.br";
    private const string FilePrefix = "ipadic-";
    private const string FileExtension = ".lunadic";

    /// <summary>The app's cache folder for the expanded dictionary.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunalyr", "Dictionaries");

    /// <summary>How the last <see cref="Open(string)"/> got the dictionary, for the log.</summary>
    public static string LastSource { get; private set; } = "not opened";

    /// <summary>Opens the bundled dictionary, expanding it into <paramref name="folder"/> if it is not there yet.
    /// Disposing the dictionary unmaps the file. Throws when the bundled data is missing or damaged.</summary>
    public static JapaneseDictionary Open(string folder)
    {
        var dictionary = Open(folder, out var source);
        LastSource = source;
        return dictionary;
    }

    /// <summary><see cref="Open(string)"/>, saying how the dictionary was got: "mapped" (already expanded),
    /// "expanded and mapped", or "in memory" with the reason.</summary>
    public static JapaneseDictionary Open(string folder, out string source)
    {
        var compressed = ReadResource();
        var hash = Convert.ToHexStringLower(SHA256.HashData(compressed))[..16];
        var path = Path.Combine(folder, FilePrefix + hash + FileExtension);
        try
        {
            Directory.CreateDirectory(folder);
            if (TryMap(path) is { } existing)
            {
                source = "mapped";
                return existing;
            }
            var temporary = Path.Combine(folder, $"{FilePrefix}{hash}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    Expand(compressed, file);
                    file.Flush(flushToDisk: true);
                }
                try
                {
                    File.Move(temporary, path, overwrite: false);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another process expanded it first (or a damaged copy is in the way and still mapped).
                }
            }
            finally
            {
                TryDelete(temporary);
            }
            if (TryMap(path) is { } mapped)
            {
                source = "expanded and mapped";
                DeleteOtherVersions(folder, path);
                return mapped;
            }
            source = "in memory (the cached file could not be used)";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            source = $"in memory ({exception.GetType().Name})";
        }
        using var memory = new MemoryStream();
        Expand(compressed, memory);
        return JapaneseDictionary.Open(memory.ToArray());
    }

    private static byte[] ReadResource()
    {
        using var stream = typeof(BundledJapaneseDictionary).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The bundled Japanese dictionary ({ResourceName}) is missing.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void Expand(byte[] compressed, Stream output)
    {
        using var brotli = new BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress);
        brotli.CopyTo(output);
    }

    /// <summary>The dictionary mapped from <paramref name="path"/>, or null when there is no usable file there (a
    /// damaged one is deleted when it can be).</summary>
    private static JapaneseDictionary? TryMap(string path)
    {
        if (!File.Exists(path)) return null;
        MappedFile? mapped = null;
        try
        {
            mapped = MappedFile.Open(path);
            // The dictionary owns the mapping. The app opens it once and keeps it for the whole run.
            return JapaneseDictionary.Open(mapped.Memory, mapped);
        }
        catch (InvalidDataException)
        {
            ((IDisposable?)mapped)?.Dispose();
            TryDelete(path);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ((IDisposable?)mapped)?.Dispose();
            return null;
        }
    }

    private static void DeleteOtherVersions(string folder, string current)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, FilePrefix + "*"))
            {
                if (!string.Equals(file, current, StringComparison.OrdinalIgnoreCase)) TryDelete(file);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // In use by another process, or not ours to delete: left for next time.
        }
    }

    /// <summary>A read-only memory-mapped file as <see cref="Memory{T}"/>.</summary>
    private sealed unsafe class MappedFile : MemoryManager<byte>
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        private readonly byte* _pointer;
        private readonly int _length;

        private MappedFile(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* pointer, int length)
        {
            _file = file;
            _view = view;
            _pointer = pointer;
            _length = length;
        }

        public static MappedFile Open(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var length = stream.Length;
            if (length is <= 0 or > int.MaxValue)
            {
                stream.Dispose();
                throw new InvalidDataException("The cached Japanese dictionary has the wrong size.");
            }
            var file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            var view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            return new MappedFile(file, view, pointer + view.PointerOffset, (int)length);
        }

        public override Span<byte> GetSpan() => new(_pointer, _length);

        public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _file.Dispose();
        }
    }
}
