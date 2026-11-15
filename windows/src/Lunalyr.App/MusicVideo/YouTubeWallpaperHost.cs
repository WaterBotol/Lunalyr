// YouTube's player for the Music video wallpaper: the Windows counterpart of the Mac's YouTubeMusicVideoCoordinator
// (MusicVideoView.swift, Lunalyr's own code), re-implemented for WebView2. NOT VERIFIED LIVE: the development rules forbid
// loading YouTube, so this has only been compiled; its decisions (YouTubeEmbedSession, MusicVideoSyncLoop,
// MusicVideoIdlePlayer, YouTubeEmbed) are the tested Core types.
//
// - Where: a disabled WS_CHILD window under Progman, the size of the main display's wallpaper surface and just beneath it
//   in z-order (above Windows' own wallpaper). The surface draws the "musicVideo.youtube" layer, which is see-through
//   while the player is uncovered, so the video shows through it under the lyrics. Disabled: it takes no clicks or keys.
// - Framing and hiding, the Mac's (the owner's decision, 14 Nov 2026, replacing milestone 6's player "as YouTube draws
//   it"): filled, the page draws the player 1.25x larger, so YouTube's title bar and logo fall off-screen; shown whole
//   (musicVideoFraming "whole"), the WebView2 is only the visible picture's size, centred (never larger than the screen,
//   MusicVideoFramings.VisiblePixels), and the page sizes the player inside it (YouTubeEmbed.Box) from the video's own
//   shape (oEmbed, AppController.MusicVideoAspect). The player is uncovered only while it plays (YouTubePlayerReveal):
//   paused, buffering or ended, the wallpaper's layer covers it with the video's still; shown whole, each start, resume
//   and seek stays covered for 5 s while YouTube's title overlay fades. Covering is done by the wallpaper's layer, not by
//   hiding the WebView2, so the page keeps playing and rendering behind it. After ten minutes not playing, the player is
//   closed and the still shows.
// - Silence does not rest on YouTube: the browser runs with Chromium's --mute-audio, the WebView2 is muted
//   (CoreWebView2.IsMuted) before anything is loaded, and the page mutes the player again before every play, seek and
//   sample. The profile is InPrivate (no cookies kept), the page is served from a virtual https host, it may not navigate
//   anywhere else, opens no windows and is granted no permission.
// - Only the documented IFrame Player API is used (YouTubeEmbed.Html). Nothing here searches YouTube.

using System.Runtime.InteropServices;
using System.Text;
using Lunalyr.Core.Logging;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Render;
using Lunalyr.Render.Pacing;
using Lunalyr.Shell;
using Lunalyr.Shell.MusicVideo;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Wallpaper;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace Lunalyr.App.MusicVideo;

internal sealed partial class YouTubeWallpaperHost : IMusicVideoPlaybackSurface, IMusicVideoIdleHost, IDisposable
{
    private const string WindowClass = "LunalyrYouTubeHost";

    private readonly DispatcherQueue _ui;
    private readonly AppController _app;
    private readonly MusicVideoController _video;
    private readonly MusicVideoFeed _feed;
    private readonly RenderEngine _engine;
    private readonly string _userData;
    private readonly Func<AppSettings> _settings;
    private readonly CategoryLogger _log;
    private readonly YouTubeEmbedSession _session = new();
    private readonly MusicVideoIdlePlayer _idle;
    private readonly MusicVideoSyncLoop _loop;

    private nint _hwnd;
    private nint _surface;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private bool _opening;
    private string? _videoId;
    private string? _pageVideo;
    private int _pageNumber;
    private double _pageStart;
    private MusicVideoHold _hold = MusicVideoHold.Other;
    private DispatcherQueueTimer? _placement;
    private bool _disposed;
    private readonly YouTubePlayerReveal _reveal;
    /// <summary>What the page and the WebView2 were last given: framing, box, blur, bounds (null: nothing yet).</summary>
    private (MusicVideoFraming Framing, YouTubeEmbed.Box? Box, double Blur, Windows.Foundation.Rect Bounds)? _look;

    public YouTubeWallpaperHost(DispatcherQueue ui, AppController app, MusicVideoController video, MusicVideoFeed feed, RenderEngine engine,
        string userDataFolder, Func<AppSettings> settings, Logger log)
    {
        _ui = ui;
        _app = app;
        _video = video;
        _feed = feed;
        _engine = engine;
        _userData = userDataFolder;
        _settings = settings;
        _log = log.App;
        _idle = new MusicVideoIdlePlayer(schedule: (delay, work) =>
        {
            var timer = ui.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(delay);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => work();
            timer.Start();
            return timer.Stop;
        }) { Host = this };
        _loop = new MusicVideoSyncLoop(() => _app.ReadSongPosition().Time, () => _app.ReadSongPosition().Playing, () => _video.CurrentOffset,
            () => Environment.TickCount64 / 1000.0, Schedule)
        {
            Surface = this,
            AlignDragging = () => _video.IsAlignDragging,
        };
        _reveal = new YouTubePlayerReveal((delay, work) =>
        {
            var timer = ui.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(delay);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                work();
            };
            timer.Start();
            return timer.Stop;
        }) { OnShow = Uncover };
        _session.OnEffect = effect => _ui.TryEnqueue(() => Perform(effect));
        video.Changed += () => _ui.TryEnqueue(Follow);
        // Reduce Motion (Animation effects) turning on or off; the video's shape arriving (oEmbed).
        app.StatusChanged += (_, _) => _ui.TryEnqueue(() =>
        {
            Follow();
            ApplyLook();
            // Playing again wakes a resting loop too, so a player closed during a long pause comes back.
            FollowPause();
        });
        // The loop rests once a paused, covered or hidden wallpaper has paused the video; these wake it again. Power
        // saving arrives the same way, and may draw another type instead of the video.
        engine.StatusChanged += (_, _) => _ui.TryEnqueue(() =>
        {
            Follow();
            FollowPause();
        });
        _ui.TryEnqueue(Follow);
    }

    private IDisposable Schedule(double interval, Action tick)
    {
        var timer = _ui.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(interval);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => tick();
        timer.Start();
        return new TimerHandle(timer);
    }

    private sealed class TimerHandle(DispatcherQueueTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }

    // ---- Following the controller (UI thread) ----

    /// <summary>Opens the player for the YouTube video on screen, or closes it when there is none.</summary>
    private void Follow()
    {
        if (_disposed) return;
        // The type drawn, not the one chosen: power saving can show Line patterns or Colour only instead of the video. Under
        // Reduce Motion (Animation effects off) no player is opened: the wallpaper shows the video's still, as on the Mac.
        var wanted = WallpaperPower.EffectiveMode(_settings().BackgroundMode, _app.Power) == "musicVideo" && !_app.Scene.ReduceMotion
            ? _video.ShowingYouTubeId : null;
        if (wanted == _videoId) return;
        if (wanted is null)
        {
            Close();
            return;
        }
        if (wanted != _videoId) _idle.Reset();
        _videoId = wanted;
        _loop.IsCurrent = () => _video.IsShowing(new MusicVideoSource.YouTube(wanted));
        _loop.OnDuration = length => _video.NoteVideoDuration(length, new MusicVideoSource.YouTube(wanted));
        _loop.OnSample = (sample, _, at) => _video.NoteVideoSample(sample.VideoTime, sample.VideoPlaying && !sample.Buffering,
            new MusicVideoSource.YouTube(wanted), at);
        _ = OpenAsync(wanted);
    }

    private async Task OpenAsync(string videoId)
    {
        try
        {
            if (!EnsureWindow()) return;
            if (_controller is null && !_opening)
            {
                _opening = true;
                var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--mute-audio" };
                _environment ??= await CoreWebView2Environment.CreateWithOptionsAsync("", _userData, options);
                var controllerOptions = _environment.CreateCoreWebView2ControllerOptions();
                controllerOptions.IsInPrivateModeEnabled = true;
                var controller = await _environment.CreateCoreWebView2ControllerAsync(
                    CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_hwnd), controllerOptions);
                _opening = false;
                if (_disposed || _videoId is null)
                {
                    controller.Close();
                    return;
                }
                Configure(controller);
                _controller = controller;
            }
            if (_controller is null) return;
            // A song change while WebView2 was being made: the video wanted now, not the one asked for then.
            if (_videoId is not { } current) return;
            Perform(_session.Attach(current));
            _loop.Start();
            _placement ??= Repeat(2, () =>
            {
                Place();
                FollowPause();
            });
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or FileNotFoundException)
        {
            _opening = false;
            _log.Warn("Music video: YouTube's player (WebView2) couldn't be started.", ex);
            _video.ReportPlaybackFailure(new MusicVideoSource.YouTube(videoId), MusicVideoPlaybackFailure.Transient);
        }
    }

    private void Configure(CoreWebView2Controller controller)
    {
        // Held for as long as the controller lives: the event handlers below are registered through this projection
        // object, and if it were collected its handlers would go with it while WebView2 still calls them (a crash to
        // address 0 inside WebView2's window procedure, seen live on 13 Nov 2026).
        var core = _core = controller.CoreWebView2;
        // Muted before anything loads: the video must never be heard.
        core.IsMuted = true;
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreHostObjectsAllowed = false;
        controller.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
        controller.IsVisible = false;
        Resize(controller);
        core.AddWebResourceRequestedFilter($"{YouTubeEmbed.BaseUrl.GetLeftPart(UriPartial.Authority)}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => Serve(e);
        core.NavigationStarting += (_, e) =>
        {
            if (!YouTubeEmbed.Allows(true, Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri : null)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.WebMessageReceived += (_, e) => Receive(e);
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) Perform(_session.NavigationFailed(e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled));
        };
        core.ProcessFailed += (_, _) => Perform(_session.ContentProcessTerminated());
    }

    /// <summary>The page itself, from memory, for the current video and page; nothing else on the virtual host exists.</summary>
    private void Serve(CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_environment is null) return;
        var uri = Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var parsed) ? parsed : null;
        if (uri?.AbsolutePath == "/player.html" && _pageVideo is { } video && YouTubeVideoId.IsValid(video))
        {
            var (framing, box, _) = Look();
            var html = Encoding.UTF8.GetBytes(YouTubeEmbed.Html(video, _pageStart, _pageNumber, overscan: framing.YouTubeOverscan(), box: box));
            e.Response = _environment.CreateWebResourceResponse(Stream(html), 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: strict-origin-when-cross-origin");
            return;
        }
        e.Response = _environment.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    private static IRandomAccessStream Stream(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }
        stream.Seek(0);
        return stream;
    }

    private void Receive(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (YouTubeEmbedSession.Message.Parse(e.WebMessageAsJson) is not { } message) return;
        var source = Uri.TryCreate(e.Source, UriKind.Absolute, out var uri) ? uri : null;
        var trusted = source is not null && YouTubeEmbed.Trusts(true, source.Scheme, source.Host);
        var buffering = message is { Kind: YouTubeEmbedSession.MessageKind.State, Value: 3 };
        Perform(_session.Receive(message, trusted), buffering);
    }

    /// <param name="buffering">The effect answers a buffering state (3) message.</param>
    private void Perform(YouTubeEmbedSession.Effect effect, bool buffering = false)
    {
        if (_disposed) return;
        switch (effect.Kind)
        {
            case YouTubeEmbedSession.EffectKind.Ready:
                // The page's window shows beneath the wallpaper from now on; the wallpaper uncovers it once it plays.
                _look = null;
                ApplyLook();
                if (_controller is not null) _controller.IsVisible = _session.HasPlayer;
                _loop.Tick();
                break;
            case YouTubeEmbedSession.EffectKind.Load when _controller is not null:
                _reveal.Reset();
                Uncover(false);
                _controller.IsVisible = false;
                _pageVideo = effect.VideoId;
                _pageNumber = effect.Page;
                _pageStart = MusicVideoTiming.VideoTime(_app.ReadSongPosition().Time, _video.CurrentOffset, null) ?? 0;
                _look = null;
                ApplyLook();
                _controller.CoreWebView2.Navigate(YouTubeEmbed.PageUrl(effect.Page).AbsoluteUri);
                break;
            case YouTubeEmbedSession.EffectKind.Visible:
                _reveal.Set(effect.Visible, buffering);
                break;
            case YouTubeEmbedSession.EffectKind.Fail when effect.VideoId is { } id:
                _reveal.Set(false);
                if (_controller is not null) _controller.IsVisible = false;
                _idle.Reset();
                Run("window.lunalyrPause && window.lunalyrPause()");
                _log.Info($"Music video: YouTube's player couldn't show the video ({effect.Failure}).");
                _video.ReportPlaybackFailure(new MusicVideoSource.YouTube(id), effect.Failure);
                break;
        }
    }

    /// <summary>
    /// Uncovers the player (the wallpaper's layer turns see-through over it) or covers it again with the video's still
    /// (<see cref="YouTubePlayerReveal"/> decides when). The WebView2 itself stays shown from ready, so it keeps playing.
    /// </summary>
    private void Uncover(bool visible)
    {
        // A sample answered after the player closed must not leave the wallpaper see-through over nothing.
        visible &= _controller is not null && _session.HasPlayer;
        if (_feed.PlayerVisible == visible) return;
        _feed.PlayerVisible = visible;
        // A wallpaper frozen by "Pause wallpaper" (or resting) still redraws once, so it covers a player that stopped.
        _engine.RequestFrame();
    }

    /// <summary>The framing chosen, the player's box in the page (whole only) and the WebView2's bounds in the host window.</summary>
    private (MusicVideoFraming Framing, YouTubeEmbed.Box? Box, Windows.Foundation.Rect Bounds) Look()
    {
        var framing = _settings().YouTubeFraming;
        var width = 0;
        var height = 0;
        if (_hwnd != 0 && GetClientRect(_hwnd, out var client))
        {
            width = client.Right - client.Left;
            height = client.Bottom - client.Top;
        }
        if (framing != MusicVideoFraming.Whole || width <= 0 || height <= 0)
            return (framing, null, new Windows.Foundation.Rect(0, 0, Math.Max(0, width), Math.Max(0, height)));
        // Shown whole: never larger than the screen; the page draws the 16:9 player at the box inside it, so YouTube's own
        // bars for a video of another shape fall outside.
        var aspect = _app.MusicVideoAspect ?? MusicVideoFramings.YouTubeAspect;
        var (x, y, w, h) = MusicVideoFramings.VisiblePixels(aspect, width, height);
        var layout = MusicVideoFramings.WholeLayoutFor(aspect, new VideoSize(width, height));
        return (framing, layout.Box, new Windows.Foundation.Rect(x, y, w, h));
    }

    /// <summary>Gives the page and the WebView2 the framing, box, blur and bounds wanted now, when they changed.</summary>
    private void ApplyLook()
    {
        if (_disposed || _controller is null) return;
        var (framing, box, bounds) = Look();
        var blur = Math.Max(0, _settings().MusicVideoBlur);
        _reveal.Framing = framing;
        var last = _look;
        if (last is { } was && was.Framing == framing && Equals(was.Box, box) && was.Blur == blur && was.Bounds == bounds) return;
        _look = (framing, box, blur, bounds);
        _controller.Bounds = bounds;
        if (!_session.Ready) return; // a loading page gets its framing and box in its HTML; the blur once it is ready
        Run(YouTubeEmbed.OverscanScript(framing.YouTubeOverscan()));
        Run(YouTubeEmbed.BoxScript(box));
        Run($"window.lunalyrBlur && window.lunalyrBlur({blur.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    private void Run(string script)
    {
        try
        {
            _ = _controller?.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (COMException)
        {
            // The page went away; the session hears of it.
        }
    }

    // ---- IMusicVideoPlaybackSurface ----

    public double SeekLead => MusicVideoSeekLatency.Initial;

    public MusicVideoCorrectionStyle CorrectionStyle => MusicVideoCorrectionStyle.LedSeek;

    public void NoteHold(MusicVideoHold hold)
    {
        // The wallpaper paused, covered or hidden pauses the video too (covered by its still once it stops playing).
        _loop.SetSurfacePaused(SurfacePaused());
        _hold = hold;
        _reveal.Hold = hold;
        _idle.NoteHold(hold);
    }

    /// <summary>Tells the sync loop whether the wallpaper is paused, covered or hidden (from outside the loop, which may rest).</summary>
    private void FollowPause()
    {
        // Not only while a player is open: after a long pause the idle player closes it, and the loop (resting behind a
        // covered or hidden wallpaper) is what reopens it when the song plays again. Waiting for a player here left the
        // video gone for good once both had happened (seen live on the Yoga, 15 Nov 2026: Apple Music paused for over
        // ten minutes, the screen off, then playing again showed only the still).
        if (_disposed || _videoId is null) return;
        var paused = SurfacePaused();
        _loop.SetSurfacePaused(paused);
        if (!paused && !_loop.IsRunning) _loop.Start();
    }

    private bool SurfacePaused()
    {
        if (_settings().WallpaperPaused) return true;
        var surfaces = _engine.Surfaces;
        return surfaces.Count > 0 && surfaces.All(s => s.State != SurfaceState.Drawing);
    }

    public async void Sample(Action<MusicVideoDrift.Sample?, double?> done)
    {
        if (!_session.Ready || _controller is null)
        {
            done(null, null);
            return;
        }
        string json;
        try
        {
            json = await _controller.CoreWebView2.ExecuteScriptAsync("window.lunalyrSample && window.lunalyrSample()");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // The page or the WebView2 went away mid-call (async void: nothing may escape).
            done(null, null);
            return;
        }
        if (_disposed || _controller is null)
        {
            done(null, null);
            return;
        }
        if (YouTubeEmbed.Sample(json) is not { } read)
        {
            done(null, null);
            return;
        }
        // Also from each sample, in case a state message was missed (through the reveal's hold, like the messages).
        var showing = YouTubeEmbed.ShowsVideo(read.Sample);
        _reveal.Set(_session.Ready && showing, read.Sample.Buffering);
        var stuck = _session.NotePlayback(_hold == MusicVideoHold.Live, showing, Environment.TickCount64 / 1000.0);
        if (stuck.Kind != YouTubeEmbedSession.EffectKind.None)
        {
            Perform(stuck);
            done(null, null);
            return;
        }
        done(read.Sample, read.Duration);
    }

    public void PlayVideo() => Run("window.lunalyrPlay()");

    /// <summary>Just pauses: the paused player is covered by its still (there is no frozen frame on Windows).</summary>
    public void PauseVideo() => _idle.Pause();

    public void SeekVideo(double seconds)
    {
        Run($"window.lunalyrSeek({seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})");
    }

    // ---- IMusicVideoIdleHost ----

    public void SendPause() => Run("window.lunalyrPause()");

    public void ReleasePlayer()
    {
        _session.Stop();
        Run("window.lunalyrPause && window.lunalyrPause()");
        CloseWebView();
    }

    public void RecreatePlayer()
    {
        if (_videoId is { } id) _ = OpenAsync(id);
    }

    // ---- The window under the wallpaper ----

    private bool EnsureWindow()
    {
        if (_hwnd != 0) return true;
        var progman = FindWindowW("Progman", null);
        if (progman == 0) return false;
        Register();
        _hwnd = CreateWindowExW(0x08000000 /* WS_EX_NOACTIVATE */, WindowClass, "Lunalyr music video",
            0x40000000 | 0x08000000 /* WS_CHILD | WS_DISABLED */, 0, 0, 1, 1, progman, 0, GetModuleHandleW(null), 0);
        if (_hwnd == 0)
        {
            _log.Warn($"Music video: the YouTube player's window couldn't be made (error {Marshal.GetLastPInvokeError()}).");
            return false;
        }
        Place();
        return true;
    }

    /// <summary>The size of the main display's wallpaper surface, just beneath it; shown only while that surface exists.</summary>
    private void Place()
    {
        if (_hwnd == 0) return;
        var progman = FindWindowW("Progman", null);
        _surface = MainSurface(progman);
        if (_surface == 0 || !GetWindowRect(_surface, out var rect))
        {
            ShowWindow(_hwnd, 0);
            return;
        }
        var origin = new Point { X = rect.Left, Y = rect.Top };
        ScreenToClient(progman, ref origin);
        SetWindowPos(_hwnd, _surface, origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x0010 | 0x0040 /* NOACTIVATE | SHOWWINDOW */);
        // Also every two seconds: a framing, blur or display size changed in the meantime.
        ApplyLook();
    }

    private void Resize(CoreWebView2Controller controller) => controller.Bounds = Look().Bounds;

    /// <summary>This process's wallpaper surface on the main display (the one at the desktop's origin), under Progman.</summary>
    private static unsafe nint MainSurface(nint progman)
    {
        var pid = (uint)Environment.ProcessId;
        nint found = 0;
        var child = FindWindowExW(progman, 0, null, null);
        var name = stackalloc char[64];
        while (child != 0)
        {
            GetWindowThreadProcessId(child, out var owner);
            var length = GetClassNameW(child, name, 64);
            if (owner == pid && new string(name, 0, length).StartsWith("LunalyrWallpaper_", StringComparison.Ordinal)
                && GetWindowRect(child, out var rect) && rect.Left <= 0 && rect.Top <= 0 && rect.Right > 0 && rect.Bottom > 0)
            {
                found = child;
                break;
            }
            child = FindWindowExW(progman, child, null, null);
        }
        return found;
    }

    private DispatcherQueueTimer Repeat(double seconds, Action work)
    {
        var timer = _ui.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(seconds);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => work();
        timer.Start();
        return timer;
    }

    // ---- Closing ----

    private void Close()
    {
        _loop.Stop();
        _session.Stop();
        _idle.Reset();
        _videoId = null;
        CloseWebView();
        _placement?.Stop();
        _placement = null;
        if (_hwnd != 0) ShowWindow(_hwnd, 0);
    }

    private void CloseWebView()
    {
        _reveal.Reset();
        if (_feed.PlayerVisible)
        {
            _feed.PlayerVisible = false;
            _engine.RequestFrame();
        }
        _look = null;
        _controller?.Close();
        _controller = null;
        _core = null; // only once the controller is closed: no more events can come
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        void Teardown()
        {
            Close();
            if (_hwnd != 0) DestroyWindow(_hwnd);
            _hwnd = 0;
        }
        if (_ui.HasThreadAccess) Teardown();
        else _ui.TryEnqueue(Teardown);
    }

    // ---- Win32 ----

    private static bool s_registered;

    private static unsafe void Register()
    {
        if (s_registered) return;
        fixed (char* name = WindowClass)
        {
            var wc = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx), Proc = &WindowProc, Instance = GetModuleHandleW(null), ClassName = name,
            };
            RegisterClassExW(&wc);
        }
        s_registered = true;
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        // Never activated, never takes the mouse (WM_MOUSEACTIVATE: MA_NOACTIVATEANDEAT).
        if (message == 0x0021) return 4;
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> Proc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string? className, string? windowName);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint after, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(nint hwnd, char* name, int max);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint hwnd, ref Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width,
        int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static unsafe partial ushort RegisterClassExW(WndClassEx* windowClass);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
}
