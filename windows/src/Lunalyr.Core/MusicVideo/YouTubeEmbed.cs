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
// - The Mac draws the player at 1.25x and clips it, hides it whenever it is not playing, and lays a frozen frame over a
//   paused one, to keep YouTube's own interface off the wallpaper. YouTube's API policies don't allow obscuring, cropping
//   or covering the embedded player's interface, so here the player fills the page as YouTube draws it, fitted
//   (letterboxed) without cropping, and stays on screen in every state once it is ready.
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
    /// Whether the player is on screen: from the moment it is ready, in every state (playing, paused, buffering, ended), as
    /// YouTube draws it; never before it is ready or after it failed (there is no player to show then).
    /// </summary>
    public static bool ShowsPlayer(bool ready, bool failed) => ready && !failed;

    /// <summary>A sync sample of a video that is playing, not buffering (for the stuck-player check).</summary>
    public static bool IsPlaying(MusicVideoDrift.Sample sample) => sample.VideoPlaying && !sample.Buffering;

    /// <summary>The interface language for the player's own text: Windows' (two or three ASCII letters, else "en").</summary>
    public static string PlayerLanguage(CultureInfo? culture = null)
    {
        var code = (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName.ToLowerInvariant();
        return code.Length is 2 or 3 && code.All(char.IsAsciiLetter) ? code : "en";
    }

    /// <summary>The embed's address: youtube-nocookie's privacy-enhanced player with only documented parameters.</summary>
    public static string EmbedSource(string videoId, double start, string language)
    {
        if (!YouTubeVideoId.IsValid(videoId)) throw new ArgumentException("Not a YouTube video ID.", nameof(videoId));
        var second = Math.Max(0, (int)(double.IsFinite(start) ? start : 0));
        language = SafeLanguage(language);
        return $"https://www.youtube-nocookie.com/embed/{videoId}?enablejsapi=1&autoplay=0&mute=1&controls=0&disablekb=1&fs=0"
            + $"&iv_load_policy=3&rel=0&playsinline=1&loop=0&cc_load_policy=0&hl={language}&start={second}"
            + $"&origin={Uri.EscapeDataString(BaseUrl.GetLeftPart(UriPartial.Authority))}";
    }

    private static string SafeLanguage(string language) =>
        language.Length > 0 && language.All(char.IsAsciiLetter) ? language : "en";

    public static string Html(string videoId, double start, int page = 0, string? language = null)
    {
        var source = EmbedSource(videoId, start, language ?? PlayerLanguage());
        var pageText = page.ToString(CultureInfo.InvariantCulture);
        return $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <meta name="referrer" content="strict-origin-when-cross-origin">
            <style>
            html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#000}
            #frame{position:absolute;left:0;top:0;width:100%;height:100%;pointer-events:none}
            #frame iframe{width:100%;height:100%;border:0;pointer-events:none}
            </style></head><body><div id="frame"><iframe id="player" src="{{source}}" title="Music video"
            allow="autoplay; encrypted-media" referrerpolicy="strict-origin-when-cross-origin"></iframe></div>
            <script>
            var player = null, ready = false;
            function post(type, value) {
              try { window.chrome.webview.postMessage({type: type, value: value, video: '{{videoId}}', page: {{pageText}}}); } catch (e) {}
            }
            function hideCaptions() {
              if (!player) return;
              try { player.unloadModule('captions'); } catch (e) {}
              try { player.unloadModule('cc'); } catch (e) {}
              try { player.setOption('captions', 'track', {}); } catch (e) {}
            }
            function onYouTubeIframeAPIReady() {
              player = new YT.Player('player', {
                events: {
                  onReady: function (event) { ready = true; event.target.mute(); hideCaptions(); post('ready', 0); },
                  onError: function (event) { post('error', event.data); },
                  onApiChange: function () { hideCaptions(); },
                  onStateChange: function (event) {
                    var state = event.data;
                    if (state === 1 || state === 3) { player.mute(); }
                    if (state === -1 || state === 1 || state === 5) { hideCaptions(); }
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
            window.lunalyrPlay = function () { if (ready) { player.mute(); player.playVideo(); } };
            window.lunalyrPause = function () { if (ready) player.pauseVideo(); };
            window.lunalyrSeek = function (t) { if (ready) { player.mute(); player.seekTo(t, true); } };
            window.lunalyrBlur = function (px) {
              var frame = document.getElementById('frame');
              frame.style.filter = px > 0 ? 'blur(' + px + 'px)' : '';
            };
            </script>
            <script src="https://www.youtube.com/iframe_api"></script>
            </body></html>
            """;
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

    public enum EffectKind { None, Ready, Load, Fail, Visible }

    /// <summary>What the host does next.</summary>
    public readonly record struct Effect(EffectKind Kind, string? VideoId = null, int Page = 0,
        MusicVideoPlaybackFailure Failure = MusicVideoPlaybackFailure.Transient, bool Visible = false)
    {
        public static Effect None => new(EffectKind.None);
        public static Effect Ready => new(EffectKind.Ready);
        public static Effect Load(string videoId, int page) => new(EffectKind.Load, videoId, page);
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

    /// <summary>Whether the player is on screen now (<see cref="YouTubeEmbed.ShowsPlayer"/>).</summary>
    public bool ShowsPlayer => YouTubeEmbed.ShowsPlayer(Ready, _failed);
    public int Reloads { get; private set; }

    /// <summary>Effects that arrive on their own (the ready timeout), on a pool thread.</summary>
    public Action<Effect>? OnEffect { get; set; }

    /// <summary>A new video: a fresh page, with its own reload allowance.</summary>
    public Effect Attach(string videoId)
    {
        Reloads = 0;
        VideoId = videoId;
        return StartPage();
    }

    private Effect StartPage()
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
        return Effect.Load(videoId, page);
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
                return Effect.Show(ShowsPlayer);
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
