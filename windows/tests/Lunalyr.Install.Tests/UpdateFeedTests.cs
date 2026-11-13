using System.Security.Cryptography;
using System.Text;
using Lunalyr.Install;

namespace Lunalyr.Install.Tests;

/// <summary>
/// The update feed's configuration and signature. The Mac's UpdateConfigurationTests are Verso lines, so these are
/// written from its behaviour (a feed and key both needed; insecure, credentialed or fragment feeds and malformed keys
/// refused), with a P-256 key in place of the Mac's Ed25519. Keys are made per test; none is checked in.
/// </summary>
public sealed class UpdateFeedTests
{
    private static readonly (string Private, string Public) Key = UpdateFeeds.NewKey();
    private const string Feed = "https://updates.example.com/lunalyr/windows/update.json";

    [Fact]
    public void AnHttpsFeedAndAP256KeyMakeAConfiguration()
    {
        var config = UpdateConfiguration.Create(Feed, Key.Public);
        Assert.NotNull(config);
        Assert.Equal("updates.example.com", config.Feed.Host);
        Assert.Equal(Feed + ".sig", config.SignatureUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData(null, "key")]
    [InlineData("", "key")]
    [InlineData(Feed, null)]
    [InlineData(Feed, "")]
    [InlineData("   ", "  ")]
    public void AMissingFeedOrKeyLeavesUpdatesUnconfigured(string? feed, string? key) =>
        Assert.Null(UpdateConfiguration.Create(feed, key == "key" ? Key.Public : key));

    [Theory]
    [InlineData("http://example.com/update.json")]
    [InlineData("file:///C:/tmp/update.json")]
    [InlineData("https://user:pass@example.com/update.json")]
    [InlineData("https://example.com/update.json#other")]
    [InlineData("update.json")]
    public void InsecureCredentialedAndFragmentFeedsAreRefused(string feed) =>
        Assert.Null(UpdateConfiguration.Create(feed, Key.Public));

    [Fact]
    public void MalformedAndWrongKindsOfKeysAreRefused()
    {
        Assert.Null(UpdateConfiguration.Create(Feed, "not a key"));
        Assert.Null(UpdateConfiguration.Create(Feed, Convert.ToBase64String(new byte[32])));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Null(UpdateConfiguration.Create(Feed, Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));
        using var rsa = RSA.Create(2048);
        Assert.Null(UpdateConfiguration.Create(Feed, Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())));
        var trailing = Convert.FromBase64String(Key.Public).Concat(new byte[] { 0 }).ToArray();
        Assert.Null(UpdateConfiguration.Create(Feed, Convert.ToBase64String(trailing)));
    }

    [Fact]
    public void ASignedFeedIsRead()
    {
        var bytes = UpdateFeeds.Serialize(Sample());
        var (feed, problem) = UpdateFeeds.Read(bytes, UpdateFeeds.Sign(bytes, Key.Private), PublicKey());
        Assert.Null(problem);
        Assert.Equal(Sample(), feed);
    }

    [Fact]
    public void AnyChangeToTheFeedBreaksItsSignature()
    {
        var bytes = UpdateFeeds.Serialize(Sample());
        var signature = UpdateFeeds.Sign(bytes, Key.Private);
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("0.9.0", "9.9.0", StringComparison.Ordinal));
        Assert.Contains("isn’t signed", UpdateFeeds.Read(changed, signature, PublicKey()).Problem);

        var other = UpdateFeeds.NewKey();
        Assert.Contains("isn’t signed", UpdateFeeds.Read(bytes, UpdateFeeds.Sign(bytes, other.PrivateKeyPem), PublicKey()).Problem);
        Assert.Contains("unreadable", UpdateFeeds.Read(bytes, "%%%", PublicKey()).Problem);
    }

    [Theory]
    [InlineData("http://example.com/LunalyrSetup.exe", "isn’t a secure")]
    [InlineData("https://user:pw@example.com/LunalyrSetup.exe", "isn’t a secure")]
    public void ASignedFeedStillNeedsASafeDownload(string url, string problem)
    {
        var bytes = UpdateFeeds.Serialize(Sample() with { Url = url });
        Assert.Contains(problem, UpdateFeeds.Read(bytes, UpdateFeeds.Sign(bytes, Key.Private), PublicKey()).Problem);
    }

    [Fact]
    public void ASignedFeedForSomethingElseIsRefused()
    {
        foreach (var feed in new[] { Sample() with { Product = "Other" }, Sample() with { Version = "next" },
                     Sample() with { Sha256 = "abc" }, Sample() with { Size = 0 } })
        {
            var bytes = UpdateFeeds.Serialize(feed);
            Assert.NotNull(UpdateFeeds.Read(bytes, UpdateFeeds.Sign(bytes, Key.Private), PublicKey()).Problem);
        }
    }

    [Fact]
    public void ADownloadMatchesOnlyItsSizeAndChecksum()
    {
        var installer = Encoding.UTF8.GetBytes("installer bytes");
        var feed = Sample() with { Size = installer.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(installer)) };
        Assert.True(UpdateFeeds.Matches(new MemoryStream(installer), feed));
        installer[0] ^= 1;
        Assert.False(UpdateFeeds.Matches(new MemoryStream(installer), feed));
        Assert.False(UpdateFeeds.Matches(new MemoryStream(installer[..^1]), feed));
    }

    private static byte[] PublicKey() => Convert.FromBase64String(Key.Public);

    private static UpdateFeed Sample() => new("Lunalyr", "0.9.0", "https://example.com/LunalyrSetup-0.9.0.exe",
        new string('a', 64), 40_000_000, "Notes", new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero));
}
