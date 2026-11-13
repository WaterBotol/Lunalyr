using Lunalyr.Core.NowPlaying;

namespace Lunalyr.Core.Tests.NowPlaying;

public sealed class AppleMusicWindowsMetadataTests
{
    // Seen live on 13 Nov 2026: Apple Music for Windows reported artist and album artist "Wallows — Nothing Happens", album "".
    private static readonly MediaTrackInfo Reported = new("Scrawny", "Wallows — Nothing Happens", "", "Wallows — Nothing Happens");

    [Fact]
    public void AppleMusicsJoinedArtistAndAlbumAreSplit()
    {
        var track = AppleMusicWindowsMetadata.Normalize(Reported, MediaSourceKind.AppleMusic);
        Assert.Equal(("Scrawny", "Wallows", "Nothing Happens", "Wallows"), (track.Title, track.Artist, track.AlbumTitle, track.AlbumArtist));
    }

    [Fact]
    public void AnAlbumWithItsOwnDashKeepsIt()
    {
        var track = AppleMusicWindowsMetadata.Normalize(Reported with { Artist = "Queen — A Night at the Opera — Deluxe" },
            MediaSourceKind.AppleMusic);
        Assert.Equal(("Queen", "A Night at the Opera — Deluxe"), (track.Artist, track.AlbumTitle));
    }

    [Fact]
    public void OtherPlayersAndRealAlbumsAreLeftAsReported()
    {
        Assert.Same(Reported, AppleMusicWindowsMetadata.Normalize(Reported, MediaSourceKind.Spotify));
        var withAlbum = Reported with { AlbumTitle = "Nothing Happens" };
        Assert.Same(withAlbum, AppleMusicWindowsMetadata.Normalize(withAlbum, MediaSourceKind.AppleMusic));
        var plain = Reported with { Artist = "Wallows", AlbumArtist = "Wallows" };
        Assert.Same(plain, AppleMusicWindowsMetadata.Normalize(plain, MediaSourceKind.AppleMusic));
        var hyphen = Reported with { Artist = "Jay-Z - Live" };
        Assert.Same(hyphen, AppleMusicWindowsMetadata.Normalize(hyphen, MediaSourceKind.AppleMusic));
    }
}
