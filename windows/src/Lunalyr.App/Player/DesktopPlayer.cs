using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Player;
using Lunalyr.Render;
using Lunalyr.Shell;
using Lunalyr.Shell.Player;
using Lunalyr.Shell.Settings;

namespace Lunalyr.App.Player;

/// <summary>
/// The desktop player, "Sleeve": an album-coloured card that comes up in the middle of the display where the pointer
/// rests on the empty desktop. It reads the song from the controller, drags the wallpaper's clock with its seek bar,
/// and sends a command to the player only for a click or key on it, through <see cref="PlayerCommands"/>.
/// </summary>
/// <remarks>
/// Everything runs on the UI thread (the tray's thread, under either message loop): a Win32 timer polls the pointer
/// (<see cref="HoverPolicy"/> intervals), another drives the card's frames while it shows. See docs/APP-SHELL.md,
/// "Desktop player".
/// </remarks>
internal sealed unsafe partial class DesktopPlayer : IDisposable
{
    private const nuint PollTimer = 1, FrameTimer = 2, CoverTimer = 3;

    private static DesktopPlayer? s_instance;

    private readonly Func<AppController?> _controller;
    private readonly Func<RenderEngine?> _engine;
    private readonly PlayerCommands _commands;
    private readonly Func<AppSettings> _settings;
    private readonly Action<Func<AppSettings, AppSettings>> _updateSettings;
    private readonly Action<Action> _post;
    private readonly IReadOnlyList<string> _debug;
    private readonly CategoryLogger _log;
    private readonly Logger _logger;
    private readonly CardSurface _surface;
    private readonly CardPainter _painter = new();
    private readonly CardModel _model = new();
    private readonly PlayerInteraction _interaction = new();
    private readonly ScrubPreview _preview = new();
    private readonly ScrubController _scrub;
    private readonly DesktopHoverSession _hover = new();
    private readonly HoverIntent _intent = new();
    private readonly List<HoverDisplay> _displays = [];
    private readonly Dictionary<long, (PlayerNative.RECT Bounds, PlayerNative.RECT Work, double Scale)> _monitors = [];
    private long _primary;
    private readonly double _debugScrub = double.NaN;
    private readonly bool _debugShow;
    private readonly bool _debugTop;
    private PlayerAutomation? _automation;
    private SearchField? _field;

    // Hover poll.
    private uint _pollMs;
    private PlayerNative.POINT _lastPoint = new() { X = int.MinValue };
    private bool _lastHeld;
    private double _lastMotion;
    private double _displaysRead = double.NegativeInfinity;
    private (nint Root, bool Exposed, double At) _lastHit;

    // The card.
    private long? _shownOn;
    private double _scale = 1;
    private PlayerNative.RECT _work;
    private int _cardX, _cardY, _cardW, _cardH;
    private bool _visible;
    private Tween _reveal = new(0), _rise = new(0), _record = new(1), _thickness = new(4), _active = new(0), _capsules = new(0),
        _titles = new(1), _note = new(0);
    private double _noteUntil = double.NegativeInfinity;
    private uint _frameMs;
    private double _automationAt = double.NegativeInfinity;
    private bool _dirty = true;
    private int _drawnVersion = -1;

    // The song.
    private PlayerNow? _now;
    private double _nowReadAt = double.NegativeInfinity;
    private object? _contextLyrics;
    private (double Offset, IReadOnlyDictionary<int, string>? Readings, RomanizationDisplay Display, string? Track, double Duration, bool CanSeek) _contextKey;
    private CoverArt? _paletteCover;
    private bool _paletteNeutral;
    private ArtworkPalette _palette = ArtworkPalette.Neutral;
    private OptimisticToggle _toggle;
    private bool _lastPlaying;

    // Input.
    private CardPart _pressed;
    private bool _tracking;
    private bool _coverPending;
    private nint _previousForeground;

    public DesktopPlayer(Func<AppController?> controller, Func<RenderEngine?> engine, PlayerCommands commands, Func<AppSettings> settings,
        Action<Func<AppSettings, AppSettings>> updateSettings, Action<Action> post, IReadOnlyList<string> debug, Logger logger)
    {
        _controller = controller;
        _engine = engine;
        _commands = commands;
        _settings = settings;
        _updateSettings = updateSettings;
        _post = post;
        _debug = debug;
        _logger = logger;
        _log = logger.Player;
        _surface = new CardSurface($"Lunalyr.Player.{Environment.ProcessId}", logger.App);
        _scrub = new ScrubController(0, Seek, () => Elapsed(Now()), Now, _interaction, _preview);
        _preview.ButtonsHeld = () => PlayerNative.ButtonsHeld() || !double.IsNaN(_debugScrub);
        _preview.Changed += PreviewChanged;
        foreach (var flag in debug)
        {
            if (flag == "show") _debugShow = true;
            else if (flag == "top") _debugTop = true;
            else if (flag.StartsWith("scrub=", StringComparison.Ordinal)
                     && double.TryParse(flag.AsSpan(6), NumberStyles.Float, CultureInfo.InvariantCulture, out var share))
                _debugScrub = Math.Clamp(share, 0, 1);
        }
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Creates the (hidden) card window and starts watching the pointer. Call on the UI thread.</summary>
    public void Start()
    {
        if (s_instance is not null) throw new InvalidOperationException("Only one desktop player per process.");
        s_instance = this;
        _surface.CreateWindow(&WndProc);
        _automation = new PlayerAutomation(this, _surface.Hwnd);
        StartSearch();
        _model.ShowsRemaining = _settings().DesktopPlayerTimeMode != "total";
        RefreshLook();
        SchedulePoll(_debugShow ? 100 : (uint)(HoverPolicy.Interval * 1000));
        if (_debug.Count > 0) _log.Info($"Desktop player: debug {string.Join(", ", _debug)}.");
        _log.Info("Desktop player ready: it comes up where the pointer rests in the middle of the desktop.");
    }

    public nint Hwnd => _surface.Hwnd;

    /// <summary>Settings changed (the hover switch, the time label).</summary>
    public void ApplySettings(AppSettings settings)
    {
        var remaining = settings.DesktopPlayerTimeMode != "total";
        if (remaining != _model.ShowsRemaining)
        {
            _model.ShowsRemaining = remaining;
            Invalidate();
        }

        if (!settings.ShowControlsOnHover && _shownOn is not null && !_debugShow) Apply(null, Now());
    }

    public void Dispose()
    {
        if (_surface.Hwnd != 0)
        {
            PlayerNative.KillTimer(_surface.Hwnd, PollTimer);
            PlayerNative.KillTimer(_surface.Hwnd, FrameTimer);
            PlayerNative.KillTimer(_surface.Hwnd, CoverTimer);
        }

        _controller()?.ClearScrubPreview();
        StopSearch();
        _automation?.Dispose();
        _painter.Dispose();
        _surface.Dispose();
        if (ReferenceEquals(s_instance, this)) s_instance = null;
    }

    // ---- Hover ----

    private void SchedulePoll(uint ms)
    {
        if (ms == _pollMs) return;
        _pollMs = ms;
        PlayerNative.SetTimer(_surface.Hwnd, PollTimer, ms, 0);
    }

    private void Poll()
    {
        var now = Now();
        PlayerNative.GetCursorPos(out var point);
        var held = PlayerNative.ButtonsHeld();
        var moved = point.X != _lastPoint.X || point.Y != _lastPoint.Y || held != _lastHeld;
        _lastPoint = point;
        _lastHeld = held;

        // A drag only stands while a button is down: a press whose card went away can never freeze the lyrics.
        if (_scrub.IsDragging && !held && double.IsNaN(_debugScrub))
        {
            _scrub.Cancel();
            Invalidate();
        }

        if (_debugShow)
        {
            ReadDisplays(now, force: false);
            var primary = _displays.Count > 0 ? PrimaryDisplay() : (long?)null;
            if (primary != _shownOn) Apply(primary, now);
            return;
        }

        var shown = _hover.Active is not null || _shownOn is not null;
        if (moved || shown || _intent.IsPending) _lastMotion = now;
        SchedulePoll((uint)(HoverPolicy.PollInterval(now - _lastMotion, shown, _intent.IsPending) * 1000));
        // A resting pointer cannot start a reveal; a shown card still needs its linger timed.
        if (!moved && !shown && !_intent.IsPending) return;
        if (!_settings().ShowControlsOnHover)
        {
            if (_shownOn is not null) Apply(null, now);
            return;
        }

        ReadDisplays(now, force: false);
        var index = DesktopHoverSession.DisplayAt(_displays, point.X, point.Y);
        if (index < 0)
        {
            _hover.Reset();
            _intent.Reset();
            Apply(Pinned(null), now);
            return;
        }

        var display = _displays[index];
        var inZone = display.HotZone.Contains(point.X, point.Y);
        var previous = _hover.Active;
        var potential = (!held && inZone) || previous == display.Id;
        var blocked = potential && !IsExposed(point, moved, now);
        var stepped = _hover.Step(_displays, point.X, point.Y, blocked, held, now);
        // Intent is judged in DIPs, so its speeds mean the same at every scale; and only while a reveal could start.
        var scale = _monitors.TryGetValue(display.Id, out var m) ? m.Scale : 1;
        var intentReady = _intent.Sample(point.X / scale, point.Y / scale, now,
            inHotZone: !held && !blocked && inZone && previous != display.Id);
        if (intentReady && previous is null && BusyFullScreen()) intentReady = false;
        var dragging = _interaction.Dragging && held;
        var holds = previous is not null && PlayerDismissGuard.Holds(dragging, _interaction.SearchFocused, _interaction.LastClick, now, blocked);
        var active = HoverPolicy.Decide(stepped, previous, intentReady, holds);
        if (active is { } a)
        {
            if (_hover.Active != a || (holds && dragging)) _hover.Hold(a, now);
            _intent.Reset();
        }
        else if (_hover.Active is not null)
        {
            _hover.Reset();
        }

        // A typed search keeps the card on the display it was typed on.
        Apply(Pinned(active), now);
    }

    /// <summary>Whether the desktop (or Lunalyr's own card) is under the pointer, not an app's window.</summary>
    private bool IsExposed(PlayerNative.POINT point, bool moved, double now)
    {
        if (!moved && now - _lastHit.At < 1) return _lastHit.Exposed;
        var hit = PlayerNative.WindowFromPoint(point);
        var root = hit == 0 ? 0 : PlayerNative.GetAncestor(hit, PlayerNative.GA_ROOT);
        if (root != 0 && root == _lastHit.Root && now - _lastHit.At < 2) return _lastHit.Exposed;
        bool exposed;
        if (root == 0) exposed = false;
        else if (root == _surface.Hwnd || (_field is not null && root == _field.Hwnd)) exposed = true;
        // The desktop: the wallpaper and its icons (Lunalyr's own wallpaper is a child of Progman). Hovering an icon
        // counts as the desktop, as on the Mac.
        else exposed = PlayerNative.ClassIs(root, "Progman") || PlayerNative.ClassIs(root, "WorkerW");
        _lastHit = (root, exposed, now);
        return exposed;
    }

    /// <summary>A full-screen app, a game or a presentation is in front: no new reveal.</summary>
    private static bool BusyFullScreen()
    {
        // QUNS_BUSY 2, QUNS_RUNNING_D3D_FULL_SCREEN 3, QUNS_PRESENTATION_MODE 4.
        return PlayerNative.SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;
    }

    private void ReadDisplays(double now, bool force)
    {
        if (!force && now - _displaysRead < 2) return;
        _displaysRead = now;
        _displays.Clear();
        _monitors.Clear();
        PlayerNative.EnumDisplayMonitors(0, 0, &CollectMonitor, 0);
        foreach (var (id, info) in _monitors)
        {
            var bounds = new HoverRect(info.Bounds.Left, info.Bounds.Top, info.Bounds.Right, info.Bounds.Bottom);
            var work = new HoverRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
            var s = info.Scale;
            // The hot zone: the resting card's place and a margin round it, in the middle of the work area. The keep
            // zone: wherever the card is now (it can grow later), and a smaller margin.
            var rest = work.Centred(PlayerCard.Width * s, PlayerCard.RestHeight * s);
            var hot = rest.Inflated(56 * s, 40 * s);
            var keep = work.Centred(PlayerCard.Width * s, _cardHeightDip * s).Inflated(28 * s, 28 * s);
            _displays.Add(new HoverDisplay(id, bounds, hot, keep));
        }
    }

    [UnmanagedCallersOnly]
    private static int CollectMonitor(nint monitor, nint hdc, PlayerNative.RECT* rect, nint data)
    {
        var self = s_instance;
        if (self is null) return 0;
        var info = new PlayerNative.MONITORINFO { cbSize = (uint)sizeof(PlayerNative.MONITORINFO) };
        if (!PlayerNative.GetMonitorInfo(monitor, ref info)) return 1;
        var scale = PlayerNative.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
        var id = (long)monitor;
        self._monitors[id] = (info.rcMonitor, info.rcWork, scale);
        if ((info.dwFlags & 1) != 0) self._primary = id;
        return 1;
    }

    private long PrimaryDisplay() => _monitors.ContainsKey(_primary) ? _primary : _displays[0].Id;

    // ---- Showing and hiding ----

    private void Apply(long? active, double now)
    {
        if (active == _shownOn) return;
        if (active is { } id && _monitors.TryGetValue(id, out var monitor)) Show(id, monitor.Scale, monitor.Work, now);
        else Hide(now);
    }

    private void Show(long display, double scale, PlayerNative.RECT work, double now)
    {
        var moving = _shownOn is not null && _shownOn != display;
        _log.Debug($"Desktop player: up on display 0x{display:X} (scale {scale:0.##}).");
        _shownOn = display;
        _scale = scale;
        _work = work;
        RefreshLook();
        ReadSong(now, force: true);
        SearchShown(now);
        UpdateSearch(now);
        var reduce = _model.Look.ReduceMotion;
        if (!_visible || moving)
        {
            _reveal.Snap(0);
            _rise.Snap((float)PlayerCard.RevealRise(reduce));
        }

        _reveal.To(1, now, PlayerCard.RevealSeconds(true, reduce));
        _rise.To(0, now, PlayerCard.RevealSeconds(true, reduce));
        _record.Snap(_model.DisplayedPlaying || reduce ? 1 : 0);
        Place(now);
        if (!_visible)
        {
            _visible = true;
            PlayerNative.ShowWindow(_surface.Hwnd, PlayerNative.SW_SHOWNOACTIVATE);
            PlaceAboveDesktop();
        }

        ApplyLyricFocus();
        _automation?.Shown(true);
        Invalidate();
        StartFrames(16);
        if (!double.IsNaN(_debugScrub)) _post(StartDebugScrub);
    }

    private void Hide(double now)
    {
        if (_shownOn is null) return;
        _shownOn = null;
        _log.Debug("Desktop player: going.");
        var reduce = _model.Look.ReduceMotion;
        _reveal.To(0, now, PlayerCard.RevealSeconds(false, reduce));
        _scrub.PointerExited();
        if (_scrub.IsDragging) _scrub.Cancel();
        _model.HoverPart = CardPart.None;
        ApplyLyricFocus();
        _automation?.Shown(false);
        ReleaseKeyboard();
        SearchHidden();
        StartFrames(16);
    }

    /// <summary>Where the card sits: centred in the work area of its display, rising into place as it appears.</summary>
    private void Place(double now)
    {
        var height = _cardHeightDip;
        _cardW = (int)Math.Round(PlayerCard.Width * _scale);
        _cardH = (int)Math.Round(height * _scale);
        var top = _work.Top + (int)Math.Round(PlayerCard.CardTop(_work.Height / _scale, height) * _scale);
        _cardX = _work.Left + (_work.Width - _cardW) / 2;
        _cardY = top + (int)Math.Round(_rise.Value(now) * _scale);
        PlayerNative.SetWindowPos(_surface.Hwnd, 0, _cardX, _cardY, _cardW, _cardH,
            PlayerNative.SWP_NOACTIVATE | PlayerNative.SWP_NOZORDER | PlayerNative.SWP_NOOWNERZORDER);
    }

    /// <summary>Just above the desktop, under every app's window: a window over the middle of the screen covers the card.</summary>
    private void PlaceAboveDesktop()
    {
        if (_debugTop)
        {
            // --player-debug top: over every window, to check what the card looks like on screen (never by default).
            PlayerNative.SetWindowPos(_surface.Hwnd, -1, 0, 0, 0, 0, PlayerNative.SWP_NOMOVE | PlayerNative.SWP_NOSIZE | PlayerNative.SWP_NOACTIVATE);
            return;
        }

        var desktop = PlayerNative.FindWindow("Progman", null);
        var above = desktop == 0 ? 0 : PlayerNative.GetWindow(desktop, PlayerNative.GW_HWNDPREV);
        if (above == _surface.Hwnd) return;
        PlayerNative.SetWindowPos(_surface.Hwnd, above == 0 ? 0 : above, 0, 0, 0, 0,
            PlayerNative.SWP_NOMOVE | PlayerNative.SWP_NOSIZE | PlayerNative.SWP_NOACTIVATE | PlayerNative.SWP_NOOWNERZORDER);
    }

    // ---- Frames ----

    private void StartFrames(uint ms)
    {
        if (ms == _frameMs) return;
        _frameMs = ms;
        PlayerNative.SetTimer(_surface.Hwnd, FrameTimer, ms, 0);
    }

    private void StopFrames()
    {
        if (_frameMs == 0) return;
        _frameMs = 0;
        PlayerNative.KillTimer(_surface.Hwnd, FrameTimer);
    }

    private void Invalidate() => _dirty = true;

    private void Frame()
    {
        var now = Now();
        if (_shownOn is null && !_reveal.Moving(now))
        {
            if (_visible)
            {
                _visible = false;
                PlayerNative.ShowWindow(_surface.Hwnd, PlayerNative.SW_HIDE);
            }

            StopFrames();
            return;
        }

        ReadSong(now, force: false);
        ApplyLyricFocus();
        if (!double.IsNaN(_debugScrub) && _shownOn is not null && !_scrub.IsDragging && _scrub.Current.CanSeek) StartDebugScrub();
        UpdateSearch(now);
        UpdateModel(now);
        if (_rise.Moving(now) || _rise.Value(now) != 0) Place(now);
        var animating = _reveal.Moving(now) || _rise.Moving(now) || _record.Moving(now) || _thickness.Moving(now) || _active.Moving(now)
            || _capsules.Moving(now) || _titles.Moving(now) || _note.Moving(now) || _scrub.IsDragging;
        if (_dirty || animating || _scrub.Version != _drawnVersion) Draw();
        // Quick while anything moves; while shown, often enough for the elapsed time (and the key's capsule) to tick.
        StartFrames(animating ? 16u : _model.DisplayedPlaying || _scrub.Flashing ? 100u : 250u);
    }

    // Made once: a frame allocates no delegate.
    private Action<Vortice.Direct2D1.ID2D1DeviceContext>? _paint;

    private void PaintCard(Vortice.Direct2D1.ID2D1DeviceContext dc) =>
        _painter.Paint(dc, _surface.Factory, _model, (float)PlayerCard.Width, _cardHeightDip);

    private void Draw()
    {
        _dirty = false;
        _drawnVersion = _scrub.Version;
        if (_cardW <= 0) return;
        var reset = !_surface.HasDevice;
        try
        {
            _surface.EnsureSize(_cardW, _cardH);
            if (reset) _painter.ReleaseDevice();
            var dc = _surface.Dc!;
            dc.Dpi = new Vortice.Mathematics.Size((float)(96 * _scale), (float)(96 * _scale));
            _paint ??= PaintCard;
            if (!_surface.Draw(_paint)) _painter.ReleaseDevice();
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException or InvalidOperationException)
        {
            _log.Warn("Desktop player: couldn't draw the card; trying again with a new device.", ex);
            _painter.ReleaseDevice();
            _surface.ReleaseDevice();
        }
    }

    // ---- The song ----

    private double Elapsed(double now)
    {
        if (_now is not { HasTimeline: true } song) return 0;
        var position = song.Position + (song.IsPlaying ? now - _nowReadAt : 0);
        return song.Duration > 0 ? Math.Clamp(position, 0, song.Duration) : Math.Max(0, position);
    }

    private void ReadSong(double now, bool force)
    {
        if (!force && now - _nowReadAt < 0.1) return;
        var controller = _controller();
        if (controller is null) return;
        var song = controller.ReadPlayer();
        _now = song;
        _nowReadAt = now;
        var m = _model;
        m.HasSong = song.HasSong;
        m.Title = song.Title is { Length: > 0 } title ? title : "Nothing playing";
        m.Artist = song.HasSong ? song.Artist : "Play something in a music app";
        m.Album = song.Album is { Length: > 0 } album && album != song.Title ? album : null;
        m.PlayerName = song.Player ?? "";
        m.CommandsOff = _commands.AreOff;
        m.IsStream = song.HasSong && song.HasTimeline && song.Duration <= 0;
        if (!ReferenceEquals(m.Cover, song.Cover))
        {
            m.Cover = song.Cover;
            Invalidate();
        }

        if (song.IsPlaying != _lastPlaying)
        {
            _lastPlaying = song.IsPlaying;
            _toggle.Reconcile(song.IsPlaying, now);
        }

        UpdatePalette(song);
        UpdateContext(song);
        SearchTrackChanged(song, now);
    }

    private void UpdatePalette(PlayerNow song)
    {
        var cover = song.HasSong ? song.Cover : null;
        if (ReferenceEquals(cover, _paletteCover) && (cover is not null || _paletteNeutral)) return;
        _paletteCover = cover;
        _paletteNeutral = cover is null;
        _palette = cover is null ? ArtworkPalette.Neutral : ArtworkPalette.Of(PixelsOf(cover));
        _model.Theme = CardTheme.For(_palette, _model.Look);
        _surface.SetBorderColour(_model.Theme.Border);
        Invalidate();
    }

    /// <summary>The cover as straight (not premultiplied) RGBA, for the card's colours.</summary>
    private static ArtworkPixels PixelsOf(CoverArt cover)
    {
        var rgba = new byte[cover.Width * cover.Height * 4];
        var source = cover.Pixels;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var a = source[i + 3];
            if (a == 0) continue;
            rgba[i] = (byte)Math.Min(255, source[i + 2] * 255 / a);
            rgba[i + 1] = (byte)Math.Min(255, source[i + 1] * 255 / a);
            rgba[i + 2] = (byte)Math.Min(255, source[i] * 255 / a);
            rgba[i + 3] = a;
        }

        return new ArtworkPixels(cover.Width, cover.Height, rgba);
    }

    private void UpdateContext(PlayerNow song)
    {
        // Scrub when there is a length and the player allows seeking, or when commands are off for this run: then
        // the drag only previews on the wallpaper, and the card says so on release.
        var canSeek = song.HasSong && song.Duration > 0 && (song.Capabilities.CanSeek || _commands.AreOff);
        var settings = _settings();
        var display = RomanizationOptions.ParseDisplay(settings.RomanizationDisplay) ?? RomanizationDisplay.Below;
        var key = (Math.Round(song.LyricOffset, 3), song.Readings, display, song.TrackKey, song.Duration, canSeek);
        if (ReferenceEquals(_contextLyrics, song.Lyrics) && key == _contextKey) return;
        _contextLyrics = song.Lyrics;
        _contextKey = key;
        var lines = song.Lyrics is LyricResult.Synced synced
            ? ScrubLine.Lines(synced.Lines, song.LyricOffset, display == RomanizationDisplay.Off ? null : song.Readings)
            : [];
        if (display == RomanizationDisplay.Off) lines = [.. lines.Select(l => l with { Romanized = null })];
        _scrub.Update(new ScrubController.Context(song.Duration, song.TrackKey ?? "", lines, canSeek, display));
        _model.Romanization = display;
        Invalidate();
    }

    private void UpdateModel(double now)
    {
        var m = _model;
        var song = _now;
        var reduce = m.Look.ReduceMotion;
        m.DisplayedPlaying = _toggle.Displayed(song?.IsPlaying ?? false, now);
        _record.To(m.DisplayedPlaying || reduce ? 1 : 0, now, reduce ? 0 : 0.55);
        m.RowKind = ScrubRowKinds.Of(song?.HasSong ?? false, isAdvert: false, isStream: m.IsStream, song?.Duration ?? 0,
            _scrub.Current.CanSeek);
        if (song is { HasSong: true, HasTimeline: false }) m.RowKind = ScrubRowKind.Hidden;
        var elapsed = _scrub.DisplayedTime(Elapsed(now));
        if (Math.Floor(elapsed) != Math.Floor(m.Elapsed)) Invalidate();
        m.Elapsed = elapsed;
        m.Duration = song?.Duration ?? 0;
        m.Scrub = _scrub.Current;
        m.Session = _scrub.Session;
        m.Hover = _scrub.HoverState;
        m.Flash = _scrub.Flash;

        var seekable = m.RowKind == ScrubRowKind.Seekable;
        var dragging = _scrub.IsDragging;
        var active = seekable && (_scrub.Hovering || dragging);
        _thickness.To(!seekable ? 4 : dragging ? 9 : active ? 7 : 4, now, reduce ? 0 : 0.18);
        _active.To(active ? 1 : 0, now, reduce ? 0.1 : 0.18);
        var capsulesUp = seekable && _scrub.CapsulesUp;
        _capsules.To(capsulesUp ? 1 : 0, now, capsulesUp ? 0.12 : 0.10);
        _titles.To((float)PlayerCard.TitleOpacity(capsulesUp), now, capsulesUp ? 0.10 : 0.14);
        if (_scrub.Session is { } session) (m.CapsuleX, m.CapsuleTime) = (session.DisplayX, session.TargetTime);
        else if (_scrub.HoverState is { } hover) (m.CapsuleX, m.CapsuleTime) = (hover.X, hover.Time);
        else if (_scrub.Flash is { } flash) (m.CapsuleX, m.CapsuleTime) = (ScrubMath.X(flash, m.Duration), flash);

        _note.To(now < _noteUntil ? 1 : 0, now, 0.3);
        m.Reveal = _reveal.Value(now);
        m.RecordOut = _record.Value(now);
        m.Thickness = _thickness.Value(now);
        m.Active = _active.Value(now);
        m.Capsules = _capsules.Value(now);
        m.TitleAlpha = _titles.Value(now);
        m.NoteAlpha = _note.Value(now);
        m.PressedPart = _pressed;
        // What UI Automation reads changes at most once a second (the time, the line) or on a press: a few times a
        // second is plenty, and the frames in between build nothing for it.
        if (now - _automationAt >= 0.25)
        {
            _automationAt = now;
            _automation?.Update(m);
        }
    }

    private void RefreshLook()
    {
        var look = SystemLook.Read(_debug);
        if (look == _model.Look && _surface.Hwnd != 0 && _model.Theme is not null && _paletteCover is not null) return;
        _model.Look = look;
        _model.Theme = CardTheme.For(_palette, look);
        if (_surface.Hwnd != 0) _surface.SetBorderColour(_model.Theme.Border);
        Invalidate();
    }

    /// <summary>A note over the song for a few seconds (and read out by Narrator).</summary>
    private void ShowNote(string note)
    {
        _model.Note = note;
        _noteUntil = Now() + PlayerCard.NoteSeconds(note);
        _automation?.Announce(note);
        Invalidate();
        StartFrames(16);
    }
}
