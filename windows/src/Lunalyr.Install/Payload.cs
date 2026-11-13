using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lunalyr.Install;

/// <summary>What the payload says about itself (<see cref="Product.ManifestName"/>).</summary>
public sealed record PayloadManifest(string Product, string Version, string Exe, long UnpackedBytes);

[JsonSerializable(typeof(PayloadManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class InstallJsonContext : JsonSerializerContext;

/// <summary>
/// The installer is one file: the setup program, then a zip of the app, then a trailer saying how long the zip is and
/// its SHA-256:
/// <code>
/// [setup exe][zip][zip length: 8 bytes LE][SHA-256 of the zip: 32 bytes]["LUNAPAY1"][zero padding to 8 bytes]
/// [Authenticode signature, once signed]
/// </code>
/// Signing appends its certificate table after everything, and the PE header says where that table starts, so the
/// payload is found the same way before and after signing (and the signature covers it).
/// </summary>
public static class Payload
{
    private static readonly byte[] Magic = "LUNAPAY1"u8.ToArray();
    private const int TrailerLength = 8 + 32 + 8;

    /// <summary>A payload found inside an installer.</summary>
    public sealed record Located(long Offset, long Length, byte[] Sha256);

    /// <summary>Zips <paramref name="appFolder"/> (the published app) with the setup program and the manifest.</summary>
    public static void WriteZip(Stream output, string appFolder, string setupExe, string version)
    {
        var files = Directory.GetFiles(appFolder, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (!files.Any(f => string.Equals(Path.GetRelativePath(appFolder, f), Product.ExeName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{appFolder} has no {Product.ExeName}.");

        long unpacked = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        foreach (var file in files)
        {
            var name = Product.PayloadAppPrefix + Path.GetRelativePath(appFolder, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, name, CompressionLevel.SmallestSize);
            unpacked += new FileInfo(file).Length;
        }
        zip.CreateEntryFromFile(setupExe, Product.PayloadSetupEntry, CompressionLevel.SmallestSize);
        unpacked += new FileInfo(setupExe).Length;

        var manifest = new PayloadManifest(Product.Name, version, Product.ExeName, unpacked);
        var entry = zip.CreateEntry(Product.ManifestName);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, manifest, InstallJsonContext.Default.PayloadManifest);
    }

    /// <summary>Writes the installer: <paramref name="setupStub"/>, the zip, the trailer, padded to 8 bytes.</summary>
    public static void WriteInstaller(string setupStub, Stream zip, string output)
    {
        using var stub = File.OpenRead(setupStub);
        if (FindCertificateTable(stub) is not null)
            throw new InvalidDataException($"{setupStub} is already signed: build the installer from the unsigned setup, then sign the result (the signed setup goes inside the payload).");
        stub.Position = 0;

        using var outStream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
        stub.CopyTo(outStream);
        zip.Position = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long length = 0;
        int read;
        while ((read = zip.Read(buffer)) > 0)
        {
            outStream.Write(buffer, 0, read);
            sha.AppendData(buffer, 0, read);
            length += read;
        }
        Span<byte> trailer = stackalloc byte[TrailerLength];
        BinaryPrimitives.WriteInt64LittleEndian(trailer, length);
        sha.GetHashAndReset().CopyTo(trailer[8..]);
        Magic.CopyTo(trailer[40..]);
        outStream.Write(trailer);
        var pad = (int)((8 - outStream.Position % 8) % 8);
        outStream.Write(new byte[pad]);
        outStream.Flush(flushToDisk: true);
    }

    /// <summary>Finds the payload in an installer, or null when it has none (the bare setup, an uninstaller copy).</summary>
    public static Located? Locate(Stream installer)
    {
        var end = FindCertificateTable(installer)?.Offset ?? installer.Length;
        // Skip the padding before the certificate table (at most 7 zero bytes).
        Span<byte> tail = stackalloc byte[TrailerLength + 7];
        var tailStart = Math.Max(0, end - tail.Length);
        installer.Position = tailStart;
        var got = installer.ReadAtLeast(tail[..(int)(end - tailStart)], (int)(end - tailStart), throwOnEndOfStream: false);
        var span = tail[..got];
        var last = span.Length;
        for (var i = 0; i < 7 && last > 0 && span[last - 1] == 0; i++) last--;
        if (last < TrailerLength || !span[(last - 8)..last].SequenceEqual(Magic)) return null;
        var trailer = span[(last - TrailerLength)..last];
        var length = BinaryPrimitives.ReadInt64LittleEndian(trailer);
        var trailerStart = tailStart + last - TrailerLength;
        var offset = trailerStart - length;
        if (length <= 0 || offset < 0) return null;
        return new Located(offset, length, trailer[8..40].ToArray());
    }

    /// <summary>Checks the zip's SHA-256 against the trailer; a damaged download fails here, before anything is unpacked.</summary>
    public static bool Verify(Stream installer, Located payload)
    {
        installer.Position = payload.Offset;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        var left = payload.Length;
        while (left > 0)
        {
            var read = installer.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read == 0) return false;
            sha.AppendData(buffer, 0, read);
            left -= read;
        }
        return CryptographicOperations.FixedTimeEquals(sha.GetHashAndReset(), payload.Sha256);
    }

    /// <summary>The payload's zip, read in place.</summary>
    public static ZipArchive Open(Stream installer, Located payload) =>
        new(new SubStream(installer, payload.Offset, payload.Length), ZipArchiveMode.Read, leaveOpen: false);

    public static PayloadManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(Product.ManifestName) ?? throw new InvalidDataException("The installer's payload has no manifest.");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize(stream, InstallJsonContext.Default.PayloadManifest)
            ?? throw new InvalidDataException("The installer's manifest is empty.");
    }

    /// <summary>The PE's Authenticode certificate table (offset, size), or null when the file isn't signed.</summary>
    internal static (long Offset, long Size)? FindCertificateTable(Stream pe)
    {
        if (pe.Length < 0x40) return null;
        Span<byte> buf = stackalloc byte[4];
        pe.Position = 0;
        if (pe.ReadAtLeast(buf[..2], 2, false) < 2 || buf[0] != 'M' || buf[1] != 'Z') return null;
        pe.Position = 0x3C;
        pe.ReadExactly(buf);
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(buf);
        if (peOffset <= 0 || peOffset + 24 > pe.Length) return null;
        pe.Position = peOffset;
        pe.ReadExactly(buf);
        if (buf[0] != 'P' || buf[1] != 'E' || buf[2] != 0 || buf[3] != 0) return null;
        var optional = peOffset + 24;
        pe.Position = optional;
        pe.ReadExactly(buf[..2]);
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(buf);
        var dirs = magic switch { 0x20B => optional + 112, 0x10B => optional + 96, _ => -1 };
        if (dirs < 0) return null;
        pe.Position = dirs + 4 * 8; // IMAGE_DIRECTORY_ENTRY_SECURITY
        Span<byte> entry = stackalloc byte[8];
        if (pe.ReadAtLeast(entry, 8, false) < 8) return null;
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
        return offset == 0 || size == 0 || offset > pe.Length ? null : (offset, size);
    }

    /// <summary>A read-only window onto part of a stream.</summary>
    private sealed class SubStream(Stream inner, long offset, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, length);
        }

        public override int Read(byte[] buffer, int offset1, int count) => Read(buffer.AsSpan(offset1, count));

        public override int Read(Span<byte> buffer)
        {
            var left = length - _position;
            if (left <= 0) return 0;
            if (buffer.Length > left) buffer = buffer[..(int)left];
            inner.Position = offset + _position;
            var read = inner.Read(buffer);
            _position += read;
            return read;
        }

        public override long Seek(long offset1, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset1,
            SeekOrigin.Current => _position + offset1,
            _ => length + offset1,
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset1, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
