using System.Buffers.Binary;
using Lunalyr.Install;

namespace Lunalyr.Install.Tests;

public sealed class PayloadTests : IDisposable
{
    private readonly InstallFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void AnInstallerCarriesItsPayloadAndManifest()
    {
        var installer = _f.Installer("0.8.0", (Product.ExeName, "exe"), (@"Assets\a.png", "png"), ("Lunalyr.pdb", "symbols"));
        using var stream = File.OpenRead(installer);
        var payload = Payload.Locate(stream);
        Assert.NotNull(payload);
        Assert.True(Payload.Verify(stream, payload));
        using var zip = Payload.Open(stream, payload);
        var manifest = Payload.ReadManifest(zip);
        Assert.Equal(("Lunalyr", "0.8.0", Product.ExeName), (manifest.Product, manifest.Version, manifest.Exe));
        var names = zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
        // Symbols stay out; the setup program rides along to become the installed uninstaller.
        Assert.Equal(["app/Assets/a.png", "app/Lunalyr.exe", "lunalyr-install.json", "setup/Lunalyr Setup.exe"], names);
        Assert.Equal(0, new FileInfo(installer).Length % 8);
    }

    [Fact]
    public void ReadingThePayloadLeavesTheInstallerOpen()
    {
        using var stream = File.OpenRead(_f.Installer("0.8.0"));
        var payload = Payload.Locate(stream)!;
        using (var zip = Payload.Open(stream, payload)) Assert.Equal("0.8.0", Payload.ReadManifest(zip).Version);
        Assert.True(Payload.Verify(stream, payload)); // Setup reads the version, then installs from the same stream.
    }

    [Fact]
    public void TheBareSetupHasNoPayload()
    {
        using var stream = File.OpenRead(_f.Stub());
        Assert.Null(Payload.Locate(stream));
    }

    [Fact]
    public void ADamagedPayloadFailsItsChecksum()
    {
        var installer = _f.Installer("0.8.0");
        var bytes = File.ReadAllBytes(installer);
        using (var stream = new MemoryStream(bytes))
        {
            var payload = Payload.Locate(stream)!;
            bytes[(int)payload.Offset + 40] ^= 0xFF;
        }
        using var damaged = new MemoryStream(bytes);
        var located = Payload.Locate(damaged);
        Assert.NotNull(located);
        Assert.False(Payload.Verify(damaged, located));
    }

    [Fact]
    public void ATruncatedDownloadHasNoPayload()
    {
        var bytes = File.ReadAllBytes(_f.Installer("0.8.0"));
        using var truncated = new MemoryStream(bytes[..(bytes.Length / 2)]);
        Assert.Null(Payload.Locate(truncated));
    }

    [Fact]
    public void ThePayloadIsFoundTheSameWayAfterSigning()
    {
        // A minimal PE32+ header, then the payload, then a certificate table the way signtool appends one: the PE's
        // security directory points at it.
        var stubPath = Path.Combine(_f.Folder, "pe-stub.exe");
        File.WriteAllBytes(stubPath, MinimalPe());
        using var zip = new MemoryStream();
        Payload.WriteZip(zip, _f.AppFolder((Product.ExeName, "exe")), _f.Stub(), "0.8.0");
        var installer = Path.Combine(_f.Folder, "pe-installer.exe");
        Payload.WriteInstaller(stubPath, zip, installer);

        Payload.Located before;
        using (var s = File.OpenRead(installer)) before = Payload.Locate(s)!;

        var unsigned = File.ReadAllBytes(installer);
        var certificate = new byte[24];
        certificate[0] = 24;
        var signed = unsigned.Concat(certificate).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(SecurityDirectory), (uint)unsigned.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(SecurityDirectory + 4), (uint)certificate.Length);

        using var stream = new MemoryStream(signed);
        Assert.Equal(((long)unsigned.Length, (long)certificate.Length), Payload.FindCertificateTable(stream));
        var after = Payload.Locate(stream);
        Assert.NotNull(after);
        Assert.Equal((before.Offset, before.Length), (after.Offset, after.Length));
        Assert.True(Payload.Verify(stream, after));

        // A signed setup can't be the base of an installer: its signature would sit in the middle.
        var signedStub = Path.Combine(_f.Folder, "signed-stub.exe");
        File.WriteAllBytes(signedStub, signed);
        zip.Position = 0;
        Assert.Throws<InvalidDataException>(() => Payload.WriteInstaller(signedStub, zip, Path.Combine(_f.Folder, "x.exe")));
    }

    [Fact]
    public void AnAppFolderWithoutLunalyrIsRefused()
    {
        using var zip = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => Payload.WriteZip(zip, _f.AppFolder(("Other.exe", "x")), _f.Stub(), "0.8.0"));
    }

    private const int PeOffset = 0x80;
    private const int SecurityDirectory = PeOffset + 24 + 112 + 4 * 8;

    private static byte[] MinimalPe()
    {
        var pe = new byte[0x200];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3C), PeOffset);
        "PE\0\0"u8.CopyTo(pe.AsSpan(PeOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(PeOffset + 24), 0x20B);
        return pe;
    }
}
