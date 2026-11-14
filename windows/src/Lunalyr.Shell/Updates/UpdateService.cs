using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Lunalyr.Core.Logging;
using Lunalyr.Install;

namespace Lunalyr.Shell.Updates;

/// <summary>Where an update stands.</summary>
public enum UpdatePhase
{
    /// <summary>This build has no update service (no feed or key built in).</summary>
    Unconfigured,

    /// <summary>Checks are off for this run (<c>--no-network</c>, the demo).</summary>
    Off,

    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,

    /// <summary>Downloaded and verified: Restart and Update installs it.</summary>
    Ready,

    Failed,
}

/// <summary>What the General page and the tray show.</summary>
public sealed record UpdateState(UpdatePhase Phase, string Status, UpdateFeed? Feed = null, double Progress = 0,
                                 string? InstallerPath = null)
{
    public string? NewVersion => Feed?.Version;
}

/// <summary>What an update needs from outside: tests pass fakes and temp folders.</summary>
public sealed class UpdateEnvironment
{
    public required UpdateConfiguration? Configuration { get; init; }
    public required string CurrentVersion { get; init; }

    /// <summary>Where downloads go (<c>{data}\Updates</c>), also holding when the last check was.</summary>
    public required string Folder { get; init; }

    public required HttpMessageHandler Http { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public string UserAgent { get; init; } = "Lunalyr";

    /// <summary>Why checks are off this run, or null.</summary>
    public string? OffReason { get; init; }

    /// <summary>Downloads must carry a trusted Authenticode signature (on once Lunalyr is signed).</summary>
    public bool RequireSignature { get; init; }

    /// <summary>The signature check (WinVerifyTrust; a fake in tests).</summary>
    public Func<string, bool> IsSignatureTrusted { get; init; } = Authenticode.IsTrusted;
}

/// <summary>
/// Checks the signed feed, downloads the installer and verifies it (size, SHA-256, the installer's own payload checksum
/// and version, and its Authenticode signature once Lunalyr is signed). Installing is the app's job: it starts the
/// verified installer with <c>--update</c> and quits. Re-implemented from the Mac's behaviour (Sparkle, Verso lines);
/// automatic checks happen at most once a day while the setting is on.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private const string LastCheckFile = "last-check.txt";

    private readonly UpdateEnvironment _env;
    private readonly HttpClient _http;
    private readonly CategoryLogger _log;
    private readonly ProductVersion _current;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private UpdateState _state;

    public UpdateService(UpdateEnvironment environment, CategoryLogger log)
    {
        _env = environment;
        _log = log;
        _current = ProductVersion.Parse(environment.CurrentVersion) ?? new ProductVersion(0, 0, 0, "");
        _http = new HttpClient(environment.Http, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(environment.UserAgent);
        _state = environment.Configuration is null
            ? new(UpdatePhase.Unconfigured, UpdateCopy.Unconfigured)
            : environment.OffReason is { } reason
                ? new(UpdatePhase.Off, UpdateCopy.Off(reason))
                : new(UpdatePhase.Idle, UpdateCopy.Idle);
    }

    public UpdateState State => Volatile.Read(ref _state);

    /// <summary>Raised on any thread whenever <see cref="State"/> changes.</summary>
    public event Action<UpdateState>? Changed;

    public bool CanCheck => State.Phase is UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.Available or UpdatePhase.Failed;

    /// <summary>When the last check finished (from <c>last-check.txt</c>), or null.</summary>
    public DateTimeOffset? LastCheck
    {
        get
        {
            try
            {
                var text = File.ReadAllText(Path.Combine(_env.Folder, LastCheckFile)).Trim();
                return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Whether an automatic check is due now: the setting is on, checks can run, and a day has passed.</summary>
    public bool AutomaticCheckDue(bool settingOn)
    {
        if (!settingOn || State.Phase is not (UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.Failed)) return false;
        var last = LastCheck;
        var now = _env.Time.GetUtcNow();
        return last is null || now - last.Value >= CheckInterval || last.Value > now + TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Asks the feed for the newest version. With <paramref name="download"/> a newer version is downloaded and verified
    /// straight away (automatic checks do; the button asks first).
    /// </summary>
    public async Task CheckAsync(bool download, CancellationToken cancel = default)
    {
        var config = _env.Configuration;
        if (config is null)
        {
            Set(new(UpdatePhase.Unconfigured, UpdateCopy.UnconfiguredAsked));
            return;
        }
        if (_env.OffReason is { } reason)
        {
            Set(new(UpdatePhase.Off, UpdateCopy.Off(reason)));
            return;
        }
        if (!await _busy.WaitAsync(0, cancel).ConfigureAwait(false)) return;
        try
        {
            Set(new(UpdatePhase.Checking, UpdateCopy.Checking));
            var feedBytes = await GetSmallAsync(config.Feed, cancel).ConfigureAwait(false);
            var signature = System.Text.Encoding.ASCII.GetString(await GetSmallAsync(config.SignatureUrl, cancel).ConfigureAwait(false));
            var (feed, problem) = UpdateFeeds.Read(feedBytes, signature, config.PublicKey);
            RecordCheck();
            if (feed is null)
            {
                _log.Warn($"Update check: {problem}");
                Set(new(UpdatePhase.Failed, UpdateCopy.Failed(problem!)));
                return;
            }
            var offered = ProductVersion.Parse(feed.Version)!;
            if (offered <= _current)
            {
                _log.Info($"Update check: {_current} is the newest (the feed offers {feed.Version}).");
                Set(new(UpdatePhase.UpToDate, UpdateCopy.UpToDate(_current.ToString(), _env.Time.GetLocalNow())));
                return;
            }
            _log.Info($"Update check: Lunalyr {feed.Version} is available.");
            if (ExistingDownload(feed) is { } ready)
            {
                Set(new(UpdatePhase.Ready, UpdateCopy.Ready(feed.Version), feed, 1, ready));
                return;
            }
            Set(new(UpdatePhase.Available, UpdateCopy.Available(feed.Version), feed));
            if (download) await DownloadCoreAsync(feed, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Set(new(UpdatePhase.Idle, UpdateCopy.Idle));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            _log.Warn($"Update check failed: {ex.Message}");
            Set(new(UpdatePhase.Failed, UpdateCopy.Failed(UpdateCopy.Describe(ex))));
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Downloads and verifies the version <see cref="CheckAsync"/> found.</summary>
    public async Task DownloadAsync(CancellationToken cancel = default)
    {
        if (State is not { Phase: UpdatePhase.Available, Feed: { } feed }) return;
        if (!await _busy.WaitAsync(0, cancel).ConfigureAwait(false)) return;
        try
        {
            await DownloadCoreAsync(feed, cancel).ConfigureAwait(false);
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task DownloadCoreAsync(UpdateFeed feed, CancellationToken cancel)
    {
        Directory.CreateDirectory(_env.Folder);
        var final = InstallerPath(feed);
        var partial = final + ".partial";
        try
        {
            Set(new(UpdatePhase.Downloading, UpdateCopy.Downloading(feed.Version, 0), feed));
            using (var response = await _http.GetAsync(new Uri(feed.Url), HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
            {
                EnsureSafe(response);
                await using var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long total = 0;
                var lastReport = 0.0;
                int read;
                while ((read = await body.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > feed.Size) throw new InvalidDataException("The download is larger than the update feed says.");
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    var fraction = (double)total / feed.Size;
                    if (fraction - lastReport >= 0.01)
                    {
                        lastReport = fraction;
                        Set(new(UpdatePhase.Downloading, UpdateCopy.Downloading(feed.Version, fraction), feed, fraction));
                    }
                }
                await file.FlushAsync(cancel).ConfigureAwait(false);
            }

            if (Verify(partial, feed) is { } problem)
            {
                TryDelete(partial);
                _log.Warn($"Update download refused: {problem}");
                Set(new(UpdatePhase.Failed, UpdateCopy.Failed(problem), feed));
                return;
            }
            File.Move(partial, final, overwrite: true);
            RemoveOtherDownloads(final);
            _log.Info($"Downloaded and verified Lunalyr {feed.Version} ({feed.Size:N0} bytes).");
            Set(new(UpdatePhase.Ready, UpdateCopy.Ready(feed.Version), feed, 1, final));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            TryDelete(partial);
            Set(new(UpdatePhase.Available, UpdateCopy.Available(feed.Version), feed));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException
                                       or UnauthorizedAccessException)
        {
            TryDelete(partial);
            _log.Warn($"Update download failed: {ex.Message}");
            Set(new(UpdatePhase.Failed, UpdateCopy.Failed(UpdateCopy.Describe(ex)), feed));
        }
    }

    /// <summary>Why a downloaded installer can't be used, or null.</summary>
    private string? Verify(string path, UpdateFeed feed)
    {
        using (var stream = File.OpenRead(path))
        {
            if (!UpdateFeeds.Matches(stream, feed)) return "The download doesn’t match the update feed’s checksum.";
            var payload = Payload.Locate(stream);
            if (payload is null || !Payload.Verify(stream, payload)) return "The downloaded installer is damaged.";
            using var zip = Payload.Open(stream, payload);
            var manifest = Payload.ReadManifest(zip);
            if (!string.Equals(manifest.Version, feed.Version, StringComparison.Ordinal))
                return $"The downloaded installer holds {manifest.Version}, not {feed.Version}.";
        }
        if (_env.RequireSignature && !_env.IsSignatureTrusted(path))
            return "The downloaded installer isn’t signed by Lunalyr.";
        return null;
    }

    private string? ExistingDownload(UpdateFeed feed)
    {
        var path = InstallerPath(feed);
        if (!File.Exists(path)) return null;
        try
        {
            if (Verify(path, feed) is null) return path;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _log.Info($"An earlier download can't be used: {ex.Message}");
        }
        TryDelete(path);
        return null;
    }

    /// <summary>
    /// Removes downloads this version has no use for: installers for this version or older (an update that has been
    /// installed) and unfinished downloads. Called at start-up.
    /// </summary>
    public void RemoveStaleDownloads()
    {
        if (!Directory.Exists(_env.Folder)) return;
        foreach (var file in Directory.EnumerateFiles(_env.Folder, "Lunalyr Setup *"))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
                continue;
            }
            var version = ProductVersion.Parse(Path.GetFileNameWithoutExtension(name)["Lunalyr Setup ".Length..]);
            if (version is not null && version <= _current)
            {
                TryDelete(file);
                _log.Info($"Removed the installed update's download ({name}).");
            }
        }
    }

    private string InstallerPath(UpdateFeed feed) =>
        Path.Combine(_env.Folder, $"Lunalyr Setup {ProductVersion.Parse(feed.Version)}.exe");

    private void RemoveOtherDownloads(string keep)
    {
        foreach (var file in Directory.EnumerateFiles(_env.Folder, "Lunalyr Setup *"))
            if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) TryDelete(file);
    }

    private async Task<byte[]> GetSmallAsync(Uri url, CancellationToken cancel)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        EnsureSafe(response);
        if (response.Content.Headers.ContentLength > UpdateFeeds.MaxFeedBytes) throw new InvalidDataException("The update feed is too large.");
        await using var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var copy = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + read > UpdateFeeds.MaxFeedBytes) throw new InvalidDataException("The update feed is too large.");
            copy.Write(buffer, 0, read);
        }
        return copy.ToArray();
    }

    /// <summary>A redirect may only land on another https address; anything but 200 is a failure.</summary>
    private static void EnsureSafe(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri is { } final && !UpdateConfiguration.IsSafeDownload(final))
            throw new HttpRequestException("The update server redirected to an insecure address.");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"The update server answered {(int)response.StatusCode}.", null, response.StatusCode);
    }

    private void RecordCheck()
    {
        try
        {
            Directory.CreateDirectory(_env.Folder);
            File.WriteAllText(Path.Combine(_env.Folder, LastCheckFile), _env.Time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Info($"Couldn't note the update check: {ex.Message}");
        }
    }

    private void Set(UpdateState state)
    {
        Volatile.Write(ref _state, state);
        Changed?.Invoke(state);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _busy.Dispose();
    }
}

/// <summary>What the update rows say. The unconfigured wording is the Mac's.</summary>
public static class UpdateCopy
{
    public const string Unconfigured = "An update service hasn’t been set up for this build yet.";

    public const string UnconfiguredAsked = "Updates aren’t available for this build yet. The person distributing this copy needs to set up its update service; for now, install the newer Lunalyr they share with you.";

    public const string Idle = "Lunalyr checks for a new version once a day and downloads it; it installs when you say so.";

    public const string Checking = "Checking for updates…";

    public static string Off(string reason) => $"Update checks are off for this run ({reason}).";

    public static string UpToDate(string version, DateTimeOffset at) =>
        $"Lunalyr {version} is the newest version. Checked {at.ToString("t", CultureInfo.CurrentCulture)}.";

    public static string Available(string version) => $"Lunalyr {version} is available.";

    public static string Downloading(string version, double fraction) =>
        $"Downloading Lunalyr {version}… {Math.Clamp(fraction, 0, 1):P0}";

    public static string Ready(string version) =>
        $"Lunalyr {version} is downloaded and checked. Restart and update installs it; your settings stay.";

    public static string Failed(string reason) => $"Couldn’t update: {reason}";

    public static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => $"the update server answered {(int)code}. Try again later.",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError } =>
            "Lunalyr can’t reach the update server. Check your connection.",
        TaskCanceledException => "the update server took too long to answer.",
        _ => ex.Message,
    };

    /// <summary>The button beside the status.</summary>
    public static string Button(UpdateState state) => state.Phase switch
    {
        UpdatePhase.Available => "Download and install",
        UpdatePhase.Ready => "Restart and update",
        _ => "Check for updates",
    };

    public static bool ButtonEnabled(UpdateState state) =>
        state.Phase is not (UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Off);
}
