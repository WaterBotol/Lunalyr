using Lunalyr.App.Player;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Core.Player;
using Lunalyr.Core.Player.Spotify;
using Lunalyr.Platform.Imaging;
using Lunalyr.Shell.Demo;
using Lunalyr.Shell.Player;

namespace Lunalyr.App.Hosting;

// The desktop player's search and queue (docs/APP-SHELL.md, "Search and queue in the player"): which players the card
// offers, their searches, covers for the rows, and the way to Settings › Music players. With --player-debug fake-search
// (only while playback commands are off, as under --demo) the card searches invented songs that never play.
internal sealed partial class AppHost
{
    private LyricHttpClient? _coverHttp;

    /// <summary>
    /// Why Spotify isn't there for the settings page and the card: "--demo", "--no-network", or null when it is (or
    /// is still starting: <see cref="Spotify"/> is null then too).
    /// </summary>
    public string? SpotifyOffReason => _demoSpotify is not null ? null : _options.Demo ? "--demo" : _options.NoNetwork ? "--no-network" : null;

    /// <summary>
    /// <c>--player-debug fake-spotify</c> (with playback off): a Spotify account for checks of the settings section,
    /// whose HTTP client refuses every request and whose browser opens nothing. It keeps no sign-in.
    /// </summary>
    private SpotifyAccount? _demoSpotify;

    private PlayerSearchSource CreatePlayerSearchSource()
    {
        var debug = _options.PlayerDebug;
        if (debug.Contains("fake-spotify") && PlaybackGuard.Process.IsBlocked)
        {
            var http = new LyricHttpClient(new HttpClient(new RefusingHandler()), new LyricHttpOptions { UserAgent = UserAgent, IsNetworkAvailable = () => false }, Log);
            var account = new SpotifyAccount(http, new NoTokenStore(), new NoBrowser(_log), _settings.Current.SpotifyClientId, new SpotifyOptions { Logger = Log });
            _settings.Changed += (_, e) => account.SetClientId(e.NewValue.SpotifyClientId);
            _demoSpotify = account;
            _log.Info("Settings: a Spotify account that sends nothing stands in for checks (--player-debug fake-spotify).");
        }
        PlayerPane? pane = null;
        string? query = null;
        PlayerSearchAvailability? forced = null;
        foreach (var flag in debug)
        {
            if (flag == "pane=search") pane = PlayerPane.Search;
            else if (flag == "pane=queue") pane = PlayerPane.Queue;
            else if (flag.StartsWith("query=", StringComparison.Ordinal)) query = flag[6..].Replace('+', ' ');
            else if (flag == "search=setup") forced = PlayerSearchAvailability.NeedsSetUp;
            else if (flag == "search=signin") forced = PlayerSearchAvailability.NeedsSignIn;
            else if (flag == "search=none") forced = PlayerSearchAvailability.None;
        }

        if (debug.Contains("fake-search"))
        {
            if (PlaybackGuard.Process.IsBlocked) return FakeSearchSource(pane, query, forced);
            _log.Warn("--player-debug fake-search is only for runs with playback off (--demo or --no-playback); ignored.");
        }

        return new PlayerSearchSource
        {
            Apps = () => _sessions?.Sessions.Select(s => s.SourceApp).ToList() ?? [],
            For = app => (PlayerSearch.Availability(app), PlayerSearch.For(app)),
            SpotifyOffReason = () => SpotifyOffReason,
            LoadCover = _options.Demo || _options.NoNetwork ? null : LoadSpotifyCoverAsync,
            OpenSettingsPage = OpenSettingsPage,
            DebugPane = pane,
            DebugQuery = query,
        };
    }

    /// <summary>The demo search: an invented Spotify that answers from made-up songs and refuses every play.</summary>
    private PlayerSearchSource FakeSearchSource(PlayerPane? pane, string? query, PlayerSearchAvailability? forced)
    {
        var fake = new DemoPlayerSearch(PlaybackGuard.Process, () =>
        {
            var song = _controller?.ReadPlayer();
            return song is { HasSong: true } ? new PlayedTrack(song.Title!, song.Artist ?? "") : null;
        }, TimeSpan.FromMilliseconds(350), Log);
        var spotify = new MediaSourceApp("Spotify.LunalyrDemo", MediaSourceKind.Spotify, "Spotify");
        var availability = forced ?? PlayerSearchAvailability.Ready;
        _log.Info($"Desktop player: the demo search stands in for Spotify ({availability}); it plays nothing (--player-debug).");
        return new PlayerSearchSource
        {
            Apps = () => _sessions?.Sessions.Select(s => s.SourceApp).ToList() ?? [],
            For = app => app?.Kind == MediaSourceKind.Spotify
                ? (availability, availability == PlayerSearchAvailability.Ready ? fake : new NoPlayerSearch("Spotify"))
                : (PlayerSearchAvailability.None, new NoPlayerSearch(app?.DisplayName ?? "This player")),
            QueueFor = _ => availability == PlayerSearchAvailability.Ready ? fake : new NoPlayerSearch("Spotify"),
            SpotifyOffReason = () => null,
            LoadCover = LoadDemoCoverAsync,
            OpenSettingsPage = OpenSettingsPage,
            Preferred = spotify,
            DebugPane = pane,
            DebugQuery = query,
        };
    }

    /// <summary>Refuses every request before anything is sent (the fake-spotify account's network).</summary>
    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Nothing is sent to Spotify in this run."));
    }

    private sealed class NoTokenStore : ISpotifyTokenStore
    {
        public SpotifyStoredToken? Load() => null;

        public void Save(SpotifyStoredToken token)
        {
        }

        public void Clear()
        {
        }
    }

    private sealed class NoBrowser(Lunalyr.Core.Logging.CategoryLogger log) : IBrowserLauncher
    {
        public bool Open(Uri address)
        {
            log.Info("Spotify sign-in: no browser is opened in this run (--player-debug fake-spotify).");
            return false;
        }
    }

    /// <summary>A demo row's cover, painted in code from its id (never fetched).</summary>
    private static Task<CoverArt?> LoadDemoCoverAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != DemoPlayerSearch.CoverScheme) return Task.FromResult<CoverArt?>(null);
        return Task.Run(() =>
        {
            // A stable hash of the id (string.GetHashCode changes from run to run).
            var hash = uri.AbsolutePath.Aggregate(2166136261u, (h, c) => unchecked((h ^ c) * 16777619u));
            hash ^= hash >> 15;
            hash = unchecked(hash * 0x2C1B3C6Du);
            hash ^= hash >> 12;
            DemoCover.Rgb Colour(int shift) =>
                new((byte)(60 + ((hash >> shift) & 0x7F)), (byte)(40 + ((hash >> (shift + 7)) & 0x9F)), (byte)(80 + ((hash >> (shift + 3)) & 0x7F)));
            var bytes = DemoCover.Paint(Colour(0), Colour(5), Colour(11), Colour(17), new DemoCover.Rgb(255, 214, 170), side: 96);
            return ArtworkDecoder.DecodeCover(bytes, 96);
        }, cancellationToken);
    }

    /// <summary>
    /// A result's cover from Spotify's image servers (HTTPS only, *.scdn.co or *.spotifycdn.com): only for results the
    /// user searched for, and never under --demo or --no-network.
    /// </summary>
    private async Task<CoverArt?> LoadSpotifyCoverAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        var host = uri.Host;
        if (!host.EndsWith(".scdn.co", StringComparison.OrdinalIgnoreCase) && !host.EndsWith(".spotifycdn.com", StringComparison.OrdinalIgnoreCase))
            return null;
        _coverHttp ??= new LyricHttpClient(LyricHttpClient.CreateDefaultHttpClient(), new LyricHttpOptions { UserAgent = UserAgent }, Log);
        try
        {
            var response = await _coverHttp.GetAsync("Spotify covers", uri, TimeSpan.FromSeconds(10), null, cancellationToken).ConfigureAwait(false);
            return response.IsOk ? ArtworkDecoder.DecodeCover(response.Body, 96) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A cover that can't be had stays a plain tile.
            return null;
        }
    }
}
