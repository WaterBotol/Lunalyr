// Apple catalogue matches (cover and track ID) by song, ported from the Mac's CatalogueMatches and its lookUp
// (DiscordPresence.swift, Lunalyr's own lines): a song is searched for once, misses are asked again after ten minutes.

using Lunalyr.Core.Extras;

namespace Lunalyr.Shell.Extras;

/// <summary>Fetches the iTunes search reply for a song (the title and artist only), or null on any failure.</summary>
public delegate Task<byte[]?> CatalogueSearch(string title, string artist, CancellationToken cancellationToken);

/// <summary>
/// The Apple catalogue's cover and catalogue ID per song, for Discord's cover and "Play this song" link. Without a
/// search (<c>--demo</c>, <c>--no-network</c>, tests) nothing is ever looked up.
/// </summary>
public sealed class CatalogueMatches
{
    public const int Capacity = 256;

    /// <summary>A song whose lookup failed or found nothing is asked again after this, so a network blip doesn't last.</summary>
    public static readonly TimeSpan LookupRetry = TimeSpan.FromSeconds(600);

    private readonly CatalogueSearch? _search;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly RecentCache<CatalogueMatch> _matches = new(Capacity);
    private readonly RecentCache<long> _misses = new(Capacity);
    private readonly HashSet<string> _lookingUp = new(StringComparer.Ordinal);

    public CatalogueMatches(CatalogueSearch? search, TimeProvider? timeProvider = null)
    {
        _search = search;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised (on any thread) when a lookup finishes, so whoever asked can look again.</summary>
    public event Action? Changed;

    /// <summary>Whether lookups can happen at all this run.</summary>
    public bool Searches => _search is not null;

    public static string Key(string title, string artist, string? album) => $"{title}|{artist}|{album ?? ""}";

    /// <summary>The cached match, starting a lookup the first time a song is seen unless <paramref name="lookUpIfMissing"/> is false.</summary>
    public CatalogueMatch? Match(string title, string artist, string? album, bool lookUpIfMissing = true)
    {
        var key = Key(title, artist, album);
        lock (_gate)
        {
            if (_matches.TryGet(key, out var known)) return known;
            if (_misses.TryGet(key, out var missed) && _time.GetElapsedTime(missed) < LookupRetry) return null;
            if (!lookUpIfMissing || _search is null || !_lookingUp.Add(key)) return null;
        }

        // Off the caller's thread, so whoever asked hears about the answer only after it has its own.
        _ = Task.Run(() => LookUpAsync(key, title, artist));
        return null;
    }

    private async Task LookUpAsync(string key, string title, string artist)
    {
        CatalogueMatch? found = null;
        try
        {
            var reply = await _search!(title, artist, CancellationToken.None).ConfigureAwait(false);
            if (reply is not null) found = DiscordPresence.SearchMatch(reply, title, artist);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            found = null;
        }

        lock (_gate)
        {
            if (found is not null)
            {
                _matches.Set(key, found);
                _misses.Remove(key);
            }
            else
            {
                _misses.Set(key, _time.GetTimestamp());
            }
            _lookingUp.Remove(key);
        }
        Changed?.Invoke();
    }
}
