using Lunalyr.Core.NowPlaying;
using Lunalyr.Shell;
using Lunalyr.Shell.Visualisers;

namespace Lunalyr.App.Hosting;

// System audio and the visualiser layers: they follow the settings, the wallpaper's surfaces and power saving, and the
// followed player. Under --demo a built-in synthetic signal stands in and nothing is captured.
internal sealed partial class AppHost
{
    private SystemAudioService? _systemAudio;

    /// <summary>Connects the visualiser layers to the app and starts System audio's controller (capture stays off until it is switched on).</summary>
    private void StartVisualisers(AppController controller)
    {
        VisualiserServices.UseSettings(() => _settings.Current);
        // Windows' own setting, even with "Animate anyway": it also keeps beat flashes off, a safety matter, not a look.
        VisualiserServices.UseReduceMotion(() => SystemReducesMotion);
        var audio = new SystemAudioService(() => _settings.Current, () =>
        {
            var status = controller.Status;
            return (status.Playback == PlaybackStatus.Playing, status.Player);
        }, _options.Demo, Log, () => controller.DisplayKeys, () => FirstRunWelcomeOpen,
            () => controller.MusicVideo?.Status is Core.MusicVideo.MusicVideoStatus.Showing);
        _systemAudio = audio;
        _settings.Changed += (_, e) => audio.SettingsChanged(e.OldValue, e.NewValue);
        if (_wallpaper?.Engine is { } engine)
        {
            engine.StatusChanged += (_, e) => audio.RenderStatusChanged(e.Surfaces, e.Power);
            audio.RenderStatusChanged(engine.Surfaces, null);
        }
        audio.Start();
        if (_options.Demo) _log.Info("Visualisers: the demo's synthetic signal stands in for System audio; nothing is captured.");
    }

    private void StopVisualisers()
    {
        _systemAudio?.Dispose();
        _systemAudio = null;
    }
}
