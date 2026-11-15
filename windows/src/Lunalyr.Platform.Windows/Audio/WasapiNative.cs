// The Windows Core Audio interfaces System audio uses, as source-generated COM (Native AOT safe). Only what loopback
// capture of the default output device, its notifications and its peak meter need: no render client, no session
// control, nothing that can play a sound.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Lunalyr.Platform.Audio;

internal enum DataFlow { Render = 0, Capture = 1, All = 2 }

internal enum DeviceRole { Console = 0, Multimedia = 1, Communications = 2 }

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}

[GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(DataFlow flow, uint stateMask, out nint devices);
    [PreserveSig] int GetDefaultAudioEndpoint(DataFlow flow, DeviceRole role, out IMMDevice? device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[GeneratedComInterface, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig] int Activate(in Guid iid, uint context, nint activationParams, out nint instance);
    [PreserveSig] int OpenPropertyStore(uint access, out nint store);
    [PreserveSig] int GetId(out nint id);
    [PreserveSig] int GetState(out uint state);
}

[GeneratedComInterface, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged(nint deviceId, uint newState);
    [PreserveSig] int OnDeviceAdded(nint deviceId);
    [PreserveSig] int OnDeviceRemoved(nint deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(DataFlow flow, DeviceRole role, nint deviceId);
    [PreserveSig] int OnPropertyValueChanged(nint deviceId, PropertyKey key);
}

[GeneratedComInterface, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
internal partial interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, nint sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closest);
    [PreserveSig] int GetMixFormat(out nint format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(nint handle);
    [PreserveSig] int GetService(in Guid iid, out nint service);
}

[GeneratedComInterface, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
internal partial interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[GeneratedComInterface, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
internal partial interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
    [PreserveSig] int GetMeteringChannelCount(out uint count);
    [PreserveSig] int GetChannelsPeakValues(uint count, nint peaks);
    [PreserveSig] int QueryHardwareSupport(out uint mask);
}

internal static partial class WasapiNative
{
    public static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IidMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid IidAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public const uint ClsctxAll = 0x17;
    public const int SharedMode = 0;
    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint BufferFlagsDataDiscontinuity = 0x1;
    public const uint BufferFlagsSilent = 0x2;
    public const int AudclntSPacketEmpty = 0x08890001;
    public const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    public const int AudclntEServiceNotRunning = unchecked((int)0x88890010);
    public const int RpcEChangedMode = unchecked((int)0x80010106);
    public const ushort WaveFormatPcm = 1, WaveFormatIeeeFloat = 3, WaveFormatExtensible = 0xFFFE;

    public static StrategyBasedComWrappers Wrappers { get; } = new();

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint memory);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
    public static partial nint CreateEvent(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    /// <summary>A new device enumerator, on a thread that has initialised COM.</summary>
    public static IMMDeviceEnumerator CreateEnumerator()
    {
        Check(CoCreateInstance(ClsidMMDeviceEnumerator, 0, ClsctxAll, IidMMDeviceEnumerator, out var instance), "creating the device enumerator");
        return Wrap<IMMDeviceEnumerator>(instance);
    }

    /// <summary>Wraps a COM pointer (taking over its reference).</summary>
    public static T Wrap<T>(nint instance) where T : class
    {
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(instance, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    public static T Activate<T>(IMMDevice device, Guid iid) where T : class
    {
        Check(device.Activate(iid, ClsctxAll, 0, out var instance), $"activating {typeof(T).Name}");
        return Wrap<T>(instance);
    }

    public static string? Id(IMMDevice device)
    {
        if (device.GetId(out var id) < 0 || id == 0) return null;
        try
        {
            return Marshal.PtrToStringUni(id);
        }
        finally
        {
            CoTaskMemFree(id);
        }
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new WasapiException(hr, what);
    }

    /// <summary>Releases a wrapped COM object now rather than at finalisation.</summary>
    public static void Release(object? com)
    {
        if (com is ComObject wrapper) wrapper.FinalRelease();
    }
}

/// <summary>A Core Audio call failed.</summary>
public sealed class WasapiException(int hresult, string what) : Exception($"Windows audio failed while {what} (0x{hresult:X8}).")
{
    public int Code { get; } = hresult;

    /// <summary>The device went away or was reconfigured: reconnect rather than report a fault.</summary>
    public bool DeviceGone => Code is WasapiNative.AudclntEDeviceInvalidated or WasapiNative.AudclntEServiceNotRunning;
}
