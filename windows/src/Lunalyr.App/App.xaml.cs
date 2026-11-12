using Lunalyr.App.Hosting;
using Microsoft.UI.Xaml;

namespace Lunalyr.App;

/// <summary>
/// The WinUI application, started only when a window is first needed. It never quits on its own: the tray decides
/// (<see cref="DispatcherShutdownMode.OnExplicitShutdown"/>), so closing the settings window leaves Lunalyr running.
/// </summary>
public partial class App : Application
{
    private readonly AppHost _host;
    private SettingsWindow? _window;

    internal App(AppHost host)
    {
        _host = host;
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => _host.ReportCrash(e.Exception, "Microsoft.UI.Xaml.Application.UnhandledException", fatal: true);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args) => ShowSettings(_host.TakeRequestedPage());

    /// <summary>Opens the settings window (at <paramref name="page"/> when given), or brings it to the front.</summary>
    internal void ShowSettings(string? page = null)
    {
        if (_window is null)
        {
            _window = new SettingsWindow(_host, page);
            _window.Closed += (_, _) => _window = null;
        }
        else if (page is not null)
        {
            _window.ShowPage(page);
        }

        _window.Activate();
    }

    /// <summary>The run's look changed (<c>--theme</c> from a second launch).</summary>
    internal void ApplyTheme() => _window?.ApplyTheme();

    /// <summary>The controller's status changed: refresh what the window shows.</summary>
    internal void RefreshStatus() => _window?.RefreshStatus();
}
