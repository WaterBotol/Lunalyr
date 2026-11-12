using Lunalyr.App.Hosting;
using Lunalyr.App.Tray;
using Lunalyr.Shell.Flyout;
using Microsoft.UI.Xaml;

namespace Lunalyr.App;

/// <summary>
/// The WinUI application, started at launch right after the tray icon (no window until one is asked for: the tray
/// flyout, settings). It never quits on its own: the tray decides
/// (<see cref="DispatcherShutdownMode.OnExplicitShutdown"/>), so closing the settings window leaves Lunalyr running.
/// </summary>
public partial class App : Application
{
    private readonly AppHost _host;
    private SettingsWindow? _window;
    private TrayFlyout? _flyout;

    internal App(AppHost host)
    {
        _host = host;
        InitializeComponent();
        // --theme light|dark (for screenshots without touching Windows' own setting); otherwise Windows' theme.
        if (host.Theme is "light") RequestedTheme = ApplicationTheme.Light;
        else if (host.Theme is "dark") RequestedTheme = ApplicationTheme.Dark;
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => _host.ReportCrash(e.Exception, "Microsoft.UI.Xaml.Application.UnhandledException", fatal: true);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args) => _host.ShowPendingUi(this);

    /// <summary>Opens the tray flyout beside <paramref name="anchor"/>, or closes it.</summary>
    internal void ToggleFlyout(PixelRect anchor, bool keyboard)
    {
        try
        {
            _flyout ??= new TrayFlyout(_host);
            _flyout.Toggle(anchor, keyboard);
        }
        catch (Exception ex)
        {
            // The panel not opening must not take Lunalyr down; the right-click menu still works.
            _host.ReportCrash(ex, "tray panel", fatal: false);
        }
    }

    /// <summary>Opens the settings window, or brings it to the front.</summary>
    internal void ShowSettings()
    {
        if (_window is null)
        {
            _window = new SettingsWindow(_host);
            _window.Closed += (_, _) => _window = null;
        }

        _window.Activate();
    }

    /// <summary>The controller's status changed: refresh what the window shows.</summary>
    internal void RefreshStatus()
    {
        _window?.RefreshStatus();
        _flyout?.RefreshIfOpen();
    }
}
