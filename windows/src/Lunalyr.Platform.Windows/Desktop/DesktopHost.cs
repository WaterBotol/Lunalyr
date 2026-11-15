using System.Runtime.InteropServices;
using Lunalyr.Core.Logging;
using static Lunalyr.Platform.Desktop.DesktopNative;

namespace Lunalyr.Platform.Desktop;

public sealed record DesktopHostOptions
{
    /// <summary>One ordinary window on the primary monitor instead of wallpapers behind the icons.</summary>
    public bool Windowed { get; init; }
    public int WindowedWidth { get; init; } = 1280;
    public int WindowedHeight { get; init; } = 720;

    /// <summary>How often the window list, the screen saver and the windows' health are checked when nothing announces a change.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Owns the wallpaper windows on a thread of its own, with its own message loop, so it always answers Explorer
/// promptly (the windows are children of Explorer's <c>Progman</c>) while the renderer draws on another thread.
/// It creates one window per monitor behind the desktop icons, rebuilds them when the display layout, DPI or
/// Explorer changes, and reports what can be seen: display power, session lock and switches, screen saver,
/// full-screen apps, covering windows, battery and Energy saver.
/// </summary>
public sealed unsafe class DesktopHost : IDisposable
{
    private const uint WM_APP = 0x8000;
    private const uint MsgRebuild = WM_APP + 1, MsgCheckVisibility = WM_APP + 2, MsgPushState = WM_APP + 3, MsgStop = WM_APP + 4;
    private const nuint TimerPoll = 1, TimerLayout = 2, TimerVisibility = 3;
    private const uint LayoutDelayMs = 500, VisibilityDelayMs = 250;

    [ThreadStatic] private static DesktopHost? t_host;

    private readonly IDesktopSurfaceSink _sink;
    private readonly DesktopHostOptions _options;
    private readonly CategoryLogger _log;
    private readonly string _surfaceClass, _messageClass;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private Exception? _startError;

    // Host-thread state.
    private nint _messageWindow;
    private uint _taskbarCreated;
    private readonly List<SurfaceWindow> _windows = [];
    private readonly HashSet<nint> _own = [];
    private List<MonitorInfo> _monitors = [];
    private int _generation;
    private bool _building, _forceRebuild, _rebuildDeferred;
    private string _rebuildReason = "";
    private SystemVisibility _system;
    private bool _windowVisible = true;
    private MonitorVisibility[] _visibility = [];
    private DesktopState? _lastState;
    private nint _displayNotify, _acdcNotify;
    private readonly List<nint> _hooks = [];
    private PowerStateMonitor? _battery, _energySaver;

    public DesktopHost(IDesktopSurfaceSink sink, DesktopHostOptions? options = null, Logger? logger = null)
    {
        _sink = sink;
        _options = options ?? new DesktopHostOptions();
        _log = (logger ?? Logger.Null).Render;
        string suffix = Environment.ProcessId.ToString("X") + "_" + Guid.NewGuid().ToString("N")[..8];
        _surfaceClass = "LunalyrWallpaper_" + suffix;
        _messageClass = "LunalyrDesktopHost_" + suffix;
    }

    /// <summary>Raised on the host thread when the preview window is closed (windowed mode).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>How the current windows are attached; null before the first build or while Explorer is missing.</summary>
    public AttachMode? Mode { get; private set; }

    /// <summary>Progman's children after the last attach (for logs and checks).</summary>
    public string LastTree { get; private set; } = "";

    public IReadOnlyList<MonitorInfo> Monitors => Volatile.Read(ref _monitors);

    /// <summary>Starts the host thread and returns once the first windows are handed to the renderer.</summary>
    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Lunalyr desktop host" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startError is not null) throw new InvalidOperationException("The desktop host couldn't start.", _startError);
    }

    /// <summary>Destroys the windows and ends the host thread.</summary>
    public void Stop()
    {
        if (_thread is null) return;
        if (_messageWindow != 0) PostMessage(_messageWindow, MsgStop, 0, 0);
        _thread.Join(5000);
        _thread = null;
    }

    public void Dispose() => Stop();

    /// <summary>Rebuild every window (any thread): used after device changes the renderer can't absorb itself.</summary>
    public void RequestRebuild()
    {
        nint w = _messageWindow;
        if (w != 0) PostMessage(w, MsgRebuild, 0, 0);
    }

    /// <summary>Re-check what can be seen soon (any thread).</summary>
    public void RequestVisibilityCheck()
    {
        nint w = _messageWindow;
        if (w != 0) PostMessage(w, MsgCheckVisibility, 0, 0);
    }

    private void Run()
    {
        t_host = this;
        // Physical pixels whatever the process default: the windows, monitor bounds and DPI read on this thread all
        // follow the thread's awareness.
        SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            Initialise();
            Build("start");
        }
        catch (Exception ex)
        {
            _startError = ex;
            _log.Error("Desktop host failed to start", ex);
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

    private void Initialise()
    {
        nint inst = GetModuleHandle(null);
        fixed (char* surfaceClass = _surfaceClass)
        fixed (char* messageClass = _messageClass)
        fixed (char* title = "Lunalyr desktop host")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = sizeof(WNDCLASSEXW), lpfnWndProc = &SurfaceProc, hInstance = inst, lpszClassName = surfaceClass,
                hCursor = LoadCursor(0, 32512 /* IDC_ARROW */),
            };
            if (RegisterClassEx(&wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
            var mc = new WNDCLASSEXW { cbSize = sizeof(WNDCLASSEXW), lpfnWndProc = &MessageProc, hInstance = inst, lpszClassName = messageClass };
            if (RegisterClassEx(&mc) == 0) throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
            // A hidden top-level window: message-only windows don't get the TaskbarCreated or display broadcasts.
            _messageWindow = CreateWindowEx(WS_EX_TOOLWINDOW, messageClass, title, WS_POPUP, 0, 0, 0, 0, 0, 0, inst, 0);
            if (_messageWindow == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        }
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        // Display power and the power source arrive as WM_POWERBROADCAST; each sends its current value at once.
        Guid display = GUID_CONSOLE_DISPLAY_STATE, acdc = GUID_ACDC_POWER_SOURCE;
        _displayNotify = RegisterPowerSettingNotification(_messageWindow, &display, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        _acdcNotify = RegisterPowerSettingNotification(_messageWindow, &acdc, 0);
        if (!WTSRegisterSessionNotification(_messageWindow, 0 /* NOTIFY_FOR_THIS_SESSION */))
            _log.Warn("Session notifications unavailable: lock and user switches won't pause the wallpaper");

        // Window changes that can cover or uncover the desktop. Out of context: callbacks arrive on this thread.
        AddHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        AddHook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND);
        AddHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        AddHook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);

        _battery = new PowerStateMonitor(DesktopReader.OnBattery, _ => null); // refreshed from GUID_ACDC_POWER_SOURCE
        _energySaver = EnergySaver.CreateMonitor(() => PostMessage(_messageWindow, MsgPushState, 0, 0), _log);
        _system = _system with { ScreenSaver = DesktopReader.ScreenSaverRunning() };

        SetTimer(_messageWindow, TimerPoll, (uint)Math.Max(250, _options.PollInterval.TotalMilliseconds), 0);
    }

    private void AddHook(uint min, uint max)
    {
        nint hook = SetWinEventHook(min, max, 0, &WinEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook != 0) _hooks.Add(hook);
    }

    private void Cleanup()
    {
        try
        {
            if (_windows.Count > 0) WaitPumping(_sink.ReleaseSurfaces(), 3000);
        }
        catch (Exception ex)
        {
            _log.Warn("Releasing surfaces at shutdown failed", ex);
        }
        DestroyWindows();
        foreach (nint hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        if (_displayNotify != 0) UnregisterPowerSettingNotification(_displayNotify);
        if (_acdcNotify != 0) UnregisterPowerSettingNotification(_acdcNotify);
        _displayNotify = _acdcNotify = 0;
        _energySaver?.Dispose();
        _battery?.Dispose();
        if (_messageWindow != 0)
        {
            WTSUnRegisterSessionNotification(_messageWindow);
            KillTimer(_messageWindow, TimerPoll);
            DestroyWindow(_messageWindow);
            _messageWindow = 0;
        }
        nint inst = GetModuleHandle(null);
        fixed (char* s = _surfaceClass) UnregisterClass(s, inst);
        fixed (char* m = _messageClass) UnregisterClass(m, inst);
        t_host = null;
    }

    // ---- Building the windows ----

    private void Build(string reason)
    {
        if (_building)
        {
            _rebuildDeferred = true;
            return;
        }
        _building = true;
        try
        {
            if (_windows.Count > 0 && !WaitPumping(_sink.ReleaseSurfaces(), 3000))
                _log.Warn("The renderer took over 3 s to release its swap chains; destroying the windows anyway");
            DestroyWindows();
            var monitors = DesktopReader.Monitors();
            Volatile.Write(ref _monitors, monitors);
            nint inst = GetModuleHandle(null);
            if (_options.Windowed)
            {
                CreatePreviewWindow(monitors, inst);
            }
            else
            {
                var target = DesktopAttach.Find();
                if (target is null)
                {
                    Mode = null;
                    _log.Warn($"No desktop window to attach to ({reason}); retrying");
                    ScheduleLayout(force: true, "desktop missing", 2000);
                    return;
                }
                foreach (var monitor in monitors) CreateWallpaperWindow(monitor, target, inst);
                Mode = target.Mode;
                LastTree = DesktopAttach.DescribeTree();
            }
            _generation++;
            _visibility = new MonitorVisibility[_windows.Count];
            _sink.SetSurfaces([.. _windows], _generation);
            _log.Info($"Desktop: {_windows.Count} surface(s) {(Mode?.ToString() ?? "none")} ({reason}): "
                + string.Join(", ", _windows.Select(w => $"{w.Monitor.DeviceName} {w.Width}x{w.Height} @{w.Monitor.Dpi}dpi {w.Monitor.RefreshHz}Hz")));
            CheckVisibility(force: true);
        }
        finally
        {
            _building = false;
            if (_rebuildDeferred)
            {
                _rebuildDeferred = false;
                ScheduleLayout(force: true, "deferred");
            }
        }
    }

    private void CreatePreviewWindow(List<MonitorInfo> monitors, nint inst)
    {
        var m = monitors.FirstOrDefault(x => x.IsPrimary) ?? monitors.FirstOrDefault();
        if (m is null) return;
        int w = (int)(_options.WindowedWidth * m.Scale), h = (int)(_options.WindowedHeight * m.Scale);
        nint hwnd;
        fixed (char* cls = _surfaceClass)
        fixed (char* title = "Lunalyr engine harness")
        {
            hwnd = CreateWindowEx(WS_EX_NOREDIRECTIONBITMAP, cls, title, WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                (int)m.WorkArea.X + 80, (int)m.WorkArea.Y + 80, w, h, 0, 0, inst, 0);
        }
        if (hwnd == 0)
        {
            _log.Error($"CreateWindowEx (preview) failed: {Marshal.GetLastPInvokeError()}");
            return;
        }
        GetClientRect(hwnd, out var cr);
        _own.Add(hwnd);
        _windows.Add(new SurfaceWindow(hwnd, m, Math.Max(1, cr.Right - cr.Left), Math.Max(1, cr.Bottom - cr.Top), AttachMode.Windowed));
        Mode = AttachMode.Windowed;
        _windowVisible = true;
    }

    private void CreateWallpaperWindow(MonitorInfo monitor, DesktopAttach.Target target, nint inst)
    {
        int x = (int)monitor.Bounds.X - target.OriginX, y = (int)monitor.Bounds.Y - target.OriginY;
        int w = (int)monitor.Bounds.Width, h = (int)monitor.Bounds.Height;
        nint hwnd;
        fixed (char* cls = _surfaceClass)
        fixed (char* title = "Lunalyr wallpaper")
        {
            hwnd = CreateWindowEx(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, cls, title,
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_DISABLED, x, y, w, h, target.Parent, 0, inst, 0);
        }
        if (hwnd == 0)
        {
            _log.Error($"CreateWindowEx (wallpaper on {monitor.DeviceName}) failed: {Marshal.GetLastPInvokeError()}");
            return;
        }
        // Directly below the icons (24H2+), so above the system wallpaper.
        if (target.InsertAfter != 0) SetWindowPos(hwnd, target.InsertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _own.Add(hwnd);
        _windows.Add(new SurfaceWindow(hwnd, monitor, w, h, target.Mode));
    }

    private void DestroyWindows()
    {
        foreach (var w in _windows) if (IsWindow(w.Hwnd)) DestroyWindow(w.Hwnd);
        _windows.Clear();
        _own.Clear();
    }

    /// <summary>Waits for <paramref name="handle"/> while still answering messages (Explorer, DXGI), so neither side can deadlock.</summary>
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
                if (m.message == WM_QUIT)
                {
                    PostQuitMessage((int)m.wParam);
                    return handle.WaitOne(Math.Max(0, (int)(deadline - Environment.TickCount64)));
                }
                TranslateMessage(&m);
                DispatchMessage(&m);
            }
        }
    }

    // ---- Layout and visibility ----

    private void ScheduleLayout(bool force, string reason, uint delayMs = LayoutDelayMs)
    {
        if (force)
        {
            _forceRebuild = true;
            _rebuildReason = reason;
        }
        // Coalesce bursts (an Explorer restart or a display change sends several messages) into one check.
        SetTimer(_messageWindow, TimerLayout, delayMs, 0);
    }

    private void OnLayoutTimer()
    {
        KillTimer(_messageWindow, TimerLayout);
        if (_building)
        {
            ScheduleLayout(false, "");
            return;
        }
        if (_forceRebuild)
        {
            _forceRebuild = false;
            Build(_rebuildReason);
            return;
        }
        var now = DesktopReader.Monitors();
        var change = MonitorLayout.Compare(_monitors, now);
        if (change.HasFlag(LayoutChange.Surfaces))
        {
            Build(change == LayoutChange.Monitors ? "monitors added or removed" : "display layout, DPI or refresh changed");
        }
        else if (change == LayoutChange.WorkArea)
        {
            Volatile.Write(ref _monitors, now);
            // The surfaces keep their monitors; refresh the work areas coverage reads.
            for (int i = 0; i < _windows.Count; i++)
            {
                var fresh = now.FirstOrDefault(m => m.DeviceName == _windows[i].Monitor.DeviceName);
                if (fresh is not null) _windows[i] = _windows[i] with { Monitor = fresh };
            }
            CheckVisibility(force: false);
        }
    }

    private void ScheduleVisibilityCheck() => SetTimer(_messageWindow, TimerVisibility, VisibilityDelayMs, 0);

    private void CheckVisibility(bool force)
    {
        if (!_options.Windowed && _windows.Count > 0)
        {
            var monitors = new MonitorInfo[_windows.Count];
            for (int i = 0; i < monitors.Length; i++) monitors[i] = _windows[i].Monitor;
            var windows = DesktopReader.Windows(_own);
            var (fgMonitor, d3d) = DesktopReader.Foreground();
            var visibility = DesktopVisibility.Evaluate(monitors, windows, fgMonitor, d3d);
            for (int i = 0; i < visibility.Length; i++)
            {
                bool was = i < _visibility.Length && _visibility[i].FullScreenApp;
                if (visibility[i].FullScreenApp && !was)
                {
                    var w = DesktopVisibility.FullScreenWindow(monitors[i], windows);
                    _log.Info($"Full-screen app on {monitors[i].DeviceName}: "
                        + (w is { } fw ? $"{fw.ClassName} {fw.Handle:X}" : d3d ? "exclusive Direct3D" : "unknown"));
                }
            }
            _visibility = visibility;
        }
        PushState(force);
    }

    private void PushState(bool force)
    {
        var state = new DesktopState(_generation, _system, _visibility, _battery?.Value ?? false, _energySaver?.Value ?? false, _windowVisible);
        if (!force && _lastState is { } last && last.Generation == state.Generation && last.System == state.System
            && last.OnBattery == state.OnBattery && last.EnergySaverOn == state.EnergySaverOn
            && last.WindowVisible == state.WindowVisible && last.Monitors.SequenceEqual(state.Monitors))
            return;
        _lastState = state;
        _sink.UpdateState(state);
    }

    private void OnPoll()
    {
        // Explorer can die without TaskbarCreated arriving yet (or at all): a window that is gone means re-attach.
        foreach (var w in _windows)
        {
            if (!IsWindow(w.Hwnd))
            {
                _log.Warn("A wallpaper window is gone (Explorer restarted?); re-attaching");
                ScheduleLayout(force: true, "window lost");
                return;
            }
        }
        if (!_options.Windowed && _windows.Count == 0 && !_forceRebuild) ScheduleLayout(force: true, "no surfaces");
        bool saver = DesktopReader.ScreenSaverRunning();
        if (saver != _system.ScreenSaver) _system = _system with { ScreenSaver = saver };
        CheckVisibility(force: false);
    }

    // ---- Window procedures (host thread) ----

    [UnmanagedCallersOnly]
    private static nint SurfaceProc(nint hwnd, uint msg, nuint w, nint l)
    {
        var host = t_host;
        if (host is not null)
        {
            try
            {
                if (host.OnSurfaceMessage(hwnd, msg, w, l) is { } handled) return handled;
            }
            catch (Exception ex)
            {
                host._log.Error("Wallpaper window message failed", ex);
            }
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    private nint? OnSurfaceMessage(nint hwnd, uint msg, nuint w, nint l)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return 1;
            case WM_MOUSEACTIVATE:
                return 3; // MA_NOACTIVATE
            case WM_CLOSE when _options.Windowed:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return 0;
            case WM_SIZE when _options.Windowed:
                bool visible = (int)w != SIZE_MINIMIZED;
                if (visible != _windowVisible)
                {
                    _windowVisible = visible;
                    PushState(force: false);
                }
                int cw = (int)(l & 0xFFFF), ch = (int)((l >> 16) & 0xFFFF);
                var current = _windows.FirstOrDefault(x => x.Hwnd == hwnd);
                if (visible && current is not null && cw > 0 && ch > 0 && (cw != current.Width || ch != current.Height))
                    ScheduleLayout(force: true, "preview resized");
                return 0;
            case WM_DPICHANGED:
                ScheduleLayout(force: _options.Windowed, "DPI changed");
                return 0;
        }
        return null;
    }

    [UnmanagedCallersOnly]
    private static nint MessageProc(nint hwnd, uint msg, nuint w, nint l)
    {
        var host = t_host;
        if (host is not null)
        {
            try
            {
                if (host.OnHostMessage(msg, w, l) is { } handled) return handled;
            }
            catch (Exception ex)
            {
                host._log.Error("Desktop host message failed", ex);
            }
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    private nint? OnHostMessage(uint msg, nuint w, nint l)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _log.Info("Explorer restarted (TaskbarCreated): re-attaching");
            ScheduleLayout(force: true, "Explorer restarted");
            return 0;
        }
        switch (msg)
        {
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
            case WM_SETTINGCHANGE:
                ScheduleLayout(force: false, "");
                return msg == WM_SETTINGCHANGE ? null : 0;
            case WM_TIMER when w == TimerLayout:
                OnLayoutTimer();
                return 0;
            case WM_TIMER when w == TimerVisibility:
                KillTimer(_messageWindow, TimerVisibility);
                CheckVisibility(force: false);
                return 0;
            case WM_TIMER when w == TimerPoll:
                OnPoll();
                return 0;
            case WM_POWERBROADCAST when w == PBT_POWERSETTINGCHANGE && l != 0:
                OnPowerSetting((POWERBROADCAST_SETTING*)l);
                return 1;
            case WM_WTSSESSION_CHANGE:
                OnSessionChange((int)w);
                return 0;
            case MsgRebuild:
                ScheduleLayout(force: true, "requested");
                return 0;
            case MsgCheckVisibility:
                ScheduleVisibilityCheck();
                return 0;
            case MsgPushState:
                PushState(force: false);
                return 0;
            case MsgStop:
                PostQuitMessage(0);
                return 0;
        }
        return null;
    }

    private void OnPowerSetting(POWERBROADCAST_SETTING* setting)
    {
        if (setting->DataLength < 4) return;
        uint value = *(uint*)&setting->Data;
        if (setting->PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
        {
            // 0 off, 1 on, 2 dimmed (still visible).
            bool off = value == 0;
            if (off == _system.DisplayOff) return;
            _system = _system with { DisplayOff = off };
            _log.Info(off ? "Displays off" : "Displays on");
            PushState(force: false);
        }
        else if (setting->PowerSetting == GUID_ACDC_POWER_SOURCE)
        {
            _battery?.Refresh();
            PushState(force: false);
        }
    }

    private void OnSessionChange(int change)
    {
        var before = _system;
        _system = change switch
        {
            WTS_SESSION_LOCK => _system with { Locked = true },
            WTS_SESSION_UNLOCK => _system with { Locked = false },
            WTS_CONSOLE_DISCONNECT or WTS_REMOTE_DISCONNECT => _system with { SessionInactive = true },
            WTS_CONSOLE_CONNECT or WTS_REMOTE_CONNECT => _system with { SessionInactive = false },
            _ => _system,
        };
        if (_system == before) return;
        _log.Info($"Session change {change}: locked={_system.Locked} inactive={_system.SessionInactive}");
        PushState(force: false);
    }

    [UnmanagedCallersOnly]
    private static void WinEventProc(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Window-level events only (OBJID_WINDOW = 0); debounced into one check.
        if (idObject != 0) return;
        var host = t_host;
        if (host is not null && host._messageWindow != 0) host.ScheduleVisibilityCheck();
    }
}
