using System.Diagnostics;
using static LunalyrSpike.Native;

namespace LunalyrSpike;

public static class Program
{
    static readonly Stopwatch Launch = Stopwatch.StartNew();

    [STAThread]
    public static int Main(string[] args)
    {
        _ = Launch.IsRunning; // start the launch clock now (static fields initialise lazily)
        var opt = Options.Parse(args);
        SetProcessDpiAwarenessContext(-4); // per-monitor v2 (also in the manifest)
        if (!opt.Exp.Contains("notimer")) timeBeginPeriod(1);

        if (opt.Settings)
        {
            // WinUI 3 path: the settings window and the wallpaper share the UI thread's message loop.
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Microsoft.UI.Xaml.Application.Start(p =>
            {
                var sync = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(sync);
                _ = new App(opt);
            });
            return 0;
        }

        // Renderer-only path: a plain Win32 message loop, no XAML loaded.
        using var session = new Session(opt);
        session.Start(() => PostQuitMessage(0));
        while (GetMessage(out var m, 0, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
        session.Finish();
        return 0;
    }

    internal sealed class Session(Options opt) : IDisposable
    {
        readonly Stats _stats = new();
        Renderer? _renderer;
        WallpaperHost? _host;

        public void Start(Action quit)
        {
            _renderer = new Renderer(opt, _stats);
            _host = new WallpaperHost(opt, _renderer, _stats) { OnQuit = quit };
            _host.Build();
            _stats.StartupMs = Launch.Elapsed.TotalMilliseconds;
            // Measure steady state: drop the first second (start-up, first line build).
            var t = new System.Threading.Timer(_ => _stats.ResetWindow(), null, 1000, Timeout.Infinite);
            GC.KeepAlive(t);
            _keep = t;
        }
        System.Threading.Timer? _keep;

        public void Finish()
        {
            var tree = _host?.LastTree;
            var monitors = _host?.MonitorReport ?? [];
            int hz = _host?.RefreshHz ?? 60;
            _host?.Dispose();
            _renderer?.Dispose();
            if (!string.IsNullOrEmpty(tree)) _stats.Notes.Add("tree after attach:\n" + tree);
            if (opt.StatsPath != null) _stats.Write(opt.StatsPath, _renderer?.AdapterName ?? "", monitors, hz);
        }

        public void Dispose() { _keep?.Dispose(); }
    }
}
