using System.Net;
using System.Security.Cryptography;
using Lunalyr.Core.Logging;
using Lunalyr.Install;
using Lunalyr.Shell.Updates;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests.Updates;

/// <summary>A web server that exists only in the test: each address answers with what the test gave it.</summary>
internal sealed class FakeUpdateServer : HttpMessageHandler
{
    public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);
    public List<string> Requests { get; } = [];

    public void Serve(string url, byte[] body) => Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        lock (Requests) Requests.Add(url);
        var response = Routes.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound);
        response.RequestMessage ??= request; // A route may say where a redirect ended.
        return Task.FromResult(response);
    }
}

public sealed class UpdateServiceTests : IDisposable
{
    private const string FeedUrl = "https://updates.example.com/lunalyr/update.json";
    private const string InstallerUrl = "https://updates.example.com/lunalyr/LunalyrSetup-0.9.0.exe";
    private static readonly (string Private, string Public) Key = UpdateFeeds.NewKey();

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lunalyr-update-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeUpdateServer _server = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));

    public UpdateServiceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private UpdateService Service(string current = "0.8.0", string? off = null, bool requireSignature = false, bool signatureTrusted = false,
                                  string? feedUrl = FeedUrl) =>
        new(new UpdateEnvironment
        {
            Configuration = feedUrl is null ? null : UpdateConfiguration.Create(feedUrl, Key.Public),
            CurrentVersion = current,
            Folder = Path.Combine(_folder, "Updates"),
            Http = _server,
            Time = _time,
            OffReason = off,
            RequireSignature = requireSignature,
            IsSignatureTrusted = _ => signatureTrusted,
        }, Logger.Null.App);

    /// <summary>Publishes a signed feed for an installer of <paramref name="version"/>; returns the installer's bytes.</summary>
    private byte[] Publish(string version, Func<UpdateFeed, UpdateFeed>? tamperFeed = null, string? installerVersion = null)
    {
        var app = Path.Combine(_folder, "publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, Product.ExeName), "exe " + version);
        var stub = Path.Combine(_folder, "stub.exe");
        File.WriteAllText(stub, "setup");
        var installer = Path.Combine(_folder, $"installer-{Guid.NewGuid():N}.exe");
        using (var zip = new MemoryStream())
        {
            Payload.WriteZip(zip, app, stub, installerVersion ?? version);
            Payload.WriteInstaller(stub, zip, installer);
        }
        var bytes = File.ReadAllBytes(installer);
        var feed = new UpdateFeed("Lunalyr", version, InstallerUrl, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
        feed = tamperFeed?.Invoke(feed) ?? feed;
        var feedBytes = UpdateFeeds.Serialize(feed);
        _server.Serve(FeedUrl, feedBytes);
        _server.Serve(FeedUrl + ".sig", System.Text.Encoding.ASCII.GetBytes(UpdateFeeds.Sign(feedBytes, Key.Private)));
        _server.Serve(InstallerUrl, bytes);
        return bytes;
    }

    [Fact]
    public async Task ANewerVersionIsDownloadedVerifiedAndReady()
    {
        var bytes = Publish("0.9.0");
        using var service = Service();
        var phases = new List<UpdatePhase>();
        service.Changed += s => { lock (phases) phases.Add(s.Phase); };

        await service.CheckAsync(download: true, TestContext.Current.CancellationToken);

        var state = service.State;
        Assert.Equal(UpdatePhase.Ready, state.Phase);
        Assert.Equal("0.9.0", state.NewVersion);
        Assert.Equal(bytes, File.ReadAllBytes(state.InstallerPath!));
        Assert.EndsWith("Lunalyr Setup 0.9.0.exe", state.InstallerPath);
        Assert.Equal("Restart and Update", UpdateCopy.Button(state));
        Assert.Equal([UpdatePhase.Checking, UpdatePhase.Available, UpdatePhase.Downloading], phases.Distinct().Take(3));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "Updates"), "*.partial"));
    }

    [Fact]
    public async Task TheButtonAsksBeforeDownloading()
    {
        Publish("0.9.0");
        using var service = Service();
        await service.CheckAsync(download: false, TestContext.Current.CancellationToken);
        Assert.Equal(UpdatePhase.Available, service.State.Phase);
        Assert.Equal("Download and Install", UpdateCopy.Button(service.State));
        Assert.DoesNotContain(InstallerUrl, _server.Requests);

        await service.DownloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdatePhase.Ready, service.State.Phase);
    }

    [Theory]
    [InlineData("0.9.0")]
    [InlineData("1.0.0")]
    [InlineData("0.9.0-beta.1")]
    public async Task TheSameOrAnOlderVersionIsUpToDate(string current)
    {
        Publish("0.9.0-beta.1");
        using var service = Service(current);
        await service.CheckAsync(download: true, TestContext.Current.CancellationToken);
        Assert.Equal(UpdatePhase.UpToDate, service.State.Phase);
        Assert.Contains("is the newest version", service.State.Status);
        Assert.DoesNotContain(InstallerUrl, _server.Requests);
    }

    [Fact]
    public async Task AnInstallerThatDoesntMatchTheFeedIsThrownAway()
    {
        Publish("0.9.0", tamperFeed: f => f with { Sha256 = new string('0', 64) });
        using var service = Service();

        await service.CheckAsync(download: true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdatePhase.Failed, service.State.Phase);
        Assert.Contains("checksum", service.State.Status);
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "Updates"), "Lunalyr Setup*"));
    }

    [Fact]
    public async Task ADownloadLargerThanTheFeedSaysIsCutOff()
    {
        var bytes = Publish("0.9.0", tamperFeed: f => f with { Size = 100 });
        using var service = Service();
        await service.CheckAsync(download: true, TestContext.Current.CancellationToken);
        Assert.Equal(UpdatePhase.Failed, service.State.Phase);
        Assert.Contains("larger", service.State.Status);
        Assert.True(bytes.Length > 100);
    }

    [Fact]
    public async Task AnInstallerHoldingAnotherVersionIsRefused()
    {
        Publish("0.9.0", installerVersion: "0.8.5");
        using var service = Service();
        await service.CheckAsync(download: true, TestContext.Current.CancellationToken);
        Assert.Equal(UpdatePhase.Failed, service.State.Phase);
        Assert.Contains("holds 0.8.5", service.State.Status);
    }

    [Fact]
    public async Task AFeedSignedByAnotherKeyIsRefused()
    {
        Publish("0.9.0");
        var other = UpdateFeeds.NewKey();
        var feed = await _server.Routes[FeedUrl]().Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        _server.Serve(FeedUrl + ".sig", System.Text.Encoding.ASCII.GetBytes(UpdateFeeds.Sign(feed, other.PrivateKeyPem)));
        using var service = Service();

        await service.CheckAsync(download: true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdatePhase.Failed, service.State.Phase);
        Assert.Contains("isn’t signed by Lunalyr’s key", service.State.Status);
        Assert.DoesNotContain(InstallerUrl, _server.Requests);
    }

    [Fact]
    public async Task OnceSignedAnUnsignedDownloadIsRefused()
    {
        Publish("0.9.0");
        using (var unsigned = Service(requireSignature: true, signatureTrusted: false))
        {
            await unsigned.CheckAsync(download: true, TestContext.Current.CancellationToken);
            Assert.Equal(UpdatePhase.Failed, unsigned.State.Phase);
            Assert.Contains("isn’t signed by Lunalyr", unsigned.State.Status);
        }
        using var signed = Service(requireSignature: true, signatureTrusted: true);
        await signed.CheckAsync(download: true, TestContext.Current.CancellationToken);
        Assert.Equal(UpdatePhase.Ready, signed.State.Phase);
    }

    [Fact]
    public async Task AServerErrorIsShownCalmly()
    {
        _server.Routes[FeedUrl] = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using var service = Service();
        await service.CheckAsync(download: true, TestContext.Current.CancellationToken);
        Assert.Equal(UpdatePhase.Failed, service.State.Phase);
        Assert.Equal("Couldn’t update: the update server answered 503. Try again later.", service.State.Status);
        Assert.True(service.CanCheck);
    }

    [Fact]
    public async Task ARedirectToPlainHttpIsRefused()
    {
        Publish("0.9.0");
        _server.Routes[FeedUrl] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([]),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://updates.example.com/lunalyr/update.json"),
        };
        using var service = Service();
        await service.CheckAsync(download: true, TestContext.Current.CancellationToken);
        Assert.Contains("insecure", service.State.Status);
    }

    [Fact]
    public async Task WithoutAnUpdateServiceNothingIsAsked()
    {
        using var service = Service(feedUrl: null);
        Assert.Equal(new UpdateState(UpdatePhase.Unconfigured, UpdateCopy.Unconfigured), service.State);
        Assert.False(service.AutomaticCheckDue(settingOn: true));

        await service.CheckAsync(download: true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCopy.UnconfiguredAsked, service.State.Status);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task TestRunsNeverCheck()
    {
        using var service = Service(off: "--no-network");
        Assert.Equal(UpdatePhase.Off, service.State.Phase);
        Assert.False(UpdateCopy.ButtonEnabled(service.State));
        await service.CheckAsync(download: true, TestContext.Current.CancellationToken);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task AutomaticChecksHappenOnceADayWhileTheSettingIsOn()
    {
        Publish("0.8.0");
        using var service = Service();
        Assert.True(service.AutomaticCheckDue(settingOn: true));
        Assert.False(service.AutomaticCheckDue(settingOn: false));

        await service.CheckAsync(download: true, TestContext.Current.CancellationToken);
        Assert.Equal(_time.GetUtcNow(), service.LastCheck);
        Assert.False(service.AutomaticCheckDue(settingOn: true));

        _time.Advance(TimeSpan.FromHours(23));
        Assert.False(service.AutomaticCheckDue(settingOn: true));
        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(service.AutomaticCheckDue(settingOn: true));
    }

    [Fact]
    public async Task AnEarlierVerifiedDownloadIsUsedAgain()
    {
        Publish("0.9.0");
        using (var first = Service()) await first.CheckAsync(download: true, TestContext.Current.CancellationToken);
        _server.Requests.Clear();

        using var second = Service();
        await second.CheckAsync(download: true, TestContext.Current.CancellationToken);

        Assert.Equal(UpdatePhase.Ready, second.State.Phase);
        Assert.DoesNotContain(InstallerUrl, _server.Requests);
    }
}
