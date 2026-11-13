using System.Diagnostics;
using System.Reflection;
using Lunalyr.Install;
using Lunalyr.Shell.Startup;
using Lunalyr.Shell.Updates;

namespace Lunalyr.App.Hosting;

/// <summary>What Settings › General's Startup and Updates rows work with (milestone 8).</summary>
internal sealed record PackagingHooks(LaunchAtLoginSwitch LaunchAtLogin, UpdateService Updates, Action UpdateAction,
                                      Action OpenStartupApps);

/// <summary>Launch at login and updates (docs/PACKAGING.md).</summary>
internal sealed partial class AppHost
{
    private UpdateService? _updates;
    private LaunchAtLoginSwitch? _launchAtLogin;
    private Timer? _updateTimer;
    private bool _updateNoticeShown;

    public PackagingHooks? Packaging => _updates is null || _launchAtLogin is null
        ? null
        : new(_launchAtLogin, _updates, UpdateAction, OpenStartupApps);

    /// <summary>Why this run may not change launch at login or check for updates (a test run), or null.</summary>
    private string? TestRun => _options.Demo ? "--demo" : _options.DataRoot is not null ? "--data" : null;

    private void StartPackaging()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Product.ExeName);
        _launchAtLogin = new LaunchAtLoginSwitch(new WindowsRegistryHive(), exe, TestRun);
        var status = _launchAtLogin.Status;
        _log.Info($"Launch at login: {status.State}{(status.StartsPath is { } p && status.State != LaunchAtLoginState.On ? $" ({p})" : "")}"
                  + $"{(_options.Login ? "; this run was started at sign-in (--login)" : "")}.");
        var layout = InstallLayout.Detect(exe);
        _log.Info(layout is null ? $"Running from {Path.GetDirectoryName(exe)} (not installed)." : $"Installed in {layout.Root}.");

        var config = UpdateConfiguration.Create(Metadata("LunalyrUpdateFeed"), Metadata("LunalyrUpdateKey"));
        _updates = new UpdateService(new UpdateEnvironment
        {
            Configuration = config,
            CurrentVersion = Version,
            Folder = Path.Combine(DataRoot, "Updates"),
            Http = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(20) },
            UserAgent = UserAgent,
            OffReason = TestRun ?? (_options.NoNetwork ? "--no-network" : null),
            RequireSignature = string.Equals(Metadata("LunalyrRequireSignature"), "true", StringComparison.OrdinalIgnoreCase),
        }, Log.App);
        _log.Info(config is null ? "Updates: no update service is built into this copy." : $"Updates: checking {config.Feed.Host}.");
        _updates.Changed += OnUpdateChanged;
        _tray.DescribeUpdate = () => _updates.State is { Phase: UpdatePhase.Ready, NewVersion: { } v } ? $"Restart to update to Lunalyr {v}" : null;
        _tray.InstallUpdate = InstallUpdate;
        // The first automatic check waits until start-up has settled; then it is looked at hourly (it runs once a day).
        if (_updates.State.Phase == UpdatePhase.Idle)
            _updateTimer = new Timer(_ => CheckAutomatically(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));

        if (_options.UpdatedTo is { } updated)
        {
            _log.Info($"Updated to Lunalyr {updated}.");
            ShowHud($"Updated to Lunalyr {updated}", "arrow.down.circle");
        }
    }

    private static string? Metadata(string key) =>
        typeof(AppHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    private void CheckAutomatically()
    {
        if (_quitting || _updates is null) return;
        if (_updates.AutomaticCheckDue(_settings.Current.AutomaticUpdateChecks)) _ = _updates.CheckAsync(download: true);
    }

    private void OnUpdateChanged(UpdateState state)
    {
        // Once a run: a downloaded update says so where the user will see it, then waits in the tray menu and settings.
        if (state.Phase == UpdatePhase.Ready && !_updateNoticeShown)
        {
            _updateNoticeShown = true;
            _tray.Post(() => ShowHud($"Lunalyr {state.NewVersion} is ready to install", "arrow.down.circle"));
        }
    }

    /// <summary>The update button: check, download, or install, depending on where things stand.</summary>
    private void UpdateAction()
    {
        if (_updates is null) return;
        switch (_updates.State.Phase)
        {
            case UpdatePhase.Available: _ = _updates.DownloadAsync(); break;
            case UpdatePhase.Ready: InstallUpdate(); break;
            default: _ = _updates.CheckAsync(download: false); break;
        }
    }

    /// <summary>
    /// Starts the verified installer and quits. An installed Lunalyr updates in place with no questions
    /// (<c>--update</c>; the installer starts it again); a copy run from any other folder opens the installer normally,
    /// which installs Lunalyr for this user.
    /// </summary>
    private void InstallUpdate()
    {
        if (_updates?.State is not { Phase: UpdatePhase.Ready, InstallerPath: { } installer }) return;
        var installed = InstallLayout.Detect(Environment.ProcessPath) is not null;
        try
        {
            var start = new ProcessStartInfo(installer) { UseShellExecute = true };
            if (installed) start.Arguments = "--update";
            Process.Start(start)?.Dispose();
            _log.Info($"Started the installer for Lunalyr {_updates.State.NewVersion}{(installed ? " (--update)" : "")}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.Warn($"Couldn't start the installer: {ex.Message}");
            return;
        }
        if (installed) Quit();
    }

    /// <summary>Settings › Apps › Startup, where Windows' own switch for Lunalyr is (opened only by the user's click).</summary>
    private void OpenStartupApps()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.Warn($"Couldn't open Startup apps: {ex.Message}");
        }
    }

    private void StopPackaging()
    {
        _updateTimer?.Dispose();
        _updates?.Dispose();
    }
}
