using System.Text;
using Lunalyr.Core.Player.Spotify;
using Lunalyr.Core.Tests.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Player;

/// <summary>The token file with a stand-in protector (the DPAPI one is tested in the platform tests), in a temp folder.</summary>
public sealed class ProtectedFileTokenStoreTests
{
    /// <summary>Reverses the bytes behind a marker; "can't decrypt" without it.</summary>
    private sealed class FakeProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plain) => [0x4C, .. plain.Reverse()];

        public byte[]? Unprotect(byte[] protectedBytes) =>
            protectedBytes.Length > 0 && protectedBytes[0] == 0x4C ? [.. protectedBytes.Skip(1).Reverse()] : null;
    }

    [Fact]
    public void ATokenRoundTripsAndClearingForgetsIt()
    {
        using var folder = new TempFolder();
        var path = ProtectedFileTokenStore.PathIn(folder.Path);
        var store = new ProtectedFileTokenStore(path, new FakeProtector());
        var token = new SpotifyStoredToken(SpotifyKit.ClientId, "RT-1", "scope");
        store.Save(token);
        Assert.Equal(token, store.Load());
        store.Save(token with { RefreshToken = "RT-2" });
        Assert.Equal("RT-2", store.Load()!.RefreshToken);
        Assert.False(File.Exists(path + ".tmp"));
        store.Clear();
        Assert.Null(store.Load());
        store.Clear(); // twice is fine
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"client_id\":\"x\"}")]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    public void AnythingUnreadableMeansSignedOut(string json)
    {
        using var folder = new TempFolder();
        var path = ProtectedFileTokenStore.PathIn(folder.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var protector = new FakeProtector();
        File.WriteAllBytes(path, protector.Protect(Encoding.UTF8.GetBytes(json)));
        Assert.Null(new ProtectedFileTokenStore(path, protector).Load());

        File.WriteAllBytes(path, [0x00, 0x01]); // can't be decrypted
        Assert.Null(new ProtectedFileTokenStore(path, protector).Load());
    }
}
