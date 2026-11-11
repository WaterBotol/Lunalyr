using System.Diagnostics;
using System.Runtime.InteropServices;
using static LunalyrSpike.Native;

namespace LunalyrSpike;

/// <summary>
/// Owns the wallpaper windows on the UI thread. Rendering happens on the Renderer's own thread, so this
/// thread stays free to answer Explorer (our windows are children of Explorer's Progman window).
/// </summary>
internal sealed class WallpaperHost : IDisposable
{
    const string SurfaceClass = "LunalyrSpikeSurface", MessageClass = "LunalyrSpikeMessages";
    const nuint TimerQuit = 1, TimerRebuild = 2;
    readonly Options _opt;
    readonly Renderer _renderer;
    readonly Stats _stats;
    readonly WndProc _surfaceProc, _msgProc; // kept alive for the native side
    readonly uint _taskbarCreated;
    nint _msgWindow;
    readonly List<nint> _windows = [];
    public List<object> MonitorReport { get; } = [];
    public int RefreshHz { get; private set; } = 60;
    public Action? OnQuit;
    public string LastTree = "";

    public WallpaperHost(Options opt, Renderer renderer, Stats stats)
    {
        _opt = opt; _renderer = renderer; _stats = stats;
        _surfaceProc = SurfaceProc;
        _msgProc = MessageProc;
        nint inst = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_surfaceProc),
            hInstance = inst, lpszClassName = SurfaceClass, hCursor = LoadCursor(0, 32512),
        };
        RegisterClassEx(ref wc);
        var mc = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_msgProc), hInstance = inst, lpszClassName = MessageClass };
        RegisterClassEx(ref mc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        // A hidden top-level window: message-only windows don't get the TaskbarCreated broadcast.
        _msgWindow = CreateWindowEx(WS_EX_TOOLWINDOW, MessageClass, "Lunalyr spike", WS_POPUP, 0, 0, 0, 0, 0, 0, inst, 0);
        if (_opt.Seconds > 0) SetTimer(_msgWindow, TimerQuit, (uint)(_opt.Seconds * 1000), 0);
    }

    public void Build()
    {
        _renderer.ReleaseSurfaces();
        foreach (var w in _windows) DestroyWindow(w);
        _windows.Clear();
        MonitorReport.Clear();
        var surfaces = new List<Surface>();
        nint inst = GetModuleHandle(null);
        var monitors = Monitors.Enumerate();
        RefreshHz = monitors.Count > 0 ? monitors.Max(m => m.Hz) : 60;

        if (_opt.Windowed)
        {
            var m = monitors.FirstOrDefault(x => x.Primary) ?? monitors[0];
            float scale = m.Dpi / 96f;
            int w = 1280, h = 720;
            nint hwnd = CreateWindowEx(WS_EX_NOREDIRECTIONBITMAP, SurfaceClass, "Lunalyr spike (C#)", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                m.Bounds.Left + 80, m.Bounds.Top + 80, w, h, 0, 0, inst, 0);
            GetClientRect(hwnd, out var cr);
            _windows.Add(hwnd);
            surfaces.Add(new Surface(hwnd, cr.Width, cr.Height, scale, m.Hz));
            MonitorReport.Add(new { w = cr.Width, h = cr.Height, dpi = m.Dpi, hz = m.Hz, mode = "windowed" });
        }
        else
        {
            var target = DesktopAttach.Find();
            if (target == null) { _stats.Notes.Add("no desktop target found"); return; }
            foreach (var m in monitors)
            {
                int x = m.Bounds.Left - target.OriginX, y = m.Bounds.Top - target.OriginY;
                uint ex = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | (_opt.Layered ? WS_EX_LAYERED : 0);
                nint hwnd = CreateWindowEx(ex, SurfaceClass, "Lunalyr wallpaper", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_DISABLED,
                    x, y, m.Bounds.Width, m.Bounds.Height, target.Parent, 0, inst, 0);
                if (hwnd == 0) { _stats.Notes.Add($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}"); continue; }
                if (_opt.Layered) SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
                if (target.InsertAfter != 0)
                    SetWindowPos(hwnd, target.InsertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                _windows.Add(hwnd);
                surfaces.Add(new Surface(hwnd, m.Bounds.Width, m.Bounds.Height, m.Dpi / 96f, m.Hz));
                MonitorReport.Add(new { w = m.Bounds.Width, h = m.Bounds.Height, dpi = m.Dpi, hz = m.Hz, mode = target.Mode == AttachMode.Win24H2 ? "24h2" : "legacy" });
            }
            LastTree = DesktopAttach.DumpTree();
        }
        _renderer.SetSurfaces(surfaces);
    }

    nint SurfaceProc(nint hwnd, uint msg, nuint w, nint l)
    {
        switch (msg)
        {
            case WM_ERASEBKGND: return 1;
            case WM_MOUSEACTIVATE: return 3; // MA_NOACTIVATE
            case WM_CLOSE when _opt.Windowed: Quit(); return 0;
            case WM_DPICHANGED: ScheduleRebuild(); return 0;
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    nint MessageProc(nint hwnd, uint msg, nuint w, nint l)
    {
        if (msg == _taskbarCreated) { _stats.Notes.Add($"TaskbarCreated at {DateTime.Now:T}: re-attaching"); ScheduleRebuild(); return 0; }
        switch (msg)
        {
            case WM_DISPLAYCHANGE: case WM_DPICHANGED: ScheduleRebuild(); return 0;
            case WM_SETTINGCHANGE: if (w == 0x2F /*SPI_SETWORKAREA*/) ScheduleRebuild(); break;
            case WM_TIMER when w == TimerQuit: Quit(); return 0;
            case WM_TIMER when w == TimerRebuild:
                KillRebuildTimer();
                Build();
                return 0;
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    [DllImport("user32")] static extern bool KillTimer(nint hwnd, nuint id);
    void KillRebuildTimer() => KillTimer(_msgWindow, TimerRebuild);

    /// <summary>Coalesce bursts (Explorer restarts send several messages) into one rebuild 500 ms later.</summary>
    void ScheduleRebuild() => SetTimer(_msgWindow, TimerRebuild, 500, 0);

    void Quit()
    {
        KillTimer(_msgWindow, TimerQuit);
        OnQuit?.Invoke();
    }

    public void Dispose()
    {
        _renderer.ReleaseSurfaces();
        foreach (var w in _windows) DestroyWindow(w);
        _windows.Clear();
        if (_msgWindow != 0) DestroyWindow(_msgWindow);
        _msgWindow = 0;
    }
}
