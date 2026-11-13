// The floating lyrics' window (the Mac's FloatingLyricsController and FloatingLyricsPanel, Lunalyr's own lines, written
// for Win32; the controller's five Verso-attributed lines are a screen observer's declaration and start() shell, which
// this replaces with WM_DISPLAYCHANGE and friends). It never activates: no click, show or move ever takes the
// foreground or the keyboard from the app in front.

using System.Numerics;
using System.Runtime.InteropServices;
using Lunalyr.Core.Floating;
using Lunalyr.Core.Logging;
using static Lunalyr.Platform.Desktop.DesktopNative;
using static Lunalyr.Platform.Desktop.FloatingNative;
using MBox = Lunalyr.Core.Motion.Box;

namespace Lunalyr.Platform.Desktop;

/// <summary>What the floating lyrics should do now: the settings that place the window and draw its frame.</summary>
public sealed record FloatingLyricsOptions
{
    public static FloatingLyricsOptions Off { get; } = new();

    /// <summary>Floating is the lyrics' surface: the wallpaper draws none, whether or not the window shows.</summary>
    public bool Enabled { get; init; }

    /// <summary>The window shows (<see cref="FloatingVisibility.Shows"/>).</summary>
    public bool Shown { get; init; }

    /// <summary>Edit position: the window takes the pointer (never the keyboard) to be dragged or resized.</summary>
    public bool Editing { get; init; }

    /// <summary>Editing with nothing to show: "Lyrics appear here".</summary>
    public bool Placeholder { get; init; }

    public double TextSize { get; init; } = 44;
    public double Backdrop { get; init; } = 0.6;
    public bool FadesUnderPointer { get; init; } = true;
    public FloatingPlacement Placement { get; init; } = FloatingPlacement.Bottom;

    /// <summary>The display's key (<see cref="DisplayChoice.Key"/>), or empty for the main display.</summary>
    public string Display { get; init; } = "";

    public IReadOnlyDictionary<string, IReadOnlyList<double>> Frames { get; init; } = new Dictionary<string, IReadOnlyList<double>>();
    public IReadOnlyDictionary<string, IReadOnlyList<double>> Sizes { get; init; } = new Dictionary<string, IReadOnlyList<double>>();
}

/// <summary>The renderer's side of the floating window. Every call comes from the window's own thread.</summary>
public interface IFloatingWindowSink
{
    /// <summary>The window was made, moved to another display, or resized (same handle, new size or monitor).</summary>
    void SetWindow(SurfaceWindow window);

    /// <summary>Drop the swap chain on the window; the handle is set once it is gone, so the window can be destroyed.</summary>
    WaitHandle ReleaseWindow();

    /// <summary>The window was shown or hidden.</summary>
    void WindowShown(bool shown);

    /// <summary>The pointer came over the window or left it (only followed while it fades under the pointer).</summary>
    void PointerOver(bool over);

    /// <summary>Windows' transparency effects and accent colour (sRGB, 0–1), read at start and on every setting change.</summary>
    void Look(bool reduceTransparency, Vector4 accent);

    /// <summary>A drag in edit mode ended: the frame, relative to the display, in points, y up.</summary>
    void Placed(string displayKey, FloatingRect relative);

    /// <summary>Done was pressed in the window.</summary>
    void EditDone();
}

/// <summary>The window's styles, apart so they are tested without a window.</summary>
public static class FloatingWindowStyles
{
    public const uint Style = WS_POPUP;

    /// <summary>
    /// Never activated (no foreground, no keyboard), out of Alt+Tab and the taskbar (a tool window, never an app window),
    /// above every app, and drawn through DirectComposition with per-pixel alpha. Click-through (layered and transparent)
    /// except while editing, when it takes the pointer only.
    /// </summary>
    public static uint ExStyle(bool editing) =>
        WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED | (editing ? 0 : WS_EX_TRANSPARENT);

    /// <summary>The style bits that must be on in every mode.</summary>
    public static uint Always => WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
}

/// <summary>
/// Owns the floating lyrics window on a thread of its own: places it on the chosen display clear of the taskbar (any
/// edge, auto-hidden too), keeps it above full-screen apps, shows and hides it, fades it under the pointer, and in
/// edit mode lets it be dragged or resized with the mouse. Per-monitor DPI v2.
/// </summary>
public sealed unsafe class FloatingWindowHost : IDisposable
{
    private const uint WM_APP = 0x8000;
    private const uint MsgUpdate = WM_APP + 1, MsgFadedOut = WM_APP + 2, MsgStop = WM_APP + 3;
    private const nuint TimerPointer = 1, TimerTopmost = 2, TimerReplace = 3;
    private const uint PointerPollMs = 250, TopmostMs = 2000, ReplaceDelayMs = 300;

    [ThreadStatic] private static FloatingWindowHost? t_host;

    private readonly IFloatingWindowSink _sink;
    private readonly CategoryLogger _log;
    private readonly string _class;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private Exception? _startError;
    private volatile FloatingLyricsOptions _options;
    private volatile nint _hwnd;

    // Window-thread state.
    private FloatingLyricsOptions? _applied;
    private bool _visible, _pointerOver, _editingStyle;
    private DisplayChoice? _display;
    private FloatingDisplay _geometry;
    private MBox _rect;
    private nint _foregroundHook;
    private (MBox Start, POINT Cursor, bool Resizing)? _drag;
    private bool _donePressed;

    public FloatingWindowHost(IFloatingWindowSink sink, FloatingLyricsOptions options, Logger? logger = null)
    {
        _sink = sink;
        _options = options;
        _log = (logger ?? Logger.Null).Render;
        _class = "LunalyrFloatingLyrics_" + Environment.ProcessId.ToString("X") + "_" + Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>The window (0 before it is made).</summary>
    public nint Hwnd => _hwnd;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Lunalyr floating lyrics" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startError is not null) throw new InvalidOperationException("The floating lyrics window couldn't be made.", _startError);
    }

    /// <summary>New options (any thread): placed, shown or put into edit mode on the window's thread.</summary>
    public void Update(FloatingLyricsOptions options)
    {
        _options = options;
        nint w = _hwnd;
        if (w != 0) PostMessage(w, MsgUpdate, 0, 0);
    }

    /// <summary>The renderer has faded the window out (any thread): it is hidden unless it should show again.</summary>
    public void FadedOut()
    {
        nint w = _hwnd;
        if (w != 0) PostMessage(w, MsgFadedOut, 0, 0);
    }

    public void Stop()
    {
        if (_thread is null) return;
        nint w = _hwnd;
        if (w != 0) PostMessage(w, MsgStop, 0, 0);
        _thread.Join(5000);
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Run()
    {
        t_host = this;
        SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            Create();
        }
        catch (Exception ex)
        {
            _startError = ex;
            _log.Error("The floating lyrics window couldn't be made", ex);
            _ready.Set();
            Cleanup();
            return;
        }
        _ready.Set();
        MSG m;
        while (GetMessage(&m, 0, 0, 0) > 0)
        {
            TranslateMessage(&m);
            DispatchMessage(&m);
        }
        Cleanup();
    }

    private void Create()
    {
        nint inst = GetModuleHandle(null);
        fixed (char* cls = _class)
        fixed (char* title = "Lunalyr floating lyrics")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = sizeof(WNDCLASSEXW), lpfnWndProc = &WindowProc, hInstance = inst, lpszClassName = cls,
                hCursor = LoadCursor(0, IDC_ARROW),
            };
            if (RegisterClassEx(&wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
            var options = _options;
            var rect = Resolve(options) ?? new MBox(0, 0, 640, 200);
            _editingStyle = options.Editing;
            nint hwnd = CreateWindowEx(FloatingWindowStyles.ExStyle(_editingStyle), cls, title, FloatingWindowStyles.Style,
                (int)rect.X, (int)rect.Y, Math.Max(1, (int)rect.Width), Math.Max(1, (int)rect.Height), 0, 0, inst, 0);
            if (hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
            _hwnd = hwnd;
            _rect = rect;
        }
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, &OnForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
        ReadLook();
        HandOver();
        _log.Info($"Floating lyrics window ready: {_rect.Width}x{_rect.Height} at {_rect.X},{_rect.Y} on {_display?.Name ?? "no display"}");
        Apply(_options);
    }

    private void Cleanup()
    {
        nint hwnd = _hwnd;
        if (hwnd != 0)
        {
            try
            {
                WaitPumping(_sink.ReleaseWindow(), 3000);
            }
            catch (Exception ex)
            {
                _log.Warn("Releasing the floating lyrics' swap chain failed", ex);
            }
            DestroyWindow(hwnd);
            _hwnd = 0;
        }
        if (_foregroundHook != 0) UnhookWinEvent(_foregroundHook);
        _foregroundHook = 0;
        nint inst = GetModuleHandle(null);
        fixed (char* cls = _class) UnregisterClass(cls, inst);
        t_host = null;
    }

    // ---- Placing and showing ----

    /// <summary>Where the options put the window, in physical pixels, and on which display (null with no display).</summary>
    private MBox? Resolve(FloatingLyricsOptions options)
    {
        var displays = DisplayIdentity.Connected();
        if (FloatingFrame.ChosenIndex(options.Display, [.. displays.Select(d => d.Key)]) is not { } index) return null;
        var display = displays[index];
        var geometry = DisplayIdentity.Display(display.Monitor);
        options.Frames.TryGetValue(display.Key, out var frame);
        options.Sizes.TryGetValue(display.Key, out var size);
        _display = display;
        _geometry = geometry;
        return geometry.Place(options.Placement, FloatingFrame.Decode(frame), FloatingFrame.Decode(size)?.Size, options.TextSize);
    }

    private void Apply(FloatingLyricsOptions options)
    {
        _applied = options;
        if (options.Editing != _editingStyle)
        {
            _editingStyle = options.Editing;
            if (!options.Editing) EndDrag(store: false);
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (nint)FloatingWindowStyles.ExStyle(_editingStyle));
            SetWindowPos(_hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
            _log.Info(options.Editing ? "Floating lyrics: edit position (takes the pointer, never the keyboard)" : "Floating lyrics: locked; clicks pass through");
        }
        if (_drag is null) Place();
        bool wanted = options.Enabled && options.Shown;
        if (wanted && !_visible) Show();
        UpdateTimers();
    }

    private void Place()
    {
        var previous = _display?.Monitor;
        if (_applied is null || Resolve(_applied) is not { } rect) return;
        bool monitorChanged = previous is null || previous.DeviceName != _display!.Monitor.DeviceName || previous.Dpi != _display.Monitor.Dpi;
        if (rect == _rect && !monitorChanged) return;
        Move(rect, handOver: monitorChanged);
    }

    /// <param name="handOver">The window went to another monitor (or scale): its swap chain follows even at the same size.</param>
    private void Move(MBox rect, bool handOver = false)
    {
        bool resized = rect.Width != _rect.Width || rect.Height != _rect.Height;
        _rect = rect;
        SetWindowPos(_hwnd, 0, (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        if (resized || handOver) HandOver();
    }

    /// <summary>Tells the renderer the window's size and monitor (its swap chain follows).</summary>
    private void HandOver()
    {
        if (_display is null) return;
        _sink.SetWindow(new SurfaceWindow(_hwnd, _display.Monitor, Math.Max(1, (int)_rect.Width), Math.Max(1, (int)_rect.Height), AttachMode.Floating));
    }

    private void Show()
    {
        SetWindowPos(_hwnd, HWND_TOPMOST, (int)_rect.X, (int)_rect.Y, (int)_rect.Width, (int)_rect.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _visible = true;
        _sink.WindowShown(true);
        _log.Info("Floating lyrics shown");
    }

    private void Hide()
    {
        if (!_visible) return;
        EndDrag(store: false);
        ShowWindow(_hwnd, SW_HIDE);
        _visible = false;
        SetPointerOver(false);
        _sink.WindowShown(false);
        _log.Info("Floating lyrics hidden");
    }

    private void KeepOnTop()
    {
        if (_visible) SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private void UpdateTimers()
    {
        var options = _applied;
        bool pointer = _visible && options is { FadesUnderPointer: true, Editing: false };
        if (pointer) SetTimer(_hwnd, TimerPointer, PointerPollMs, 0);
        else
        {
            KillTimer(_hwnd, TimerPointer);
            SetPointerOver(false);
        }
        if (_visible) SetTimer(_hwnd, TimerTopmost, TopmostMs, 0);
        else KillTimer(_hwnd, TimerTopmost);
    }

    private void CheckPointer()
    {
        if (!GetCursorPos(out var p)) return;
        SetPointerOver(_visible && p.X >= _rect.X && p.X < _rect.X + _rect.Width && p.Y >= _rect.Y && p.Y < _rect.Y + _rect.Height);
    }

    private void SetPointerOver(bool over)
    {
        if (over == _pointerOver) return;
        _pointerOver = over;
        _sink.PointerOver(over);
    }

    private void ReadLook()
    {
        bool reduce = false;
        var accent = new Vector4(0, 0.47f, 0.83f, 1);
        try
        {
            var settings = new Windows.UI.ViewManagement.UISettings();
            reduce = !settings.AdvancedEffectsEnabled;
            var c = settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            accent = new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1);
        }
        catch (Exception ex)
        {
            _log.Debug($"Couldn't read transparency effects and the accent colour: {ex.Message}");
        }
        _sink.Look(reduce, accent);
    }

    // ---- Edit mode: the pointer only ----

    private FloatingHit HitAt(int x, int y) =>
        FloatingEditChrome.HitTest(x, y, _rect.Width, _rect.Height, _display?.Monitor.Scale ?? 1);

    private void OnButtonDown(int x, int y)
    {
        if (_applied is not { Editing: true }) return;
        var hit = HitAt(x, y);
        if (hit == FloatingHit.Done)
        {
            _donePressed = true;
        }
        else
        {
            if (!GetCursorPos(out var cursor)) return;
            _drag = (_rect, cursor, hit == FloatingHit.Resize);
        }
        SetCapture(_hwnd);
    }

    private void OnMouseMove()
    {
        if (_drag is not { } drag || !GetCursorPos(out var p)) return;
        var (dx, dy) = _geometry.Delta(p.X - drag.Cursor.X, p.Y - drag.Cursor.Y);
        var frame = FloatingFrame.Dragged(_geometry.ToPoints(drag.Start), dx, dy, drag.Resizing, _geometry.Area);
        var rect = _geometry.ToPixels(frame);
        if (rect != _rect) Move(rect);
    }

    private void OnButtonUp(int x, int y)
    {
        if (_donePressed)
        {
            _donePressed = false;
            ReleaseCapture();
            if (HitAt(x, y) == FloatingHit.Done) _sink.EditDone();
            return;
        }
        if (_drag is null) return;
        OnMouseMove();
        EndDrag(store: true);
    }

    private void EndDrag(bool store)
    {
        _donePressed = false;
        if (_drag is null) return;
        _drag = null;
        if (GetCapture() == _hwnd) ReleaseCapture();
        if (!store || _display is null) return;
        // The display's frame is at the origin in points, so the window's frame is already relative to it.
        var relative = FloatingFrame.Relative(_geometry.ToPoints(_rect), _geometry.ScreenFrame);
        _sink.Placed(_display.Key, relative);
    }

    private void SetCursorFor(int x, int y)
    {
        var id = _applied is { Editing: true }
            ? HitAt(x, y) switch
            {
                FloatingHit.Done => IDC_HAND,
                FloatingHit.Resize => IDC_SIZENWSE,
                _ => IDC_SIZEALL,
            }
            : IDC_ARROW;
        SetCursor(LoadCursor(0, id));
    }

    // ---- Messages ----

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint msg, nuint w, nint l)
    {
        var host = t_host;
        if (host is not null && hwnd == host._hwnd)
        {
            try
            {
                if (host.OnMessage(msg, w, l) is { } handled) return handled;
            }
            catch (Exception ex)
            {
                host._log.Error("Floating lyrics window message failed", ex);
            }
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    private static (int X, int Y) Point(nint l) => ((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));

    private nint? OnMessage(uint msg, nuint w, nint l)
    {
        switch (msg)
        {
            case MsgUpdate:
                Apply(_options);
                return 0;
            case MsgFadedOut:
                if (_applied is not { Enabled: true, Shown: true }) Hide();
                UpdateTimers();
                return 0;
            case MsgStop:
                PostQuitMessage(0);
                return 0;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_NCHITTEST:
                return HTCLIENT;
            case WM_ERASEBKGND:
                return 1;
            case WM_SETCURSOR:
                if (GetCursorPos(out var c))
                {
                    var p = new POINT { X = c.X, Y = c.Y };
                    ScreenToClient(_hwnd, ref p);
                    SetCursorFor(p.X, p.Y);
                    return 1;
                }
                return null;
            case WM_LBUTTONDOWN:
            {
                var (x, y) = Point(l);
                OnButtonDown(x, y);
                return 0;
            }
            case WM_MOUSEMOVE:
                OnMouseMove();
                return 0;
            case WM_LBUTTONUP:
            {
                var (x, y) = Point(l);
                OnButtonUp(x, y);
                return 0;
            }
            case WM_CAPTURECHANGED when _drag is not null && l != _hwnd:
                EndDrag(store: true);
                return 0;
            case WM_TIMER when w == TimerPointer:
                CheckPointer();
                return 0;
            case WM_TIMER when w == TimerTopmost:
                KeepOnTop();
                return 0;
            case WM_TIMER when w == TimerReplace:
                KillTimer(_hwnd, TimerReplace);
                if (_drag is null) Place();
                return 0;
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                SetTimer(_hwnd, TimerReplace, ReplaceDelayMs, 0);
                return 0;
            case WM_SETTINGCHANGE:
                // The work area (a taskbar moved or set to hide), transparency effects or the accent colour.
                SetTimer(_hwnd, TimerReplace, ReplaceDelayMs, 0);
                ReadLook();
                return null;
            case WM_CLOSE:
                return 0; // only the app closes it
        }
        return null;
    }

    /// <summary>Another window came to the front (a full-screen app, say): the lyrics go back on top of it.</summary>
    [UnmanagedCallersOnly]
    private static void OnForeground(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var host = t_host;
        if (host is not null && idObject == 0 && hwnd != host._hwnd) host.KeepOnTop();
    }

    private static bool WaitPumping(WaitHandle handle, int timeoutMs)
    {
        nint h = handle.SafeWaitHandle.DangerousGetHandle();
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            long left = deadline - Environment.TickCount64;
            if (left <= 0) return false;
            uint r = MsgWaitForMultipleObjectsEx(1, &h, (uint)left, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (r == WAIT_OBJECT_0) return true;
            if (r != WAIT_OBJECT_0 + 1) return handle.WaitOne(0);
            MSG m;
            while (PeekMessage(&m, 0, 0, 0, PM_REMOVE))
            {
                if (m.message == WM_QUIT) return handle.WaitOne(Math.Max(0, (int)(deadline - Environment.TickCount64)));
                TranslateMessage(&m);
                DispatchMessage(&m);
            }
        }
    }
}
