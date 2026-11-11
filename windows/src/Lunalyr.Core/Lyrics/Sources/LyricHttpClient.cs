using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using Lunalyr.Core.Logging;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>How the lyric sources talk HTTP. Every value can be changed for tests or by settings.</summary>
public sealed record LyricHttpOptions
{
    /// <summary>A generic agent naming the app; never a browser's, never anything personal.</summary>
    public const string DefaultUserAgent = "Lunalyr/0.1 (Windows; desktop lyrics)";

    public string UserAgent { get; init; } = DefaultUserAgent;

    /// <summary>Larger replies are refused: no lyric reply comes near this.</summary>
    public int MaxResponseBytes { get; init; } = 2_000_000;

    /// <summary>
    /// Politeness: requests to one host are spaced at least this far apart, whichever source sends them. Zero turns
    /// spacing off (tests).
    /// </summary>
    public TimeSpan MinimumHostInterval { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Whether the PC has a network at all, to tell "offline" from "this service is down".</summary>
    public Func<bool> IsNetworkAvailable { get; init; } = NetworkInterface.GetIsNetworkAvailable;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>A reply as received: status, body bytes (at most <see cref="LyricHttpOptions.MaxResponseBytes"/>) and Retry-After.</summary>
public sealed record LyricHttpResponse(int StatusCode, byte[] Body, TimeSpan? RetryAfter)
{
    public bool IsOk => StatusCode == 200;

    /// <summary>The body as UTF-8 text without a byte-order mark.</summary>
    public string Text => LyricHttpClient.Utf8Text(Body);
}

/// <summary>
/// The HTTP layer the lyric sources share: GET with a per-request timeout, a size cap, per-host spacing, and failures
/// mapped to <see cref="LyricSourceException"/> (see <see cref="ILyricSource"/> for the contract).
/// </summary>
public sealed class LyricHttpClient
{
    private readonly HttpClient _http;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _nextSlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly CategoryLogger _log;

    public LyricHttpClient(HttpClient http, LyricHttpOptions? options = null, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        Options = options ?? new LyricHttpOptions();
        _log = (logger ?? Logger.Null).Lyrics;
    }

    public LyricHttpOptions Options { get; }

    public TimeProvider Time => Options.TimeProvider;

    /// <summary>
    /// An <see cref="HttpClient"/> suited to the sources: no client-wide timeout (each request has its own),
    /// compressed replies inflated, connections recycled so DNS changes are seen.
    /// </summary>
    public static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseCookies = false,
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// <paramref name="address"/> with <paramref name="query"/> appended, percent-encoded. Parameters are sorted by name
    /// unless <paramref name="sorted"/> is false, so the same lookup always asks the same address.
    /// </summary>
    public static Uri BuildUri(string address, IEnumerable<KeyValuePair<string, string>> query, bool sorted = true)
    {
        var pairs = sorted ? query.OrderBy(p => p.Key, StringComparer.Ordinal) : query;
        var builder = new StringBuilder(address);
        var first = !address.Contains('?', StringComparison.Ordinal);
        foreach (var (key, value) in pairs)
        {
            builder.Append(first ? '?' : '&').Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }
        return new Uri(builder.ToString());
    }

    /// <summary>
    /// GETs <paramref name="uri"/>. Any status is returned; only transport failures throw. <paramref name="service"/> names
    /// the service in error messages ("LRCLIB").
    /// </summary>
    public async Task<LyricHttpResponse> GetAsync(string service, Uri uri, TimeSpan timeout, string? referer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ct.ThrowIfCancellationRequested();
        await WaitForHostAsync(uri.Host, ct).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(timeout, Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", Options.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        if (referer is not null)
        {
            request.Headers.Referrer = new Uri(referer);
        }
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var body = await ReadBodyAsync(service, response.Content, linked.Token).ConfigureAwait(false);
            return new LyricHttpResponse((int)response.StatusCode, body, RetryAfter(response));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            _log.Debug($"{service}: request timed out after {timeout.TotalSeconds:0.#} s");
            throw new LyricSourceException(LyricSourceFailure.TimedOut, $"{service}: timed out", inner: exception);
        }
        catch (HttpRequestException exception)
        {
            throw Unreachable(service, exception);
        }
        catch (IOException exception)
        {
            throw Unreachable(service, exception);
        }
    }

    /// <summary>GETs JSON. Anything but HTTP 200 with a JSON body throws <see cref="LyricSourceException"/>.</summary>
    public async Task<JsonDocument> GetJsonAsync(string service, Uri uri, TimeSpan timeout, string? referer, CancellationToken ct)
    {
        var response = await GetAsync(service, uri, timeout, referer, ct).ConfigureAwait(false);
        EnsureOk(service, response);
        return ParseJson(service, response.Body);
    }

    /// <summary>Throws the usual error for a reply that is not HTTP 200.</summary>
    public static void EnsureOk(string service, LyricHttpResponse response)
    {
        if (!response.IsOk)
        {
            throw new LyricSourceException(LyricSourceFailure.HttpStatus,
                $"{service}: HTTP {response.StatusCode}. The service may be temporarily unavailable.", response.StatusCode);
        }
    }

    /// <summary>Parses a JSON body (a UTF-8 byte-order mark is skipped). Not JSON: <see cref="LyricSourceFailure.BadResponse"/>.</summary>
    public static JsonDocument ParseJson(string service, ReadOnlyMemory<byte> body)
    {
        var span = body;
        if (span.Span.StartsWith(Utf8Bom))
        {
            span = span[Utf8Bom.Length..];
        }
        try
        {
            return JsonDocument.Parse(span, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 64 });
        }
        catch (JsonException exception)
        {
            throw new LyricSourceException(LyricSourceFailure.BadResponse, $"{service}: unexpected response format", inner: exception);
        }
    }

    internal static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>UTF-8 text without a leading byte-order mark.</summary>
    public static string Utf8Text(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf8Bom))
        {
            bytes = bytes[Utf8Bom.Length..];
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private LyricSourceException Unreachable(string service, Exception exception)
    {
        var offline = false;
        try
        {
            offline = !Options.IsNetworkAvailable();
        }
        catch (NetworkInformationException)
        {
        }
        _log.Debug($"{service}: {(offline ? "offline" : "unreachable")}: {exception.Message}");
        return offline
            ? new LyricSourceException(LyricSourceFailure.Offline, "No internet connection", inner: exception)
            : new LyricSourceException(LyricSourceFailure.Unreachable, $"{service}: no response", inner: exception);
    }

    private async Task<byte[]> ReadBodyAsync(string service, HttpContent content, CancellationToken ct)
    {
        var limit = Options.MaxResponseBytes;
        if (content.Headers.ContentLength is long declared && declared > limit)
        {
            throw TooLarge(service);
        }
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > limit)
            {
                throw TooLarge(service);
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static LyricSourceException TooLarge(string service) =>
        new(LyricSourceFailure.BadResponse, $"{service}: response too large");

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta)
        {
            return delta;
        }
        if (header?.Date is DateTimeOffset date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>Spaces requests to one host by <see cref="LyricHttpOptions.MinimumHostInterval"/>.</summary>
    private async Task WaitForHostAsync(string host, CancellationToken ct)
    {
        var interval = Options.MinimumHostInterval;
        if (interval <= TimeSpan.Zero)
        {
            return;
        }
        TimeSpan wait;
        lock (_gate)
        {
            var now = Time.GetUtcNow();
            var slot = _nextSlot.TryGetValue(host, out var next) && next > now ? next : now;
            _nextSlot[host] = slot + interval;
            wait = slot - now;
        }
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, Time, ct).ConfigureAwait(false);
        }
    }
}
