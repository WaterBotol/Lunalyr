using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lunalyr.Install;

/// <summary>
/// Where updates come from: an HTTPS feed (<c>update.json</c>, with its signature at <c>update.json.sig</c>) and the
/// public key that signed it. Builds without both have no update service, as the Mac's do until one is set up.
/// Re-implemented from the Mac's behaviour (its UpdateConfiguration and tests are Verso lines); the key is ECDSA P-256,
/// which .NET has built in, instead of the Mac's Ed25519.
/// </summary>
public sealed record UpdateConfiguration(Uri Feed, byte[] PublicKey)
{
    /// <summary>The signature's address: the feed's, plus <c>.sig</c>.</summary>
    public Uri SignatureUrl => new(Feed.AbsoluteUri + ".sig");

    /// <summary>
    /// The configuration, or null when the feed or key is missing or unsafe: the feed must be an absolute <c>https</c>
    /// address with no user name, password or fragment; the key a P-256 public key (SubjectPublicKeyInfo, Base64).
    /// </summary>
    public static UpdateConfiguration? Create(string? feed, string? publicKey)
    {
        if (string.IsNullOrWhiteSpace(feed) || string.IsNullOrWhiteSpace(publicKey)) return null;
        if (!Uri.TryCreate(feed.Trim(), UriKind.Absolute, out var uri) || !IsSafeDownload(uri) || uri.Fragment.Length > 0) return null;
        byte[] key;
        try
        {
            key = Convert.FromBase64String(publicKey.Trim());
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out var read);
            if (read != key.Length || ecdsa.KeySize != 256) return null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
        return new UpdateConfiguration(uri, key);
    }

    /// <summary>HTTPS with no credentials in the address.</summary>
    public static bool IsSafeDownload(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Host.Length > 0;
}

/// <summary>The newest version, as the feed describes it.</summary>
/// <param name="Url">The installer (<c>Lunalyr Setup.exe</c> with its payload).</param>
/// <param name="Sha256">The installer's SHA-256, hex.</param>
/// <param name="Size">The installer's size in bytes.</param>
public sealed record UpdateFeed(string Product, string Version, string Url, string Sha256, long Size, string? Notes = null,
                                DateTimeOffset? Published = null);

[JsonSerializable(typeof(UpdateFeed))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;

/// <summary>Reading, checking and signing the feed.</summary>
public static class UpdateFeeds
{
    /// <summary>A feed's bytes larger than this are refused before parsing.</summary>
    public const int MaxFeedBytes = 64 * 1024;

    /// <summary>Installers larger than this are refused (the app is about 40 MB zipped).</summary>
    public const long MaxInstallerBytes = 1L << 30;

    /// <summary>
    /// Checks <paramref name="signature"/> (Base64, ECDSA P-256 over SHA-256 of the exact feed bytes) and then reads the
    /// feed. Returns the feed or why it was refused.
    /// </summary>
    public static (UpdateFeed? Feed, string? Problem) Read(ReadOnlySpan<byte> feed, string signature, byte[] publicKey)
    {
        if (feed.Length > MaxFeedBytes) return (null, "The update feed is too large.");
        byte[] sig;
        try
        {
            sig = Convert.FromBase64String(signature.Trim());
        }
        catch (FormatException)
        {
            return (null, "The update feed’s signature is unreadable.");
        }
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            if (!ecdsa.VerifyData(feed, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return (null, "The update feed isn’t signed by Lunalyr’s key.");
        }
        UpdateFeed? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(feed, UpdateJsonContext.Default.UpdateFeed);
        }
        catch (JsonException)
        {
            return (null, "The update feed is unreadable.");
        }
        if (parsed is null || !string.Equals(parsed.Product, Product.Name, StringComparison.Ordinal))
            return (null, "The update feed isn’t Lunalyr’s.");
        if (ProductVersion.Parse(parsed.Version) is null) return (null, "The update feed’s version is unreadable.");
        if (!Uri.TryCreate(parsed.Url, UriKind.Absolute, out var url) || !UpdateConfiguration.IsSafeDownload(url))
            return (null, "The update’s download address isn’t a secure (https) one.");
        if (parsed.Sha256.Length != 64 || !parsed.Sha256.All(Uri.IsHexDigit)) return (null, "The update feed’s checksum is unreadable.");
        if (parsed.Size <= 0 || parsed.Size > MaxInstallerBytes) return (null, "The update feed’s size is wrong.");
        return (parsed, null);
    }

    /// <summary>The feed's JSON, as the build tool writes it.</summary>
    public static byte[] Serialize(UpdateFeed feed) => JsonSerializer.SerializeToUtf8Bytes(feed, UpdateJsonContext.Default.UpdateFeed);

    /// <summary>Signs feed bytes with a P-256 private key (PKCS#8 PEM). Build-time only; the key is the owner's.</summary>
    public static string Sign(ReadOnlySpan<byte> feed, string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        if (ecdsa.KeySize != 256) throw new CryptographicException("Lunalyr's update key is ECDSA P-256.");
        return Convert.ToBase64String(ecdsa.SignData(feed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>A new key pair: the private key (PKCS#8 PEM, keep it secret) and the public key for the app (Base64).</summary>
    public static (string PrivateKeyPem, string PublicKey) NewKey()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Whether a downloaded installer matches the feed's size and SHA-256.</summary>
    public static bool Matches(Stream installer, UpdateFeed feed)
    {
        if (installer.Length != feed.Size) return false;
        installer.Position = 0;
        var hash = SHA256.HashData(installer);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(feed.Sha256));
    }
}
