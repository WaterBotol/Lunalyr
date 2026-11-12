// "Your wallpaper" (the Mac's .desktop background): nothing is drawn but the text backing, so the user's own Windows
// wallpaper, or a wallpaper app, shows through behind the lyrics. The surface is see-through (ISeeThroughLayer), so
// the engine clears it to transparent and DWM composes it over whatever lies beneath it under Progman.

using System.Numerics;
using Lunalyr.Core.Logging;
using Lunalyr.Render.Scene;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Backgrounds;

/// <summary>Creates <see cref="DesktopLayer"/>s: the user's own wallpaper behind the lyrics, id <c>"desktop"</c>.</summary>
public sealed class DesktopLayerFactory(BackgroundOptions? options = null, Logger? log = null) : IRenderLayerFactory
{
    public string Id => "desktop";

    public IRenderLayer Create() => new DesktopLayer(options ?? BackgroundOptions.Default, log ?? Logger.Null);
}

/// <summary>
/// Draws only the soft text backing (no scrim, no grain), and only while the stage shows something, so with nothing
/// playing the desktop is left as it is (the Mac's caption). The backdrop's luminance is not known (it may be a
/// wallpaper app), so the automatic backing assumes a middling one: <see cref="AssumedLuminance"/>.
/// </summary>
public sealed class DesktopLayer : IRenderLayer, ISeeThroughLayer
{
    /// <summary>
    /// The backdrop luminance the automatic backing is worked out for. Windows' wallpaper may be a picture, a slideshow,
    /// Spotlight or a wallpaper app drawing live, none of which Lunalyr reads, so it takes a light-middling desktop:
    /// enough backing for white lyrics over a pale sky, a faint one over a dark picture.
    /// </summary>
    public const double AssumedLuminance = 0.35;

    private readonly WallpaperShadeDrawer _shade;
    private readonly Logger _log;
    private bool _ready, _failed;
    private double _lastNow;

    internal DesktopLayer(BackgroundOptions options, Logger log)
    {
        // The darkening slider doesn't apply to your own wallpaper (the Mac hides the adjustments for it).
        _shade = new WallpaperShadeDrawer(options with { ScrimStrength = 0 });
        _log = log;
    }

    public bool IsAnimating => _shade.IsEasing(_lastNow);

    public void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        _shade.CreateResources(dc, size, scale);
        _ready = true;
        _failed = false;
    }

    public void Resize(Vector2 size, float scale) => _shade.Resize(size, scale);

    public void Render(in FrameContext frame)
    {
        if (!_ready || _failed) return;
        _lastNow = frame.WallSeconds;
        try
        {
            _shade.Draw(frame, AssumedLuminance, scrim: false, grain: false);
        }
        catch (SharpGenException ex)
        {
            _failed = true;
            _log.Render.Warn("Your wallpaper: drawing the text backing failed; waiting for new resources.", ex);
        }
    }

    public void ReleaseResources()
    {
        _shade.ReleaseResources();
        _ready = false;
    }

    public void Dispose() => ReleaseResources();
}
