using Lunalyr.App.Extras;
using Lunalyr.App.Player;
using Lunalyr.Shell.Extras;

namespace Lunalyr.App.Hosting;

// Milestone 7: global shortcuts (RegisterHotKey on the tray's hidden window), lunalyr:// links and the action HUD
// (docs/APP-SHELL.md, "Shortcuts, links and the action HUD"). Everything here runs on the UI thread; settings changes
// from other threads are posted there.
internal sealed partial class AppHost
{
    private LunalyrExtras? _extras;
    private LunalyrLinkRouter? _links;
    private GlobalHotkeys? _hotkeys;
    private Win32HotkeyRegistrar? _registrar;
    private ActionHudWindow? _hud;
    private Timer? _hotkeyRetry;
    private Timer? _mirroringRecheck;
    private bool _mirroringRead;

    /// <summary>Hide lyrics while a display is mirrored, for Settings › General: the extras and their thread.</summary>
    internal Lunalyr.App.Settings.MirroringHooks Mirroring => new(() => _extras, action => _tray.Post(() =>
    {
        if (!_quitting && _extras is { } extras) action(extras);
    }));

    /// <summary>The global shortcuts, for the settings page (null until the tray window exists).</summary>
    public GlobalHotkeys? Hotkeys => _hotkeys;

    /// <summary>Builds the extras on the tray's window. Links given at launch wait until start-up has finished.</summary>
    private void StartExtras()
    {
        var extras = new LunalyrExtras(() => _settings.Current, change => _settings.Update(change), new ExtrasHooks
        {
            NudgeSync = delta => _controller?.NudgeSongOffset(delta),
            HasSong = () => _controller?.Status.Title is not null,
            // The card shortcut and lunalyr://card open the lyric card window (AppHost.LyricCards.cs).
            OpenLyricCard = () =>
            {
                if (_quitting || _app is null) return false;
                OpenLyricCard();
                return true;
            },
            ToggleTapSync = ToggleTapSync,
            OpenSettings = OpenSettings,
            PauseChanged = paused =>
            {
                // A change to "Animation effects" while paused is taken up as the wallpaper resumes.
                if (!paused) _motion?.Refresh();
                UpdateStatus();
            },
            Log = message => _log.Info($"Note: {message}"),
            // A mirrored display hides the lyrics, or no longer does (Hide lyrics while a display is mirrored).
            HiddenByMirroringChanged = hidden => _controller?.SetHiddenByMirroring(hidden),
        });
        extras.PresentHud = ShowHud;
        _extras = extras;
        // The Mac's screen observer, re-implemented: each display change reads whether a display is duplicated now, and
        // again a moment later, as Windows reports a new topology in steps.
        _tray.DisplaysChanged = () =>
        {
            CheckMirroring();
            _mirroringRecheck ??= new Timer(_ => _tray.Post(CheckMirroring), null, Timeout.Infinite, Timeout.Infinite);
            _mirroringRecheck.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        };
        CheckMirroring();
        _links = new LunalyrLinkRouter(new LunalyrLinkRouter.Actions(
            // Windows has no listen-along: a join link is checked, then answered with a note. Nothing connects.
            Join: _ => false,
            Run: command =>
            {
                _log.Info($"Running a link: {command.DoneText()}.");
                extras.Run(command);
            },
            Note: (text, symbol) => extras.Post(text, symbol, NoteOrigin.Elsewhere)), log: message => _log.Info(message));

        _registrar = new Win32HotkeyRegistrar(_tray.Handle);
        var hotkeys = new GlobalHotkeys(() => _settings.Current, change => _settings.Update(change), _registrar,
            Win32HotkeyRegistrar.KeyName, message => _log.Info(message));
        _hotkeys = hotkeys;
        hotkeys.Changed += WatchTakenShortcuts;
        _tray.Hotkey = id =>
        {
            if (_quitting || hotkeys.Pressed(id) is not { } action) return;
            _log.Info($"Global shortcut: {action.Title()} ({hotkeys.Label(action)}).");
            extras.Perform(action);
        };
        // The settings page writes the store; the shortcuts follow on this thread (nothing is registered until
        // "Use global shortcuts" is on).
        _settings.Changed += (_, e) => _tray.Post(() =>
        {
            if (_quitting) return;
            hotkeys.Sync(e.NewValue);
            extras.SettingsChanged();
        });
        hotkeys.Sync(_settings.Current);
        if (_options.Links.Count > 0) _links.Open(_options.Links);
    }

    /// <summary>Reads whether a display is mirrored and tells the extras (on the tray's thread).</summary>
    private void CheckMirroring()
    {
        if (_quitting || _extras is not { } extras) return;
        var mirrored = Lunalyr.Platform.Desktop.DisplayMirroring.Now();
        if (!_mirroringRead)
        {
            _mirroringRead = true;
            _log.Info(mirrored switch
            {
                true => "Displays: one is mirrored (duplicated) now.",
                false => "Displays: none mirrored.",
                null => "Displays: couldn't read whether one is mirrored.",
            });
        }
        if (mirrored is { } now) extras.UpdateMirroring(now);
    }

    /// <summary>Start-up has finished (or failed): links that came with the launch run now.</summary>
    private void ExtrasReady() => _tray.Post(() => _links?.BecameReady());

    /// <summary>Links from a second launch.</summary>
    private void OpenLinks(IReadOnlyList<string> links)
    {
        if (links.Count == 0) return;
        if (_links is null) _log.Info($"Ignored {links.Count} link(s): Lunalyr is still starting.");
        else _links.Open(links);
    }

    private void ShowHud(string text, string symbol)
    {
        if (_quitting) return;
        _hud ??= new ActionHudWindow(Log.App, () => SystemLook.Read(_options.PlayerDebug), _options.HudCapturePath);
        _hud.Show(text, symbol);
    }

    /// <summary>
    /// A combination another app holds is tried again every 30 s until it is free (the Mac tries when an app quits;
    /// Windows doesn't say when a hot key is released).
    /// </summary>
    private void WatchTakenShortcuts()
    {
        var wanted = _hotkeys is { Registered: true, Unavailable.Count: > 0 };
        if (wanted && _hotkeyRetry is null)
            _hotkeyRetry = new Timer(_ => _tray.Post(() => _hotkeys?.RetryUnavailable()), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        else if (!wanted && _hotkeyRetry is not null)
        {
            _hotkeyRetry.Dispose();
            _hotkeyRetry = null;
        }
    }

    /// <summary>Releases every combination and closes the HUD, before the tray's window goes.</summary>
    private void StopExtras()
    {
        _hotkeyRetry?.Dispose();
        _hotkeyRetry = null;
        _mirroringRecheck?.Dispose();
        _mirroringRecheck = null;
        _tray.DisplaysChanged = null;
        _registrar?.UnregisterAll();
        _hud?.Dispose();
        _hud = null;
    }
}
