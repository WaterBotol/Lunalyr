// Whether anything is playing on this PC: the default output device's peak meter (IAudioMeterInformation), which reads
// the mix every app plays to it. Windows' counterpart of the Mac's AudioOutputActivity (process output). Reading the
// meter opens no stream and no audio session, and plays nothing. Lunalyr itself never plays sound, so any level is
// someone else's.

using Lunalyr.Core.Logging;

namespace Lunalyr.Platform.Audio;

/// <summary>Reads the default output's peak level. Thread-safe; each read takes well under a millisecond.</summary>
public sealed class OutputActivityMeter : IDisposable
{
    /// <summary>A peak above this counts as sound (about -100 dBFS).</summary>
    public const float Threshold = 0.00001f;

    private readonly Lock _lock = new();
    private readonly CategoryLogger _log;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioMeterInformation? _meter;
    private string? _device;
    private bool _broken;
    private long _lastCheck;

    public OutputActivityMeter(Logger? logger = null) => _log = (logger ?? Logger.Null).App;

    /// <summary>The default output's current peak level (0–1), or null when it can't be read. For measurements.</summary>
    public float? Peak()
    {
        lock (_lock)
        {
            if (_broken) return null;
            try
            {
                Refresh();
                if (_meter is null) return null;
                return _meter.GetPeakValue(out var peak) >= 0 ? peak : null;
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// Whether the default output is playing anything now; null when it can't be read (counted as playing by the
    /// capture rules). Follows a change of default device on the next read.
    /// </summary>
    public bool? IsActive()
    {
        lock (_lock)
        {
            if (_broken) return null;
            try
            {
                Refresh();
                if (_meter is null) return false;
                WasapiNative.Check(_meter.GetPeakValue(out var peak), "reading the output level");
                return peak > Threshold;
            }
            catch (WasapiException ex) when (ex.DeviceGone)
            {
                Drop();
                return null;
            }
            catch (Exception ex)
            {
                _broken = true;
                _log.Warn("System audio: the output meter can't be read; playback counts as unknown.", ex);
                Drop();
                return null;
            }
        }
    }

    /// <summary>Picks up the current default device, at most every two seconds.</summary>
    private void Refresh()
    {
        var now = Environment.TickCount64;
        if (_meter is not null && now - _lastCheck < 2_000) return;
        _lastCheck = now;
        _enumerator ??= WasapiNative.CreateEnumerator();
        if (_enumerator.GetDefaultAudioEndpoint(DataFlow.Render, DeviceRole.Console, out var device) < 0 || device is null)
        {
            Drop();
            return;
        }
        var id = WasapiNative.Id(device);
        if (id == _device && _meter is not null)
        {
            WasapiNative.Release(device);
            return;
        }
        WasapiNative.Release(_meter);
        _meter = WasapiNative.Activate<IAudioMeterInformation>(device, WasapiNative.IidAudioMeterInformation);
        _device = id;
        WasapiNative.Release(device);
    }

    private void Drop()
    {
        WasapiNative.Release(_meter);
        _meter = null;
        _device = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Drop();
            WasapiNative.Release(_enumerator);
            _enumerator = null;
            _broken = true;
        }
    }
}
