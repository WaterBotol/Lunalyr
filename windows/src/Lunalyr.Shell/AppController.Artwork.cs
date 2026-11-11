using Lunalyr.Core.NowPlaying;
using Lunalyr.Platform.NowPlaying;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell;

// The album colours: on each track change the player's thumbnail is read, decoded and mapped to a Palette off the
// caller's thread, cached per track, and put in the scene. Keyed by the track (not the lookup generation), so looking
// the lyrics up again keeps the colours; a new track or player cancels a cover still loading.
public sealed partial class AppController
{
    private const int PaletteCacheSize = 32;

    private readonly Lock _publishGate = new();
    private readonly Dictionary<string, Palette> _palettes = [];
    private readonly Queue<string> _paletteOrder = new();
    private Palette _palette = PaletteMapper.Fallback;
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
    }

    private void BeginArtwork(MediaTrackInfo track, IMediaSession? session)
    {
        var key = track.TrackKey;
        CancellationTokenSource? cts = null;
        var cached = false;
        lock (_gate)
        {
            // The same track (a repeated metadata event) keeps what it has or is loading.
            if (_disposed || key == _artworkKey) return;
            CancelArtworkLocked();
            _artworkKey = key;
            if (_palettes.TryGetValue(key, out var palette))
            {
                SetPaletteLocked(palette);
                cached = true;
            }
            else if (session is null)
            {
                SetPaletteLocked(PaletteMapper.Fallback);
                cached = true;
            }
            else
            {
                // Until the new cover is read the previous colours stay, so the background fades once, not twice.
                cts = new CancellationTokenSource();
                _artworkCts = cts;
            }
        }

        if (cached) PublishScene();
        else if (cts is not null) Track(LoadArtworkAsync(key, session!, cts));
    }

    // Must hold _gate. The cancellation itself runs on the thread pool: cancelling inline would run the old load's
    // continuation right here, inside the lock.
    private void CancelArtworkLocked()
    {
        if (_artworkCts is { } old) _ = old.CancelAsync();
        _artworkCts = null;
        _artworkKey = null;
    }

    // Must hold _gate.
    private void SetPaletteLocked(Palette palette)
    {
        _palette = palette;
        _scene = _scene with { Palette = palette };
    }

    private async Task LoadArtworkAsync(string key, IMediaSession session, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await Task.Yield(); // Off the player's event thread.
            Palette? palette = null;
            for (var attempt = 0; ; attempt++)
            {
                // WaitAsync: a player or decoder that ignores cancellation must not hold this task once the track moves on.
                var stream = await session.OpenThumbnailAsync(cancellationToken: ct).WaitAsync(ct).ConfigureAwait(false);
                if (stream is not null)
                {
                    await using (stream.ConfigureAwait(false))
                    {
                        palette = await _options.ArtworkPalette(stream, ct).WaitAsync(ct).ConfigureAwait(false);
                    }

                    if (palette is null) _player.Info("The cover couldn't be read; using the default colours.");
                    break;
                }

                if (attempt >= _options.ArtworkRetries.Count)
                {
                    _player.Debug("The player shared no cover for this track; using the default colours.");
                    break;
                }

                await Task.Delay(_options.ArtworkRetries[attempt], _options.TimeProvider, ct).ConfigureAwait(false);
            }

            ApplyPalette(key, palette);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer track (or player, or shutdown) took over.
        }
        catch (Exception ex)
        {
            _player.Warn("Couldn't read the cover; using the default colours.", ex);
            ApplyPalette(key, null);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_artworkCts, cts)) _artworkCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Shows a cover's colours (or the default ones) if the track is still the one playing; caches real ones.</summary>
    private void ApplyPalette(string key, Palette? palette)
    {
        lock (_gate)
        {
            if (_disposed || key != _artworkKey) return;
            if (palette is not null && !_palettes.ContainsKey(key))
            {
                _palettes[key] = palette;
                _paletteOrder.Enqueue(key);
                while (_paletteOrder.Count > PaletteCacheSize) _palettes.Remove(_paletteOrder.Dequeue());
            }

            var shown = palette ?? PaletteMapper.Fallback;
            if (ReferenceEquals(shown, _palette)) return;
            SetPaletteLocked(shown);
        }

        _player.Debug(palette is null ? "Album colours: the default ones." : "Album colours: from the cover.");
        PublishScene();
    }
}
