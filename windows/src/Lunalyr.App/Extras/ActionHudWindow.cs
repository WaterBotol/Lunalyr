using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Lunalyr.App.Interop;
using DRect = System.Drawing.RectangleF;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Player;
using Lunalyr.Platform.Desktop;
using Lunalyr.Shell.Extras;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace Lunalyr.App.Extras;

/// <summary>
/// The action HUD (the Mac's <c>ActionHUD</c>): a brief confirmation at the top of the display the pointer is on, for
/// what a global shortcut or a <c>lunalyr://</c> link just did, or a pause from the tray. A layered, click-through
/// tool window that is never activated (<see cref="ActionHudStyles"/>), hidden whenever it isn't showing, so it costs
/// nothing at idle. A second note while one shows changes the text in place and restarts only the hold. Drawn with
/// Direct2D into a premultiplied bitmap handed to <c>UpdateLayeredWindow</c>; Windows' light or dark app mode,
/// Transparency effects (off: an opaque capsule), contrast themes and Animation effects (off: no settle) are followed.
/// Runs on the UI thread.
/// </summary>
internal sealed unsafe class ActionHudWindow : IDisposable
{
    private const string ClassName = "Lunalyr.ActionHud";
    private const string TextFamily = "Segoe UI Variable Text", GlyphFamily = "Segoe Fluent Icons";
    private const float TextSize = 14, GlyphSize = 14, Padding = 14, IconGap = 8;
    private const nuint TimerId = 1;

    private static ActionHudWindow? s_instance;

    private readonly CategoryLogger _log;
    private readonly Func<PlayerLook> _readLook;
    private readonly string? _capturePath;
    private readonly ActionHudMotion _motion = new();
    private readonly long _epoch = Stopwatch.GetTimestamp();
    private ID2D1Factory? _d2d;
    private IDWriteFactory? _dwrite;
    private ID2D1DCRenderTarget? _target;
    private nint _hwnd, _memDc, _bitmap, _oldBitmap, _bits;
    private int _width, _height;
    private HudRect _frame;
    private double _scale = 1;
    private bool _shown;
    private int _captures;

    /// <param name="log">Where problems go (the App category).</param>
    /// <param name="readLook">Windows' look now (light or dark, transparency, contrast, Animation effects).</param>
    /// <param name="capturePath">For checks: each note's bitmap is also written here as a PNG (<c>--hud-capture</c>).</param>
    public ActionHudWindow(CategoryLogger log, Func<PlayerLook> readLook, string? capturePath = null)
    {
        _log = log;
        _readLook = readLook;
        _capturePath = capturePath;
    }

    /// <summary>The window, once a note has made it (for checks).</summary>
    public nint Handle => _hwnd;

    /// <summary>Shows <paramref name="text"/> with <paramref name="symbol"/> (a Mac SF Symbol name, drawn as its Windows glyph).</summary>
    public void Show(string text, string symbol)
    {
        try
        {
            EnsureWindow();
            var look = _readLook();
            var now = Now();
            Place();
            var appears = _motion.Show(now, look.ReduceMotion);
            Paint(text, symbol, look);
            Present(now);
            if (appears) _log.Debug($"Action HUD: \"{text}\".");
            Schedule(now);
            Capture(text);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or SharpGen.Runtime.SharpGenException)
        {
            _log.Warn("The action HUD couldn't show its note.", ex);
        }
    }

    public void Dispose()
    {
        if (_hwnd != 0)
        {
            ExtrasNative.KillTimer(_hwnd, TimerId);
            Win32.DestroyWindow(_hwnd);
            _hwnd = 0;
            Win32.UnregisterClass(ClassName, Win32.GetModuleHandle(null));
        }
        ReleaseBitmap();
        _target?.Dispose();
        _target = null;
        _dwrite?.Dispose();
        _d2d?.Dispose();
        if (ReferenceEquals(s_instance, this)) s_instance = null;
    }

    private double Now() => Stopwatch.GetElapsedTime(_epoch).TotalSeconds;

    // ---- The window ----

    private void EnsureWindow()
    {
        if (_hwnd != 0) return;
        s_instance = this;
        var instance = Win32.GetModuleHandle(null);
        fixed (char* name = ClassName)
        {
            var wc = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                lpszClassName = name,
            };
            if (Win32.RegisterClassEx(wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
        }
        _hwnd = Win32.CreateWindowEx(ActionHudStyles.ExStyle, ClassName, "Lunalyr", ActionHudStyles.Style, 0, 0, 1, 1, 0, 0, instance, 0);
        if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
        // Out of screenshots and screen sharing (the Mac's sharingType = .none); still on the screen itself.
        if (!ExtrasNative.SetWindowDisplayAffinity(_hwnd, ActionHudStyles.DisplayAffinity))
            _log.Debug($"Action HUD: display affinity not set ({Marshal.GetLastPInvokeError()}); it may show in captures.");
    }

    /// <summary>Centred at the top of the work area of the display with the pointer (else the main one).</summary>
    private void Place()
    {
        Win32.GetCursorPos(out var cursor);
        var monitor = ExtrasNative.MonitorFromPoint(cursor, ExtrasNative.MONITOR_DEFAULTTONEAREST);
        var info = new ExtrasNative.MONITORINFO { cbSize = (uint)sizeof(ExtrasNative.MONITORINFO) };
        if (monitor == 0 || !ExtrasNative.GetMonitorInfo(monitor, ref info)) return;
        _scale = ExtrasNative.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        var width = (int)Math.Round(ActionHudLayout.PanelWidth * _scale);
        var height = (int)Math.Round(ActionHudLayout.PanelHeight * _scale);
        _frame = ActionHudLayout.Frame(width, height, Rect(info.rcMonitor), Rect(info.rcWork), _scale);
        EnsureBitmap((int)_frame.Width, (int)_frame.Height);
    }

    private static HudRect Rect(Win32.RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>Hands the bitmap to the window at the motion's opacity and settle, showing it (never activated) if needed.</summary>
    private void Present(double now)
    {
        var alpha = Math.Clamp(_motion.Alpha(now), 0, 1);
        var offset = _motion.SettleOffset(now) * _scale;
        var position = new Win32.POINT { X = (int)_frame.X, Y = (int)Math.Round(_frame.Y + offset) };
        var size = new ExtrasNative.SIZE { cx = _width, cy = _height };
        var source = new Win32.POINT();
        var blend = new ExtrasNative.BLENDFUNCTION
        {
            BlendOp = ExtrasNative.AC_SRC_OVER, SourceConstantAlpha = (byte)Math.Round(alpha * 255), AlphaFormat = ExtrasNative.AC_SRC_ALPHA,
        };
        if (!ExtrasNative.UpdateLayeredWindow(_hwnd, 0, &position, &size, _memDc, &source, 0, &blend, ExtrasNative.ULW_ALPHA))
            _log.Debug($"Action HUD: UpdateLayeredWindow failed ({Marshal.GetLastPInvokeError()}).");
        if (!_shown)
        {
            _shown = true;
            ExtrasNative.ShowWindow(_hwnd, ExtrasNative.SW_SHOWNOACTIVATE);
        }
        // Above every app, full-screen ones too, without activating it.
        ExtrasNative.SetWindowPos(_hwnd, ExtrasNative.HWND_TOPMOST, 0, 0, 0, 0,
            ExtrasNative.SWP_NOMOVE | ExtrasNative.SWP_NOSIZE | ExtrasNative.SWP_NOACTIVATE | ExtrasNative.SWP_SHOWWINDOW);
    }

    /// <summary>A frame every 15 ms while it moves; otherwise one tick when the hold ends.</summary>
    private void Schedule(double now)
    {
        uint wait;
        if (_motion.Animating(now)) wait = 15;
        else if (_motion.HideAt is { } hideAt) wait = (uint)Math.Clamp((hideAt - now) * 1000 + 1, 1, 10_000);
        else wait = 15;
        ExtrasNative.SetTimer(_hwnd, TimerId, wait, 0);
    }

    private void Tick()
    {
        var now = Now();
        if (!_motion.Advance(now))
        {
            ExtrasNative.KillTimer(_hwnd, TimerId);
            ExtrasNative.ShowWindow(_hwnd, ExtrasNative.SW_HIDE);
            _shown = false;
            return;
        }
        Present(now);
        Schedule(now);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case ExtrasNative.WM_MOUSEACTIVATE:
                    return ActionHudStyles.MouseActivate; // never takes the foreground, even if clicked
                case ExtrasNative.WM_NCHITTEST:
                    return ActionHudStyles.HitTest; // clicks go to the window beneath
                case ExtrasNative.WM_TIMER when wParam == TimerId:
                    s_instance?.Tick();
                    return 0;
            }
        }
        catch (Exception ex)
        {
            s_instance?._log.Warn("The action HUD's window failed.", ex);
            return 0;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ---- Drawing ----

    private void EnsureBitmap(int width, int height)
    {
        if (width == _width && height == _height && _memDc != 0) return;
        ReleaseBitmap();
        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Win32.BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32,
        };
        var screen = ExtrasNative.GetDC(0);
        try
        {
            _memDc = ExtrasNative.CreateCompatibleDC(screen);
            _bitmap = Win32.CreateDIBSection(screen, header, 0, out _bits, 0, 0);
        }
        finally
        {
            ExtrasNative.ReleaseDC(0, screen);
        }
        if (_memDc == 0 || _bitmap == 0) throw new InvalidOperationException("The HUD's bitmap couldn't be made.");
        _oldBitmap = ExtrasNative.SelectObject(_memDc, _bitmap);
        _width = width;
        _height = height;
    }

    private void ReleaseBitmap()
    {
        if (_memDc != 0)
        {
            if (_oldBitmap != 0) ExtrasNative.SelectObject(_memDc, _oldBitmap);
            ExtrasNative.DeleteDC(_memDc);
        }
        if (_bitmap != 0) Win32.DeleteObject(_bitmap);
        _memDc = _bitmap = _oldBitmap = _bits = 0;
        _width = _height = 0;
    }

    private void Paint(string text, string symbol, PlayerLook look)
    {
        _d2d ??= D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        _dwrite ??= DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        var dpi = (float)(96 * _scale);
        _target?.Dispose();
        _target = _d2d.CreateDCRenderTarget(new RenderTargetProperties(RenderTargetType.Default,
            new D2DPixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), dpi, dpi, RenderTargetUsage.None,
            FeatureLevel.Default));
        _target.BindDC(_memDc, new RawRect(0, 0, _width, _height));
        _target.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale; // ClearType can't blend onto alpha

        var ink = look.HighContrast ? new Color4(1, 1, 1, 1) : look.Light ? new Color4(0, 0, 0, 0.9f) : new Color4(1, 1, 1, 0.95f);
        var fill = look.HighContrast ? new Color4(0, 0, 0, 1)
            : look.Light ? new Color4(0.976f, 0.976f, 0.976f, look.ReduceTransparency ? 1 : 0.93f)
            : new Color4(0.173f, 0.173f, 0.173f, look.ReduceTransparency ? 1 : 0.93f);
        var stroke = look.HighContrast ? new Color4(1, 1, 1, 1) : look.Light ? new Color4(0, 0, 0, 0.12f) : new Color4(1, 1, 1, 0.12f);

        using var textFormat = _dwrite.CreateTextFormat(TextFamily, null, FontWeight.Medium, Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal, TextSize, "");
        using var glyphFormat = _dwrite.CreateTextFormat(GlyphFamily, null, FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal, GlyphSize, "");
        var panelWidth = (float)(_width / _scale);
        var panelHeight = (float)(_height / _scale);
        var margin = (float)ActionHudLayout.Margin;
        var glyph = Glyph(symbol);
        using var glyphLayout = _dwrite.CreateTextLayout(glyph, glyphFormat, 40, 40);
        var maxText = panelWidth - 2 * margin - 2 * Padding - glyphLayout.Metrics.Width - IconGap;
        using var layout = _dwrite.CreateTextLayout(text, textFormat, Math.Max(40, maxText), 40);
        layout.WordWrapping = WordWrapping.NoWrap;
        using (var sign = _dwrite.CreateEllipsisTrimmingSign(layout))
            layout.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, sign);
        var textWidth = Math.Min(layout.Metrics.WidthIncludingTrailingWhitespace, Math.Max(40, maxText));
        var capsuleWidth = Padding + glyphLayout.Metrics.Width + IconGap + textWidth + Padding;
        var capsuleHeight = (float)ActionHudLayout.CapsuleHeight;
        var capsule = new DRect((panelWidth - capsuleWidth) / 2, (panelHeight - capsuleHeight) / 2, capsuleWidth, capsuleHeight);
        var radius = capsuleHeight / 2;

        _target.BeginDraw();
        _target.Clear(new Color4(0, 0, 0, 0));
        using (var brush = _target.CreateSolidColorBrush(new Color4(0, 0, 0, 0.05f)))
        {
            // A soft shadow (the Mac's: black 18%, radius 6, 2 down), as a few widening rings.
            if (!look.HighContrast)
                for (var i = 3; i >= 1; i--)
                {
                    var grow = i * 2f;
                    var ring = new DRect(capsule.Left - grow, capsule.Top - grow + 2, capsule.Width + 2 * grow, capsule.Height + 2 * grow);
                    _target.FillRoundedRectangle(new RoundedRectangle(ring, radius + grow, radius + grow), brush);
                }
            brush.Color = fill;
            _target.FillRoundedRectangle(new RoundedRectangle(capsule, radius, radius), brush);
            brush.Color = stroke;
            var inset = new DRect(capsule.Left + 0.25f, capsule.Top + 0.25f, capsule.Width - 0.5f, capsule.Height - 0.5f);
            _target.DrawRoundedRectangle(new RoundedRectangle(inset, radius, radius), brush, look.HighContrast ? 1f : 0.5f);
            brush.Color = ink;
            var x = capsule.Left + Padding;
            _target.DrawTextLayout(new Vector2(x, capsule.Top + (capsuleHeight - glyphLayout.Metrics.Height) / 2), glyphLayout, brush);
            x += glyphLayout.Metrics.Width + IconGap;
            _target.DrawTextLayout(new Vector2(x, capsule.Top + (capsuleHeight - layout.Metrics.Height) / 2), layout, brush);
        }
        _target.EndDraw();
    }

    /// <summary>The Segoe Fluent Icons glyph standing in for a Mac SF Symbol name.</summary>
    public static string Glyph(string symbol) => symbol switch
    {
        "eye" => "",
        "eye.slash" => "",
        "clock" => "",
        "clock.arrow.circlepath" => "",
        "square.grid.3x3" => "",
        "photo" => "",
        "pause.rectangle.fill" => "",
        "play.rectangle.fill" => "",
        "film" => "",
        "waveform" => "",
        "macwindow" => "",
        "arrow.down.circle" => "",
        "person.2" => "",
        _ => "",
    };

    /// <summary><c>--hud-capture</c>: the note's bitmap as a PNG (the window itself is left out of screen captures).</summary>
    private void Capture(string text)
    {
        if (_capturePath is null || _bits == 0) return;
        try
        {
            var path = _captures++ == 0 ? _capturePath
                : Path.Combine(Path.GetDirectoryName(_capturePath) ?? "", $"{Path.GetFileNameWithoutExtension(_capturePath)}-{_captures}{Path.GetExtension(_capturePath)}");
            var bytes = new ReadOnlySpan<byte>((void*)_bits, _width * _height * 4);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, HudPng.Encode(bytes, _width, _height));
            var ex = (uint)ExtrasNative.GetWindowLongPtr(_hwnd, ExtrasNative.GWL_EXSTYLE);
            var style = (uint)ExtrasNative.GetWindowLongPtr(_hwnd, ExtrasNative.GWL_STYLE);
            ExtrasNative.GetWindowDisplayAffinity(_hwnd, out var affinity);
            _log.Info($"Action HUD capture: \"{text}\" written to {path} ({_width}×{_height} at {_frame.X},{_frame.Y}, scale {_scale:0.##}); "
                + $"style 0x{style:X8}, extended style 0x{ex:X8}, display affinity 0x{affinity:X}, "
                + $"foreground is the HUD: {ExtrasNative.GetForegroundWindow() == _hwnd}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Couldn't write the HUD capture to {_capturePath}.", ex);
        }
    }
}
