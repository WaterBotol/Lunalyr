using System.Runtime.InteropServices;
using Lunalyr.Core.Logging;
using Windows.UI.ViewManagement;

namespace Lunalyr.Platform.Desktop;

/// <summary>
/// Whether Windows asks for less motion: Settings › Accessibility › Visual effects › "Animation effects" (the client
/// area animation setting) turned off, which the wallpaper takes as the Mac's Reduce Motion. Read only: the setting is
/// never changed. Follows changes while the app runs, through <see cref="UISettings.AnimationsEnabledChanged"/> and
/// through <see cref="Refresh"/>, which the app calls on <c>WM_SETTINGCHANGE</c>.
/// </summary>
public sealed partial class SystemMotion : IDisposable
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    private readonly Func<bool?> _read;
    private readonly CategoryLogger _log;
    private readonly Lock _gate = new();
    private UISettings? _settings;
    private bool _reduceMotion;
    private bool _disposed;

    /// <param name="read">Reads "Animation effects": true on, false off, null when it can't be read. The system's own
    /// setting (<see cref="ReadAnimationEffects"/>) unless a test supplies another.</param>
    /// <param name="listen">Follow <see cref="UISettings.AnimationsEnabledChanged"/> as well as <see cref="Refresh"/>.</param>
    public SystemMotion(Func<bool?>? read = null, Logger? logger = null, bool listen = true)
    {
        _read = read ?? ReadAnimationEffects;
        _log = (logger ?? Logger.Null).App;
        _reduceMotion = ReduceMotionFrom(_read());
        if (listen) Listen();
    }

    /// <summary>The system asks for less motion now.</summary>
    public bool ReduceMotion
    {
        get { lock (_gate) return _reduceMotion; }
    }

    /// <summary>Reduce Motion turned on or off (raised on whichever thread noticed, with the new value).</summary>
    public event EventHandler<bool>? Changed;

    /// <summary>Animation effects off means less motion; on, or a setting that can't be read, means full motion.</summary>
    public static bool ReduceMotionFrom(bool? animationEffects) => animationEffects == false;

    /// <summary>Reads the setting again and raises <see cref="Changed"/> if it differs. Cheap; safe from any thread.</summary>
    public void Refresh()
    {
        var now = ReduceMotionFrom(_read());
        lock (_gate)
        {
            if (_disposed || now == _reduceMotion) return;
            _reduceMotion = now;
        }
        Changed?.Invoke(this, now);
    }

    /// <summary>Windows' "Animation effects" (<c>SPI_GETCLIENTAREAANIMATION</c>): true on, false off, null when unreadable.</summary>
    public static unsafe bool? ReadAnimationEffects()
    {
        int enabled = 0;
        return SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, &enabled, 0) ? enabled != 0 : null;
    }

    private void Listen()
    {
        try
        {
            var settings = new UISettings();
            settings.AnimationsEnabledChanged += OnAnimationsChanged;
            _settings = settings;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or TypeLoadException
                                              or PlatformNotSupportedException or NotSupportedException)
        {
            // Older Windows: setting changes still arrive through Refresh (WM_SETTINGCHANGE).
            _log.Debug($"Animation effects changes come only with WM_SETTINGCHANGE here ({exception.GetType().Name}).");
        }
    }

    private void OnAnimationsChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) => Refresh();

    public void Dispose()
    {
        UISettings? settings;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            settings = _settings;
            _settings = null;
        }
        if (settings is null) return;
        try
        {
            settings.AnimationsEnabledChanged -= OnAnimationsChanged;
        }
        catch (COMException)
        {
            // Shutting down: nothing to undo.
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SystemParametersInfo(uint action, uint param, void* value, uint winIni);
}
