using System.Net;
using System.Net.Sockets;
using Lunalyr.Core.Player;
using Lunalyr.Core.Player.Spotify;
using Lunalyr.Core.Tests.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Player;

/// <summary>
/// The Spotify sign-in: PKCE, the loopback redirect (a local fake browser on 127.0.0.1, port chosen by Windows), the
/// code exchange, refresh before expiry, rotation, revocation and sign-out. Nothing reaches Spotify.
/// </summary>
public sealed class SpotifyAccountTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(20);

    [Fact]
    public void PkceChallengeMatchesTheRfc7636Example()
    {
        // RFC 7636, appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", SpotifyPkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void VerifiersAndStatesAreRandomAndWithinTheRules()
    {
        var a = SpotifyPkce.CreateVerifier();
        var b = SpotifyPkce.CreateVerifier();
        Assert.NotEqual(a, b);
        Assert.Equal(64, a.Length);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~', $"'{c}'"));
        Assert.NotEqual(SpotifyPkce.CreateState(), SpotifyPkce.CreateState());
        Assert.DoesNotContain('=', SpotifyPkce.Challenge(a));
    }

    [Fact]
    public void TheAuthorizeAddressAsksForExactlyWhatIsNeeded()
    {
        var uri = SpotifyPkce.AuthorizeUri(SpotifyKit.ClientId, SpotifyOptions.RedirectUriFor(SpotifyOptions.DefaultRedirectPort), "CH", "ST", SpotifyOptions.Scopes);
        Assert.Equal("accounts.spotify.com", uri.Host);
        Assert.Equal("/authorize", uri.AbsolutePath);
        Assert.Equal(SpotifyKit.ClientId, FixtureHandler.QueryOf(uri, "client_id"));
        Assert.Equal("code", FixtureHandler.QueryOf(uri, "response_type"));
        Assert.Equal("http://127.0.0.1:47823/callback", FixtureHandler.QueryOf(uri, "redirect_uri"));
        Assert.Equal("S256", FixtureHandler.QueryOf(uri, "code_challenge_method"));
        Assert.Equal("CH", FixtureHandler.QueryOf(uri, "code_challenge"));
        Assert.Equal("ST", FixtureHandler.QueryOf(uri, "state"));
        Assert.Equal("user-read-playback-state user-read-currently-playing user-modify-playback-state", FixtureHandler.QueryOf(uri, "scope"));
    }

    [Fact]
    public async Task SigningInExchangesTheCodeAndKeepsTheRefreshToken()
    {
        var kit = new SpotifyKit(signedIn: false);
        kit.Fake.On("POST token", SpotifyFake.Json("token-exchange.json"));
        kit.Fake.On("GET search", SpotifyFake.Json("search.json"));
        string? page = null;
        Task? visit = null;
        kit.Browser.OnOpen = authorize =>
        {
            var redirect = FixtureHandler.QueryOf(authorize, "redirect_uri")!;
            var state = FixtureHandler.QueryOf(authorize, "state")!;
            visit = Task.Run(async () =>
            {
                // A browser asks for its icon too: that must not end the wait.
                await LocalBrowser.GetAsync(new Uri(new Uri(redirect), "/favicon.ico"));
                page = (await LocalBrowser.GetAsync(new Uri($"{redirect}?code=CODE-1&state={Uri.EscapeDataString(state)}"))).Body;
            });
            return true;
        };
        var changes = 0;
        kit.Account.StateChanged += (_, _) => changes++;
        Assert.Equal(SpotifyAccountState.SignedOut, kit.Account.State);

        var result = await kit.Account.SignInAsync(TestContext.Current.CancellationToken).WaitAsync(Bounded, TestContext.Current.CancellationToken);
        await visit!.WaitAsync(Bounded, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(SpotifyAccountState.SignedIn, kit.Account.State);
        Assert.True(changes >= 2, "signing in, then signed in");
        Assert.Contains("Signed in to Spotify", page, StringComparison.Ordinal);
        Assert.Equal(new SpotifyStoredToken(SpotifyKit.ClientId, "RT-exchange", SpotifyOptions.Scopes), kit.Store.Token);

        var authorizeUri = Assert.Single(kit.Browser.Opened);
        var exchange = Assert.Single(kit.Fake.SentTo("POST token"));
        Assert.Equal("authorization_code", exchange.Form("grant_type"));
        Assert.Equal("CODE-1", exchange.Form("code"));
        Assert.Equal(SpotifyKit.ClientId, exchange.Form("client_id"));
        Assert.Equal(FixtureHandler.QueryOf(authorizeUri, "redirect_uri"), exchange.Form("redirect_uri"));
        Assert.StartsWith("http://127.0.0.1:", exchange.Form("redirect_uri"), StringComparison.Ordinal);
        Assert.Equal(FixtureHandler.QueryOf(authorizeUri, "code_challenge"), SpotifyPkce.Challenge(exchange.Form("code_verifier")!));
        Assert.Null(exchange.Bearer);

        // The exchange's access token is used at once: no refresh.
        await kit.Search.SearchAsync("blinding lights", TestContext.Current.CancellationToken);
        Assert.Equal("AT-exchange", Assert.Single(kit.Fake.SentTo("GET search")).Bearer);
        Assert.Single(kit.Fake.SentTo("POST token"));
    }

    [Fact]
    public async Task DecliningOnSpotifysPageStoresNothing()
    {
        var kit = new SpotifyKit(signedIn: false);
        Task? visit = null;
        kit.Browser.OnOpen = authorize =>
        {
            var redirect = FixtureHandler.QueryOf(authorize, "redirect_uri")!;
            var state = FixtureHandler.QueryOf(authorize, "state")!;
            visit = Task.Run(() => LocalBrowser.GetAsync(new Uri($"{redirect}?error=access_denied&state={Uri.EscapeDataString(state)}")));
            return true;
        };
        var result = await kit.Account.SignInAsync(TestContext.Current.CancellationToken).WaitAsync(Bounded, TestContext.Current.CancellationToken);
        await visit!.WaitAsync(Bounded, TestContext.Current.CancellationToken);
        Assert.Equal(SpotifySignInStatus.Denied, result.Status);
        Assert.Equal(SpotifyAccountState.SignedOut, kit.Account.State);
        Assert.Null(kit.Store.Token);
        Assert.Empty(kit.Fake.Sent);
    }

    [Fact]
    public async Task ABusyPortIsSaidPlainlyAndTheBrowserIsNotOpened()
    {
        using var busy = LoopbackRedirectListener.Start(0);
        var kit = new SpotifyKit(signedIn: false, options: o => o with { RedirectPort = busy.Port });
        var result = await kit.Account.SignInAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SpotifySignInStatus.PortInUse, result.Status);
        Assert.Contains(busy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Message, StringComparison.Ordinal);
        Assert.Empty(kit.Browser.Opened);
        Assert.Equal(SpotifyAccountState.SignedOut, kit.Account.State);
    }

    [Fact]
    public async Task ASignInNobodyFinishesTimesOutAndCanBeCancelled()
    {
        var kit = new SpotifyKit(signedIn: false, options: o => o with { SignInTimeout = TimeSpan.FromMilliseconds(100) });
        var timedOut = await kit.Account.SignInAsync(TestContext.Current.CancellationToken).WaitAsync(Bounded, TestContext.Current.CancellationToken);
        Assert.Equal(SpotifySignInStatus.TimedOut, timedOut.Status);
        Assert.Equal(SpotifyAccountState.SignedOut, kit.Account.State);

        var patient = new SpotifyKit(signedIn: false);
        using var cancel = new CancellationTokenSource();
        var waiting = patient.Account.SignInAsync(cancel.Token);
        Assert.Equal(SpotifyAccountState.SigningIn, patient.Account.State);
        Assert.Equal(SpotifySignInStatus.AlreadySigningIn, (await patient.Account.SignInAsync(TestContext.Current.CancellationToken)).Status);
        await cancel.CancelAsync();
        Assert.Equal(SpotifySignInStatus.Cancelled, (await waiting.WaitAsync(Bounded, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(SpotifyAccountState.SignedOut, patient.Account.State);
    }

    [Fact]
    public async Task WithoutAClientIdNothingIsSentAnywhere()
    {
        var kit = new SpotifyKit(clientId: "");
        Assert.Equal(SpotifyAccountState.NotSetUp, kit.Account.State);
        Assert.Equal(SpotifySignInStatus.NotSetUp, (await kit.Account.SignInAsync(TestContext.Current.CancellationToken)).Status);
        var error = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", TestContext.Current.CancellationToken));
        Assert.Equal(PlayerSearchError.NotSetUp, error.Error);
        Assert.Empty(kit.Browser.Opened);
        Assert.Empty(kit.Fake.Sent);
    }

    [Fact]
    public async Task TheAccessTokenIsRefreshedBeforeItExpires()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("GET search", SpotifyFake.Json("search.json"));

        await kit.Search.SearchAsync("blinding", TestContext.Current.CancellationToken);
        var refresh = Assert.Single(kit.Fake.SentTo("POST token"));
        Assert.Equal("refresh_token", refresh.Form("grant_type"));
        Assert.Equal("RT-stored", refresh.Form("refresh_token"));
        Assert.Equal(SpotifyKit.ClientId, refresh.Form("client_id"));

        kit.Time.AdvanceSeconds(3000); // 600 s left
        await kit.Search.SearchAsync("blinding", TestContext.Current.CancellationToken);
        Assert.Single(kit.Fake.SentTo("POST token"));

        kit.Time.AdvanceSeconds(560); // 40 s left: inside the minute's margin
        await kit.Search.SearchAsync("blinding", TestContext.Current.CancellationToken);
        Assert.Equal(2, kit.Fake.SentTo("POST token").Count);
        Assert.All(kit.Fake.SentTo("GET search"), r => Assert.Equal("AT-refreshed", r.Bearer));
        Assert.Equal(0, kit.Store.Saves);
        Assert.Equal("RT-stored", kit.Store.Token!.RefreshToken);
    }

    [Fact]
    public async Task ARotatedRefreshTokenIsKept()
    {
        var kit = new SpotifyKit();
        kit.Fake.On("POST token", SpotifyFake.Json("token-refresh-rotated.json"));
        kit.Fake.On("GET search", SpotifyFake.Json("search.json"));
        await kit.Search.SearchAsync("blinding", TestContext.Current.CancellationToken);
        Assert.Equal("RT-rotated", kit.Store.Token!.RefreshToken);
        Assert.Equal(1, kit.Store.Saves);
        Assert.Equal("AT-rotated", Assert.Single(kit.Fake.SentTo("GET search")).Bearer);
    }

    [Fact]
    public async Task CallersWaitingForATokenShareOneRefresh()
    {
        var kit = new SpotifyKit();
        var release = new TaskCompletionSource();
        kit.Fake.On("POST token", SpotifyFake.Json("token-refresh.json") with { Wait = release.Task });
        kit.Fake.On("GET search", SpotifyFake.Json("search.json"));

        var searches = Enumerable.Range(0, 3).Select(_ => kit.Search.SearchAsync("blinding", TestContext.Current.CancellationToken)).ToList();
        await Until(() => kit.Fake.SentTo("POST token").Count == 1);
        release.SetResult();
        await Task.WhenAll(searches).WaitAsync(Bounded, TestContext.Current.CancellationToken);

        Assert.Single(kit.Fake.SentTo("POST token"));
        Assert.Equal(3, kit.Fake.SentTo("GET search").Count);
    }

    [Fact]
    public async Task ARevokedSignInSignsOutAndAsksForANewOne()
    {
        var kit = new SpotifyKit();
        kit.Fake.On("POST token", SpotifyFake.Json("error-invalid-grant.json", 400));
        var changes = 0;
        kit.Account.StateChanged += (_, _) => changes++;

        var error = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", TestContext.Current.CancellationToken));
        Assert.Equal(PlayerSearchError.SignInExpired, error.Error);
        Assert.Equal("Your Spotify sign-in has ended. Sign in again in Lunalyr’s settings.", PlayerSearchText.Words(error, "Spotify"));
        Assert.Null(kit.Store.Token);
        Assert.Equal(1, kit.Store.Clears);
        Assert.Equal(SpotifyAccountState.SignedOut, kit.Account.State);
        Assert.Equal(1, changes);

        var again = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", TestContext.Current.CancellationToken));
        Assert.Equal(PlayerSearchError.NotSignedIn, again.Error);
        Assert.Single(kit.Fake.Sent);
    }

    [Fact]
    public async Task BeingOfflineOrASpotifyFaultDuringARefreshKeepsTheSignIn()
    {
        var kit = new SpotifyKit { NetworkUp = false };
        kit.Fake.On("POST token", Reply.Broken);
        var offline = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", TestContext.Current.CancellationToken));
        Assert.Equal(PlayerSearchError.Offline, offline.Error);
        Assert.Equal("You’re offline. Try again when you’re back online.", PlayerSearchText.Words(offline, "Spotify"));

        kit.NetworkUp = true;
        kit.Fake.On("POST token", Reply.Text(503, ""));
        var fault = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", TestContext.Current.CancellationToken));
        Assert.Equal(PlayerSearchError.Failed, fault.Error);
        Assert.Equal(503, fault.StatusCode);

        Assert.Equal("RT-stored", kit.Store.Token!.RefreshToken);
        Assert.Equal(0, kit.Store.Clears);
        Assert.Equal(SpotifyAccountState.SignedIn, kit.Account.State);
    }

    [Fact]
    public async Task AnotherClientIdDoesNotUseThisSignIn()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Account.SetClientId("ffffffffffffffffffffffffffffffff");
        Assert.Equal(SpotifyAccountState.SignedOut, kit.Account.State);
        var error = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", TestContext.Current.CancellationToken));
        Assert.Equal(PlayerSearchError.NotSignedIn, error.Error);
        Assert.Empty(kit.Fake.Sent);
        Assert.NotNull(kit.Store.Token);

        kit.Account.SetClientId($"  {SpotifyKit.ClientId} ");
        Assert.Equal(SpotifyAccountState.SignedIn, kit.Account.State);
        kit.Account.SetClientId("not a client id");
        Assert.Equal(SpotifyAccountState.NotSetUp, kit.Account.State);
    }

    [Fact]
    public async Task SigningOutForgetsEverythingWithoutAskingSpotify()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("GET search", SpotifyFake.Json("search.json"));
        await kit.Search.SearchAsync("blinding", TestContext.Current.CancellationToken);
        var sent = kit.Fake.Sent.Count;

        kit.Account.SignOut();
        Assert.Equal(SpotifyAccountState.SignedOut, kit.Account.State);
        Assert.Null(kit.Store.Token);
        Assert.Equal(sent, kit.Fake.Sent.Count);
        var error = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", TestContext.Current.CancellationToken));
        Assert.Equal(PlayerSearchError.NotSignedIn, error.Error);
        Assert.Equal(sent, kit.Fake.Sent.Count);
    }

    [Fact]
    public void TokensNeverPrint()
    {
        var stored = new SpotifyStoredToken(SpotifyKit.ClientId, "RT-secret", "scope");
        Assert.DoesNotContain("RT-secret", stored.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ClientIdsAreTrimmedAndCheckedForShape()
    {
        Assert.Equal(SpotifyKit.ClientId, SpotifyOptions.NormalizeClientId($" {SpotifyKit.ClientId}\n"));
        Assert.Equal("", SpotifyOptions.NormalizeClientId("abc"));
        Assert.Equal("", SpotifyOptions.NormalizeClientId(SpotifyKit.ClientId + "0"));
        Assert.Equal("", SpotifyOptions.NormalizeClientId("0123456789abcdef0123456789abcde!"));
        Assert.Equal("", SpotifyOptions.NormalizeClientId(null));
    }

    internal static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 2000 && !condition(); i++) await Task.Delay(5);
        Assert.True(condition(), "timed out waiting");
    }
}

/// <summary>Plays the browser's part against the loopback listener: plain HTTP to 127.0.0.1, never through a proxy.</summary>
internal static class LocalBrowser
{
    public static async Task<(int Status, string Body)> GetAsync(Uri address)
    {
        Assert.Equal(IPAddress.Loopback.ToString(), address.Host);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await http.GetAsync(address);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Sends raw bytes and reads the answer (for requests a browser wouldn't make).</summary>
    public static async Task<string> RawAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
