// The song's sound for a lyric card: WASAPI loopback of the default output, kept in memory while the card's lines play
// (the Windows counterpart of the Mac's SongAudioRecorder, written from Microsoft's documentation as WasapiLoopback
// is). Shared mode, loopback only: nothing is ever played, muted or rerouted. Started only by the card window's
// "Get the song's sound" (through SongRecorder), never on its own and never under --demo.
//
// NOT VERIFIED LIVE: the development rules forbid recording the PC's real sound, so this has never been run. Its path
// after the capture (buffer → WAV → mix → AAC) is checked with synthetic sound (LoopbackSongCaptureTests' fake source).

using System.Runtime.InteropServices;
using Lunalyr.Core.Cards;
using Lunalyr.Core.Visualisers.Capture;

namespace Lunalyr.Platform.Audio;

/// <summary>Starts loopback captures of the default output device.</summary>
public sealed class LoopbackSongCaptureSource : ISongCaptureSource
{
    public ISongCapture Start()
    {
        var capture = new LoopbackSongCapture();
        capture.Begin();
        return capture;
    }
}

internal sealed unsafe class LoopbackSongCapture : ISongCapture
{
    private readonly object _gate = new();
    private readonly List<float> _buffer = new(48_000 * 2);
    private readonly ManualResetEventSlim _ready = new();
    private volatile bool _stopping;
    private Thread? _thread;
    private Exception? _startError;
    private volatile string? _failure;

    public double SampleRate { get; private set; } = 48_000;

    public string? Failure => _failure;

    public void Begin()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Lunalyr card recording", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            Dispose();
            throw new SongRecordingException("The audio output didn’t start in time.");
        }
        if (_startError is { } error)
        {
            Dispose();
            throw new SongRecordingException($"Couldn’t record the PC’s sound: {error.Message}");
        }
    }

    public void Drain(List<float> stereo)
    {
        lock (_gate)
        {
            stereo.AddRange(_buffer);
            _buffer.Clear();
        }
    }

    private void Run()
    {
        WasapiNative.CoInitializeEx(0, 0);
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        nint mix = 0;
        var started = false;
        try
        {
            enumerator = WasapiNative.CreateEnumerator();
            WasapiNative.Check(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, DeviceRole.Console, out device), "finding the default output");
            if (device is null) throw new InvalidOperationException("There is no output device.");
            client = WasapiNative.Activate<IAudioClient>(device, WasapiNative.IidAudioClient);
            WasapiNative.Check(client.GetMixFormat(out mix), "reading the output format");
            var format = ReadFormat(mix) ?? throw new InvalidOperationException("The output's format can't be recorded.");
            SampleRate = format.SampleRate;
            WasapiNative.Check(client.Initialize(WasapiNative.SharedMode, WasapiNative.StreamFlagsLoopback, 2_000_000, 0, mix, 0), "opening loopback");
            WasapiNative.Check(client.GetService(WasapiNative.IidAudioCaptureClient, out var instance), "getting the capture client");
            capture = WasapiNative.Wrap<IAudioCaptureClient>(instance);
            WasapiNative.Check(client.Start(), "starting loopback");
            started = true;
            _ready.Set();
            var chunk = new List<float>(4096);
            while (!_stopping)
            {
                Thread.Sleep(10);
                while (!_stopping)
                {
                    WasapiNative.Check(capture.GetNextPacketSize(out var next), "reading the next packet");
                    if (next == 0) break;
                    WasapiNative.Check(capture.GetBuffer(out var data, out var frames, out var flags, out _, out _), "reading a packet");
                    try
                    {
                        chunk.Clear();
                        Decode(format, (flags & WasapiNative.BufferFlagsSilent) != 0 ? 0 : data, (int)frames, chunk);
                        lock (_gate) _buffer.AddRange(chunk);
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                }
            }
        }
        catch (Exception ex) when (!_ready.IsSet)
        {
            _startError = ex;
            _ready.Set();
        }
        catch (Exception ex)
        {
            _failure = ex is WasapiException { DeviceGone: true } ? "The audio output changed while recording." : $"The recording stopped: {ex.Message}";
        }
        finally
        {
            if (started) client?.Stop();
            if (mix != 0) WasapiNative.CoTaskMemFree(mix);
            WasapiNative.Release(capture);
            WasapiNative.Release(client);
            WasapiNative.Release(device);
            WasapiNative.Release(enumerator);
            WasapiNative.CoUninitialize();
        }
    }

    /// <summary>A packet as interleaved stereo float: the first two channels (mono doubled); silence when <paramref name="data"/> is 0.</summary>
    internal static void Decode(PcmFormat format, nint data, int frames, List<float> into)
    {
        var channels = format.Channels;
        for (var f = 0; f < frames; f++)
        {
            if (data == 0)
            {
                into.Add(0);
                into.Add(0);
                continue;
            }
            var frame = (byte*)data + (long)f * format.BlockAlign;
            var left = Sample(format, frame);
            var right = channels > 1 ? Sample(format, frame + format.SampleBytes) : left;
            into.Add(left);
            into.Add(right);
        }
    }

    private static float Sample(PcmFormat format, byte* at) => format.Encoding switch
    {
        PcmEncoding.Float32 => *(float*)at,
        PcmEncoding.Float64 => (float)*(double*)at,
        PcmEncoding.Int16 => *(short*)at / 32768f,
        PcmEncoding.Int24 => ((at[0] << 8) | (at[1] << 16) | (at[2] << 24)) / 2147483648f,
        _ => *(int*)at / 2147483648f,
    };

    private static PcmFormat? ReadFormat(nint mix)
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
        return known ? PcmFormat.From(isFloat, bits, valid, channels, blockAlign, rate) : null;
    }

    public void Dispose()
    {
        _stopping = true;
        if (_thread is { } thread && thread != Thread.CurrentThread) thread.Join(3000);
        _ready.Dispose();
    }
}
