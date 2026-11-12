using System.Text;
using Lunalyr.Core.Player.Spotify;
using Lunalyr.Platform.Spotify;

namespace Lunalyr.Platform.Windows.Tests.Spotify;

/// <summary>
/// The Spotify token file: DPAPI for the current user, in a temp folder (never the real data folder), and the
/// browser launcher's address check. Nothing here opens a browser or reads any other credential store.
/// </summary>
public sealed class SpotifyTokenProtectionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lunalyr-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void DpapiRoundTripsAndRefusesTamperedBytes()
    {
        var protector = new DpapiTokenProtector();
        var plain = Encoding.UTF8.GetBytes("{\"refresh_token\":\"RT-secret\"}");
        var sealedBytes = protector.Protect(plain);
        Assert.False(sealedBytes.AsSpan().IndexOf("RT-secret"u8) >= 0, "the token is not in the file as text");
        Assert.Equal(plain, protector.Unprotect(sealedBytes));

        sealedBytes[^5] ^= 0x5A;
        Assert.Null(protector.Unprotect(sealedBytes));
        Assert.Null(protector.Unprotect([]));
        Assert.Null(protector.Unprotect([1, 2, 3]));
    }

    [Fact]
    public void TheStoreKeepsTheTokenEncryptedInItsOwnFile()
    {
        var path = ProtectedFileTokenStore.PathIn(_folder);
        Assert.Equal(Path.Combine(_folder, "Spotify", "account.bin"), path);
        var store = new ProtectedFileTokenStore(path, new DpapiTokenProtector());
        Assert.Null(store.Load());

        var token = new SpotifyStoredToken("0123456789abcdef0123456789abcdef", "RT-secret", SpotifyOptions.Scopes);
        store.Save(token);
        Assert.DoesNotContain("RT-secret", Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
        Assert.Equal(token, new ProtectedFileTokenStore(path, new DpapiTokenProtector()).Load());
        Assert.Equal(["account.bin"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));

        store.Clear();
        Assert.False(File.Exists(path));
        Assert.Null(store.Load());
    }

    [Fact]
    public void AFileThatCantBeDecryptedMeansSignedOut()
    {
        var path = ProtectedFileTokenStore.PathIn(_folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not encrypted at all"));
        Assert.Null(new ProtectedFileTokenStore(path, new DpapiTokenProtector()).Load());
    }

    [Theory]
    [InlineData("https://accounts.spotify.com/authorize?client_id=x", true)]
    [InlineData("https://ACCOUNTS.spotify.com/authorize", true)]
    [InlineData("http://accounts.spotify.com/authorize", false)]
    [InlineData("https://accounts.spotify.com:8443/authorize", false)]
    [InlineData("https://accounts.spotify.com/api/token", false)]
    [InlineData("https://open.spotify.com/track/0VjIjW4GlUZAMYd2vXMi3b", false)]
    [InlineData("spotify:track:0VjIjW4GlUZAMYd2vXMi3b", false)]
    [InlineData("https://accounts.spotify.com.example/authorize", false)]
    public void OnlySpotifysSignInPageIsEverOpened(string address, bool allowed) =>
        Assert.Equal(allowed, SystemBrowserLauncher.IsSignInPage(new Uri(address)));
}
