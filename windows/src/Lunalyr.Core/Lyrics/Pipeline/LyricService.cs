using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Core.Lyrics.Storage;

namespace Lunalyr.Core.Lyrics.Pipeline;

/// <summary>What <see cref="LyricService.CreateDefault"/> builds the service with. Every value has a working default.</summary>
public sealed record LyricServiceOptions
{
    /// <summary>Sources only the platform layer can provide (Apple Music on Windows). They replace a built-in one for the same source.</summary>
    public IReadOnlyList<ILyricSource> PlatformSources { get; init; } = [];

    /// <summary>Languages, reading detection and word splitting for the parsers; from <see cref="Readings"/> when null.</summary>
    public LyricParseOptions? Parse { get; init; }

    /// <summary>
    /// The system's reading services (ICU, the Japanese tokenizer): they tell supplied readings from translations and
    /// split words for estimated timing (<see cref="ReadingParseOptions.ParseOptions"/>). The built-in rules when null.
    /// </summary>
    public ReadingServices? Readings { get; init; }

    /// <summary>Whether the network is up; assumed up when null.</summary>
    public ILyricConnectivity? Connectivity { get; init; }

    /// <summary>HTTP politeness, size limits and the User-Agent.</summary>
    public LyricHttpOptions? Http { get; init; }

    /// <summary>Grace periods and per-source limits; the Mac's when null.</summary>
    public LyricLookupTiming? Timing { get; init; }

    /// <summary>Where artists' other names are looked up; MusicBrainz over the shared client when null.</summary>
    public IArtistAliasSearch? ArtistSearch { get; init; }

    /// <summary>Search the Chinese catalogues again under an artist's other names.</summary>
    public bool UseArtistAliases { get; init; } = true;

    /// <summary>
    /// Search again under a song's original title, from Apple's catalogue, when the listed (English) title finds nothing
    /// usable. The Mac shares one setting with <see cref="UseArtistAliases"/> ("Search under other names").
    /// </summary>
    public bool UseOriginalTitles { get; init; } = true;

    /// <summary>Where songs' original titles are looked up; Apple's catalogue (iTunes Search) over the shared client when null.</summary>
    public IOriginalTitleCatalogue? TitleCatalogue { get; init; }

    /// <summary>This PC's Apple storefront ("au"); the Windows region when null.</summary>
    public string? Storefront { get; init; }

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public Logger? Logger { get; init; }
}

/// <summary>One row of the source list: what the source is, whether it is switched on, and whether it works here.</summary>
/// <param name="Id">The source.</param>
/// <param name="Title">Its name ("LRCLIB", "Apple Music").</param>
/// <param name="Timing">The timing it can deliver.</param>
/// <param name="Enabled">Switched on in the options.</param>
/// <param name="UnavailableReason">Why it cannot be asked on this PC; null when it can.</param>
public sealed record LyricSourceStatus(LyricSourceId Id, string Title, string Timing, bool Enabled, string? UnavailableReason)
{
    public bool IsAvailable => UnavailableReason is null;
}

/// <summary>
/// The lyric core in one place, for the app: sources → <see cref="RawLyrics"/> → parsers → credits → candidates →
/// the progressive <see cref="LyricLookup"/>, with saved choices and the cache on disk.
/// </summary>
/// <remarks>
/// What shows is ranked by timing first: word or mixed timing (Apple Music, then LRCLIB, then the others), then line
/// timing (Apple Music first), then plain text; timing with a fault ranks below sound timing; LRCLIB loses its lead
/// when two other sources agree against its timing. A saved choice always wins. Presentation steps the Mac runs after
/// choosing (estimated word timing for line-timed lyrics, QQ's gap repair, mixed-timing copy removal) are left to the
/// display layer: see <c>LyricGapTiming</c>, <c>QQGapRepair</c> and <c>LyricLineIndex</c>.
/// </remarks>
public sealed class LyricService
{
    private readonly LyricSourceSet _sources;
    private readonly SavedLyrics _saved;

    public LyricService(LyricSourceSet sources, SavedLyrics saved, ILyricConnectivity? connectivity = null, LyricLookupTiming? timing = null,
        TimeProvider? timeProvider = null, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(saved);
        _sources = sources;
        _saved = saved;
        Lookup = new LyricLookup(saved, sources.Fetch, connectivity, timing, timeProvider: timeProvider,
            localFingerprint: sources.LocalFingerprint, logger: logger, unavailable: sources.UnavailableReason,
            notConfigured: sources.NotConfiguredReason);
    }

    /// <summary>
    /// The service with every built-in source over one shared HTTP client, plus <see cref="LyricServiceOptions.PlatformSources"/>.
    /// Saved choices, the automatic cache, artists' other names and songs' original titles are kept in <paramref name="cacheFolder"/>.
    /// </summary>
    /// <param name="cacheFolder">The app's lyric data folder.</param>
    /// <param name="httpClient">The HTTP client to send through; one suited to the sources is made when null.</param>
    /// <param name="options">Everything else.</param>
    public static LyricService CreateDefault(string cacheFolder, HttpClient? httpClient = null, LyricServiceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheFolder);
        options ??= new LyricServiceOptions();
        var http = new LyricHttpClient(httpClient ?? LyricHttpClient.CreateDefaultHttpClient(),
            (options.Http ?? new LyricHttpOptions()) with { TimeProvider = options.TimeProvider }, options.Logger);
        var aliases = new ArtistAliases(cacheFolder, options.ArtistSearch ?? new MusicBrainzArtistSearch(http), options.TimeProvider);
        var titles = new OriginalTitles(cacheFolder, options.TitleCatalogue ?? new ItunesCatalogue(http), options.Storefront,
            timeProvider: options.TimeProvider);
        var parse = options.Parse ?? options.Readings?.ParseOptions();
        var sources = new LyricSourceSet([.. LyricSourceCatalog.CreateDefault(http, stateFolder: cacheFolder), .. options.PlatformSources], aliases, parse, titles)
        {
            UsesArtistAliases = options.UseArtistAliases,
            UsesOriginalTitles = options.UseOriginalTitles,
        };
        return new LyricService(sources, new SavedLyrics(cacheFolder, options.TimeProvider), options.Connectivity, options.Timing,
            options.TimeProvider, options.Logger);
    }

    /// <summary>The progressive lookup underneath, for callers that need more than this facade offers.</summary>
    public LyricLookup Lookup { get; }

    public LyricSourceSet Sources => _sources;

    /// <summary>
    /// The word distributor the parsers use (the system word breaker when reading services were given), for the display
    /// steps that spread estimated words again (<see cref="Timing.LyricGapTiming.EstimatingWords"/>,
    /// <see cref="Timing.QQGapRepair"/>, <see cref="Timing.LyricLineIndex.RemovingMixedTimingCopies"/>); null for the built-in one.
    /// </summary>
    public Timing.WordDistributor? WordDistributor => _sources.ParseOptions?.Distribute;

    /// <summary>Every source in <paramref name="options"/>' order (switched-off ones after), with whether it works here.</summary>
    public IReadOnlyList<LyricSourceStatus> SourceList(LyricSourceSnapshot options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return [.. LyricSourceInfo.All.OrderBy(options.OrderOf).ThenBy(LyricSourceSnapshot.RecommendedOrder.ToList().IndexOf)
            .Select(id => new LyricSourceStatus(id, id.Title(), id.TimingDescription(), options.IsEnabled(id), _sources.UnavailableReason(id)))];
    }

    /// <summary>
    /// Looks up lyrics for <paramref name="query"/> (<see cref="LyricLookup.LookupAsync"/>): a saved choice, the cache,
    /// or every enabled source at once, reporting progress through <paramref name="onUpdate"/>.
    /// </summary>
    public Task<LyricLookupReport> LookupAsync(LyricQuery query, LyricSourceSnapshot options, bool force = false,
        Func<LyricLookupReport, ValueTask>? onUpdate = null, CancellationToken cancellationToken = default) =>
        Lookup.LookupAsync(query, options, force, onUpdate, cancellationToken);

    /// <summary>Re-reads only the local lyric files after they change (<see cref="LyricLookup.RefreshLocalAsync"/>).</summary>
    public Task<LyricLookupReport?> RefreshLocalAsync(LyricQuery query, LyricLookupReport previous, LyricSourceSnapshot options,
        CancellationToken cancellationToken = default) =>
        Lookup.RefreshLocalAsync(query, previous, options, cancellationToken);

    /// <summary>Saves the user's choice for this recording; it shows from then on, whatever the sources say.</summary>
    public bool Pin(LyricQuery query, LyricResult result)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _saved.Save(result, query.RecordingKey, pin: true);
    }

    /// <summary>Forgets the user's choice for this recording.</summary>
    public void Unpin(LyricQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _saved.Unpin(query.RecordingKey);
    }
}
