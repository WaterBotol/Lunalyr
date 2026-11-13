// Media Foundation through the COM vtables, as VideoReader makes its calls: no package. Shared by the lyric card's
// writers and readers. Nothing here renders sound or video: Source Readers and Sink Writers only decode and encode.

using System.Runtime.InteropServices;

namespace Lunalyr.Platform.Media;

internal static unsafe partial class MfInterop
{
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MF_MT_MAX_KEYFRAME_SPACING = new("c16eb52b-73a1-476f-8d62-839d6a020652");
    public static readonly Guid MF_MT_VIDEO_PRIMARIES = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    public static readonly Guid MF_MT_TRANSFER_FUNCTION = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    public static readonly Guid MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    public static readonly Guid MF_TRANSCODE_CONTAINERTYPE = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    public static readonly Guid MFTranscodeContainerType_MPEG4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_Float = new("00000003-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00AA00389B71");

    public const uint FirstVideoStream = 0xFFFFFFFC, FirstAudioStream = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;
    public const uint ReadErrorFlag = 0x1, ReadEndOfStreamFlag = 0x2;

    private static readonly Lazy<bool> Started = new(() => MFStartup(0x00020070, 0) >= 0);

    /// <summary>Starts Media Foundation for the process (once); false when it isn't available.</summary>
    public static bool Start() => Started.Value;

    public static void* Slot(nint com, int index) => (*(void***)com)[index];

    public static void Release(nint com)
    {
        if (com != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(com, 2))(com);
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8}).");
    }

    // IMFAttributes: 7 GetUINT32, 8 GetUINT64, 10 GetGUID, 21 SetUINT32, 22 SetUINT64, 24 SetGUID.
    public static void SetUInt32(nint attributes, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attributes, 21))(attributes, &key, value), "SetUINT32");

    public static void SetUInt64(nint attributes, Guid key, ulong value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(attributes, 22))(attributes, &key, value), "SetUINT64");

    public static void SetGuid(nint attributes, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value), "SetGUID");

    public static uint? GetUInt32(nint attributes, Guid key)
    {
        uint value;
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(attributes, 7))(attributes, &key, &value) >= 0 ? value : null;
    }

    public static ulong? GetUInt64(nint attributes, Guid key)
    {
        ulong value;
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(attributes, 8))(attributes, &key, &value) >= 0 ? value : null;
    }

    public static Guid? GetGuid(nint attributes, Guid key)
    {
        Guid value;
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attributes, 10))(attributes, &key, &value) >= 0 ? value : null;
    }

    public static nint CreateAttributes(uint size)
    {
        Check(MFCreateAttributes(out var attributes, size), "MFCreateAttributes");
        return attributes;
    }

    public static nint CreateMediaType()
    {
        Check(MFCreateMediaType(out var type), "MFCreateMediaType");
        return type;
    }

    /// <summary>The source's length in seconds (MF_PD_DURATION), or 0 when it doesn't say.</summary>
    public static double Duration(nint reader)
    {
        var value = stackalloc byte[24];
        Guid duration = MF_PD_DURATION;
        // IMFSourceReader: 12 GetPresentationAttribute.
        if (((delegate* unmanaged[Stdcall]<nint, uint, Guid*, byte*, int>)Slot(reader, 12))(reader, MediaSource, &duration, value) < 0) return 0;
        var vt = *(ushort*)value;
        var ticks = vt == 21 /* VT_UI8 */ ? *(ulong*)(value + 8) : 0;
        PropVariantClear(value);
        return ticks / 1e7;
    }

    /// <summary>Selects only <paramref name="stream"/> on <paramref name="reader"/>; false when the file has no such stream.</summary>
    public static bool SelectOnly(nint reader, uint stream)
    {
        // IMFSourceReader: 4 SetStreamSelection.
        ((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(reader, 4))(reader, AllStreams, 0);
        return ((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(reader, 4))(reader, stream, 1) >= 0;
    }

    /// <summary>Moves <paramref name="reader"/> to <paramref name="seconds"/>.</summary>
    public static bool Seek(nint reader, double seconds)
    {
        var position = stackalloc byte[24];
        *(ushort*)position = 20; // VT_I8
        *(long*)(position + 8) = (long)(Math.Max(0, seconds) * 10_000_000);
        var none = Guid.Empty;
        // IMFSourceReader: 8 SetCurrentPosition.
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, byte*, int>)Slot(reader, 8))(reader, &none, position) >= 0;
    }

    /// <summary>A Source Reader for <paramref name="path"/>, or 0 (with the reason) when it can't be opened.</summary>
    public static nint OpenReader(string path, out string? error)
    {
        error = null;
        if (!Start())
        {
            error = "Media Foundation isn't available.";
            return 0;
        }
        nint reader;
        int hr;
        fixed (char* url = path) hr = MFCreateSourceReaderFromURL(url, 0, out reader);
        if (hr >= 0) return reader;
        error = $"This file can't be read (0x{hr:X8}).";
        return 0;
    }

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateAttributes(out nint attributes, uint initialSize);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateMediaType(out nint type);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMemoryBuffer(uint length, out nint buffer);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateSample(out nint sample);

    [LibraryImport("mfreadwrite.dll")]
    public static partial int MFCreateSourceReaderFromURL(char* url, nint attributes, out nint reader);

    [LibraryImport("mfreadwrite.dll")]
    public static partial int MFCreateSinkWriterFromURL(char* url, nint byteStream, nint attributes, out nint writer);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(byte* value);
}

/// <summary>An MP4 Sink Writer with helpers for its streams and samples.</summary>
internal sealed unsafe class SinkWriter : IDisposable
{
    private nint _writer;

    private SinkWriter(nint writer) => _writer = writer;

    /// <summary>A writer for an MP4 at <paramref name="path"/> (which must not exist), encoding on the GPU where it can.</summary>
    public static SinkWriter Create(string path)
    {
        if (!MfInterop.Start()) throw new InvalidOperationException("Media Foundation isn't available.");
        var attributes = MfInterop.CreateAttributes(3);
        try
        {
            MfInterop.SetUInt32(attributes, MfInterop.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            // Writing is never real time: samples are handed over as fast as they are made.
            MfInterop.SetUInt32(attributes, MfInterop.MF_SINK_WRITER_DISABLE_THROTTLING, 1);
            MfInterop.SetGuid(attributes, MfInterop.MF_TRANSCODE_CONTAINERTYPE, MfInterop.MFTranscodeContainerType_MPEG4);
            nint writer;
            fixed (char* url = path) MfInterop.Check(MfInterop.MFCreateSinkWriterFromURL(url, 0, attributes, out writer), "MFCreateSinkWriterFromURL");
            return new SinkWriter(writer);
        }
        finally
        {
            MfInterop.Release(attributes);
        }
    }

    // IMFSinkWriter: 3 AddStream, 4 SetInputMediaType, 5 BeginWriting, 6 WriteSample, 11 Finalize.
    public uint AddStream(nint outputType)
    {
        uint index;
        MfInterop.Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)MfInterop.Slot(_writer, 3))(_writer, outputType, &index), "AddStream");
        return index;
    }

    public void SetInputMediaType(uint stream, nint inputType) =>
        MfInterop.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)MfInterop.Slot(_writer, 4))(_writer, stream, inputType, 0), "SetInputMediaType");

    public void BeginWriting() => MfInterop.Check(((delegate* unmanaged[Stdcall]<nint, int>)MfInterop.Slot(_writer, 5))(_writer), "BeginWriting");

    public void WriteSample(uint stream, nint sample) =>
        MfInterop.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)MfInterop.Slot(_writer, 6))(_writer, stream, sample), "WriteSample");

    public void Finish() => MfInterop.Check(((delegate* unmanaged[Stdcall]<nint, int>)MfInterop.Slot(_writer, 11))(_writer), "Finalize");

    /// <summary>A sample holding a copy of <paramref name="data"/>, at <paramref name="time"/> for <paramref name="duration"/> (100 ns units).</summary>
    public static nint Sample(ReadOnlySpan<byte> data, long time, long duration)
    {
        MfInterop.Check(MfInterop.MFCreateMemoryBuffer((uint)data.Length, out var buffer), "MFCreateMemoryBuffer");
        nint sample = 0;
        try
        {
            byte* target;
            // IMFMediaBuffer: 3 Lock, 4 Unlock, 6 SetCurrentLength.
            MfInterop.Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)MfInterop.Slot(buffer, 3))(buffer, &target, null, null), "Lock");
            data.CopyTo(new Span<byte>(target, data.Length));
            ((delegate* unmanaged[Stdcall]<nint, int>)MfInterop.Slot(buffer, 4))(buffer);
            MfInterop.Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)MfInterop.Slot(buffer, 6))(buffer, (uint)data.Length), "SetCurrentLength");
            MfInterop.Check(MfInterop.MFCreateSample(out sample), "MFCreateSample");
            // IMFSample: 42 AddBuffer, 36 SetSampleTime, 38 SetSampleDuration.
            MfInterop.Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)MfInterop.Slot(sample, 42))(sample, buffer), "AddBuffer");
            MfInterop.Check(((delegate* unmanaged[Stdcall]<nint, long, int>)MfInterop.Slot(sample, 36))(sample, time), "SetSampleTime");
            MfInterop.Check(((delegate* unmanaged[Stdcall]<nint, long, int>)MfInterop.Slot(sample, 38))(sample, duration), "SetSampleDuration");
            return sample;
        }
        catch
        {
            MfInterop.Release(sample);
            throw;
        }
        finally
        {
            MfInterop.Release(buffer);
        }
    }

    public void Dispose()
    {
        MfInterop.Release(_writer);
        _writer = 0;
    }
}
