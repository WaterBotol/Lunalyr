using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Storage;

/// <summary>
/// Lyrics remembered on disk, one JSON file per entry in a folder the app chooses:
/// <list type="bullet">
/// <item><c>cache-&lt;sha256&gt;.json</c>: a lookup's automatic online choice, keyed by recording key plus the options
/// fingerprint. Expires after 14 days; at most 150 are kept, the least recently written going first.</item>
/// <item><c>pin-&lt;sha256&gt;.json</c>: a choice the user made for a recording, keyed by the recording key. Never expires.</item>
/// </list>
/// The hash is SHA-256 of the key's UTF-8 bytes, in lower-case hex, as on the Mac. Writes go to a temp file and then
/// replace the entry, so a crash never leaves half a file. Every read and write fails soft.
/// </summary>
/// <remarks>
/// Re-implemented from a behaviour description where the Mac's lines trace back to the Verso import (file naming,
/// the expiry check, the cache bound); the entry shape is this port's own JSON.
/// </remarks>
public sealed class SavedLyrics
{
    /// <summary>Automatic choices older than this are looked up again.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(14);

    /// <summary>The most automatic choices kept; explicit choices are not counted or removed.</summary>
    public const int MaxCacheEntries = 150;

    /// <summary>Entries larger than this are neither written nor read.</summary>
    public const int MaxEntryBytes = 2_000_000;

    private const string CachePrefix = "cache-";
    private const string PinPrefix = "pin-";
    private readonly TimeProvider _time;

    public SavedLyrics(string folder, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        Folder = folder;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Folder { get; }

    /// <summary>The file name an entry for <paramref name="key"/> is stored under.</summary>
    public static string FileName(string key, bool pin)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return (pin ? PinPrefix : CachePrefix) + hash + ".json";
    }

    /// <summary>The saved result for <paramref name="key"/>, or null when there is none, it expired, or it cannot be read.</summary>
    public LyricResult? Read(string key, bool pin) => Entry(key, pin)?.Result is { } dto ? ToResult(dto) : null;

    /// <summary>The cached automatic online choice, as a candidate that can be ranked again.</summary>
    public LyricCandidate? ReadCandidate(string key)
    {
        var entry = Entry(key, pin: false);
        if (entry?.Result is not { } dto || !LyricSourceInfo.TryParseRaw(entry.Source, out var source)
            || entry.Title is not { } title || entry.Artist is not { } artist || ToResult(dto) is not { } result)
        {
            return null;
        }
        return new LyricCandidate(source, result, title, artist, entry.Duration, 0);
    }

    /// <summary>
    /// How long a provisional choice is kept: a source that could have beaten it gave no answer, so it is asked again
    /// after this, while replays meanwhile ask nothing (the Mac's b3c0909).
    /// </summary>
    public static readonly TimeSpan ProvisionalLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Caches a lookup's automatic choice under <paramref name="key"/>. <paramref name="provisional"/>: kept for
    /// <see cref="ProvisionalLifetime"/> instead of <see cref="CacheLifetime"/>; its time is set back so it expires then,
    /// so the entry's shape is unchanged.
    /// </summary>
    public bool Save(LyricCandidate candidate, string key, bool provisional = false) => Write(new SavedEntryDto
    {
        Result = ToDto(candidate.Result),
        StoredAt = provisional ? _time.GetUtcNow() + ProvisionalLifetime - CacheLifetime : _time.GetUtcNow(),
        Source = candidate.Source.RawValue(),
        Title = candidate.Title,
        Artist = candidate.Artist,
        Duration = candidate.Duration,
    }, key, pin: false);

    /// <summary>Saves a result: a pin (the user's choice for a recording) or an automatic cache entry.</summary>
    public bool Save(LyricResult result, string key, bool pin) =>
        Write(new SavedEntryDto { Result = ToDto(result), StoredAt = _time.GetUtcNow() }, key, pin);

    /// <summary>Forgets the user's choice for a recording.</summary>
    public void Unpin(string key)
    {
        try
        {
            File.Delete(Path.Combine(Folder, FileName(key, pin: true)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A choice that cannot be removed now stays; nothing else depends on it.
        }
    }

    private SavedEntryDto? Entry(string key, bool pin)
    {
        try
        {
            var path = Path.Combine(Folder, FileName(key, pin));
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxEntryBytes)
            {
                return null;
            }
            var entry = JsonSerializer.Deserialize(File.ReadAllBytes(path), SavedLyricsJsonContext.Default.SavedEntryDto);
            if (entry is null || (!pin && _time.GetUtcNow() - entry.StoredAt >= CacheLifetime))
            {
                return null;
            }
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private bool Write(SavedEntryDto entry, string key, bool pin)
    {
        string? temp = null;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, SavedLyricsJsonContext.Default.SavedEntryDto);
            if (bytes.Length > MaxEntryBytes)
            {
                return false;
            }
            Directory.CreateDirectory(Folder);
            // The temp name never matches the cache-/pin- patterns, so pruning and reads ignore it.
            temp = Path.Combine(Folder, $"write-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, Path.Combine(Folder, FileName(key, pin)), overwrite: true);
            temp = null;
            Prune();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException
            or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for the next write to overwrite; it matches no entry pattern.
                }
            }
        }
    }

    /// <summary>Bounds automatic cache growth: the least recently written automatic choices go first. Pins are kept.</summary>
    private void Prune()
    {
        var automatic = new DirectoryInfo(Folder).GetFiles(CachePrefix + "*");
        if (automatic.Length <= MaxCacheEntries)
        {
            return;
        }
        foreach (var file in automatic.OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal)
                     .Take(automatic.Length - MaxCacheEntries))
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tried again on the next write.
            }
        }
    }

    private static SavedResultDto ToDto(LyricResult result) => result switch
    {
        LyricResult.Synced synced => new SavedResultDto
        {
            Kind = "synced",
            Precision = synced.Provenance.Precision,
            Source = synced.Provenance.Source,
            Lines = [.. synced.Lines.Select(line => new SavedLineDto
            {
                Start = line.Start,
                End = line.End,
                EndFromNextLine = line.EndFromNextLine,
                Text = line.Text,
                Words = [.. line.Words.Select(w => new SavedWordDto { Text = w.Text, Start = w.Start, End = w.End })],
                IsInstrumental = line.IsInstrumental,
                WordTimed = line.WordTimed,
                Romanization = line.Romanization,
                Translation = line.Translation,
                Credit = line.Credit,
            })],
        },
        LyricResult.Plain plain => new SavedResultDto
        {
            Kind = "plain",
            Precision = plain.Provenance.Precision,
            Source = plain.Provenance.Source,
            Text = [.. plain.Lines],
        },
        _ => throw new NotSupportedException("Unknown lyric result."),
    };

    private static LyricResult? ToResult(SavedResultDto dto)
    {
        var provenance = new LyricProvenance(dto.Precision, dto.Source);
        return dto.Kind switch
        {
            "synced" => new LyricResult.Synced(
                [.. (dto.Lines ?? []).Select(line => new LyricLine
                {
                    Start = line.Start,
                    End = line.End,
                    EndFromNextLine = line.EndFromNextLine,
                    Text = line.Text,
                    Words = [.. (line.Words ?? []).Select(w => new LyricWord(w.Text, w.Start, w.End))],
                    IsInstrumental = line.IsInstrumental,
                    WordTimed = line.WordTimed,
                    Romanization = line.Romanization,
                    Translation = line.Translation,
                    Credit = line.Credit,
                })],
                provenance),
            "plain" => new LyricResult.Plain([.. dto.Text ?? []], provenance),
            _ => null,
        };
    }
}
