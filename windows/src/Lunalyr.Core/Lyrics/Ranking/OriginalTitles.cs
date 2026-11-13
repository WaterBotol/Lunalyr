using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>A song's title, and album, in its original language ("夜に駆ける", "夜に駆ける - Single").</summary>
public sealed record OriginalTitle(string Title, string? Album);

/// <summary>One song in an Apple catalogue reply (iTunes Search), reduced to the fields read.</summary>
public sealed record CatalogueSong(long? TrackId, string? TrackName, string? ArtistName, string? CollectionName, long? TrackTimeMillis);

/// <summary>
/// Apple's public catalogue (the iTunes Search API; the HTTP workstream implements it over the shared client). Pacing
/// is done by <see cref="OriginalTitles"/>.
/// </summary>
public interface IOriginalTitleCatalogue
{
    /// <summary>Songs matching <paramref name="term"/> in the <paramref name="country"/> storefront; null when the request failed.</summary>
    Task<IReadOnlyList<CatalogueSong>?> SearchAsync(string term, string country, CancellationToken cancellationToken);

    /// <summary>The catalogue entry <paramref name="id"/> as the <paramref name="country"/> storefront names it in <paramref name="language"/>; null when the request failed.</summary>
    Task<IReadOnlyList<CatalogueSong>?> LookupAsync(long id, string country, string language, CancellationToken cancellationToken);
}

/// <summary>
/// A song's title in its original language ("夜に駆ける"), for a song Apple Music lists under an English or romanised one
/// ("Racing into the Night"), from Apple's public catalogue: the song is found in this PC's storefront by title,
/// artist and length, then the same catalogue entry is read from the Japanese storefront. Ported from the Mac's
/// <c>OriginalTitles</c> (Lunalyr's own code); the primary-artist step is re-implemented from a behaviour description.
/// </summary>
/// <remarks>
/// Asked only when a lyric service found nothing usable under the listed title (<see cref="WithOriginalTitleAsync"/>).
/// Only the title and artist are sent, with the storefront. Requests are spaced a second apart, every answer, "none"
/// too, is kept on disk for 30 days, and sources asking at once share one request. Thread-safe.
/// </remarks>
public sealed partial class OriginalTitles
{
    /// <summary>The sources searched again under a song's original title.</summary>
    public static IReadOnlySet<LyricSourceId> Sources { get; } = new HashSet<LyricSourceId>
    {
        LyricSourceId.Lrclib, LyricSourceId.NetEase, LyricSourceId.QQMusic, LyricSourceId.Kugou, LyricSourceId.Kuwo,
        LyricSourceId.LyricsPlus, LyricSourceId.Amll,
    };

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    public const int MaxEntries = 2000;

    /// <summary>Apple's catalogue limits how often one client may ask.</summary>
    public static readonly TimeSpan DefaultSpacing = TimeSpan.FromSeconds(1);

    /// <summary>Where original titles are read: Japan, in Japanese.</summary>
    public const string OriginalCountry = "jp";

    public const string OriginalLanguage = "ja_jp";

    /// <summary>
    /// The lookup rule answers are stored under. 2 (13 Nov 2026) adds the artist's native-script entry and Japan's
    /// title search; a "none" stored by rule 1 (no "rule" field) is asked again, a title found by it still stands.
    /// </summary>
    public const int CurrentRule = 2;

    /// <summary>MusicBrainz countries whose artists mostly sing in Chinese, Japanese or Korean.</summary>
    public static IReadOnlySet<string> EastAsianCountries { get; } =
        new HashSet<string>(["JP", "KR", "CN", "TW", "HK", "MO"], StringComparer.OrdinalIgnoreCase);

    private readonly string _file;
    private readonly IOriginalTitleCatalogue _catalogue;
    private readonly string _storefront;
    private readonly TimeSpan _spacing;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<(bool Answered, OriginalTitle? Found)>> _inFlight = new(StringComparer.Ordinal);
    private Dictionary<string, OriginalTitleEntryDto>? _entries;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    /// <param name="folder">Where <c>OriginalTitles.json</c> lives (the app's lyric data folder).</param>
    /// <param name="catalogue">Apple's catalogue.</param>
    /// <param name="storefront">This PC's storefront ("us", "au"); <see cref="DefaultStorefront"/> when null.</param>
    /// <param name="spacing">The least time between two requests; <see cref="DefaultSpacing"/> when null.</param>
    public OriginalTitles(string folder, IOriginalTitleCatalogue catalogue, string? storefront = null, TimeSpan? spacing = null,
        TimeProvider? timeProvider = null)
    {
        _file = Path.Combine(folder, "OriginalTitles.json");
        _catalogue = catalogue;
        _storefront = (storefront ?? DefaultStorefront()).ToLowerInvariant();
        _spacing = spacing ?? DefaultSpacing;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>This PC's region as an Apple storefront ("au"), or "us" when Windows names none.</summary>
    public static string DefaultStorefront()
    {
        try
        {
            var code = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            if (code.Length == 2 && code.All(char.IsAsciiLetter) && !string.Equals(code, "IV", StringComparison.OrdinalIgnoreCase))
            {
                return code.ToLowerInvariant();
            }
        }
        catch (ArgumentException)
        {
        }
        return "us";
    }

    /// <summary>The cache key: normalised title and artist, and the length in whole seconds.</summary>
    public static string Key(LyricQuery query) =>
        string.Join('|', LyricQuery.Normalize(query.Title), LyricQuery.Normalize(query.Artist),
            ((long)Math.Round(query.Duration, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture));

    /// <summary>The original title (and album) of the playing song, null when it has none other than the one listed, or it could not be found.</summary>
    public async Task<OriginalTitle?> OriginalAsync(LyricQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasArtist || query.Duration <= 0 || LyricLanguage.HasNative(query.Title))
        {
            return null;
        }
        var key = Key(query);
        Task<(bool Answered, OriginalTitle? Found)> request;
        lock (_gate)
        {
            // A "none" found by an earlier rule is asked again: the later rules look further.
            if (Load().TryGetValue(key, out var entry) && _time.GetUtcNow() - entry.StoredAt < Lifetime
                && (entry.Found is not null || entry.Rule >= CurrentRule))
            {
                return entry.Found is { } found ? new OriginalTitle(found.Title, found.Album) : null;
            }
            if (!_inFlight.TryGetValue(key, out request!))
            {
                request = RequestAndStoreAsync(key, query);
                _inFlight[key] = request;
            }
        }
        return (await request.WaitAsync(cancellationToken).ConfigureAwait(false)).Found;
    }

    private async Task<(bool Answered, OriginalTitle? Found)> RequestAndStoreAsync(string key, LyricQuery query)
    {
        await Task.Yield();
        try
        {
            var answer = await RequestAsync(query).ConfigureAwait(false);
            // A failed request is not remembered, so it is asked again next time.
            if (answer.Answered)
            {
                Store(key, answer.Found);
            }
            return answer;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(key);
            }
        }
    }

    /// <summary>
    /// Not answered when a request failed; answered with null when the song has no other title. The Mac's order
    /// (next/integration 1cdde21, ported from the Mac session's description): (a) this storefront's entry with the listed
    /// title, read again from the Japanese storefront by id, is final; (b) else the artist's first entry in that same
    /// reply that is native-script and of the playing length (<see cref="NativeEntry"/>); (c) else the Japanese
    /// storefront searched by the title alone, with the same rule. A Japanese storefront goes straight to (c).
    /// </summary>
    private async Task<(bool Answered, OriginalTitle? Found)> RequestAsync(LyricQuery query)
    {
        if (_storefront != OriginalCountry)
        {
            var term = PrimaryArtist(query.Artist) + " " + query.Title;
            if (await AskAsync(token => _catalogue.SearchAsync(term, _storefront, token)).ConfigureAwait(false) is not { } listed)
            {
                return (false, null);
            }
            if (Match(listed, query) is { TrackId: { } id })
            {
                if (await AskAsync(token => _catalogue.LookupAsync(id, OriginalCountry, OriginalLanguage, token)).ConfigureAwait(false) is not { } local)
                {
                    return (false, null);
                }
                // The same catalogue entry decides: an English recording that Japan also lists in English has no other title.
                return (true, OriginalName(local.FirstOrDefault(song => song.TrackId == id), query));
            }
            if (NativeEntry(listed, query) is { } native)
            {
                return (true, native);
            }
        }
        // Japan's storefront lists the song under its Japanese title, which an English title may still find.
        if (await AskAsync(token => _catalogue.SearchAsync(query.Title, OriginalCountry, token)).ConfigureAwait(false) is not { } japanese)
        {
            return (false, null);
        }
        return (true, NativeEntry(japanese, query));
    }

    /// <summary>
    /// The artist's first entry for the playing version (the same version tags) whose name is in native script and
    /// whose length is within max(3 s, 2%) of the playing one; an entry without a length doesn't count. Its name is the
    /// song's original title.
    /// </summary>
    public static OriginalTitle? NativeEntry(IEnumerable<CatalogueSong> results, LyricQuery query)
    {
        var tags = VersionTags.Tags(query.Title);
        var tolerance = Math.Max(3, query.Duration * 0.02);
        var song = results.FirstOrDefault(song => song.TrackName?.Trim() is { Length: > 0 } name && LyricLanguage.HasNative(name)
            && VersionTags.Tags(name).SetEquals(tags) && ArtistMatches(song.ArtistName, query.Artist)
            && song.TrackTimeMillis is { } millis && millis > 0 && Math.Abs(millis / 1000.0 - query.Duration) <= tolerance);
        if (song is null)
        {
            return null;
        }
        var album = song.CollectionName?.Trim();
        return new OriginalTitle(song.TrackName!.Trim(), string.IsNullOrEmpty(album) ? null : album);
    }

    /// <summary>Asks the catalogue in the next free slot, a <see cref="DefaultSpacing"/> after the last request.</summary>
    private async Task<IReadOnlyList<CatalogueSong>?> AskAsync(Func<CancellationToken, Task<IReadOnlyList<CatalogueSong>?>> ask)
    {
        TimeSpan wait;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var slot = _lastRequest == DateTimeOffset.MinValue || now > _lastRequest + _spacing ? now : _lastRequest + _spacing;
            _lastRequest = slot;
            wait = slot - now;
        }
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time).ConfigureAwait(false);
        }
        try
        {
            return await ask(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The catalogue entry for the playing song: the same title (and version tags) and artist, and the length nearest
    /// the playing one, within 3 s. The first of equally near entries.
    /// </summary>
    public static CatalogueSong? Match(IEnumerable<CatalogueSong> results, LyricQuery query)
    {
        var tags = VersionTags.Tags(query.Title);
        return results.Where(song =>
        {
            if (song.TrackName is not { } name || song.TrackId is null || LyricQuery.Similarity(name, query.Title) < 0.95
                || !VersionTags.Tags(name).SetEquals(tags) || !ArtistMatches(song.ArtistName, query.Artist))
            {
                return false;
            }
            return song.TrackTimeMillis is not { } millis || millis <= 0 || Math.Abs(millis / 1000.0 - query.Duration) <= 3;
        }).MinBy(song => Distance(song, query));
    }

    private static double Distance(CatalogueSong song, LyricQuery query) =>
        song.TrackTimeMillis is { } millis ? Math.Abs(millis / 1000.0 - query.Duration) : 3;

    /// <summary>The entry's name in the original storefront, when it is another name written in native script.</summary>
    public static OriginalTitle? OriginalName(CatalogueSong? song, LyricQuery query)
    {
        var name = song?.TrackName?.Trim();
        if (string.IsNullOrEmpty(name) || !LyricLanguage.HasNative(name) || LyricQuery.Similarity(name, query.Title) >= 0.95)
        {
            return null;
        }
        var album = song!.CollectionName?.Trim();
        return new OriginalTitle(name, string.IsNullOrEmpty(album) ? null : album);
    }

    /// <summary>
    /// The first name of an artist credit, as the catalogue search is asked: what comes before the first "&amp;" or
    /// comma, without the spaces around it ("YOASOBI" for "YOASOBI &amp; Ayase"); the whole credit when that leaves
    /// nothing. Re-implemented from a behaviour description (the Mac's lines come from the Verso import).
    /// </summary>
    public static string PrimaryArtist(string artist)
    {
        var end = artist.IndexOfAny(['&', ',']);
        var first = (end < 0 ? artist : artist[..end]).Trim(' ', '\t');
        return first.Length == 0 ? artist : first;
    }

    /// <summary>A shared credit ("A" for "A &amp; B", "A, B &amp; C" for "A"), compared as whole names: "Max" is not "Ava Max".</summary>
    public static bool ArtistMatches(string? candidate, string wanted)
    {
        var have = Credits(candidate ?? "");
        var want = Credits(wanted);
        return have.Count > 0 && want.Count > 0 && have.Overlaps(want);
    }

    /// <summary>The individual names in an artist credit, folded for comparison.</summary>
    public static HashSet<string> Credits(string artist)
    {
        var separated = CreditSeparator().Replace(LyricTextFolding.Fold(artist), "|");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in separated.Split('|'))
        {
            var name = new StringBuilder(part.Length);
            foreach (var rune in part.EnumerateRunes())
            {
                if (IsAlphanumeric(rune))
                {
                    name.Append(rune.ToString());
                }
            }
            if (name.Length > 0)
            {
                names.Add(name.ToString());
            }
        }
        return names;
    }

    private static bool IsAlphanumeric(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.DecimalDigitNumber
        or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    [GeneratedRegex(@"\s*(?:,|&|\+|/|;|×|\bfeaturing\b|\bfeat\b\.?|\bft\b\.?|\bwith\b|\bvs\b\.?|\bx\b|\band\b)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex CreditSeparator();

    /// <summary>
    /// A source's answer, searched again under the song's original title (<see cref="OriginalAsync"/>) when the listed
    /// title found nothing usable: no match, a language version ("English Version") the title does not name, or, for
    /// an East Asian artist (<see cref="IsEastAsianAsync"/>), lyrics in Latin script only. A match found that way is
    /// recorded under the listed title (it is the same song) and marked <see cref="LyricCandidate.ViaOriginalTitle"/>.
    /// A title that names a language version, or is already in native script, is not searched again.
    /// </summary>
    /// <param name="aliases">Where the artist's MusicBrainz country is read (cached with the artist's other names); null to judge by genre and script alone.</param>
    public static async ValueTask<LyricCandidate?> WithOriginalTitleAsync(LyricSourceId source, LyricQuery query, OriginalTitles? titles,
        bool enabled, Func<LyricQuery, CancellationToken, ValueTask<LyricCandidate?>> search, CancellationToken cancellationToken = default,
        ArtistAliases? aliases = null)
    {
        var first = await search(query, cancellationToken).ConfigureAwait(false);
        if (titles is null || !enabled || !Sources.Contains(source) || !query.HasArtist || query.Duration <= 0
            || LyricLanguage.HasNative(query.Title) || VersionTags.Tags(query.Title).Overlaps(VersionTags.Languages)
            || await UsableAsync(first, query, aliases, cancellationToken).ConfigureAwait(false))
        {
            return first;
        }
        if (await titles.OriginalAsync(query, cancellationToken).ConfigureAwait(false) is not { } original)
        {
            return first;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var other = new LyricQuery(original.Title, query.Artist, original.Album ?? query.Album, query.Duration) { Genre = query.Genre };
        if (await search(other, cancellationToken).ConfigureAwait(false) is not { } found
            || VersionTags.Mismatch(other, found.Title).Count > 0
            || (found.MetadataReported && LyricQuery.Similarity(found.Title, original.Title) < 0.65)
            || LyricLanguage.ScriptOf(found) == SungScript.Latin)
        {
            return first;
        }
        return found with { Title = query.Title, ViaOriginalTitle = true };
    }

    /// <summary>
    /// Whether a source's answer under the listed title can stand: it names no language version the song does not,
    /// and, for an East Asian artist, its lyrics are not in Latin script only.
    /// </summary>
    private static async ValueTask<bool> UsableAsync(LyricCandidate? candidate, LyricQuery query, ArtistAliases? aliases,
        CancellationToken cancellationToken)
    {
        if (candidate is null)
        {
            return false;
        }
        if (VersionTags.Mismatch(query, candidate.Title).Overlaps(VersionTags.Languages))
        {
            return false;
        }
        return LyricLanguage.ScriptOf(candidate) != SungScript.Latin
            || !await IsEastAsianAsync(query, aliases, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the playing artist most likely sings in Chinese, Japanese or Korean: an East Asian genre, an artist or
    /// album written in native script, or a MusicBrainz country of Japan, Korea, China, Taiwan, Hong Kong or Macau
    /// (asked last, and only when <paramref name="aliases"/> is given; kept 30 days with the artist's other names).
    /// The Mac's rule (next/integration 1cdde21), ported from the Mac session's description.
    /// </summary>
    public static async ValueTask<bool> IsEastAsianAsync(LyricQuery query, ArtistAliases? aliases, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (LyricLanguage.IsEastAsianGenre(query.Genre) || LyricLanguage.HasNative(query.Artist) || LyricLanguage.HasNative(query.Album))
        {
            return true;
        }
        if (aliases is null || !query.HasArtist) return false;
        // The country is only used once it is known: waiting for MusicBrainz here held every new artist's Latin lyrics
        // back by up to 4 s (plain lyrics.ovh text showed first; measured 13 Nov 2026). An unknown country is asked for
        // in the background, so the next song by this artist knows it.
        if (aliases.KnownCountry(query.Artist) is { } known) return EastAsianCountries.Contains(known);
        aliases.LearnCountryInBackground(query.Artist);
        await Task.CompletedTask.ConfigureAwait(false);
        return false;
    }

    private Dictionary<string, OriginalTitleEntryDto> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }
        try
        {
            _entries = File.Exists(_file)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(_file), OriginalTitlesJsonContext.Default.DictionaryStringOriginalTitleEntryDto) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _entries = [];
        }
        return _entries;
    }

    private void Store(string key, OriginalTitle? found)
    {
        byte[] bytes;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var all = Load().Where(pair => now - pair.Value.StoredAt < Lifetime).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            all[key] = new OriginalTitleEntryDto
            {
                Found = found is null ? null : new OriginalTitleDto { Title = found.Title, Album = found.Album },
                StoredAt = now,
                Rule = CurrentRule,
            };
            if (all.Count > MaxEntries)
            {
                foreach (var old in all.OrderBy(pair => pair.Value.StoredAt).Take(all.Count - MaxEntries).ToList())
                {
                    all.Remove(old.Key);
                }
            }
            _entries = all;
            bytes = JsonSerializer.SerializeToUtf8Bytes(all, OriginalTitlesJsonContext.Default.DictionaryStringOriginalTitleEntryDto);
        }
        try
        {
            var folder = Path.GetDirectoryName(_file)!;
            Directory.CreateDirectory(folder);
            var temp = Path.Combine(folder, $"OriginalTitles-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; asked again after a restart.
        }
    }
}

internal sealed class OriginalTitleEntryDto
{
    [JsonPropertyName("found")]
    public OriginalTitleDto? Found { get; set; }

    [JsonPropertyName("storedAt")]
    public DateTimeOffset StoredAt { get; set; }

    /// <summary>The rule the answer was found by (<see cref="OriginalTitles.CurrentRule"/>); 1 when absent.</summary>
    [JsonPropertyName("rule")]
    public int Rule { get; set; } = 1;
}

internal sealed class OriginalTitleDto
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("album")]
    public string? Album { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, OriginalTitleEntryDto>))]
internal sealed partial class OriginalTitlesJsonContext : JsonSerializerContext;
