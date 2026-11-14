using System.Runtime.InteropServices;
using Lunalyr.App.MusicVideo;
using Lunalyr.App.Tray;
using Lunalyr.Core.Logging;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.MusicVideo;
using Lunalyr.Shell;
using Lunalyr.Shell.MusicVideo;
using Lunalyr.Shell.Settings;

namespace Lunalyr.App.Hosting;

// The Music video wallpaper type (docs/APP-SHELL.md, "Music video"). Built after the controller, as the visualisers are.
// - Per-song state (offsets, links, chosen files, the lookup cache) lives in {data}\music-video.json.
// - YouTube is built only when nothing forbids it this run: never under --demo or --no-network (nothing may reach
//   YouTube: no results-page search, no Data API client, no key store, no player), and the player not under --no-playback either (it plays a
//   video stream, muted). A saved link in a test data folder therefore loads nothing.
internal sealed partial class AppHost
{
    private MusicVideoController? _musicVideo;
    private MusicVideoHttpClient? _musicVideoHttp;
    private YouTubeWallpaperHost? _youTubeHost;

    /// <summary>WinUI's thread (set when it starts): the YouTube player's WebView2 lives there.</summary>
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

    /// <summary>The music video controller (Settings, the flyout, the player card), or null before start-up.</summary>
    public MusicVideoController? MusicVideo => _musicVideo;

    private void StartMusicVideo(AppController controller)
    {
        var storage = new FileMusicVideoStorage(Path.Combine(DataRoot, "music-video.json"));
        var files = new MusicVideoFiles(storage);
        var offReason = _options.Demo ? "--demo" : _options.NoNetwork ? "--no-network" : null;
        YouTubeApiKeyStore? keys = null;
        YouTubeDataApi? api = null;
        if (offReason is null)
        {
            keys = new YouTubeApiKeyStore(Path.Combine(DataRoot, "MusicVideo", "youtube-key.bin"));
            _musicVideoHttp = new MusicVideoHttpClient();
            api = new YouTubeDataApi(_musicVideoHttp, () => keys.Key);
        }
        // YouTube's results page first, the Mac's way (no key); the owner's key as the fallback, or alone under "Use my API key".
        var resolver = new MusicVideoResolver(files, new MediaFoundationPlayability(), api, new MusicVideoCache(storage), new MusicVideoLinks(storage),
            page: _musicVideoHttp,
            searchMode: () => _settings.Current.MusicVideoSearch == "apiKey" ? MusicVideoSearchMode.ApiKey : MusicVideoSearchMode.Site);
        var video = new MusicVideoController(resolver, new MusicVideoOffsets(storage), controller.ReadSongPosition, controller.MusicVideoLyrics,
            files: files)
        {
            ButtonsHeld = () => (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0,
        };
        _musicVideo = video;
        // The player plays a (muted) video stream: never while playback is off for the run.
        var playerOff = offReason ?? (PlaybackGuard.Process.Reason is not null ? "--no-playback" : null);
        if (playerOff is null && _wallpaper is not null && _uiDispatcher is not null)
        {
            _youTubeHost = new YouTubeWallpaperHost(_uiDispatcher, controller, video, MusicVideoFeed.Shared, _wallpaper.Engine, Path.Combine(DataRoot, "MusicVideo", "WebView2"),
                () => _settings.Current, Log);
        }
        controller.UseMusicVideo(video, MusicVideoFeed.Shared, _youTubeHost is not null);
        // The video's still (Reduce Motion, the settings preview, the other displays) comes from YouTube's image host, only
        // when a player could be shown at all.
        controller.UseMusicVideoStills(_youTubeHost is not null ? _musicVideoHttp : null);
        // Choosing Music video keeps the type it replaced as "Without a video" (the Mac's rule).
        _settings.Changed += (_, e) =>
        {
            var remembered = AppSettings.RememberMusicVideoFallback(e.OldValue, e.NewValue);
            if (remembered.MusicVideoFallback != e.NewValue.MusicVideoFallback)
                _settings.Update(s => s with { MusicVideoFallback = remembered.MusicVideoFallback });
        };
        MusicVideoUi.Controller = video;
        MusicVideoUi.App = controller;
        MusicVideoUi.Keys = keys;
        MusicVideoUi.YouTubeOffReason = playerOff;
        MusicVideoUi.Http = _musicVideoHttp;
        MusicVideoUi.OpenUrl = PlaybackGuard.Process.Reason is null ? url => ShellUrlLauncher.Instance.Launch(new Uri(url)) : null;
        // --player-debug fake-youtube (checks, only with playback off): invented results, so the Search YouTube window's rows
        // can be seen without sending a song to YouTube.
        var fakeYouTube = _options.PlayerDebug.Contains("fake-youtube") && PlaybackGuard.Process.IsBlocked;
        MusicVideoUi.Search = new MusicVideoSearchModel(fakeYouTube ? new InventedSearching(new ControllerSearching(video, _log)) : new ControllerSearching(video, _log));
        // --youtube-search (checks): the window opens once there is a picker to show.
        if (_options.OpenYouTubeSearch) _uiDispatcher?.TryEnqueue(() => _app?.ShowMusicVideoSearch());
        _log.Info($"Music video: video files on; YouTube {(playerOff is null ? $"on (search: {(_settings.Current.MusicVideoSearch == "apiKey" ? "your API key" : "YouTube's site")}, an API key {(keys!.HasKey ? "is" : "isn't")} set)" : $"off for this run ({playerOff})")}.");
    }

    private void StopMusicVideo()
    {
        _youTubeHost?.Dispose();
        _youTubeHost = null;
        _musicVideoHttp?.Dispose();
        _musicVideoHttp = null;
    }

    /// <summary>The picker's view of the controller (the Mac's MusicVideoController: MusicVideoSearching).</summary>
    private sealed class ControllerSearching(MusicVideoController video, CategoryLogger log) : IMusicVideoSearching
    {
        public MusicVideoSong? SearchSong => video.SearchSong;
        public string? ShowingYouTubeId => video.ShowingYouTubeId;
        public string? PickedVideoId => video.PickedVideoId;
        public Task<MusicVideoSearchOutcome> SearchYouTubeAsync(string query, CancellationToken ct) => video.SearchYouTubeAsync(query, ct);
        public bool UsePickedVideo(string videoId, string songKey)
        {
            var used = video.UsePickedVideo(videoId, songKey);
            log.Info(used ? $"Music video: picked {videoId} from search for the song." : $"Music video: the pick {videoId} wasn't used (the song changed).");
            return used;
        }

        public void ClearPastedLink()
        {
            log.Info("Music video: back to choosing automatically.");
            video.ClearPastedLink();
        }
    }

    /// <summary>The demo song's results, made up (no video id is real; nothing is fetched or played).</summary>
    private sealed class InventedSearching(IMusicVideoSearching inner) : IMusicVideoSearching
    {
        public MusicVideoSong? SearchSong => inner.SearchSong;
        public string? ShowingYouTubeId => "aaaaaaaaaa1";
        public string? PickedVideoId => null;
        public bool UsePickedVideo(string videoId, string songKey) => false;
        public void ClearPastedLink() { }

        public async Task<MusicVideoSearchOutcome> SearchYouTubeAsync(string query, CancellationToken ct)
        {
            await Task.Delay(600, ct);
            var length = inner.SearchSong?.Duration;
            return new MusicVideoSearchOutcome.Results(
            [
                new("aaaaaaaaaa1", "The Lunalyr Demo Band - Neon Harbour (Official Music Video)", "The Lunalyr Demo Band", VerifiedArtist: true, Duration: length, Views: "1.2M views", Uploaded: "2 years ago"),
                new("bbbbbbbbbb2", "Neon Harbour (Lyric Video)", "The Lunalyr Demo Band", VerifiedArtist: true, Duration: length, Views: "310K views", Uploaded: "2 years ago"),
                new("cccccccccc3", "Neon Harbour – Live at the Pier, a very long title that runs on to show how two lines are cut short", "Harbour Sessions", Duration: (length ?? 200) + 41, Views: "48K views", Uploaded: "8 months ago"),
                new("dddddddddd4", "neon harbour but it's slowed + reverb", "night drives", Duration: (length ?? 200) * 1.25, Views: "2.1K views", Uploaded: "3 weeks ago"),
            ]);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
}
