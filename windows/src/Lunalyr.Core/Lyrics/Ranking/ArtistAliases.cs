using System.Text.Json;
using System.Text.Json.Serialization;
using Lunalyr.Core.Lyrics.Storage;

namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>A MusicBrainz artist search reply (<c>/ws/2/artist?query="…"&amp;limit=5&amp;fmt=json</c>), reduced to what alias picking reads.</summary>
public sealed record ArtistAliasReply(IReadOnlyList<ArtistAliasReply.Artist> Artists)
{
    public sealed record Artist(string Name, int? Score, IReadOnlyList<Alias>? Aliases);

    public sealed record Alias(string Name, string? Locale, bool? Primary);

    /// <summary>Reads a MusicBrainz JSON reply; null when it is not one.</summary>
    public static ArtistAliasReply? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize(json, ArtistAliasJsonContext.Default.MusicBrainzReplyDto);
            if (dto?.Artists is null)
            {
                return null;
            }
            return new ArtistAliasReply([.. dto.Artists.Where(a => a.Name is not null).Select(a => new Artist(a.Name!, a.Score,
                a.Aliases?.Where(alias => alias.Name is not null).Select(alias => new Alias(alias.Name!, alias.Locale, alias.Primary)).ToList()))]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The request MusicBrainz expects for <paramref name="artist"/>: the name quoted, five results, JSON.</summary>
    public static Uri SearchUri(string artist)
    {
        var quoted = "\"" + artist.Replace("\\", "", StringComparison.Ordinal).Replace("\"", "", StringComparison.Ordinal) + "\"";
        return new Uri("https://musicbrainz.org/ws/2/artist?query=" + Uri.EscapeDataString(quoted) + "&limit=5&fmt=json");
    }
}

/// <summary>
/// Asks MusicBrainz for an artist (the HTTP workstream implements it: <see cref="ArtistAliasReply.SearchUri"/>, a 4 s
/// timeout, at most 1 MB, then <see cref="ArtistAliasReply.Parse"/>). Pacing to one request a second is done by
/// <see cref="ArtistAliases"/>.
/// </summary>
public interface IArtistAliasSearch
{
    /// <returns>The reply, or null when the request failed (a failure is not remembered, so it is asked again next time).</returns>
    Task<ArtistAliasReply?> SearchAsync(string artist, CancellationToken cancellationToken);
}

/// <summary>
/// An artist's names in other scripts ("米津玄師" for "Kenshi Yonezu"), from MusicBrainz, so the Chinese catalogues
/// (NetEase, QQ, Kugou, Kuwo), which often index only the native name, can be searched again under it. Asked only
/// when such a source found nothing, or only another artist, under the library's name. One request per artist, at
/// most one a second, and every answer, empty ones too, is kept on disk for 30 days. Thread-safe. Ported from the
/// Mac's <c>ArtistAliases</c> (Lunalyr's own code).
/// </summary>
public sealed class ArtistAliases
{
    /// <summary>The sources searched again under another name.</summary>
    public static IReadOnlySet<LyricSourceId> Catalogues { get; } =
        new HashSet<LyricSourceId> { LyricSourceId.NetEase, LyricSourceId.QQMusic, LyricSourceId.Kugou, LyricSourceId.Kuwo };

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    public const int MaxEntries = 2000;

    /// <summary>MusicBrainz allows one request a second from each client; this leaves a margin.</summary>
    public static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(1.1);

    private static readonly HashSet<string> NativeLocales =
        new(["ja", "zh", "zh_Hans", "zh_Hant", "zh_TW", "zh_CN", "zh_HK", "ko"], StringComparer.Ordinal);

    private readonly string _file;
    private readonly IArtistAliasSearch _search;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<IReadOnlyList<string>?>> _inFlight = new(StringComparer.Ordinal);
    private Dictionary<string, ArtistAliasEntryDto>? _entries;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    /// <param name="folder">Where <c>ArtistAliases.json</c> lives (the app's data folder).</param>
    public ArtistAliases(string folder, IArtistAliasSearch search, TimeProvider? timeProvider = null)
    {
        _file = Path.Combine(folder, "ArtistAliases.json");
        _search = search;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Up to two other names for <paramref name="artist"/>, native scripts first. Empty when MusicBrainz has none or cannot tell which artist this is.</summary>
    public async Task<IReadOnlyList<string>> NamesAsync(string artist, CancellationToken cancellationToken = default)
    {
        var key = LyricQuery.Normalize(artist);
        if (key.Length == 0)
        {
            return [];
        }
        Task<IReadOnlyList<string>?> request;
        lock (_gate)
        {
            if (Load().TryGetValue(key, out var entry) && _time.GetUtcNow() - entry.StoredAt < Lifetime)
            {
                return entry.Names;
            }
            if (!_inFlight.TryGetValue(key, out request!))
            {
                request = RequestAndStoreAsync(key, artist);
                _inFlight[key] = request;
            }
        }
        return await request.WaitAsync(cancellationToken).ConfigureAwait(false) ?? [];
    }

    private async Task<IReadOnlyList<string>?> RequestAndStoreAsync(string key, string artist)
    {
        await Task.Yield();
        try
        {
            // Each request takes the next free slot before waiting, so two artists asked at once still queue.
            TimeSpan wait;
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                var slot = _lastRequest == DateTimeOffset.MinValue || now > _lastRequest + RequestSpacing ? now : _lastRequest + RequestSpacing;
                _lastRequest = slot;
                wait = slot - now;
            }
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _time).ConfigureAwait(false);
            }
            ArtistAliasReply? reply;
            try
            {
                reply = await _search.SearchAsync(artist, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                reply = null;
            }
            // A failed request (null) is not remembered, so it is asked again next time.
            if (reply is null)
            {
                return null;
            }
            var names = Pick(reply, artist);
            Store(key, names);
            return names;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(key);
            }
        }
    }

    /// <summary>The other names of the one artist that <paramref name="artist"/> names. Two strong matches mean two artists share the name, and nothing is guessed.</summary>
    public static IReadOnlyList<string> Pick(ArtistAliasReply reply, string artist)
    {
        var matches = reply.Artists.Where(found => (found.Score ?? 0) >= 90 && Named(found)).ToList();
        if (matches.Count != 1)
        {
            return [];
        }
        var match = matches[0];
        var ranked = new List<(string Name, int Rank)> { (match.Name, 1) };
        foreach (var alias in match.Aliases ?? [])
        {
            ranked.Add((alias.Name, NativeLocales.Contains(alias.Locale ?? "") ? (alias.Primary == true ? 0 : 1) : 2));
        }
        var names = new List<string>();
        foreach (var entry in ranked.OrderBy(r => r.Rank))
        {
            var name = entry.Name.Trim();
            // Only names the library's spelling could not already find: another script.
            if (name.Length == 0 || LyricQuery.Similarity(name, artist) >= 0.65 || !IsOtherScript(name)
                || names.Any(known => LyricQuery.Normalize(known) == LyricQuery.Normalize(name)))
            {
                continue;
            }
            names.Add(name);
            if (names.Count == 2)
            {
                break;
            }
        }
        return names;

        bool Named(ArtistAliasReply.Artist found) =>
            new[] { found.Name }.Concat((found.Aliases ?? []).Select(a => a.Name)).Any(name => LyricQuery.Similarity(name, artist) >= 0.95);
    }

    /// <summary>Written in Chinese, Japanese or Korean script.</summary>
    public static bool IsOtherScript(string name) => name.EnumerateRunes().Any(rune => rune.Value is
        (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x9FFF) or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF));

    /// <summary>
    /// A catalogue source's answer, searched again under the artist's other names when the library's name found
    /// nothing, or only another artist. A match found that way is recorded under the library's artist, since it
    /// matched the same artist's other name.
    /// </summary>
    public static async ValueTask<LyricCandidate?> WithAliasesAsync(LyricSourceId source, LyricQuery query, ArtistAliases? aliases,
        bool enabled, Func<LyricQuery, CancellationToken, ValueTask<LyricCandidate?>> search, CancellationToken cancellationToken = default)
    {
        var first = await search(query, cancellationToken).ConfigureAwait(false);
        if (!enabled || !Catalogues.Contains(source) || !query.HasArtist)
        {
            return first;
        }
        if (first is not null && (!first.MetadataReported || query.ArtistScore(first.Artist) >= 0.65))
        {
            return first;
        }
        // A Windows addition: a title or artist written in traditional Chinese is searched again in simplified script,
        // which is how these catalogues list most Chinese songs ("晴天 周杰倫" as "晴天 周杰伦"). The reverse is not
        // tried: these catalogues seldom list a name only in traditional script, and one simplified character can stand
        // for several traditional ones.
        if (SimplifiedForm(query) is { } simplified)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await search(simplified, cancellationToken).ConfigureAwait(false) is { } found
                && LyricQuery.Similarity(found.Artist, simplified.Artist) >= 0.65)
            {
                return found with { Artist = query.Artist };
            }
        }
        if (aliases is null || IsOtherScript(query.Artist))
        {
            return first;
        }
        foreach (var name in await aliases.NamesAsync(query.Artist, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var other = new LyricQuery(query.Title, name, query.Album, query.Duration) { Genre = query.Genre };
            if (await search(other, cancellationToken).ConfigureAwait(false) is not { } found
                || LyricQuery.Similarity(found.Artist, name) < 0.65)
            {
                continue;
            }
            return found with { Artist = query.Artist };
        }
        return first;
    }

    /// <summary>
    /// <paramref name="query"/> with its title and artist in simplified Chinese, or null when neither has a traditional
    /// character. A name with kana is Japanese, which these catalogues list as written, so it is left alone.
    /// </summary>
    public static LyricQuery? SimplifiedForm(LyricQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var title = ChineseScripts.HasKana(query.Title) ? query.Title : ChineseScripts.ToSimplified(query.Title);
        var artist = ChineseScripts.HasKana(query.Artist) ? query.Artist : ChineseScripts.ToSimplified(query.Artist);
        return title == query.Title && artist == query.Artist
            ? null
            : new LyricQuery(title, artist, query.Album, query.Duration) { Genre = query.Genre };
    }

    private Dictionary<string, ArtistAliasEntryDto> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }
        try
        {
            _entries = File.Exists(_file)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(_file), SavedLyricsJsonContext.Default.DictionaryStringArtistAliasEntryDto) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _entries = [];
        }
        return _entries;
    }

    private void Store(string key, IReadOnlyList<string> names)
    {
        byte[] bytes;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var all = Load().Where(pair => now - pair.Value.StoredAt < Lifetime).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            all[key] = new ArtistAliasEntryDto { Names = [.. names], StoredAt = now };
            if (all.Count > MaxEntries)
            {
                foreach (var old in all.OrderBy(pair => pair.Value.StoredAt).Take(all.Count - MaxEntries).ToList())
                {
                    all.Remove(old.Key);
                }
            }
            _entries = all;
            bytes = JsonSerializer.SerializeToUtf8Bytes(all, SavedLyricsJsonContext.Default.DictionaryStringArtistAliasEntryDto);
        }
        try
        {
            var folder = Path.GetDirectoryName(_file)!;
            Directory.CreateDirectory(folder);
            var temp = Path.Combine(folder, $"ArtistAliases-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; asked again after a restart.
        }
    }
}

internal sealed class MusicBrainzReplyDto
{
    [JsonPropertyName("artists")]
    public List<MusicBrainzArtistDto>? Artists { get; set; }
}

internal sealed class MusicBrainzArtistDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("score")]
    public int? Score { get; set; }

    [JsonPropertyName("aliases")]
    public List<MusicBrainzAliasDto>? Aliases { get; set; }
}

internal sealed class MusicBrainzAliasDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("locale")]
    public string? Locale { get; set; }

    [JsonPropertyName("primary")]
    public bool? Primary { get; set; }
}

[JsonSerializable(typeof(MusicBrainzReplyDto))]
internal sealed partial class ArtistAliasJsonContext : JsonSerializerContext;
