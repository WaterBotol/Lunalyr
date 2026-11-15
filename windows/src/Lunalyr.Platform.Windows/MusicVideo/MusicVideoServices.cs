// Windows' services for the music video (the Mac's MusicVideoServices.swift does the same jobs with AVFoundation, URLSession
// and AppleScript): whether Media Foundation can play a chosen file, the HTTP client (YouTube's results page, as the Mac
// asks it, the YouTube Data API and the stills), and the API key, kept with DPAPI. Nothing here plays anything: a check opens the file's video stream only and reads one frame.

using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Platform.Media;

namespace Lunalyr.Platform.MusicVideo;

/// <summary>Whether Media Foundation can decode a file's video (an audio stream is never opened).</summary>
public sealed class MediaFoundationPlayability : IMusicVideoPlayabilityCheck
{
    public Task<MusicVideoPlayability> CheckAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        if (!File.Exists(path)) return MusicVideoPlayability.Unplayable;
        // A copy-protected file (PlayReady) doesn't open in the Source Reader at all, so it reads as unplayable: Windows
        // can't tell it from a broken one here, unlike AVFoundation's hasProtectedContent.
        return VideoReader.HasVideo(path, out _) ? MusicVideoPlayability.Playable : MusicVideoPlayability.Unplayable;
    }, ct);
}

/// <summary>
/// GETs for the music video, the Mac's URLSessionMusicVideoHTTP: HTTPS only, to YouTube's results page, the YouTube Data API
/// YouTube's image host (the results' stills) and YouTube's oEmbed address for one video's shape; no cookies, no redirects followed, nothing cached, eight seconds at most,
/// <c>Accept-Language: en</c>. youtube.com is sent the Mac's WebKit User-Agent (its results data is served only to a
/// browser); every other host is told it is Lunalyr. Anything else is refused before it is sent.
/// </summary>
public sealed class MusicVideoHttpClient : IMusicVideoHttp, IDisposable
{
    public const string UserAgent = "Lunalyr/0.1 (Windows)";

    /// <summary>The only hosts asked: the Data API, and i.ytimg.com for the results' stills (and youtube.com's results page alone).</summary>
    public static readonly IReadOnlySet<string> AllowedHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { YouTubeDataApi.Host, "i.ytimg.com" };

    private readonly HttpClient _client;

    public MusicVideoHttpClient(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
    }

    /// <summary>
    /// Whether <paramref name="url"/> may be asked: the allowed hosts, YouTube's results page, or exactly the oEmbed address
    /// of one video (its shape for "Show the whole video": only the video's ID is sent).
    /// </summary>
    public static bool Allows(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && (AllowedHosts.Contains(url.Host) || YouTubeSearchPage.IsResultsPage(url) || YouTubeVideoShape.IsOEmbed(url));

    /// <summary>The User-Agent for <paramref name="url"/>: the Mac's WebKit one for youtube.com, Lunalyr's own elsewhere.</summary>
    public static string UserAgentFor(Uri url) =>
        url.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) ? YouTubeSearchPage.WebKitAgent : UserAgent;

    public async Task<MusicVideoHttpReply> GetAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (!Allows(url)) throw new InvalidOperationException("The music video asks only YouTube's results page, its Data API and its stills.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // youtube.com is sent nothing of the caller's (never a key, never a cookie); the others never a cookie or a second agent.
        var page = YouTubeSearchPage.IsResultsPage(url);
        foreach (var (name, value) in headers)
        {
            if (page || name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) || name.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(name, value);
        }
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgentFor(url));
        request.Headers.TryAddWithoutValidation("Accept-Language", YouTubeSearchPage.AcceptLanguage);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var replyHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response.Headers.RetryAfter is { } retry)
        {
            var seconds = retry.Delta?.TotalSeconds ?? (retry.Date is { } at ? (at - DateTimeOffset.UtcNow).TotalSeconds : (double?)null);
            if (seconds is { } s) replyHeaders["Retry-After"] = Math.Max(0, s).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        // A redirect is never followed: where it pointed tells a consent or sign-in wall (Windows' guardrail).
        if (response.Headers.Location is { } location) replyHeaders["Location"] = location.ToString();
        return new MusicVideoHttpReply((int)response.StatusCode, body, replyHeaders);
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// The owner's YouTube Data API key, kept in <c>{data}\MusicVideo\youtube-key.bin</c> encrypted with DPAPI for the current
/// Windows user (with Lunalyr's own entropy). Never in settings.json, never logged. Only this file is touched.
/// </summary>
public sealed partial class YouTubeApiKeyStore
{
    private const int UiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Lunalyr.YouTubeDataApi.v1");
    private readonly string _path;
    private readonly Lock _gate = new();
    private string? _key;
    private bool _loaded;

    public YouTubeApiKeyStore(string path) => _path = path;

    /// <summary>Raised after the key changes (any thread).</summary>
    public event Action? Changed;

    /// <summary>The key, or null when none is set (or the file can't be read).</summary>
    public string? Key
    {
        get
        {
            lock (_gate)
            {
                if (_loaded) return _key;
                _loaded = true;
                try
                {
                    if (File.Exists(_path) && Transform(File.ReadAllBytes(_path), protect: false) is { } plain)
                        _key = Encoding.UTF8.GetString(plain);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _key = null;
                }
                return _key;
            }
        }
    }

    public bool HasKey => !string.IsNullOrWhiteSpace(Key);

    /// <summary>
    /// Whether <paramref name="text"/> looks like a Google API key ("AIza" and 35 more URL-safe characters). Only the shape
    /// is checked; whether YouTube accepts it shows on the first search.
    /// </summary>
    public static bool LooksLikeKey(string? text)
    {
        var key = text?.Trim() ?? "";
        return key.Length == 39 && key.StartsWith("AIza", StringComparison.Ordinal)
            && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }

    /// <summary>Keeps <paramref name="key"/> (trimmed); false when it doesn't look like a key or couldn't be written.</summary>
    public bool Set(string key)
    {
        if (!LooksLikeKey(key)) return false;
        key = key.Trim();
        lock (_gate)
        {
            if (Transform(Encoding.UTF8.GetBytes(key), protect: true) is not { } sealedKey) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllBytes(_path, sealedKey);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            _key = key;
            _loaded = true;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Forgets the key and deletes its file.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Forgotten for this run anyway.
            }
            _key = null;
            _loaded = true;
        }
        Changed?.Invoke();
    }

    private static unsafe byte[]? Transform(byte[] input, bool protect)
    {
        if (input.Length == 0) return null;
        fixed (byte* data = input)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { Size = (uint)input.Length, Data = data };
            var entropyBlob = new DataBlob { Size = (uint)Entropy.Length, Data = entropy };
            DataBlob outBlob = default;
            var ok = protect
                ? CryptProtectData(&inBlob, null, &entropyBlob, 0, 0, UiForbidden, &outBlob)
                : CryptUnprotectData(&inBlob, 0, &entropyBlob, 0, 0, UiForbidden, &outBlob);
            if (!ok) return null;
            try
            {
                return new ReadOnlySpan<byte>(outBlob.Data, (int)outBlob.Size).ToArray();
            }
            finally
            {
                if (outBlob.Data is not null)
                {
                    new Span<byte>(outBlob.Data, (int)outBlob.Size).Clear();
                    LocalFree((nint)outBlob.Data);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DataBlob
    {
        public uint Size;
        public byte* Data;
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptProtectData(DataBlob* dataIn, string? description, DataBlob* entropy, nint reserved, nint prompt,
        int flags, DataBlob* dataOut);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptUnprotectData(DataBlob* dataIn, nint description, DataBlob* entropy, nint reserved, nint prompt,
        int flags, DataBlob* dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
