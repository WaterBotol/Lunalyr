// Ported from the Mac's MusicVideoView.swift (YouTubeEmbed, YouTubeEmbedSession; Lunalyr's own code), for WebView2.
// Differences, each to keep to YouTube's documented IFrame Player API:
// - The page creates the privacy-enhanced embed itself (an <iframe> of youtube-nocookie.com/embed with enablejsapi=1 and
//   the documented player parameters) and YT.Player attaches to it, as the API's documentation describes. The Mac passes
//   an undocumented `host` option instead.
// - Captions are forced off as on the Mac (the owner saw them on the wallpaper, 14 Nov): cc_load_policy=0 alone leaves
//   YouTube's default, which shows them for some videos and accounts, so the page also unloads the captions module and
//   clears the track (unloadModule, setOption: the player's own calls, not in the documented reference) when the player
//   is ready, when its API changes (the captions module loading) and on each load or play. Only the display is switched
//   off, as the player's own CC button does; no caption track is read.
// - Messages go through WebView2's window.chrome.webview.postMessage; the page is served from a virtual https host so
//   the embed gets a real origin and referrer (the Mac's baseURL).
// - Framing, as on the Mac (the owner's decision, 14 Nov 2026, replacing milestone 6's "as YouTube draws it"): filled, the
//   player covers the page and is drawn 1.25x larger, so YouTube's title bar and logo fall off-screen; shown whole
//   (MusicVideoFraming.Whole) there is no overscan, the page is only as large as the visible picture and the player is
//   sized inside it (Box, window.lunalyrBox) so YouTube's own letterbox bars fall outside. The player shows only while it
//   plays (ShowsVideo): paused, ended or buffering, the wallpaper's layer covers it with the video's still. Shown whole, a
//   player shows as soon as it plays, with YouTube's controls covered for their first seconds (MusicVideoOverlayMask), and
//   a buffering one is covered at once (HideDelay). The Mac's frozen frame (a picture
//   of a paused player) is not part of this; the still shows instead.
// Silence does not rest on the page: the host mutes the whole WebView2 (CoreWebView2.IsMuted) before it navigates.

using System.Globalization;
using System.Text.Json;

namespace Lunalyr.Core.MusicVideo;

public static class YouTubeEmbed
{
    /// <summary>The page's own origin (a WebView2 virtual host mapped to a folder of Lunalyr's).</summary>
    public const string VirtualHost = "lunalyr-video.example";

    public static readonly Uri BaseUrl = new($"https://{VirtualHost}/");

    /// <summary>The page's address for a video and page number.</summary>
    public static Uri PageUrl(int page) => new(BaseUrl, $"player.html?page={page.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Error codes after which the video cannot be shown: 2 bad ID, 5 player error, 100 removed or private, 101/150 not
    /// embeddable, 152/153 the embed refused for this page.
    /// </summary>
    public static readonly IReadOnlySet<int> FatalErrors = new HashSet<int> { 2, 5, 100, 101, 150, 152, 153 };

    /// <summary>Of those, the passing one: an HTML5 player error, never remembered for the song.</summary>
    public static readonly IReadOnlySet<int> TransientErrors = new HashSet<int> { 5 };

    /// <summary>What a player error means for the song, null when it is not fatal.</summary>
    public static MusicVideoPlaybackFailure? Failure(int code)
    {
        if (!FatalErrors.Contains(code)) return null;
        return TransientErrors.Contains(code) ? MusicVideoPlaybackFailure.Transient : MusicVideoPlaybackFailure.Refused;
    }

    /// <summary>
    /// How much larger than the screen a filled player is drawn (and clipped): enough to push YouTube's title bar (top left)
    /// and logo (bottom right) off the edges. The video is aspect-filled anyway. Shown whole, there is none
    /// (<see cref="MusicVideoFramings.YouTubeOverscan"/>).
    /// </summary>
    public const double Overscan = MusicVideoFramings.FillOverscan;

    /// <summary>
    /// The player is visible only while it plays (state 1). Paused, ended, cued, unstarted or buffering, YouTube draws its
    /// title bar and a big play or pause button over the video; the wallpaper shows the video's still instead. Buffering
    /// hides it only after a grace (<see cref="HideDelay"/>).
    /// </summary>
    public static bool ShowsVideo(int state) => state == 1;

    /// <summary>The same rule from a sync sample: playing, not buffering.</summary>
    public static bool ShowsVideo(MusicVideoDrift.Sample sample) => sample.VideoPlaying && !sample.Buffering;

    /// <summary>
    /// A playing video that starts buffering (a drift seek, a stall) stays up this long before it is hidden: most seeks land
    /// sooner, and hiding at once flashed the still on every one.
    /// </summary>
    public const double BufferingGrace = 0.3;

    /// <summary>
    /// How long to wait before hiding the player. Only buffering while the song plays waits, and not when a picture is ready
    /// to cover it; anything else (a pause, the end, an error) hides at once. Shown whole, a buffering player hides at once:
    /// YouTube shows its overlays again once the seek or stall is over.
    /// </summary>
    public static double HideDelay(bool buffering, MusicVideoHold hold, bool hasFrame, MusicVideoFraming framing = MusicVideoFraming.Fill) =>
        buffering && hold == MusicVideoHold.Live && !hasFrame && framing == MusicVideoFraming.Fill ? BufferingGrace : 0;

    /// <summary>
    /// Shown whole: the player's size in per cent of the web view, which is only as large as the visible picture. A video of
    /// another shape than 16:9 is drawn larger so YouTube's own bars fall outside the view; nothing is ever drawn larger
    /// than the screen.
    /// </summary>
    public sealed record Box
    {
        private Box(double width, double height)
        {
            Width = width;
            Height = height;
        }

        public double Width { get; }
        public double Height { get; }

        /// <summary>A box of these per cents (each capped at 400), or null when either is not a positive number.</summary>
        public static Box? Create(double width, double height) =>
            double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0
                ? new Box(Math.Min(400, width), Math.Min(400, height))
                : null;

        /// <summary>The box as the page's script reads it: <c>[w,h]</c>, three decimals.</summary>
        public string Script => string.Create(CultureInfo.InvariantCulture, $"[{Width:0.000},{Height:0.000}]");
    }

    /// <summary>The page's overscan, as the page's script reads it (one decimal at least: "1.0", "1.25", "2.0").</summary>
    public static string Number(double value) => value.ToString(value == Math.Floor(value) ? "0.0" : "0.0##", CultureInfo.InvariantCulture);

    /// <summary>The script that sets the page's overscan.</summary>
    public static string OverscanScript(double overscan) => $"window.lunalyrOverscan && window.lunalyrOverscan({Number(ClampOverscan(overscan))})";

    /// <summary>The script that sets (or, with null, clears) the player's box.</summary>
    public static string BoxScript(Box? box) => box is null
        ? "window.lunalyrBox && window.lunalyrBox(0, 0)"
        : string.Create(CultureInfo.InvariantCulture, $"window.lunalyrBox && window.lunalyrBox({box.Width:0.000}, {box.Height:0.000})");

    private static double ClampOverscan(double overscan) => double.IsFinite(overscan) ? Math.Min(2, Math.Max(1, overscan)) : Overscan;

    /// <summary>The interface language for the player's own text: Windows' (two or three ASCII letters, else "en").</summary>
    public static string PlayerLanguage(CultureInfo? culture = null)
    {
        var code = (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName.ToLowerInvariant();
        return code.Length is 2 or 3 && code.All(char.IsAsciiLetter) ? code : "en";
    }

    /// <summary>
    /// The caption language to ask the player for: the first part of a language tag ("en-GB" gives "en"), two or three
    /// ASCII letters; null for anything else, so nothing but a plain code reaches the page.
    /// </summary>
    public static string? CaptionLanguage(string? language)
    {
        if (string.IsNullOrEmpty(language)) return null;
        var code = language.Split('-', '_')[0].ToLowerInvariant();
        return code.Length is 2 or 3 && code.All(char.IsAsciiLetter) ? code : null;
    }

    /// <summary>How long after the player is ready its captions stay loaded when none were captured (the Mac's captionWait).</summary>
    public const double CaptionWait = 60;

    /// <summary>The script that unloads the captions once captured (<c>lunalyrCaptionsDone</c>).</summary>
    public const string CaptionsDoneScript = "window.lunalyrCaptionsDone && window.lunalyrCaptionsDone()";

    /// <summary>
    /// The embed's address: youtube-nocookie's privacy-enhanced player with only documented parameters. With
    /// <paramref name="captions"/> (a language, or "" for the video's own track) the player loads the video's captions,
    /// unseen (<see cref="MusicVideoCaptionSync.HideScript"/>), for caption sync; otherwise they stay unloaded.
    /// </summary>
    public static string EmbedSource(string videoId, double start, string language, string? captions = null)
    {
        if (!YouTubeVideoId.IsValid(videoId)) throw new ArgumentException("Not a YouTube video ID.", nameof(videoId));
        var second = Math.Max(0, (int)(double.IsFinite(start) ? start : 0));
        language = SafeLanguage(language);
        return $"https://www.youtube-nocookie.com/embed/{videoId}?enablejsapi=1&autoplay=0&mute=1&controls=0&disablekb=1&fs=0"
            + $"&iv_load_policy=3&rel=0&playsinline=1&loop=0&cc_load_policy={(captions is null ? 0 : 1)}"
            + (CaptionLanguage(captions) is { } wanted ? $"&cc_lang_pref={wanted}" : "")
            + $"&hl={language}&start={second}"
            + $"&origin={Uri.EscapeDataString(BaseUrl.GetLeftPart(UriPartial.Authority))}";
    }

    private static string SafeLanguage(string language) =>
        language.Length > 0 && language.All(char.IsAsciiLetter) ? language : "en";

    /// <summary>
    /// The player's page. <paramref name="overscan"/> is its zoom (1.25 filled, 1 whole; kept within 1…2), and
    /// <paramref name="box"/> the player's size within the page when shown whole (null: it covers the page). Both can change
    /// later without a reload (<see cref="OverscanScript"/>, <see cref="BoxScript"/>).
    /// </summary>
    /// <param name="captions">Load the video's captions, unseen, in this language ("" for the video's own track); null leaves them unloaded.</param>
    public static string Html(string videoId, double start, int page = 0, string? language = null, double overscan = Overscan, Box? box = null,
        string? captions = null)
    {
        var source = EmbedSource(videoId, start, language ?? PlayerLanguage(), captions);
        var captionTrack = CaptionLanguage(captions) is { } track ? $"'{track}'" : "null";
        var captionsWanted = captions is null ? "false" : "true";
        var captionWait = ((int)(CaptionWait * 1000)).ToString(CultureInfo.InvariantCulture);
        var cueWait = ((int)(CueWait * 1000)).ToString(CultureInfo.InvariantCulture);
        var pageText = page.ToString(CultureInfo.InvariantCulture);
        var zoom = Number(ClampOverscan(overscan));
        var boxText = box?.Script ?? "null";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <meta name="referrer" content="strict-origin-when-cross-origin">
            <style>
            html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#000}
            #frame{position:absolute;left:50%;top:50%;width:max(100vw,177.78vh);height:max(100vh,56.25vw);
            transform:translate(-50%,-50%) scale({{zoom}});pointer-events:none;opacity:0;
            transition:opacity .6s cubic-bezier(0.22,1,0.36,1)}
            #frame iframe{width:100%;height:100%;border:0;pointer-events:none}
            </style></head><body><div id="frame"><iframe id="player" src="{{source}}" title="Music video"
            allow="autoplay; encrypted-media" referrerpolicy="strict-origin-when-cross-origin"></iframe></div>
            <script>
            var player = null, ready = false, blurPx = 0, overscan = {{zoom}}, box = {{boxText}};
            var captionsWanted = {{captionsWanted}}, captionTrack = {{captionTrack}}, captionsLoaded = false, captionTimer = null;
            var currentVideo = '{{videoId}}', currentPage = {{pageText}}, awaitingCue = false;
            function post(type, value) {
              try { window.chrome.webview.postMessage({type: type, value: value, video: currentVideo, page: currentPage}); } catch (e) {}
            }
            function visibleFor(state) { return state === 1; }
            function show(state) {
              // Buffering keeps what shows: Lunalyr covers the player if it lasts.
              if (state === 3) return;
              document.getElementById('frame').style.opacity = visibleFor(state) ? '1' : '0';
            }
            function hideCaptions() {
              if (!player || captionsWanted) return;
              try { player.unloadModule('captions'); } catch (e) {}
              try { player.unloadModule('cc'); } catch (e) {}
              try { player.setOption('captions', 'track', {}); } catch (e) {}
            }
            function onYouTubeIframeAPIReady() {
              player = new YT.Player('player', {
                events: {
                  onReady: function (event) {
                    ready = true; event.target.mute(); hideCaptions(); show(-1); post('ready', 0);
                    if (captionsWanted) captionTimer = setTimeout(window.lunalyrCaptionsDone, {{captionWait}});
                  },
                  onError: function (event) { show(-1); post('error', event.data); },
                  onApiChange: function () { hideCaptions(); },
                  onStateChange: function (event) {
                    var state = event.data;
                    if (state === 1 || state === 3) { player.mute(); }
                    if (state === -1 || state === 1 || state === 5) { hideCaptions(); }
                    // Another video switched to in this player (lunalyrLoad) is ready once cued.
                    if (state === 5 && awaitingCue) { awaitingCue = false; post('ready', 0); }
                    // Caption sync: the captions module is loaded (unseen) once the video plays, in the lyrics' language.
                    if (state === 1 && captionsWanted && !captionsLoaded) {
                      captionsLoaded = true;
                      try { player.loadModule('captions'); } catch (e) {}
                      if (captionTrack) { try { player.setOption('captions', 'track', {languageCode: captionTrack}); } catch (e) {} }
                    }
                    show(state);
                    post('state', state);
                  }
                }
              });
            }
            window.lunalyrSample = function () {
              if (!ready || !player || !player.getCurrentTime) return null;
              // Checked on every sample too: the video must never be heard.
              try { if (player.isMuted && !player.isMuted()) player.mute(); } catch (e) {}
              return {time: player.getCurrentTime(), state: player.getPlayerState(), duration: player.getDuration()};
            };
            window.lunalyrCaptionsDone = function () {
              captionsWanted = false;
              if (captionTimer) { clearTimeout(captionTimer); captionTimer = null; }
              hideCaptions();
            };
            // Another video in this same player (the Mac's YouTubePlayerReuse, e7e50f7): cued at start, not played (the sync
            // loop plays it), numbered pageNo so its messages are told apart from the last video's; its captions are
            // loaded again, unseen, when captions names a language ('' for the video's own track).
            window.lunalyrLoad = function (id, start, pageNo, captions) {
              if (!ready || !player || !player.cueVideoById) return false;
              currentVideo = id; currentPage = pageNo;
              show(-1);
              player.mute();
              if (captionTimer) { clearTimeout(captionTimer); captionTimer = null; }
              captionsWanted = captions !== null; captionTrack = captions ? captions : null; captionsLoaded = false;
              awaitingCue = true;
              if (!captionsWanted) hideCaptions();
              player.cueVideoById({videoId: id, startSeconds: Math.max(0, Math.floor(start))});
              if (captionsWanted) captionTimer = setTimeout(window.lunalyrCaptionsDone, {{captionWait}});
              setTimeout(function () { if (awaitingCue) { awaitingCue = false; post('ready', 0); } }, {{cueWait}});
              return true;
            };
            window.lunalyrPlay = function () { if (ready) { player.mute(); player.playVideo(); } };
            window.lunalyrPause = function () { if (ready) player.pauseVideo(); };
            window.lunalyrSeek = function (t) { if (ready) { player.mute(); player.seekTo(t, true); } };
            function applyLook() {
              var frame = document.getElementById('frame');
              frame.style.filter = blurPx > 0 ? 'blur(' + blurPx + 'px)' : '';
              frame.style.transform = 'translate(-50%,-50%) scale(' + (overscan * (blurPx > 0 ? 1.04 : 1)) + ')';
              frame.style.width = box ? box[0] + '%' : '';
              frame.style.height = box ? box[1] + '%' : '';
            }
            window.lunalyrBlur = function (px) { blurPx = px; applyLook(); };
            window.lunalyrOverscan = function (scale) { overscan = scale; applyLook(); };
            window.lunalyrBox = function (w, h) { box = (w > 0 && h > 0) ? [w, h] : null; applyLook(); };
            applyLook();
            </script>
            <script src="https://www.youtube.com/iframe_api"></script>
            </body></html>
            """;
    }

    /// <summary>
    /// A video switched to in the same player (<see cref="SwitchScript"/>) is taken as ready this long after it was asked
    /// for, if the player never said it was cued (the Mac's cueWait).
    /// </summary>
    public const double CueWait = 2;

    /// <summary>
    /// Switches the ready player to <paramref name="videoId"/> (the page's lunalyrLoad), starting at
    /// <paramref name="start"/>, numbered <paramref name="page"/>; <paramref name="captions"/> as for <see cref="Html"/>. The
    /// script's result is "true" when it could.
    /// </summary>
    public static string SwitchScript(string videoId, double start, int page, string? captions)
    {
        if (!YouTubeVideoId.IsValid(videoId)) throw new ArgumentException("Not a YouTube video ID.", nameof(videoId));
        var second = Math.Max(0, (int)(double.IsFinite(start) ? start : 0)).ToString(CultureInfo.InvariantCulture);
        var track = captions is null ? "null" : CaptionLanguage(captions) is { } language ? $"'{language}'" : "''";
        return $"!!(window.lunalyrLoad && window.lunalyrLoad('{videoId}', {second}, {page.ToString(CultureInfo.InvariantCulture)}, {track}))";
    }

    /// <summary>The page itself may load only its own address; YouTube's frames inside it load what they need.</summary>
    public static bool Allows(bool mainFrame, Uri? url)
    {
        if (!mainFrame) return true;
        if (url is null) return false;
        return url.Scheme == "about" || (url.Scheme == BaseUrl.Scheme && string.Equals(url.Host, BaseUrl.Host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a web message comes from the page Lunalyr loaded: its main document at its own origin (WebView2 delivers
    /// only top-level messages, with the document's address as their source).
    /// </summary>
    public static bool Trusts(bool mainFrame, string scheme, string host) =>
        mainFrame && string.Equals(scheme, BaseUrl.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(host, BaseUrl.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>The IFrame API's player state from <c>window.lunalyrSample()</c>'s JSON: 1 playing, 3 buffering, -1 unstarted.</summary>
    public static (MusicVideoDrift.Sample Sample, double? Duration)? Sample(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("time", out var timeValue)
                || timeValue.ValueKind != JsonValueKind.Number || !double.IsFinite(timeValue.GetDouble())) return null;
            var state = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : -1;
            double? duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                && d.GetDouble() is var length && length > 0 && double.IsFinite(length) ? length : null;
            return (new MusicVideoDrift.Sample(timeValue.GetDouble(), state == 1, state == 3), duration);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The YouTube page's life, apart from WebView2 so it can be tested: which page is current, whether its player answered in
/// time, which messages to believe, and how often a crashed page is reloaded.
/// </summary>
public sealed class YouTubeEmbedSession
{
    /// <summary>The player has this long to say it is ready; otherwise YouTube is taken as unreachable for now.</summary>
    public const double DefaultReadyTimeout = 12;

    /// <summary>Reloads after the web content process quits, per video.</summary>
    public const int DefaultReloadLimit = 2;

    /// <summary>A ready player that should play but has not played for this long is stuck: the wallpaper falls back.</summary>
    public const double PlayTimeout = 20;

    public enum MessageKind { Ready, Error, State, Other }

    public readonly record struct Message(MessageKind Kind, int Value, string? Video, int? Page)
    {
        /// <summary>A web message's JSON (<c>{type, value, video, page}</c>), or null when it is not one.</summary>
        public static Message? Parse(string? json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String) return null;
                var value = root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : (int?)null;
                var kind = type.GetString() switch
                {
                    "ready" => MessageKind.Ready,
                    "error" => MessageKind.Error,
                    "state" => MessageKind.State,
                    _ => MessageKind.Other,
                };
                var video = root.TryGetProperty("video", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                int? page = root.TryGetProperty("page", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pn) ? pn : null;
                return new Message(kind, value ?? (kind == MessageKind.State ? -1 : 0), video, page);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// <see cref="EffectKind.Load"/>: a new page; <see cref="EffectKind.Switch"/>: the next video inside the page's ready player
    /// (the Mac's YouTubePlayerReuse, e7e50f7: 0.8–1.2 s to a first frame against 2.0–2.5 s for a new page).
    /// </summary>
    public enum EffectKind { None, Ready, Load, Fail, Visible, Switch }

    /// <summary>What the host does next.</summary>
    public readonly record struct Effect(EffectKind Kind, string? VideoId = null, int Page = 0,
        MusicVideoPlaybackFailure Failure = MusicVideoPlaybackFailure.Transient, bool Visible = false)
    {
        public static Effect None => new(EffectKind.None);
        public static Effect Ready => new(EffectKind.Ready);
        public static Effect Load(string videoId, int page) => new(EffectKind.Load, videoId, page);
        public static Effect Switch(string videoId, int page) => new(EffectKind.Switch, videoId, page);
        public static Effect Fail(string videoId, MusicVideoPlaybackFailure failure) => new(EffectKind.Fail, videoId, Failure: failure);
        public static Effect Show(bool visible) => new(EffectKind.Visible, Visible: visible);
    }

    private readonly double _readyTimeout;
    private readonly int _reloadLimit;
    private readonly Func<double, CancellationToken, Task> _sleep;
    private bool _failed;
    private double? _notPlayingSince;
    private CancellationTokenSource? _timeout;

    public YouTubeEmbedSession(double readyTimeout = DefaultReadyTimeout, int reloadLimit = DefaultReloadLimit,
        Func<double, CancellationToken, Task>? sleep = null)
    {
        _readyTimeout = readyTimeout;
        _reloadLimit = reloadLimit;
        _sleep = sleep ?? ((seconds, ct) => Task.Delay(TimeSpan.FromSeconds(seconds), ct));
    }

    public string? VideoId { get; private set; }
    public int Page { get; private set; }
    public bool Ready { get; private set; }

    /// <summary>
    /// The page has a working player (ready, not failed): its window can be shown beneath the wallpaper, which covers it
    /// except while it plays (<see cref="YouTubeEmbed.ShowsVideo(int)"/>).
    /// </summary>
    public bool HasPlayer => Ready && !_failed;
    public int Reloads { get; private set; }

    /// <summary>Effects that arrive on their own (the ready timeout), on a pool thread.</summary>
    public Action<Effect>? OnEffect { get; set; }

    /// <summary>
    /// A new video, with its own reload allowance: switched to inside the page's player when that is up and well (the Mac's
    /// YouTubePlayerReuse.load), else a fresh page.
    /// </summary>
    public Effect Attach(string videoId)
    {
        Reloads = 0;
        var reuse = YouTubePlayerReuse.LoadFor(hasView: VideoId is not null, pageReady: HasPlayer) == YouTubePlayerReuse.Load.SwitchVideo;
        VideoId = videoId;
        return StartPage(reuse);
    }

    /// <summary>The page couldn't switch (its player went away): a fresh page instead.</summary>
    public Effect SwitchRefused() => VideoId is null ? Effect.None : StartPage();

    private Effect StartPage(bool switchVideo = false)
    {
        if (VideoId is not { } videoId) return Effect.None;
        Page++;
        Ready = false;
        _failed = false;
        _notPlayingSince = null;
        _timeout?.Cancel();
        var cts = new CancellationTokenSource();
        _timeout = cts;
        var page = Page;
        _ = WaitAsync(page, cts.Token);
        return switchVideo ? Effect.Switch(videoId, page) : Effect.Load(videoId, page);
    }

    private async Task WaitAsync(int page, CancellationToken ct)
    {
        try
        {
            await _sleep(_readyTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested) return;
        var effect = TimedOut(page);
        if (effect.Kind != EffectKind.None) OnEffect?.Invoke(effect);
    }

    /// <summary>The timeout for <paramref name="page"/> ran out.</summary>
    public Effect TimedOut(int page) => page != Page || Ready ? Effect.None : Fail(MusicVideoPlaybackFailure.Transient);

    /// <summary>A web message. Only the page's own main document, about the current video and page, is listened to.</summary>
    public Effect Receive(Message message, bool trusted)
    {
        if (!trusted || VideoId is not { } videoId || message.Video != videoId || message.Page != Page) return Effect.None;
        switch (message.Kind)
        {
            case MessageKind.Ready:
                if (_failed) return Effect.None;
                Ready = true;
                _timeout?.Cancel();
                return Effect.Ready;
            case MessageKind.Error:
                return YouTubeEmbed.Failure(message.Value) is { } failure ? Fail(failure) : Effect.None;
            case MessageKind.State:
                return _failed ? Effect.Show(false) : Effect.Show(YouTubeEmbed.ShowsVideo(message.Value));
            default:
                return Effect.None;
        }
    }

    /// <summary>
    /// From each sync check of the ready player: whether it should be playing and whether it is. Not playing when it should
    /// for <paramref name="timeout"/> in a row is a passing failure.
    /// </summary>
    public Effect NotePlayback(bool shouldPlay, bool playing, double now, double timeout = PlayTimeout)
    {
        if (!Ready || _failed || VideoId is null || !shouldPlay || playing)
        {
            _notPlayingSince = null;
            return Effect.None;
        }
        if (_notPlayingSince is not { } since)
        {
            _notPlayingSince = now;
            return Effect.None;
        }
        return now - since >= timeout ? Fail(MusicVideoPlaybackFailure.Transient) : Effect.None;
    }

    /// <summary>The page's own navigation failed (offline, say). A load cancelled because the next one replaced it is not a failure.</summary>
    public Effect NavigationFailed(bool cancelled) => cancelled ? Effect.None : Fail(MusicVideoPlaybackFailure.Transient);

    /// <summary>The web content process quit: reloaded a couple of times per video, then given up as a passing failure.</summary>
    public Effect ContentProcessTerminated()
    {
        if (VideoId is null || _failed) return Effect.None;
        if (Reloads >= _reloadLimit) return Fail(MusicVideoPlaybackFailure.Transient);
        Reloads++;
        return StartPage();
    }

    private Effect Fail(MusicVideoPlaybackFailure failure)
    {
        if (VideoId is not { } videoId || _failed) return Effect.None;
        _failed = true;
        Ready = false;
        _timeout?.Cancel();
        return Effect.Fail(videoId, failure);
    }

    /// <summary>The player is closed: nothing is on screen until the next <see cref="Attach"/>.</summary>
    public void Stop()
    {
        _timeout?.Cancel();
        _timeout = null;
        VideoId = null;
        Ready = false;
    }
}

/// <summary>
/// Showing the next YouTube video sooner (the Mac's YouTubePlayerReuse, e7e50f7): a page whose IFrame player is up is told to
/// switch videos (lunalyrLoad, cueVideoById) instead of a new page loading YouTube's script and player again. On Windows one
/// player host serves every song, so the Mac's parking of a layer's page has no counterpart: the page simply stays.
/// </summary>
public static class YouTubePlayerReuse
{
    /// <summary>How the page shows a video.</summary>
    public enum Load
    {
        /// <summary>A new page (HTML, YouTube's script, a new player).</summary>
        NewPage,

        /// <summary>The video switched inside the page's ready player.</summary>
        SwitchVideo,
    }

    /// <summary>
    /// Switch inside the page only when its player is up and well: never a page still loading, one whose player failed, or
    /// one whose web content quit (each of those loads a new page).
    /// </summary>
    public static Load LoadFor(bool hasView, bool pageReady) => hasView && pageReady ? Load.SwitchVideo : Load.NewPage;
}
