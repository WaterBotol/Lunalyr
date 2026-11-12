// System audio on Windows: WASAPI loopback capture of the default output device, for analysis only. Nothing is
// recorded to disk and nothing is ever played: there is no render client, no stream is started on an output, and the
// loopback stream only reads the mix the device is already playing. Follows the default device as it changes and
// reconnects when the device goes away (the Mac's AudioCaptureRecovery behaviour, through the controller).

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Visualisers.Capture;

namespace Lunalyr.Platform.Audio;

/// <summary>Opens loopback sessions on the default output device.</summary>
public static class WasapiLoopback
{
    /// <summary>
    /// Starts loopback capture of the default output device into <paramref name="feed"/> and returns once it runs (or
    /// throws). <paramref name="events"/> hears of default-device changes and device loss.
    /// </summary>
    public static ICaptureSession Open(CaptureFeed feed, ICaptureSessionEvents events, Logger? logger = null)
    {
        var session = new WasapiLoopbackSession(feed, events, logger ?? Logger.Null);
        session.Start();
        return session;
    }
}

/// <summary>One loopback stream on its own capture thread.</summary>
internal sealed unsafe partial class WasapiLoopbackSession : ICaptureSession
{
    private readonly CaptureFeed _feed;
    private readonly ICaptureSessionEvents _events;
    private readonly CategoryLogger _log;
    private readonly ManualResetEventSlim _ready = new();
    private volatile bool _stopping;
    private Thread? _thread;
    private Exception? _startError;
    private Notifications? _notifications;

    public WasapiLoopbackSession(CaptureFeed feed, ICaptureSessionEvents events, Logger logger)
    {
        _feed = feed;
        _events = events;
        _log = logger.App;
    }

    public string MethodLabel => "System audio";

    public double? OutputLatency { get; private set; }

    public string Device { get; private set; } = "";

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Lunalyr audio capture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            Dispose();
            throw new TimeoutException("The output device didn't start its loopback stream in time.");
        }
        if (_startError is { } error)
        {
            _thread.Join(2000);
            throw error;
        }
    }

    private void Run()
    {
        WasapiNative.CoInitializeEx(0, 0); // multithreaded
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        nint wake = 0, mix = 0;
        var started = false;
        try
        {
            enumerator = WasapiNative.CreateEnumerator();
            WasapiNative.Check(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, DeviceRole.Console, out device), "finding the default output");
            if (device is null) throw new InvalidOperationException("There is no output device.");
            var id = WasapiNative.Id(device) ?? "";
            client = WasapiNative.Activate<IAudioClient>(device, WasapiNative.IidAudioClient);
            WasapiNative.Check(client.GetMixFormat(out mix), "reading the output format");
            var format = ReadFormat(mix, out var description);
            // Loopback reads what the device plays; 200 ms of buffer rides out a busy moment on this thread.
            WasapiNative.Check(client.Initialize(WasapiNative.SharedMode,
                WasapiNative.StreamFlagsLoopback | WasapiNative.StreamFlagsEventCallback, 2_000_000, 0, mix, 0), "opening loopback");
            wake = WasapiNative.CreateEvent(0, false, false, 0);
            WasapiNative.Check(client.SetEventHandle(wake), "setting the capture event");
            WasapiNative.Check(client.GetService(WasapiNative.IidAudioCaptureClient, out var captureInstance), "getting the capture client");
            capture = WasapiNative.Wrap<IAudioCaptureClient>(captureInstance);
            if (client.GetStreamLatency(out var latency) >= 0 && latency > 0) OutputLatency = latency / 1e7;
            client.GetDevicePeriod(out var period, out _);
            _notifications = new Notifications(id, _events);
            enumerator.RegisterEndpointNotificationCallback(_notifications);
            WasapiNative.Check(client.Start(), "starting loopback");
            started = true;
            Device = description;
            _log.Info($"System audio: loopback of the default output ({description}); stream latency "
                + $"{(OutputLatency is { } l ? $"{l * 1000:0.0} ms" : "not reported")}, device period {period / 10_000.0:0.0} ms. "
                + "Analysed in memory only.");
            _ready.Set();
            Loop(capture, wake, format);
        }
        catch (Exception ex) when (!_ready.IsSet)
        {
            _startError = ex;
            _ready.Set();
        }
        catch (WasapiException ex) when (ex.DeviceGone)
        {
            if (!_stopping) _events.RouteChanged("The audio output went away; reconnecting…");
        }
        catch (Exception ex)
        {
            _log.Warn("System audio: the loopback stream stopped.", ex);
            if (!_stopping) _events.Failed(ex.Message);
        }
        finally
        {
            if (started) client?.Stop();
            if (_notifications is not null) enumerator?.UnregisterEndpointNotificationCallback(_notifications);
            if (mix != 0) WasapiNative.CoTaskMemFree(mix);
            if (wake != 0) WasapiNative.CloseHandle(wake);
            WasapiNative.Release(capture);
            WasapiNative.Release(client);
            WasapiNative.Release(device);
            WasapiNative.Release(enumerator);
            WasapiNative.CoUninitialize();
        }
    }

    private void Loop(IAudioCaptureClient capture, nint wake, PcmFormat? format)
    {
        var rate = format?.SampleRate ?? 48_000;
        while (!_stopping)
        {
            // The event wakes the thread when a packet is ready; the timeout keeps a check on _stopping and covers
            // systems where loopback doesn't signal it.
            WasapiNative.WaitForSingleObject(wake, 20);
            while (!_stopping)
            {
                WasapiNative.Check(capture.GetNextPacketSize(out var next), "reading the next packet");
                if (next == 0) break;
                WasapiNative.Check(capture.GetBuffer(out var data, out var frames, out var flags, out _, out var qpc), "reading a packet");
                try
                {
                    var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                    // The packet's capture time is in 100 ns units of the performance counter; its last frame follows.
                    var last = qpc > 0 ? qpc / 1e7 + (frames - 1) / rate : now;
                    var bytes = format is { } f ? (int)frames * f.BlockAlign : 0;
                    var span = data == 0 || bytes == 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>((void*)data, bytes);
                    _feed.Write(span, (int)frames, format, last, now,
                        silent: (flags & WasapiNative.BufferFlagsSilent) != 0,
                        discontinuity: (flags & WasapiNative.BufferFlagsDataDiscontinuity) != 0);
                }
                finally
                {
                    capture.ReleaseBuffer(frames);
                }
            }
        }
    }

    /// <summary>The mix format as capture decodes it (null when it can't), and a short description for the log.</summary>
    private static PcmFormat? ReadFormat(nint mix, out string description)
    {
        var tag = *(ushort*)mix;
        var channels = *(ushort*)(mix + 2);
        var rate = *(uint*)(mix + 4);
        var blockAlign = *(ushort*)(mix + 12);
        var bits = *(ushort*)(mix + 14);
        var isFloat = tag == WasapiNative.WaveFormatIeeeFloat;
        var valid = (int)bits;
        var known = tag is WasapiNative.WaveFormatPcm or WasapiNative.WaveFormatIeeeFloat;
        if (tag == WasapiNative.WaveFormatExtensible && *(ushort*)(mix + 16) >= 22)
        {
            valid = *(ushort*)(mix + 18);
            var subtype = *(Guid*)(mix + 24);
            isFloat = subtype == WasapiNative.SubtypeIeeeFloat;
            known = isFloat || subtype == WasapiNative.SubtypePcm;
        }
        description = $"{rate} Hz, {channels} ch, {bits}-bit {(isFloat ? "float" : "integer")}";
        return known ? PcmFormat.From(isFloat, bits, valid, channels, blockAlign, rate) : null;
    }

    public void Dispose()
    {
        _stopping = true;
        if (_thread is { } thread && thread != Thread.CurrentThread) thread.Join(3000);
        _ready.Dispose();
    }

    /// <summary>Hears the default output change, or the device in use going away, and says so once.</summary>
    [GeneratedComClass]
    internal sealed partial class Notifications(string deviceId, ICaptureSessionEvents events) : IMMNotificationClient
    {
        private int _said;

        private void Say(string reason)
        {
            if (Interlocked.Exchange(ref _said, 1) == 0) events.RouteChanged(reason);
        }

        public int OnDeviceStateChanged(nint id, uint state)
        {
            // Anything but active (disabled, unplugged, not present) for the device being captured.
            if (state != 1 && Marshal.PtrToStringUni(id) == deviceId) Say("The audio output went away; reconnecting…");
            return 0;
        }

        public int OnDeviceAdded(nint id) => 0;

        public int OnDeviceRemoved(nint id)
        {
            if (Marshal.PtrToStringUni(id) == deviceId) Say("The audio output went away; reconnecting…");
            return 0;
        }

        public int OnDefaultDeviceChanged(DataFlow flow, DeviceRole role, nint id)
        {
            if (flow == DataFlow.Render && role == DeviceRole.Console && Marshal.PtrToStringUni(id) != deviceId)
                Say("Audio output changed; reconnecting…");
            return 0;
        }

        public int OnPropertyValueChanged(nint id, PropertyKey key) => 0;
    }
}
