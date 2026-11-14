using Lunalyr.App.Extras;

namespace Lunalyr.App.Hosting;

// Windows' "Animation effects" off: the notice that says why the wallpaper keeps still (MotionNoticeWindow), once.
internal sealed partial class AppHost
{
    private MotionNoticeWindow? _motionNotice;

    /// <summary>
    /// Shows the notice when Windows asks for less motion, the wallpaper follows it, and it has not been answered: after
    /// the welcome (never over it), never in checks (<c>--quiet-windows</c>, <c>--demo</c>). Any thread.
    /// </summary>
    private void MotionMayNeedNotice()
    {
        var settings = _settings.Current;
        if (!SystemReducesMotion || !settings.MotionFollowsWindows || settings.MotionNoticeShown || !settings.OnboardingDone) return;
        if (_options.QuietWindows || _options.Demo || _quitting) return;
        _uiDispatcher?.TryEnqueue(() =>
        {
            if (_quitting || _motionNotice is not null || _settings.Current.MotionNoticeShown) return;
            try
            {
                _log.Info("Animation effects are off: saying why the wallpaper keeps still (once).");
                var notice = new MotionNoticeWindow(this);
                notice.Closed += (_, _) => _motionNotice = null;
                _motionNotice = notice;
                notice.Open();
            }
            catch (Exception ex)
            {
                ReportCrash(ex, "Animation effects notice", fatal: false);
            }
        });
    }
}
