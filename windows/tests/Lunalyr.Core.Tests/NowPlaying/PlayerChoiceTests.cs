using Lunalyr.Core.NowPlaying;

namespace Lunalyr.Core.Tests.NowPlaying;

/// <summary>
/// The player choice (Settings › Music players › Player). The Mac's players are installed apps; Windows' are the apps
/// sharing with its media controls, so the Mac's tests are ported with apps sharing in place of apps installed. The
/// Mac's webPlayers/localPlayers switches (macOS permission prompts) have no Windows counterpart: their assertions are
/// left out.
/// </summary>
public sealed class PlayerChoiceTests
{
    private static readonly MediaSourceApp AppleMusic = MediaSourceApp.FromAppId("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App");
    private static readonly MediaSourceApp Spotify = MediaSourceApp.FromAppId("Spotify.exe");
    private static readonly MediaSourceApp Edge = MediaSourceApp.FromAppId("MSEdge");
    private static readonly MediaSourceApp Chrome = MediaSourceApp.FromAppId("Chrome");
    private static readonly MediaSourceApp Foobar = MediaSourceApp.FromAppId("foobar2000.exe");
    private static readonly MediaSourceApp MediaPlayer = MediaSourceApp.FromAppId("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic");

    // Ported from MorePlayersTests.swift: testEveryPlayerCanBeChosenAlone (Windows' players for the Mac's kinds; the
    // Automatic assertions about local players and browsers being switched on don't apply: Automatic follows every app
    // sharing with Windows).
    [Fact]
    public void EveryPlayerCanBeChosenAlone()
    {
        MediaSourceApp[] apps = [AppleMusic, Spotify, Edge, Chrome, Foobar, MediaPlayer, MediaSourceApp.FromAppId("firefox.exe"),
            MediaSourceApp.FromAppId("Brave"), MediaSourceApp.FromAppId("TheBrowserCompany.Arc_ttt1ap7aakyb4!Arc")];
        foreach (var app in apps)
        {
            var only = PlayerChoice.Only(app);
            Assert.False(PlayerChoice.IsAutomatic(only), app.AppId);
            Assert.Equal(only, PlayerChoice.Normalize(only));
            Assert.Equal([app], apps.Where(other => PlayerChoice.Follows(only, other)));
        }
        Assert.All(apps, app => Assert.True(PlayerChoice.Follows(PlayerChoice.Automatic, app)));
        Assert.Equal("spotify", PlayerChoice.Only(Spotify));
        Assert.Equal("appleMusic", PlayerChoice.Only(AppleMusic));
        Assert.Equal("edge", PlayerChoice.Only(Edge));
        Assert.Equal("app:foobar2000.exe", PlayerChoice.Only(Foobar));
        // Spotify's Store build is Spotify too.
        Assert.True(PlayerChoice.Follows("spotify", MediaSourceApp.FromAppId("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify")));
        Assert.True(PlayerChoice.Follows("app:FOOBAR2000.EXE", Foobar));
    }

    // Ported from MorePlayersTests.swift: testPickersOfferOnlyWhatIsInstalled (what shares with Windows now stands in for
    // what is installed; the web/local switches don't apply).
    [Fact]
    public void PickersOfferOnlyWhatIsThere()
    {
        Assert.Equal(["automatic", "appleMusic", "spotify"], PlayerChoice.Offered([], PlayerChoice.Automatic));
        Assert.Equal(["automatic", "appleMusic", "spotify", "edge", "chrome", "app:foobar2000.exe"],
            PlayerChoice.Offered([Foobar, Chrome, Spotify, Edge, Chrome], "automatic"));
        Assert.Contains("app:vlc.exe", PlayerChoice.Offered([], "app:vlc.exe")); // the current choice stays offered
        Assert.Contains("vivaldi", PlayerChoice.Offered([], "vivaldi"));
        Assert.Equal(["Automatic", "Apple Music", "Spotify", "Web in Edge", "foobar2000"],
            PlayerChoice.Offered([Edge, Foobar], null).Select(PlayerChoice.Title));
    }

    // Ported from SpotifyPlayerTests.swift: testThePlayerChoiceIsRememberedUnderACustomKey (the reading part: an unknown
    // value is Automatic; the settings round trip is SettingsAndOptionsTests.ThePlayerChoiceIsRememberedUnderItsKey).
    [Fact]
    public void AnUnknownChoiceIsAutomatic()
    {
        Assert.Equal("automatic", PlayerChoice.Normalize(null));
        Assert.Equal("automatic", PlayerChoice.Normalize(""));
        Assert.Equal("automatic", PlayerChoice.Normalize("winamp"));
        Assert.Equal("automatic", PlayerChoice.Normalize("app:"));
        Assert.Equal("automatic", PlayerChoice.Normalize("app:  "));
        Assert.Equal("spotify", PlayerChoice.Normalize("spotify"));
        Assert.Equal("app:winamp.exe", PlayerChoice.Normalize("app:winamp.exe"));
        Assert.True(PlayerChoice.Follows("appleMusic", AppleMusic));
        Assert.False(PlayerChoice.Follows("appleMusic", Spotify));
    }
}
