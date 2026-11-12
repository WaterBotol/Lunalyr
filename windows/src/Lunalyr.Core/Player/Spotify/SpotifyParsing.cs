namespace Lunalyr.Core.Player.Spotify;

/// <summary>Spotify's search and queue replies as the card's models.</summary>
internal static class SpotifyParsing
{
    /// <summary>Songs shown at most (the Mac's catalogue search asks for 8).</summary>
    public const int SongLimit = 8;

    /// <summary>Albums shown at most (the Mac asks for 4).</summary>
    public const int AlbumLimit = 4;

    /// <summary>Covers at least this wide are preferred (the Mac upgrades Apple's 100 px covers to 200 px).</summary>
    private const int CoverWidth = 200;

    /// <summary>Songs, then albums. Local files and songs Spotify marks unplayable are left out.</summary>
    public static IReadOnlyList<PlayerSearchResult> Search(ReadOnlySpan<byte> body)
    {
        var reply = SpotifyJsonRead.Parse(body, SpotifyJsonContext.Default.SpotifySearchReply)
            ?? throw new PlayerSearchException(PlayerSearchError.Failed, "unreadable search reply");
        var results = new List<PlayerSearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in reply.Tracks?.Items ?? [])
        {
            if (results.Count >= SongLimit) break;
            if (track is not { Uri: { Length: > 0 } uri, Name: { Length: > 0 } name } || track.IsLocal == true || track.IsPlayable == false) continue;
            var id = $"sp-t-{track.Id ?? uri}";
            if (!seen.Add(id)) continue;
            var album = track.Album;
            results.Add(new PlayerSearchResult(id, PlayerSearchResultKind.Song, name, Artists(track), album?.Name ?? "",
                Cover(album?.Images), new PlayerSearchTarget(uri, album?.Uri is { Length: > 0 } albumUri ? albumUri : null,
                    track.TrackNumber ?? 0, track.DiscNumber ?? 0, album?.TotalTracks ?? 0)));
        }

        var albums = 0;
        foreach (var album in reply.Albums?.Items ?? [])
        {
            if (albums >= AlbumLimit) break;
            if (album is not { Uri: { Length: > 0 } uri, Name: { Length: > 0 } name }) continue;
            var id = $"sp-a-{album.Id ?? uri}";
            if (!seen.Add(id)) continue;
            albums++;
            results.Add(new PlayerSearchResult(id, PlayerSearchResultKind.Album, name, Artists(album), name, Cover(album.Images),
                new PlayerSearchTarget(uri, AlbumTracks: album.TotalTracks ?? 0)));
        }
        return results;
    }

    /// <summary>
    /// The song playing (index 1) and what Spotify lists after it (2, 3…). <paramref name="state"/> gives shuffle and
    /// the context; the queue reply's own current item is preferred over the state's.
    /// </summary>
    public static PlayerQueueSnapshot Queue(SpotifyPlaybackState? state, SpotifyQueueReply? queue)
    {
        var shuffle = state?.ShuffleState ?? false;
        var current = queue?.CurrentlyPlaying ?? state?.Item;
        if (current is null) return new PlayerQueueSnapshot([], shuffle) { ContextUri = state?.Context?.Uri };

        var items = new List<PlayerQueueItem> { Item(current, 1, isCurrent: true) };
        foreach (var next in queue?.Queue ?? [])
        {
            if (next is null) continue;
            items.Add(Item(next, items.Count + 1, isCurrent: false));
        }
        return new PlayerQueueSnapshot(items, shuffle) { ContextUri = state?.Context?.Uri };
    }

    private static PlayerQueueItem Item(SpotifyItem item, int index, bool isCurrent)
    {
        var uri = item.Uri is { Length: > 0 } u ? u : null;
        // A local file has a spotify:local: address that another app can't start, so it can't be clicked.
        var playable = uri is not null && item.IsLocal != true && !uri.StartsWith("spotify:local:", StringComparison.Ordinal);
        return new PlayerQueueItem($"q-{uri ?? item.Name ?? "item"}-{index}", item.Name ?? "", Artists(item), index, isCurrent,
            playable ? uri : null)
        {
            Album = item.Album?.Name ?? item.Show?.Name ?? "",
            ArtworkUri = Cover(item.Album?.Images ?? item.Images ?? item.Show?.Images),
        };
    }

    /// <summary>The artists' names; an episode's show.</summary>
    private static string Artists(SpotifyItem item)
    {
        var names = (item.Artists ?? []).Select(a => a?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        return names.Count > 0 ? string.Join(", ", names) : item.Show?.Name ?? "";
    }

    /// <summary>The smallest HTTPS cover at least 200 px wide, else the largest; null when there is none.</summary>
    internal static Uri? Cover(IReadOnlyList<SpotifyImage?>? images)
    {
        var usable = (images ?? [])
            .Where(i => i?.Url is { Length: > 0 } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(url, UriKind.Absolute, out _))
            .Select(i => i!)
            .ToList();
        if (usable.Count == 0) return null;
        var chosen = usable.Where(i => (i.Width ?? 0) >= CoverWidth).OrderBy(i => i.Width).FirstOrDefault()
            ?? usable.OrderByDescending(i => i.Width ?? 0).First();
        return new Uri(chosen.Url!);
    }
}
