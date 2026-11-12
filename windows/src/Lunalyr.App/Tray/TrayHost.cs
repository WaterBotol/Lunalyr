using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Lunalyr.App.Interop;
using Lunalyr.Core.Logging;
using Lunalyr.Shell.Flyout;

namespace Lunalyr.App.Tray;

/// <summary>
/// The app's hidden top-level window and its notification-area icon, in plain Win32 so the app idles without XAML.
/// The window also receives activations from a second launch (<c>WM_COPYDATA</c>), Explorer restarts
/// (<c>TaskbarCreated</c>) and work posted from other threads (<see cref="Post"/>). Everything runs on the UI thread,
/// whichever message loop (the plain one or WinUI's) is pumping it.
/// </summary>
internal sealed unsafe class TrayHost : IDisposable
{
    public const uint WM_TRAY = Win32.WM_APP + 1;
    public const uint WM_APP_QUIT = Win32.WM_APP + 2;
    public const uint WM_APP_INVOKE = Win32.WM_APP + 3;

    /// <summary>Marks a <c>WM_COPYDATA</c> as Lunalyr's command line ("LUNA").</summary>
    public const nuint ActivationMagic = 0x4C554E41;

    private const uint IconId = 1;
    private const int MenuOpenSettings = 1, MenuTogglePause = 2, MenuQuit = 3;

    private static TrayHost? s_instance;

    private readonly string _className;
    private readonly CategoryLogger _log;
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly uint _taskbarCreated;
    private nint _hwnd;
    private nint _icon;
    private bool _iconAdded;
    private string _tooltip = "Lunalyr";

    public TrayHost(string className, Logger logger)
    {
        _className = className;
        _log = logger.App;
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
    }

    public nint Handle => _hwnd;

    public string ClassName => _className;

    public Action? OpenSettings { get; set; }

    /// <summary>
    /// A left click (or Enter/Space on the focused icon): opens or closes the flyout beside the icon. Given the icon's
    /// rectangle (or the click point, as an empty one) and whether the keyboard did it. Without it, the click opens settings.
    /// </summary>
    public Action<PixelRect, bool>? ToggleFlyout { get; set; }

    public Action? TogglePause { get; set; }

    public Action? Quit { get; set; }

    /// <summary>A second launch forwarded its command line.</summary>
    public Action<string[]>? Activated { get; set; }

    /// <summary>The disabled first line of the menu ("Song — Artist") and whether the wallpaper is paused.</summary>
    public Func<(string NowPlaying, bool Paused)>? DescribeMenu { get; set; }

    /// <summary>A system setting changed (<c>WM_SETTINGCHANGE</c>, broadcast to top-level windows): read what matters again.</summary>
    public Action? SettingChanged { get; set; }

    /// <summary>Called when something inside a window message throws (it can't propagate to Windows).</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>Creates the hidden window. Call on the UI thread.</summary>
    public void Create()
    {
        if (s_instance is not null) throw new InvalidOperationException("Only one tray host per process.");
        s_instance = this;
        var instance = Win32.GetModuleHandle(null);
        fixed (char* name = _className)
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

        // A hidden top-level tool window, not a message-only one: only top-level windows get TaskbarCreated.
        _hwnd = Win32.CreateWindowEx(Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE, _className, "Lunalyr", Win32.WS_POPUP,
            0, 0, 0, 0, 0, 0, instance, 0);
        if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
    }

    /// <summary>Adds the notification-area icon (again, after Explorer restarts). Returns whether the shell took it.</summary>
    public bool AddIcon()
    {
        if (_icon == 0) _icon = TrayIconArt.Create(IconSize(), TrayIconArt.TaskbarIsLight());
        var data = NewData(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        _iconAdded = Win32.Shell_NotifyIcon(Win32.NIM_ADD, data);
        if (_iconAdded)
        {
            data.uVersion = Win32.NOTIFYICON_VERSION_4;
            Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, data);
            var id = new Win32.NOTIFYICONIDENTIFIER { cbSize = (uint)sizeof(Win32.NOTIFYICONIDENTIFIER), hWnd = _hwnd, uID = IconId };
            var hr = Win32.Shell_NotifyIconGetRect(id, out var rect);
            _log.Info(hr == 0
                ? $"Tray icon added (window 0x{_hwnd:X}, icon at {rect.Left},{rect.Top}–{rect.Right},{rect.Bottom})."
                : $"Tray icon added (window 0x{_hwnd:X}; its position isn't known yet, 0x{hr:X8}).");
        }
        else
        {
            _log.Warn("The notification area didn't take the tray icon (Explorer may still be starting); trying again when it's back.");
        }

        return _iconAdded;
    }

    /// <summary>The icon's rectangle on screen in physical pixels, or null when the shell doesn't say.</summary>
    public PixelRect? IconRect()
    {
        if (!_iconAdded) return null;
        var id = new Win32.NOTIFYICONIDENTIFIER { cbSize = (uint)sizeof(Win32.NOTIFYICONIDENTIFIER), hWnd = _hwnd, uID = IconId };
        if (Win32.Shell_NotifyIconGetRect(id, out var rect) != 0) return null;
        var r = new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return r.IsEmpty ? null : r;
    }

    /// <summary>Sets the hover text (up to 127 characters; longer text is cut).</summary>
    public void SetTooltip(string text)
    {
        _tooltip = text.Length > 127 ? text[..126] + "…" : text;
        if (!_iconAdded) return;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, NewData(Win32.NIF_TIP | Win32.NIF_SHOWTIP));
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread. Safe from any thread.</summary>
    public void Post(Action action)
    {
        _posted.Enqueue(action);
        if (_hwnd != 0) Win32.PostMessage(_hwnd, WM_APP_INVOKE, 0, 0);
    }

    public void PostQuit()
    {
        if (_hwnd != 0) Win32.PostMessage(_hwnd, WM_APP_QUIT, 0, 0);
    }

    public void Dispose()
    {
        if (_iconAdded)
        {
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, NewData(0));
            _iconAdded = false;
        }

        if (_icon != 0)
        {
            Win32.DestroyIcon(_icon);
            _icon = 0;
        }

        if (_hwnd != 0)
        {
            Win32.DestroyWindow(_hwnd);
            _hwnd = 0;
            Win32.UnregisterClass(_className, Win32.GetModuleHandle(null));
        }

        if (ReferenceEquals(s_instance, this)) s_instance = null;
    }

    private Win32.NOTIFYICONDATAW NewData(uint flags)
    {
        var data = new Win32.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(Win32.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = WM_TRAY,
            hIcon = _icon,
        };
        Win32.CopyTo(_tooltip, data.szTip, 128);
        return data;
    }

    private static int IconSize() => Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, Win32.GetDpiForSystem());

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        var host = s_instance;
        if (host is null) return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        try
        {
            return host.OnMessage(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            try
            {
                host.OnError?.Invoke(ex);
            }
            catch (Exception)
            {
                // Nothing more can be done inside a window procedure.
            }

            return 0;
        }
    }

    private nint OnMessage(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _log.Info("Explorer restarted: adding the tray icon again.");
            _iconAdded = false;
            AddIcon();
            return 0;
        }

        switch (msg)
        {
            case WM_TRAY:
                switch ((uint)Win32.LoWord(lParam))
                {
                    case Win32.WM_CONTEXTMENU:
                        ShowMenu((short)Win32.LoWord((nint)wParam), (short)Win32.HiWord((nint)wParam));
                        break;
                    case Win32.NIN_SELECT:
                    case Win32.NIN_KEYSELECT:
                        if (ToggleFlyout is { } toggle)
                        {
                            // Version 4 puts the click's (or the icon's) point in wParam; the icon's own rectangle is better.
                            var x = (short)Win32.LoWord((nint)wParam);
                            var y = (short)Win32.HiWord((nint)wParam);
                            toggle(IconRect() ?? new PixelRect(x, y, x, y), (uint)Win32.LoWord(lParam) == Win32.NIN_KEYSELECT);
                        }
                        else
                        {
                            OpenSettings?.Invoke();
                        }

                        break;
                }

                return 0;

            case Win32.WM_COPYDATA:
                {
                    var data = (Win32.COPYDATASTRUCT*)lParam;
                    if (data is null || data->dwData != ActivationMagic) return 0;
                    var text = data->cbData == 0 ? "" : new string((char*)data->lpData, 0, (int)(data->cbData / 2));
                    var args = text.Length == 0 ? [] : text.Split('\0');
                    // Act after replying, so the second launch isn't kept waiting.
                    Post(() => Activated?.Invoke(args));
                    return 1;
                }

            case WM_APP_QUIT:
                Quit?.Invoke();
                return 0;

            case WM_APP_INVOKE:
                while (_posted.TryDequeue(out var action)) action();
                return 0;

            case Win32.WM_SETTINGCHANGE:
                if (lParam != 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet") RedrawIcon();
                SettingChanged?.Invoke();
                break;
        }

        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void RedrawIcon()
    {
        var old = _icon;
        _icon = TrayIconArt.Create(IconSize(), TrayIconArt.TaskbarIsLight());
        if (_iconAdded) Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, NewData(Win32.NIF_ICON));
        if (old != 0) Win32.DestroyIcon(old);
    }

    private void ShowMenu(int x, int y)
    {
        var (nowPlaying, paused) = DescribeMenu?.Invoke() ?? ("Lunalyr", false);
        var menu = Win32.CreatePopupMenu();
        try
        {
            Win32.AppendMenu(menu, Win32.MF_STRING | Win32.MF_GRAYED, 0, MenuText(nowPlaying));
            Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
            Win32.AppendMenu(menu, Win32.MF_STRING, MenuOpenSettings, "Open settings");
            Win32.AppendMenu(menu, Win32.MF_STRING, MenuTogglePause, paused ? "Resume wallpaper" : "Pause wallpaper");
            Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
            Win32.AppendMenu(menu, Win32.MF_STRING, MenuQuit, "Quit Lunalyr");
            // The menu closes when clicking elsewhere only if our window is in front first.
            Win32.SetForegroundWindow(_hwnd);
            var command = Win32.TrackPopupMenuEx(menu,
                Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY | Win32.TPM_BOTTOMALIGN, x, y, _hwnd, 0);
            Win32.PostMessage(_hwnd, Win32.WM_NULL, 0, 0);
            switch (command)
            {
                case MenuOpenSettings: OpenSettings?.Invoke(); break;
                case MenuTogglePause: TogglePause?.Invoke(); break;
                case MenuQuit: Quit?.Invoke(); break;
            }
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    // Menu text treats '&' as a mnemonic marker, and very long titles make a very wide menu.
    private static string MenuText(string text)
    {
        var shortened = text.Length > 64 ? text[..63] + "…" : text;
        return shortened.Replace("&", "&&", StringComparison.Ordinal);
    }
}
