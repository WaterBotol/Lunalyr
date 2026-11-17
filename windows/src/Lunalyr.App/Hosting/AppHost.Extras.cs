using Lunalyr.App.Extras;
using Lunalyr.App.Player;
using Lunalyr.Shell.Extras;

namespace Lunalyr.App.Hosting;

// Milestone 7: lunalyr:// links and the action HUD (Windows has no global keyboard shortcuts)
// (docs/APP-SHELL.md, "Shortcuts, links and the action HUD"). Everything here runs on the UI thread; settings changes
// from other threads are posted there.
internal sealed partial class AppHost
{
    private LunalyrExtras? _extras;
    private LunalyrLinkRouter? _links;
    private ActionHudWindow? _hud;
    private Timer? _mirroringRecheck;
    private bool _mirroringRead;

    /// <summary>Hide lyrics while a display is mirrored, for Settings › General: the extras and their thread.</summary>
    internal Lunalyr.App.Settings.MirroringHooks Mirroring => new(() => _extras, action => _tray.Post(() =>
    {
        if (!_quitting && _extras is { } extras) action(extras);
    }));

    /// <summary>Builds the extras on the tray's window. Links given at launch wait until start-up has finished.</summary>
    private void StartExtras()
    {
        var extras = new LunalyrExtras(() => _settings.Current, change => _settings.Update(change), new ExtrasHooks
        {
            NudgeSync = delta => _controller?.NudgeSongOffset(delta),
            Displays = () => _controller?.DisplayKeys ?? [],
            HasSong = () => _controller?.Status.Title is not null,
            PlayingSong = () => _controller?.CurrentQuery,
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
            ApplyDisplays();
            _mirroringRecheck ??= new Timer(_ => _tray.Post(() =>
            {
                CheckMirroring();
                ApplyDisplays();
            }), null, Timeout.Infinite, Timeout.Infinite);
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

        // The settings page writes the store; the extras follow on this thread.
        _settings.Changed += (_, e) => _tray.Post(() =>
        {
            if (_quitting) return;
            extras.SettingsChanged();
        });
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
        _hud ??= new ActionHudWindow(Log.App, () => SystemLook.Read(_options.PlayerDebug), _options.HudCapturePath, _options.QuietWindows);
        _hud.Show(text, symbol);
    }

    /// <summary>Closes the HUD, before the tray's window goes.</summary>
    private void StopExtras()
    {
        _mirroringRecheck?.Dispose();
        _mirroringRecheck = null;
        _tray.DisplaysChanged = null;
        _hud?.Dispose();
        _hud = null;
    }
}
