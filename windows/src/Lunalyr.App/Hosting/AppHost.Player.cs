using Lunalyr.App.Player;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Shell.Player;

namespace Lunalyr.App.Hosting;

// The desktop player: made on the UI thread with the tray, before the controller exists (it reads the song once the
// controller is up). Its commands go through the process's playback guard.
internal sealed partial class AppHost
{
    private DesktopPlayer? _player;

    private void StartPlayer()
    {
        try
        {
            var commands = new PlayerCommands(() => _controller?.Monitor.Session,
                () => _controller?.Monitor.Capabilities ?? PlayerCapabilities.None, PlaybackGuard.Process, Log);
            var player = new DesktopPlayer(() => _controller, () => _wallpaper?.Engine, commands, () => _settings.Current,
                change => _settings.Update(change), _tray.Post, _options.PlayerDebug, Log)
            {
                OnError = ex => ReportCrash(ex, "desktop player window procedure", fatal: false),
            };
            player.Start();
            _player = player;
            _settings.Changed += (_, e) => _tray.Post(() => _player?.ApplySettings(e.NewValue));
        }
        catch (Exception ex)
        {
            // The wallpaper and the tray carry on without the player.
            _log.Error("Couldn't start the desktop player.", ex);
        }
    }

    private void StopPlayer()
    {
        _player?.Dispose();
        _player = null;
    }
}
