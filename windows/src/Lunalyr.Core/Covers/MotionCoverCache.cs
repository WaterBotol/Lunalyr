// The animated covers' files, kept in the data folder so a cover plays from disk (Media Foundation's Source Reader
// opens a local MP4; it has no HLS source) and is downloaded once, not on every play. Windows' own: the Mac streams the
// rendition with AVPlayer instead.

using System.Security.Cryptography;
using System.Text;
using Lunalyr.Core.Logging;

namespace Lunalyr.Core.Covers;

/// <summary>
/// A size-limited folder of downloaded cover renditions (<c>{data}\AnimatedCovers</c>). Each file is named by a hash of
/// its address, written to a <c>.part</c> file under a hard byte cap and renamed only once complete and recognisably
/// an MP4. Past the folder's limit the least recently used files go (a file still open is skipped). Only HTTPS
/// <c>.mp4</c> files on Apple's video hosts are ever fetched.
/// </summary>
public sealed class MotionCoverCache
{
    /// <summary>The folder's limit: about thirty albums' covers at the sizes Lunalyr picks.</summary>
    public const long DefaultMaxBytes = 200L * 1024 * 1024;

    /// <summary>No one cover may be larger: the renditions Lunalyr picks are 5–15 MB.</summary>
    public const long DefaultMaxFileBytes = 40L * 1024 * 1024;

    /// <summary>A download that takes longer than this is abandoned.</summary>
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly string _userAgent;
    private readonly CategoryLogger _log;
    private readonly Dictionary<string, Task<string?>> _downloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public MotionCoverCache(string folder, HttpClient http, string userAgent, long maxBytes = DefaultMaxBytes,
        long maxFileBytes = DefaultMaxFileBytes, Logger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(http);
        Folder = folder;
        _http = http;
        _userAgent = userAgent;
        MaxBytes = maxBytes;
        MaxFileBytes = Math.Min(maxFileBytes, maxBytes);
        _log = (logger ?? Logger.Null).Player;
    }

    public string Folder { get; }

    public long MaxBytes { get; }

    public long MaxFileBytes { get; }

    /// <summary>Where <paramref name="video"/> is (or would be) kept.</summary>
    public string PathFor(Uri video)
    {
        ArgumentNullException.ThrowIfNull(video);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(video.AbsoluteUri));
        return Path.Combine(Folder, Convert.ToHexStringLower(hash.AsSpan(0, 16)) + ".mp4");
    }

    /// <summary>Whether this cache would fetch <paramref name="video"/>: an HTTPS <c>.mp4</c> on Apple's video hosts.</summary>
    public static bool Accepts(Uri video) =>
        video.Scheme == Uri.UriSchemeHttps && MotionArtwork.IsMp4(video) && MotionArtworkPages.IsAppleVideoHost(video.Host);

    /// <summary>The cached file for <paramref name="video"/>, if it is already here (and marks it used).</summary>
    public string? Cached(Uri video)
    {
        var path = PathFor(video);
        if (!File.Exists(path)) return null;
        Touch(path);
        return path;
    }

    /// <summary>
    /// The local file for <paramref name="video"/>: the cached one, or downloaded now. Null when it can't be had (not an
    /// accepted address, not HTTP 200, too large, not an MP4, or the connection failed). Two calls for the same address
    /// share one download.
    /// </summary>
    public Task<string?> GetAsync(Uri video, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (!Accepts(video)) return Task.FromResult<string?>(null);
        if (Cached(video) is { } cached) return Task.FromResult<string?>(cached);
        var path = PathFor(video);
        lock (_gate)
        {
            if (_downloads.TryGetValue(path, out var running)) return running;
            var task = DownloadAsync(video, path, cancellationToken);
            _downloads[path] = task;
            _ = task.ContinueWith(_ => { lock (_gate) _downloads.Remove(path); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<string?> DownloadAsync(Uri video, string path, CancellationToken ct)
    {
        await Task.Yield();
        Directory.CreateDirectory(Folder);
        var part = path + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(DownloadTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, video);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
            {
                _log.Info($"Animated cover: {video.Host} answered {(int)response.StatusCode} for the video; showing the still cover.");
                return null;
            }
            if (response.Content.Headers.ContentLength is > 0 and var length && length > MaxFileBytes)
            {
                _log.Info($"Animated cover: the video is {length / 1048576.0:0.0} MB, over the {MaxFileBytes / 1048576} MB limit; not downloaded.");
                return null;
            }
            long written = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > MaxFileBytes)
                    {
                        _log.Info($"Animated cover: the video passed the {MaxFileBytes / 1048576} MB limit; abandoned.");
                        return null;
                    }
                    await file.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                }
            }
            if (!LooksLikeMp4(part))
            {
                _log.Info("Animated cover: the download isn't an MP4; showing the still cover.");
                return null;
            }
            File.Move(part, path, overwrite: true);
            _log.Info($"Animated cover: downloaded {written / 1048576.0:0.0} MB into the cache.");
            Trim(keep: path);
            return path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            _log.Info($"Animated cover: the video couldn't be downloaded ({ex.GetType().Name}); showing the still cover.");
            return null;
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>An ISO media file starts with a box whose type is <c>ftyp</c> (or <c>styp</c> for a segment).</summary>
    public static bool LooksLikeMp4(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[8];
            using var file = File.OpenRead(path);
            if (file.ReadAtLeast(head, 8, throwOnEndOfStream: false) < 8) return false;
            return head[4..].SequenceEqual("ftyp"u8) || head[4..].SequenceEqual("styp"u8);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps the folder within <see cref="MaxBytes"/>: the least recently used covers go first, never
    /// <paramref name="keep"/>, and a file that can't be deleted (still open) is skipped. Stray <c>.part</c> files from
    /// an earlier run are removed.
    /// </summary>
    public void Trim(string? keep = null)
    {
        if (!Directory.Exists(Folder)) return;
        try
        {
            var directory = new DirectoryInfo(Folder);
            foreach (var stray in directory.EnumerateFiles("*.part"))
                if (DateTime.UtcNow - stray.LastWriteTimeUtc > TimeSpan.FromHours(1)) TryDelete(stray.FullName);
            var files = directory.EnumerateFiles("*.mp4").OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= MaxBytes) break;
                if (keep is not null && string.Equals(file.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
                if (TryDelete(file.FullName)) total -= file.Length;
            }
        }
        catch (IOException ex)
        {
            _log.Debug($"Animated covers: the cache couldn't be trimmed ({ex.Message}).");
        }
    }

    /// <summary>The bytes the folder's covers take.</summary>
    public long Size() => Directory.Exists(Folder) ? new DirectoryInfo(Folder).EnumerateFiles("*.mp4").Sum(f => f.Length) : 0;

    // Last use is kept as the file's write time: NTFS doesn't always update access times.
    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
