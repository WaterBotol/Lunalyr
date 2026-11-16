using Lunalyr.Core.Colour;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Platform.NowPlaying;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell;

// The album colours and cover: on each track change the player's thumbnail is read, decoded and mapped to a Palette
// (and decoded again, smoothly, as the cover the opening title draws) off the caller's thread, cached per track, and put
// in the scene. Keyed by the track (not the lookup generation), so looking the lyrics up again keeps them; a new track
// or player cancels a cover still loading.
public sealed partial class AppController
{
    private const int PaletteCacheSize = 32;

    /// <summary>At most this many cached tracks keep their decoded cover (up to 0.6 MB each); the others keep only their colours.</summary>
    private const int CoverCacheSize = 8;

    private readonly Lock _publishGate = new();
    private readonly Dictionary<string, (Palette Palette, byte[] Cover, CoverArt? Art)> _palettes = [];
    private readonly Queue<string> _paletteOrder = new();
    private Palette _palette = PaletteMapper.Fallback;
    // A hash of the cover whose colours are showing (null for the default colours).
    private byte[]? _shownCover;
    // The cover the opening title draws: the playing track's own, or none. Never carried over to the next track.
    private CoverArt? _cover;
    // The album the cover showing belongs to: a new track sent the same cover is only believed for the same album.
    private string? _shownCoverAlbum;
    private string? _artworkKey;
    private CancellationTokenSource? _artworkCts;

    /// <summary>
    /// Hands the renderer the scene as it is now. Publishing reads the latest scene under one lock, so updates from
    /// different threads (lyrics, readings, colours) can never leave the renderer on an older snapshot.
    /// </summary>
    private void PublishScene()
    {
        lock (_publishGate)
        {
            SceneSnapshot scene;
            lock (_gate) scene = _scene;
            _renderer.SetScene(scene);
        }
        // Lyrics arriving, a song changing or none found decide whether the floating window shows.
        UpdateFloating();
    }

    private void BeginArtwork(MediaTrackInfo track, IMediaSession? session)
    {
        var key = track.TrackKey;
        CancellationTokenSource? cts = null;
        var publish = false;
        lock (_gate)
        {
            // The same track (a repeated metadata event) keeps what it has or is loading.
            if (_disposed || key == _artworkKey) return;
            CancelArtworkLocked();
            _artworkKey = key;
            if (_palettes.TryGetValue(key, out var known))
            {
                SetPaletteLocked(known.Palette, known.Cover, known.Art);
                _shownCoverAlbum = AlbumOf(track);
                publish = true;
                // Its colours are known but its cover was let go: read it again for the opening title.
                if (known.Art is null && session is not null)
                {
                    cts = new CancellationTokenSource();
                    _artworkCts = cts;
                }
                SetCoverPendingLocked(cts is not null);
            }
            else if (session is null)
            {
                SetPaletteLocked(PaletteMapper.Fallback, null, null);
                SetCoverPendingLocked(false);
                publish = true;
            }
            else
            {
                // Until the new cover is read the previous colours stay, so the background fades once, not twice. The
                // previous cover itself does not: the opening title never shows another song's.
                if (_cover is not null)
                {
                    _cover = null;
                    _scene = _scene with { Cover = null };
                    publish = true;
                }
                cts = new CancellationTokenSource();
                _artworkCts = cts;
                publish |= SetCoverPendingLocked(true);
            }
        }

        if (publish) PublishScene();
        if (cts is not null) Track(LoadArtworkAsync(key, session!, cts, AlbumOf(track)));
    }

    // Must hold _gate. The cancellation itself runs on the thread pool: cancelling inline would run the old load's
    // continuation right here, inside the lock.
    private void CancelArtworkLocked()
    {
        if (_artworkCts is { } old) _ = old.CancelAsync();
        _artworkCts = null;
        _artworkKey = null;
    }

    /// <summary>
    /// The playing song's cover is being read: the opening title keeps the cover's place, empty, so its words don't move
    /// when it comes (the Mac's 297b2f2). Must hold _gate; true when it changed.
    /// </summary>
    private bool SetCoverPendingLocked(bool pending)
    {
        if (_coverPending == pending) return false;
        _coverPending = pending;
        _scene = _scene with { CoverPending = pending };
        return true;
    }

    private bool _coverPending;

    // Must hold _gate.
    private void SetPaletteLocked(Palette palette, byte[]? cover, CoverArt? art)
    {
        _palette = palette;
        _shownCover = cover;
        _cover = art;
        _scene = _scene with { Palette = palette, Cover = art };
    }

    /// <summary>The player's cover for the playing track, read whole and hashed; null when it shares none.</summary>
    private static async Task<(byte[] Bytes, byte[] Hash)?> ReadCoverAsync(IMediaSession session, CancellationToken ct)
    {
        // WaitAsync: a player that ignores cancellation must not hold this task once the track moves on.
        var stream = await session.OpenThumbnailAsync(cancellationToken: ct).WaitAsync(ct).ConfigureAwait(false);
        if (stream is null) return null;
        await using (stream.ConfigureAwait(false))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, ct).WaitAsync(ct).ConfigureAwait(false);
            var bytes = copy.ToArray();
            return (bytes, System.Security.Cryptography.SHA256.HashData(bytes));
        }
    }

    /// <summary>The cover as the opening title draws it; null (logged) when it can't be decoded, which leaves the colours.</summary>
    private async Task<CoverArt?> CoverArtAsync(byte[] bytes, CancellationToken ct)
    {
        try
        {
            return await _options.ArtworkCover(bytes, ct).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _player.Debug($"The cover couldn't be decoded for the opening title ({ex.GetType().Name}).");
            return null;
        }
    }

    private bool IsShownCover(byte[] hash)
    {
        lock (_gate) return _shownCover is { } shown && shown.AsSpan().SequenceEqual(hash);
    }

    private bool IsShownAlbum(string album)
    {
        lock (_gate) return _shownCoverAlbum is { Length: > 0 } shown && shown == album;
    }

    /// <summary>The album as a comparable name (empty when the player shares none).</summary>
    private static string AlbumOf(MediaTrackInfo track) =>
        Lunalyr.Core.Lyrics.LyricQuery.Normalize(string.IsNullOrWhiteSpace(track.AlbumTitle) ? "" : track.AlbumTitle);

    private async Task LoadArtworkAsync(string key, IMediaSession session, CancellationTokenSource cts, string album)
    {
        var ct = cts.Token;
        try
        {
            await Task.Yield(); // Off the player's event thread.
            Palette? palette = null;
            CoverArt? art = null;
            (byte[] Bytes, byte[] Hash)? cover = null;
            for (var attempt = 0; ; attempt++)
            {
                cover = await ReadCoverAsync(session, ct).ConfigureAwait(false);
                if (cover is { } found)
                {
                    using var bytes = new MemoryStream(found.Bytes, writable: false);
                    // WaitAsync: a decoder that ignores cancellation must not hold this task once the track moves on.
                    palette = await _options.ArtworkPalette(bytes, ct).WaitAsync(ct).ConfigureAwait(false);
                    if (palette is null) _player.Info("The cover couldn't be read; using the default colours.");
                    else art = await CoverArtAsync(found.Bytes, ct).ConfigureAwait(false);
                    break;
                }

                if (attempt >= _options.ArtworkRetries.Count)
                {
                    _player.Debug("The player shared no cover for this track; using the default colours.");
                    break;
                }

                await Task.Delay(_options.ArtworkRetries[attempt], _options.TimeProvider, ct).ConfigureAwait(false);
            }

            // The previous track's cover again: the same album, or a player that hasn't sent the new cover yet. Its
            // colours stay, uncached, and the cover is read again in a moment; a different one replaces them.
            if (palette is not null && cover is { } same && IsShownCover(same.Hash))
            {
                // Only the same album may share a cover (tester feedback on 0.9.0: the previous album's cover showed, and
                // stayed, when the player was late with the new one). Otherwise the colours carry on but the picture is
                // not shown, and nothing is cached, until the track's own cover comes.
                var sameAlbum = album.Length > 0 && IsShownAlbum(album);
                ApplyPalette(key, palette, same.Hash, sameAlbum ? art : null, cache: false, album: sameAlbum ? album : null);
                var replaced = false;
                foreach (var wait in _options.ArtworkRechecks)
                {
                    await Task.Delay(wait, _options.TimeProvider, ct).ConfigureAwait(false);
                    if (await ReadCoverAsync(session, ct).ConfigureAwait(false) is not { } again || IsShownCover(again.Hash)) continue;
                    using var bytes = new MemoryStream(again.Bytes, writable: false);
                    if (await _options.ArtworkPalette(bytes, ct).WaitAsync(ct).ConfigureAwait(false) is not { } changed) continue;
                    _player.Debug("The player sent this track's own cover a moment late.");
                    (palette, cover) = (changed, again);
                    art = await CoverArtAsync(again.Bytes, ct).ConfigureAwait(false);
                    replaced = true;
                    break;
                }
                if (!replaced && !sameAlbum)
                {
                    _player.Debug("The player kept the previous track's cover; it isn't shown or kept for this one.");
                    return; // the colours already showing stay; no picture, nothing cached
                }
            }

            ApplyPalette(key, palette, palette is null ? null : cover?.Hash, palette is null ? null : art, album: album);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer track (or player, or shutdown) took over.
        }
        catch (Exception ex)
        {
            _player.Warn("Couldn't read the cover; using the default colours.", ex);
            ApplyPalette(key, null, null, null);
        }
        finally
        {
            var ended = false;
            lock (_gate)
            {
                if (ReferenceEquals(_artworkCts, cts))
                {
                    _artworkCts = null;
                    // Read, or none to read: the card no longer keeps a place for it.
                    ended = !_disposed && SetCoverPendingLocked(false);
                }
            }

            cts.Dispose();
            if (ended) PublishScene();
        }
    }

    // Must hold _gate. At most CoverCacheSize cached tracks keep their decoded cover: the earliest cached others let theirs
    // go (their colours stay; the cover is read again if they come back).
    private void TrimCoversLocked(string keep)
    {
        var covers = 0;
        foreach (var entry in _palettes.Values)
            if (entry.Art is not null) covers++;
        foreach (var key in _paletteOrder)
        {
            if (covers <= CoverCacheSize) break;
            if (key == keep || !_palettes.TryGetValue(key, out var entry) || entry.Art is null) continue;
            _palettes[key] = entry with { Art = null };
            covers--;
        }
    }

    /// <summary>Shows a cover's colours and cover (or the default ones) if the track is still the one playing; caches real ones.</summary>
    private void ApplyPalette(string key, Palette? palette, byte[]? cover, CoverArt? art, bool cache = true, string? album = null)
    {
        lock (_gate)
        {
            if (_disposed || key != _artworkKey) return;
            if (album is not null) _shownCoverAlbum = album;
            if (cache && palette is not null && cover is not null)
            {
                if (_palettes.TryGetValue(key, out var known))
                {
                    // Known colours whose cover had been let go: the cover is back, the colours stay the same object
                    // (so the background does not fade to what it already shows).
                    palette = known.Palette;
                    _palettes[key] = (palette, cover, art);
                }
                else
                {
                    _palettes[key] = (palette, cover, art);
                    _paletteOrder.Enqueue(key);
                    while (_paletteOrder.Count > PaletteCacheSize) _palettes.Remove(_paletteOrder.Dequeue());
                }

                if (art is not null) TrimCoversLocked(key);
            }

            var shown = palette ?? PaletteMapper.Fallback;
            if (ReferenceEquals(shown, _palette) && ReferenceEquals(art, _cover)) return;
            SetPaletteLocked(shown, palette is null ? null : cover, art);
        }

        _player.Debug(palette is null ? "Album colours: the default ones." : "Album colours: from the cover.");
        PublishScene();
    }
}
