// The tray lyric strip: the Windows stand-in for the Mac's menu-bar line (MenuBarLyricTicker.swift). Windows can't put
// text in the notification area, so the line shows in a small strip in the corner beside the taskbar's clock: a
// layered, click-through tool window that is never activated, drawn with Direct2D into a bitmap it owns and handed to
// UpdateLayeredWindow only when the line changes (and during the 0.32 s roll). Why this and not the tooltip or the
// taskbar thumbnail: docs/APP-SHELL.md, "Tray lyric".

using System.Runtime.InteropServices;
using Lunalyr.App.Tray;
using Lunalyr.Core.Extras;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Extras;
using Lunalyr.Shell;
using Lunalyr.Shell.Extras;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Lunalyr.App.Ticker.TickerNative;
using AlphaMode = Vortice.DCommon.AlphaMode;
using FactoryType = Vortice.Direct2D1.FactoryType;
using FontStyle = Vortice.DirectWrite.FontStyle;

namespace Lunalyr.App.Ticker;

/// <summary>The strip's styles, apart so they are checked without a window.</summary>
internal static class TrayTickerStyles
{
    public const uint Style = WS_POPUP;

    /// <summary>Never activated, out of Alt+Tab and the taskbar, above apps, per-pixel alpha, and click-through.</summary>
    public const uint ExStyle = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT;
}

/// <summary>
/// Owns the tray lyric strip on a thread of its own: asks <see cref="TrayTickerModel"/> what to show, wakes once per
/// change (a one-shot timer with the Mac's tolerance), rolls the new line in, places itself beside the clock, hides
/// behind full-screen apps and stops waking while the displays sleep.
/// </summary>
internal sealed unsafe class TrayTickerWindow : IDisposable
{
    private const uint MsgRefresh = WM_APP + 1, MsgStop = WM_APP + 2;
    private const nuint TimerWake = 1, TimerAnimation = 2, TimerGuard = 3;
    private const uint AnimationFrameMs = 16, GuardMs = 2000;

    /// <summary>The strip's height, and the padding inside it, in points (the text is <see cref="TickerFont"/>).</summary>
    private const float Height = 28, Pad = 10, Radius = 7;

    [ThreadStatic] private static TrayTickerWindow? t_window;

    private readonly Func<ExtrasNow> _read;
    private readonly Func<double> _length;
    private readonly Func<bool> _reduceMotion;
    private readonly CategoryLogger _log;
    private readonly string _class = "LunalyrTrayLyric_" + Environment.ProcessId.ToString("X") + "_" + Guid.NewGuid().ToString("N")[..8];
    private readonly ManualResetEventSlim _ready = new();
    private readonly Lock _frameGate = new();
    private Thread? _thread;
    private volatile nint _hwnd;
    private int _refreshQueued;

    // Window-thread state.
    private TrayTickerModel? _model;
    private IDWriteFactory? _dwrite;
    private ID2D1Factory? _d2d;
    private TickerFont? _font;
    private IDWriteTextFormat? _text, _glyph;
    private ID2D1DCRenderTarget? _target;
    private nint _memoryDc, _bitmap, _oldBitmap;
    private void* _bits;
    private int _pixelWidth, _pixelHeight;
    private float _scale = 1;
    private nint _powerNotify;
    private bool _visible, _screensAsleep, _hiddenForFullScreen, _guardArmed;
    private TickerShown? _from, _to;
    private double _animationStart = double.NegativeInfinity, _animationLength, _lastTransition = double.NegativeInfinity;
    private bool _animating;
    private bool _light;
    private (int X, int Y) _position;

    // The last frame drawn (for --ticker-capture), premultiplied BGRA.
    private byte[]? _frame;
    private int _frameWidth, _frameHeight;

    public TrayTickerWindow(Func<ExtrasNow> read, Func<double> length, Func<bool> reduceMotion, Logger? logger = null)
    {
        _read = read;
        _length = length;
        _reduceMotion = reduceMotion;
        _log = (logger ?? Logger.Null).App;
    }

    /// <summary>The strip's window (0 before it is made or after it is gone).</summary>
    public nint Hwnd => _hwnd;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Lunalyr tray lyric" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Something changed (any thread): the song, playback, the lyrics, the settings. <paramref name="force"/> redraws.</summary>
    public void Refresh(bool force = false)
    {
        nint w = _hwnd;
        if (w == 0) return;
        if (force) PostMessage(w, MsgRefresh, 1, 0);
        else if (Interlocked.Exchange(ref _refreshQueued, 1) == 0) PostMessage(w, MsgRefresh, 0, 0);
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

    /// <summary>The last frame drawn, straight-alpha BGRA, or null when none was.</summary>
    public (byte[] Bgra, int Width, int Height)? LastFrame()
    {
        lock (_frameGate)
        {
            if (_frame is null) return null;
            var copy = (byte[])_frame.Clone();
            for (var i = 0; i < copy.Length; i += 4)
            {
                var a = copy[i + 3];
                if (a is 0 or 255) continue;
                for (var c = 0; c < 3; c++) copy[i + c] = (byte)Math.Min(255, copy[i + c] * 255 / a);
            }
            return (copy, _frameWidth, _frameHeight);
        }
    }

    // ---- The thread ----

    private void Run()
    {
        t_window = this;
        try
        {
            Create();
        }
        catch (Exception ex)
        {
            _log.Error("The tray lyric strip couldn't be made.", ex);
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
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _d2d = D2D1.D2D1CreateFactory<ID2D1Factory>(FactoryType.SingleThreaded);
        _font = new TickerFont(_dwrite);
        _text = _font.Format;
        _glyph = _dwrite.CreateTextFormat("Segoe Fluent Icons", null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, TickerFont.Size - 1, "");
        _model = new TrayTickerModel(_font.Measure);

        nint inst = GetModuleHandle(null);
        fixed (char* cls = _class)
        {
            var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WindowProc, hInstance = inst, lpszClassName = cls };
            if (RegisterClassEx(&wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
        }
        nint hwnd = CreateWindowEx(TrayTickerStyles.ExStyle, _class, "Lunalyr tray lyric", TrayTickerStyles.Style, 0, 0, 1, 1, 0, 0, inst, 0);
        if (hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        _hwnd = hwnd;
        var display = GUID_CONSOLE_DISPLAY_STATE;
        _powerNotify = RegisterPowerSettingNotification(hwnd, &display, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        _log.Info("Tray lyric strip ready (click-through, never activated).");
        Update(force: true);
    }

    private void Cleanup()
    {
        nint hwnd = _hwnd;
        _hwnd = 0;
        if (_powerNotify != 0) UnregisterPowerSettingNotification(_powerNotify);
        _powerNotify = 0;
        if (hwnd != 0) DestroyWindow(hwnd);
        FreeSurface();
        _target?.Dispose();
        _target = null;
        _font?.Dispose();
        _glyph?.Dispose();
        _d2d?.Dispose();
        _dwrite?.Dispose();
        UnregisterClass(_class, GetModuleHandle(null));
        t_window = null;
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        var window = t_window;
        if (window is null || window._hwnd != hwnd && window._hwnd != 0) return DefWindowProc(hwnd, msg, wParam, lParam);
        try
        {
            return window.Handle(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            window._log.Warn("The tray lyric strip failed to handle a message.", ex);
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private nint Handle(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case MsgRefresh:
                Volatile.Write(ref _refreshQueued, 0);
                Update(force: wParam != 0);
                return 0;
            case MsgStop:
                PostQuitMessage(0);
                return 0;
            case WM_TIMER when wParam == TimerWake:
                KillTimer(hwnd, TimerWake);
                Update(force: false);
                return 0;
            case WM_TIMER when wParam == TimerAnimation:
                DrawAnimationFrame();
                return 0;
            case WM_TIMER when wParam == TimerGuard:
                Guard();
                return 0;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_NCHITTEST:
                return HTTRANSPARENT;
            case WM_DISPLAYCHANGE or WM_SETTINGCHANGE or WM_DPICHANGED:
                Update(force: true);
                return 0;
            case WM_POWERBROADCAST when wParam == PBT_POWERSETTINGCHANGE && lParam != 0:
                var setting = (POWERBROADCAST_SETTING*)lParam;
                if (setting->PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                {
                    var asleep = setting->Data == 0;
                    if (asleep != _screensAsleep)
                    {
                        _screensAsleep = asleep;
                        if (asleep) DisarmGuard();
                        Update(force: !asleep);
                    }
                }
                return 1;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ---- What to show ----

    private static double Clock() => Environment.TickCount64 / 1000.0;

    private void Update(bool force)
    {
        if (_model is null || _hwnd == 0) return;
        ExtrasNow now;
        try
        {
            now = _read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Debug($"Tray lyric: couldn't read the song ({ex.Message}).");
            now = ExtrasNow.Nothing;
        }
        var step = _model.Refresh(now, _length(), _screensAsleep, Clock(), force);
        Schedule(step.Wait);
        if (step.Shown is null)
        {
            Hide();
            return;
        }

        _hiddenForFullScreen = FullScreenAppShowing();
        if (_hiddenForFullScreen)
        {
            Hide();
            ArmGuard();
            return;
        }

        var light = TrayIconArt.TaskbarIsLight();
        var resized = Layout(step.Width);
        if (step.Changed || resized || !_visible || light != _light)
        {
            _light = light;
            var now2 = Clock();
            var roll = step.TextChanged && _visible && _to is not null && !resized && now2 - _lastTransition >= TrayLyricTicker.MinTransitionGap;
            _from = roll ? _to : null;
            _to = step.Shown;
            if (roll)
            {
                _lastTransition = now2;
                _animationStart = now2;
                _animationLength = _reduceMotion() ? TrayLyricTicker.ReducedSeconds : TrayLyricTicker.RollSeconds;
                _animating = true;
                SetCoalescableTimer(_hwnd, TimerAnimation, AnimationFrameMs, 0, 0);
                DrawAnimationFrame();
            }
            else
            {
                StopAnimation();
                Draw(_to, null, 1);
            }
        }
        Show();
        ArmGuard();
    }

    /// <summary>One-shot: each wake re-reads the line and aims the next one (the Mac's tolerance keeps line starts close).</summary>
    private void Schedule(double? wait)
    {
        KillTimer(_hwnd, TimerWake);
        if (wait is not { } seconds) return;
        var ms = (uint)Math.Clamp(Math.Round(seconds * 1000), 10, int.MaxValue);
        var tolerance = (uint)Math.Round(TrayLyricTicker.Tolerance(seconds) * 1000);
        SetCoalescableTimer(_hwnd, TimerWake, ms, 0, tolerance);
    }

    private void ArmGuard()
    {
        if (_guardArmed || _screensAsleep) return;
        _guardArmed = true;
        SetCoalescableTimer(_hwnd, TimerGuard, GuardMs, 0, GuardMs / 10);
    }

    private void DisarmGuard()
    {
        if (!_guardArmed) return;
        _guardArmed = false;
        KillTimer(_hwnd, TimerGuard);
    }

    /// <summary>Hides behind full-screen apps and comes back after: every 2 s while it shows or waits behind one, never while the displays sleep.</summary>
    private void Guard()
    {
        if (_screensAsleep)
        {
            DisarmGuard();
            return;
        }
        var fullScreen = FullScreenAppShowing();
        if (fullScreen != _hiddenForFullScreen)
        {
            Update(force: true);
            return;
        }
        // Topmost is not re-asserted: menus, the tray flyout and other pop-ups opened later stay above the strip.
        if (!_visible && !_hiddenForFullScreen) DisarmGuard();
    }

    private void Show()
    {
        if (_visible) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, _position.X, _position.Y, _pixelWidth, _pixelHeight, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
        _visible = true;
        _log.Info($"Tray lyric shown: {_pixelWidth}x{_pixelHeight} at {_position.X},{_position.Y}.");
    }

    private void Hide()
    {
        StopAnimation();
        if (!_visible) return;
        ShowWindow(_hwnd, SW_HIDE);
        _visible = false;
        _to = null;
        if (!_hiddenForFullScreen) DisarmGuard();
        _log.Info(_hiddenForFullScreen ? "Tray lyric hidden behind a full-screen app." : "Tray lyric hidden.");
    }

    // ---- Where and how big ----

    /// <summary>Sizes the strip for <paramref name="widthPoints"/> and places it beside the clock; true when its size changed.</summary>
    private bool Layout(double widthPoints)
    {
        nint taskbar = FindWindow("Shell_TrayWnd", null);
        nint monitor = MonitorFromWindow(taskbar, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfo(monitor, &info);
        _scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96f : 1;

        var bar = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
        var edge = SHAppBarMessage(ABM_GETTASKBARPOS, &bar) != 0 ? bar.uEdge : ABE_BOTTOM;
        var state = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
        var autoHide = ((nint)SHAppBarMessage(ABM_GETSTATE, &state) & ABS_AUTOHIDE) != 0;
        double ax = info.rcWork.Left, ay = info.rcWork.Top, aw = info.rcWork.Right - info.rcWork.Left, ah = info.rcWork.Bottom - info.rcWork.Top;
        if (autoHide)
        {
            // The work area doesn't leave an auto-hidden taskbar out: keep clear of where it slides in.
            var reserve = edge is ABE_LEFT or ABE_RIGHT ? bar.rc.Right - bar.rc.Left : bar.rc.Bottom - bar.rc.Top;
            reserve = Math.Clamp(reserve, (int)(2 * _scale), (int)(64 * _scale));
            switch (edge)
            {
                case ABE_LEFT: ax += reserve; aw -= reserve; break;
                case ABE_TOP: ay += reserve; ah -= reserve; break;
                case ABE_RIGHT: aw -= reserve; break;
                default: ah -= reserve; break;
            }
        }

        var width = (int)Math.Ceiling((widthPoints + 2 * Pad) * _scale);
        var height = (int)Math.Ceiling(Height * _scale);
        var (x, y) = TrayLyricTicker.Place(ax, ay, aw, ah, edge switch
        {
            ABE_LEFT => TrayLyricTicker.TaskbarEdge.Left,
            ABE_TOP => TrayLyricTicker.TaskbarEdge.Top,
            ABE_RIGHT => TrayLyricTicker.TaskbarEdge.Right,
            _ => TrayLyricTicker.TaskbarEdge.Bottom,
        }, width, height, TrayLyricTicker.PlaceMargin * _scale);
        var resized = width != _pixelWidth || height != _pixelHeight;
        var moved = (int)x != _position.X || (int)y != _position.Y;
        _position = ((int)x, (int)y);
        if (resized) MakeSurface(width, height);
        if ((resized || moved) && _visible)
            SetWindowPos(_hwnd, 0, _position.X, _position.Y, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        return resized;
    }

    private void MakeSurface(int width, int height)
    {
        FreeSurface();
        _pixelWidth = width;
        _pixelHeight = height;
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32,
        };
        nint screen = GetDC(0);
        _memoryDc = CreateCompatibleDC(screen);
        void* bits;
        _bitmap = CreateDIBSection(screen, &header, 0, &bits, 0, 0);
        ReleaseDC(0, screen);
        _bits = bits;
        _oldBitmap = SelectObject(_memoryDc, _bitmap);
        _target ??= _d2d!.CreateDCRenderTarget(new RenderTargetProperties(RenderTargetType.Default,
            new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, RenderTargetUsage.None, FeatureLevel.Default));
    }

    private void FreeSurface()
    {
        if (_memoryDc != 0)
        {
            if (_oldBitmap != 0) SelectObject(_memoryDc, _oldBitmap);
            DeleteDC(_memoryDc);
        }
        if (_bitmap != 0) DeleteObject(_bitmap);
        _memoryDc = _bitmap = _oldBitmap = 0;
        _bits = null;
        _pixelWidth = _pixelHeight = 0;
    }

    // ---- Drawing ----

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        KillTimer(_hwnd, TimerAnimation);
    }

    private void DrawAnimationFrame()
    {
        if (!_animating || _to is null)
        {
            StopAnimation();
            return;
        }
        var t = _animationLength > 0 ? (Clock() - _animationStart) / _animationLength : 1;
        if (t >= 1)
        {
            StopAnimation();
            Draw(_to, null, 1);
            return;
        }
        Draw(_to, _from, TrayLyricTicker.EaseOut(t));
    }

    /// <summary>Draws <paramref name="next"/> (rolling in over <paramref name="previous"/> at <paramref name="progress"/>) and hands it to the window.</summary>
    private void Draw(TickerShown? next, TickerShown? previous, double progress)
    {
        if (_target is null || _memoryDc == 0 || next is null) return;
        var rt = _target;
        rt.BindDC(_memoryDc, new Vortice.RawRect(0, 0, _pixelWidth, _pixelHeight));
        rt.SetDpi(96 * _scale, 96 * _scale);
        rt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        var widthDips = _pixelWidth / _scale;
        rt.BeginDraw();
        rt.Clear(new Color4(0, 0, 0, 0));
        var background = _light ? new Color4(0.976f, 0.976f, 0.976f, 0.93f) : new Color4(0.125f, 0.125f, 0.125f, 0.90f);
        var border = _light ? new Color4(0, 0, 0, 0.10f) : new Color4(1, 1, 1, 0.09f);
        using (var fill = rt.CreateSolidColorBrush(background))
        using (var line = rt.CreateSolidColorBrush(border))
        {
            var pill = new RoundedRectangle(new System.Drawing.RectangleF(0.5f, 0.5f, widthDips - 1, Height - 1), Radius, Radius);
            rt.FillRoundedRectangle(pill, fill);
            rt.DrawRoundedRectangle(pill, line, 1);
        }

        rt.PushAxisAlignedClip(new Rect(Pad / 2, 0, widthDips - Pad, Height), Vortice.Direct2D1.AntialiasMode.Aliased);
        var reduce = _reduceMotion();
        var e = (float)progress;
        if (previous is not null && e < 1)
        {
            // The push: the old line rises out as the new one rises in (a cross-fade with Reduce Motion).
            DrawLine(rt, previous, widthDips, reduce ? 0 : -Height * e, 1 - e);
            DrawLine(rt, next, widthDips, reduce ? 0 : Height * (1 - e), e);
        }
        else
        {
            DrawLine(rt, next, widthDips, 0, 1);
        }
        rt.PopAxisAlignedClip();
        rt.EndDraw();

        var size = new SIZE { Cx = _pixelWidth, Cy = _pixelHeight };
        var source = new POINT();
        var destination = new POINT { X = _position.X, Y = _position.Y };
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        nint screen = GetDC(0);
        UpdateLayeredWindow(_hwnd, screen, &destination, &size, _memoryDc, &source, 0, &blend, ULW_ALPHA);
        ReleaseDC(0, screen);

        if (!_animating || progress >= 1)
        {
            lock (_frameGate)
            {
                var length = _pixelWidth * _pixelHeight * 4;
                if (_frame?.Length != length) _frame = new byte[length];
                new ReadOnlySpan<byte>(_bits, length).CopyTo(_frame);
                _frameWidth = _pixelWidth;
                _frameHeight = _pixelHeight;
            }
        }
    }

    private void DrawLine(ID2D1RenderTarget rt, TickerShown shown, float widthDips, float dy, float alpha)
    {
        if (alpha <= 0.001f) return;
        var a = alpha * (shown.Dimmed ? 0.55f : 0.96f);
        var ink = _light ? new Color4(0.09f, 0.09f, 0.09f, a) : new Color4(1, 1, 1, a);
        using var brush = rt.CreateSolidColorBrush(ink);
        var x = Pad;
        if (shown.PauseSymbol)
        {
            using var glyph = _dwrite!.CreateTextLayout("", _glyph!, (float)TrayTickerModel.SymbolWidth, Height);
            glyph.ParagraphAlignment = ParagraphAlignment.Center;
            rt.DrawTextLayout(new System.Numerics.Vector2(x, dy), glyph, brush, DrawTextOptions.None);
            x += (float)TrayTickerModel.SymbolWidth;
        }
        using var layout = _dwrite!.CreateTextLayout(shown.Text, _text!, Math.Max(1, widthDips - x - Pad), Height);
        layout.ParagraphAlignment = ParagraphAlignment.Center;
        layout.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, null);
        rt.DrawTextLayout(new System.Numerics.Vector2(x, dy), layout, brush, DrawTextOptions.None);
    }
}
