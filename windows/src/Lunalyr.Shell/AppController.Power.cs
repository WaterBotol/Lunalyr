using Lunalyr.Render.Pacing;
using Lunalyr.Shell.Wallpaper;

namespace Lunalyr.Shell;

// Power saving in the app: the constraint the renderer reports (Low power visuals, Energy saver, the battery) decides
// which wallpaper type is drawn (WallpaperPower), as the Mac's effectiveBackgroundMode does.
public sealed partial class AppController
{
    private PowerConstraint? _power;

    /// <summary>The power saving in force, as the renderer last reported it, or null.</summary>
    public PowerConstraint? Power
    {
        get { lock (_gate) return _power; }
    }

    /// <summary>The wallpaper type drawn now: the chosen one, or what power saving shows instead.</summary>
    public string EffectiveBackgroundMode => WallpaperPower.EffectiveMode(Settings.BackgroundMode, Power);
}
