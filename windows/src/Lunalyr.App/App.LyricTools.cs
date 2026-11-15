using Lunalyr.App.LyricTools;

namespace Lunalyr.App;

public partial class App
{
    private TapSyncPanel? _tapSync;

    /// <summary>Opens the Tap to sync panel and starts listening, or stops a session listening.</summary>
    internal void ToggleTapSync()
    {
        try
        {
            _tapSync ??= new TapSyncPanel(_host);
            _tapSync.Toggle();
        }
        catch (Exception ex)
        {
            // The panel not opening must not take Lunalyr down.
            _host.ReportCrash(ex, "Tap to sync panel", fatal: false);
        }
    }
}
