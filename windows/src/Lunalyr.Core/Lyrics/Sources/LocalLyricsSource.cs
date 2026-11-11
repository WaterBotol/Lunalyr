using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Lyrics exported by Lyrimuse into a folder the user chose, read only: <c>Artist - Title.lrc</c> files with optional
/// sidecars beside them (<c>.ttml</c> or <c>.yrc</c> word timing, <c>.roma.lrc</c> readings, <c>.tr.lrc</c>
/// translations). Only the exported files are opened, for reading; the folder is never written to.
/// </summary>
/// <remarks>
/// The LRC is the payload; sidecars travel in <see cref="RawLyrics.Extras"/> under
/// <see cref="RawLyricsExtras.SidecarTtml"/> (preferred) or <see cref="RawLyricsExtras.SidecarYrc"/>,
/// <see cref="RawLyricsExtras.RomanizationLrc"/> and <see cref="RawLyricsExtras.TranslationLrc"/>. Merging a partial
/// word-timed sidecar into the LRC's lines is the parser's job.
/// </remarks>
public sealed class LocalLyricsSource(Func<string?> folder, ILyricPayloadProbe? probe = null) : IFolderLyricSource
{
    private const int MaxEntries = 30_000;
    private const int MaxFilesRead = 150;
    private const long MaxFileBytes = 2_000_000;
    private readonly ILyricPayloadProbe _probe = probe ?? ParsedLyricPayloadProbe.Instance;

    public LocalLyricsSource(string folder, ILyricPayloadProbe? probe = null)
        : this(() => folder, probe)
    {
    }

    /// <summary>A source with no folder of its own: each lookup names the folder (<see cref="IFolderLyricSource"/>).</summary>
    public LocalLyricsSource(ILyricPayloadProbe? probe = null)
        : this(() => null, probe)
    {
    }

    public LyricSourceId Id => LyricSourceId.Lyrimuse;

    public Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct) => FetchAsync(query, folder(), ct);

    public Task<RawLyrics?> FetchAsync(LyricQuery query, string? folder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() => Find(query, folder, ct), ct);
    }

    string IFolderLyricSource.Fingerprint(LyricQuery query, string? folder) => Fingerprint(query, folder);

    private RawLyrics? Find(LyricQuery query, string? path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new LyricSourceException(LyricSourceFailure.NotConfigured, "Choose the Lyrimuse lyrics folder");
        }
        List<FileInfo> all;
        try
        {
            all = [.. Entries(path).OfType<FileInfo>()];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                              or System.Security.SecurityException)
        {
            throw new LyricSourceException(LyricSourceFailure.LocalFiles, "The lyrics folder can't be read", inner: exception);
        }
        catch (TooManyFilesException)
        {
            throw new LyricSourceException(LyricSourceFailure.LocalFiles, "Choose a lyrics folder with fewer than 30,000 files");
        }
        var files = all
            .Where(f => f.Extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase)
                        && !f.Name.EndsWith(".tr.lrc", StringComparison.Ordinal) && !f.Name.EndsWith(".roma.lrc", StringComparison.Ordinal)
                        && MayBelong(f.Name, query))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Take(MaxFilesRead);
        var hasArtist = query.HasArtist;
        RawLyrics? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            // A track without an artist tag is matched on title and [length:].
            if (Read(file.FullName) is not { } text || LrcText.Tag("ti", text) is not { } title)
            {
                continue;
            }
            var artist = LrcText.Tag("ar", text);
            if (artist is null && hasArtist)
            {
                continue;
            }
            var titleMatch = LyricQuery.Similarity(query.Title, title);
            var artistMatch = query.ArtistScore(artist ?? "");
            if (titleMatch < 0.92 || artistMatch < (hasArtist ? 0.85 : 0))
            {
                continue;
            }
            var length = Length(LrcText.Tag("length", text));
            var album = LrcText.Tag("al", text);
            if (!string.IsNullOrEmpty(query.Album) && album is not null && LyricQuery.Similarity(query.Album, album) < 0.80)
            {
                continue;
            }
            var stem = file.FullName[..^file.Extension.Length];
            var ttml = Read(stem + ".ttml");
            var yrc = ttml is null ? Read(stem + ".yrc") : null;
            var lines = LrcParser.Parse(text, query.Duration);
            var timed = lines.Any(line => !line.IsInstrumental);
            var sidecar = (ttml is not null && LrcText.TtmlHasTimedSpans(ttml) && _probe.IsUsable(LyricFormat.Ttml, ttml))
                          || (yrc is not null && _probe.IsUsable(LyricFormat.Yrc, yrc));
            if (!timed && !sidecar)
            {
                continue;
            }
            // Lyrics running well past the playing track are another recording's.
            if (query.Duration > 0 && lines.Count > 0 && lines[^1].Start > query.Duration + 8)
            {
                continue;
            }
            var native = lines.Any(line => line.WordTimed) || sidecar;
            var albumMatches = album is not null && LyricQuery.Normalize(album) == LyricQuery.Normalize(query.Album ?? "");
            var score = LyricMatchScore.Score(titleMatch, artistMatch, length is double l ? Math.Abs(l - query.Duration) : 0,
                length is not null && query.Duration > 0, native, lineTimed: true, albumMatches) + LyricMatchScore.LocalFileBonus;
            if (best is not null && score <= bestScore)
            {
                continue;
            }
            var extras = new Dictionary<string, string>();
            if (ttml is not null)
            {
                extras[RawLyricsExtras.SidecarTtml] = ttml;
            }
            if (yrc is not null)
            {
                extras[RawLyricsExtras.SidecarYrc] = yrc;
            }
            if (Read(stem + ".roma.lrc") is { } readings)
            {
                extras[RawLyricsExtras.RomanizationLrc] = readings;
            }
            if (Read(stem + ".tr.lrc") is { } translation)
            {
                extras[RawLyricsExtras.TranslationLrc] = translation;
            }
            if (LrcText.Tag("source", text) is { } upstream)
            {
                extras[RawLyricsExtras.Upstream] = upstream;
            }
            best = new RawLyrics(LyricSourceId.Lyrimuse, LyricFormat.Lrc, text, title, artist ?? "", length, extras);
            bestScore = score;
        }
        return best;
    }

    /// <summary>
    /// Names, sizes and modification times of the files that could hold this track's lyrics, sidecars included, hashed.
    /// Exports for other songs leave it unchanged; editing a matching file in place changes it.
    /// </summary>
    public static string Fingerprint(LyricQuery query, string? folder)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrEmpty(folder))
        {
            return "unconfigured";
        }
        List<FileSystemInfo> all;
        try
        {
            all = [.. Entries(folder)];
        }
        catch (TooManyFilesException)
        {
            return "too many files";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                              or System.Security.SecurityException)
        {
            return "unreadable";
        }
        var rows = all.Where(entry => MayBelong(entry.Name, query)).Select(entry =>
        {
            var size = entry is FileInfo f ? f.Length : -1;
            return string.Join('|', entry.Name, size.ToString(CultureInfo.InvariantCulture),
                entry.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }).OrderBy(row => row, StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));
    }

    /// <summary>
    /// A file's <c>[length:]</c> in seconds: "03:41", "3:41.50", "221" or "221000ms"; a bare number too large to be
    /// seconds is read as milliseconds. Null for a value that cannot be a song's length (outside 1 s … 3 h).
    /// </summary>
    public static double? Length(string? value)
    {
        if (value is null || ClockSeconds(value) is not double seconds)
        {
            return null;
        }
        if (seconds > 10_800 && !value.Contains(':', StringComparison.Ordinal))
        {
            seconds /= 1000;
        }
        return seconds is >= 1 and <= 10_800 ? seconds : null;
    }

    /// <summary>Whether a file's name could be this track's: it contains the title without edition qualifiers.</summary>
    public static bool MayBelong(string fileName, LyricQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var name = Path.GetFileNameWithoutExtension(fileName);
        var core = LyricQuery.Core(query.Title);
        var key = core.Length == 0 ? LyricQuery.Normalize(query.Title) : core;
        if (key.Length > 0)
        {
            return LyricQuery.Normalize(name).Contains(key, StringComparison.Ordinal);
        }
        // Titles made only of symbols match as written.
        var raw = query.Title.Trim();
        return raw.Length > 0 && name.Contains(raw, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>A time as TTML and LRC tags write it: "12.5s", "1500ms", "03:41", "1:02:03.5"; null when unreadable.</summary>
    private static double? ClockSeconds(string value)
    {
        var text = value.Trim();
        double? result = null;
        if (text.EndsWith("ms", StringComparison.Ordinal))
        {
            result = Number(text[..^2]) / 1000;
        }
        else if (text.EndsWith('s'))
        {
            result = Number(text[..^1]);
        }
        else
        {
            var parts = text.Split(':');
            if (parts.Length is >= 1 and <= 3 && parts.All(p => Number(p) is not null))
            {
                result = parts.Aggregate(0.0, (total, part) => (total * 60) + Number(part)!.Value);
            }
        }
        return result is double r && double.IsFinite(r) && r >= 0 && r <= 864_000 ? r : null;
    }

    private static double? Number(string text) =>
        text.Length > 0 && !char.IsWhiteSpace(text[0]) && !char.IsWhiteSpace(text[^1])
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>The folder's visible entries (not recursive), at most 30,000.</summary>
    private static List<FileSystemInfo> Entries(string folder)
    {
        var list = new List<FileSystemInfo>();
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (entry.Name.StartsWith('.') || entry.Attributes.HasFlag(FileAttributes.Hidden))
            {
                continue;
            }
            list.Add(entry);
            if (list.Count > MaxEntries)
            {
                throw new TooManyFilesException();
            }
        }
        return list;
    }

    private static string? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            return bytes.Length > MaxFileBytes ? null : LyricTextDecoder.Decode(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class TooManyFilesException : Exception;
}
