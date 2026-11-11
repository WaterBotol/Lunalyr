using Microsoft.UI.Xaml;

namespace LunalyrSpike;

public partial class App : Application
{
    readonly Options _opt;
    Program.Session? _session;
    SettingsWindow? _window;

    internal App(Options opt)
    {
        _opt = opt;
        UnhandledException += (_, e) =>
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "spike-crash.log"), $"{DateTime.Now:O} {e.Exception}\n{e.Message}\n");
        };
        InitializeComponent();
        // A tray app keeps running with no windows open; by default WinUI quits when the last window closes.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new SettingsWindow();
        _window.Closed += (_, _) => { if (!_autoClosed) Shutdown(); };
        if (_opt.CloseSettingsAfter > 0)
        {
            // Measure the always-running process after a window has been opened and closed (WinUI stays loaded).
            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(_opt.CloseSettingsAfter);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => { _autoClosed = true; _window?.Close(); _window = null; GC.Collect(); };
            timer.Start();
            _closeTimer = timer;
        }
        _window.Activate();
        _session = new Program.Session(_opt);
        _session.Start(Shutdown);
    }

    bool _done, _autoClosed;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _closeTimer;
    void Shutdown()
    {
        if (_done) return;
        _done = true;
        _session?.Finish();
        _session?.Dispose();
        Exit();
    }
}
