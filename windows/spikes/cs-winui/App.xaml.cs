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
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new SettingsWindow();
        _window.Closed += (_, _) => Shutdown();
        _window.Activate();
        _session = new Program.Session(_opt);
        _session.Start(Shutdown);
    }

    bool _done;
    void Shutdown()
    {
        if (_done) return;
        _done = true;
        _session?.Finish();
        _session?.Dispose();
        Exit();
    }
}
