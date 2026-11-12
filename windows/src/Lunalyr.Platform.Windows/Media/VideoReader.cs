// Reads a video file's frames with Media Foundation's Source Reader, for "Imported image or video". Only the first video
// stream is ever selected: an audio stream is never decoded, so nothing can be heard (no audio session is opened; a
// Source Reader renders nothing itself). Frames are decoded on the GPU when a Direct3D 11 device is given (DXVA through
// a DXGI device manager, converted to BGRA by the reader's video processor) and come out as textures on that device;
// without one they come out in memory. Made through the COM vtables, as the DirectComposition calls are: no package.

using System.Runtime.InteropServices;

namespace Lunalyr.Platform.Media;

/// <summary>One decoded frame. Dispose it once copied.</summary>
public unsafe struct VideoFrame : IDisposable
{
    internal nint Sample, Buffer;

    /// <summary>The frame's <c>ID3D11Texture2D</c> (hardware decoding), or 0. Valid until disposed.</summary>
    public nint Texture { get; internal set; }

    /// <summary>The texture's subresource (decoders write into texture arrays).</summary>
    public uint Subresource { get; internal set; }

    /// <summary>The frame's pixels (software decoding): BGRA rows <see cref="Pitch"/> bytes apart, or null.</summary>
    public byte* Pixels { get; internal set; }

    public int Pitch { get; internal set; }

    /// <summary>Presentation time in seconds from the file's start.</summary>
    public double Time { get; set; }

    public void Dispose()
    {
        if (Pixels is not null && Buffer != 0) VideoReader.Call(Buffer, 4); // IMF2DBuffer::Unlock2D
        Pixels = null;
        if (Texture != 0) VideoReader.Release(Texture);
        Texture = 0;
        if (Buffer != 0) VideoReader.Release(Buffer);
        Buffer = 0;
        if (Sample != 0) VideoReader.Release(Sample);
        Sample = 0;
    }
}

/// <summary>What <see cref="VideoReader.Read"/> found.</summary>
public enum VideoRead
{
    Frame,
    EndOfStream,
    /// <summary>Nothing this time (a gap in the stream); read again.</summary>
    Nothing,
    Failed,
}

/// <summary>A silent, looping-capable reader of one video file's frames.</summary>
public sealed unsafe partial class VideoReader : IDisposable
{
    private const uint FirstVideoStream = 0xFFFFFFFC, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;
    private const uint ErrorFlag = 0x1, EndOfStreamFlag = 0x2, CurrentTypeChangedFlag = 0x20;

    private static readonly Guid MF_SOURCE_READER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_MINIMUM_DISPLAY_APERTURE = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_ARGB32 = new("00000015-0000-0010-8000-00AA00389B71");
    private static readonly Guid IID_IMFDXGIBuffer = new("e7174cfa-1c9e-48b1-8866-626226bfc258");
    private static readonly Guid IID_IMF2DBuffer = new("7DC9D5F9-9ED9-44ec-9BBF-0600BB589FBB");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private static readonly Lazy<bool> Started = new(() => MFStartup(0x00020070, 0) >= 0);

    private nint _reader, _manager;

    private VideoReader() { }

    /// <summary>The frames' size in pixels (the visible area).</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>The file's length in seconds, or 0 when it doesn't say.</summary>
    public double Duration { get; private set; }

    /// <summary>Frames are decoded on the GPU into textures on the device given to <see cref="Open"/>.</summary>
    public bool Hardware { get; private set; }

    /// <summary>
    /// Opens <paramref name="path"/> for its first video stream only, decoding on <paramref name="d3dDevice"/> (an
    /// <c>ID3D11Device</c> made with video support and multithread protection) when not 0, else in memory. Null when
    /// the file has no video stream Media Foundation can decode; <paramref name="error"/> says why.
    /// </summary>
    public static VideoReader? Open(string path, nint d3dDevice, out string? error)
    {
        error = null;
        if (!Started.Value)
        {
            error = "Media Foundation isn't available.";
            return null;
        }
        if (d3dDevice != 0 && TryOpen(path, d3dDevice, out var reader, out error)) return reader;
        return TryOpen(path, 0, out reader, out error) ? reader : null;
    }

    /// <summary>Whether <paramref name="path"/> has a video stream Media Foundation can decode (the import check).</summary>
    public static bool HasVideo(string path, out string? error)
    {
        using var reader = Open(path, 0, out error);
        if (reader is null) return false;
        var result = reader.Read(out var frame);
        frame.Dispose();
        if (result is VideoRead.Frame or VideoRead.Nothing) return true;
        error = "This file has no playable video track.";
        return false;
    }

    private static bool TryOpen(string path, nint device, out VideoReader? opened, out string? error)
    {
        opened = null;
        error = null;
        var reader = new VideoReader();
        nint attributes = 0;
        try
        {
            Check(MFCreateAttributes(out attributes, 3), "MFCreateAttributes");
            if (device != 0)
            {
                Check(MFCreateDXGIDeviceManager(out var token, out reader._manager), "MFCreateDXGIDeviceManager");
                // IMFDXGIDeviceManager: 7 ResetDevice.
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Slot(reader._manager, 7))(reader._manager, device, token), "ResetDevice");
                SetUnknown(attributes, MF_SOURCE_READER_D3D_MANAGER, reader._manager);
                SetUInt32(attributes, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            }
            SetUInt32(attributes, MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
            fixed (char* url = path) Check(MFCreateSourceReaderFromURL(url, attributes, out reader._reader), "MFCreateSourceReaderFromURL");

            // The first video stream only: audio is never selected, so never decoded or heard.
            var r = reader._reader;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(r, 4))(r, AllStreams, 0), "SetStreamSelection");
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(r, 4))(r, FirstVideoStream, 1), "SetStreamSelection (video)");
            Check(MFCreateMediaType(out var type), "MFCreateMediaType");
            try
            {
                SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                SetGuid(type, MF_MT_SUBTYPE, MFVideoFormat_ARGB32);
                // IMFSourceReader: 7 SetCurrentMediaType.
                Check(((delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)Slot(r, 7))(r, FirstVideoStream, null, type), "SetCurrentMediaType");
            }
            finally
            {
                Release(type);
            }
            reader.ReadFormat();
            reader.Duration = reader.ReadDuration();
            reader.Hardware = device != 0;
            opened = reader;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            error = ex.Message;
            reader.Dispose();
            return false;
        }
        finally
        {
            if (attributes != 0) Release(attributes);
        }
    }

    private void ReadFormat()
    {
        nint type;
        // IMFSourceReader: 6 GetCurrentMediaType.
        Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_reader, 6))(_reader, FirstVideoStream, &type), "GetCurrentMediaType");
        try
        {
            ulong size;
            Guid frameSize = MF_MT_FRAME_SIZE, aperture = MF_MT_MINIMUM_DISPLAY_APERTURE;
            // IMFAttributes: 8 GetUINT64.
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(type, 8))(type, &frameSize, &size), "MF_MT_FRAME_SIZE");
            Width = (int)(size >> 32);
            Height = (int)(size & 0xFFFFFFFF);
            // A coded size padded to the decoder's blocks (1088 for 1080) shows only its display aperture.
            var area = stackalloc byte[16];
            uint blob;
            // IMFAttributes: 15 GetBlob.
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, byte*, uint, uint*, int>)Slot(type, 15))(type, &aperture, area, 16, &blob) >= 0
                && blob == 16)
            {
                int w = *(int*)(area + 8), h = *(int*)(area + 12);
                if (w > 0 && h > 0 && w <= Width && h <= Height)
                {
                    Width = w;
                    Height = h;
                }
            }
        }
        finally
        {
            Release(type);
        }
    }

    private double ReadDuration()
    {
        var value = stackalloc byte[24];
        Guid duration = MF_PD_DURATION;
        // IMFSourceReader: 12 GetPresentationAttribute.
        if (((delegate* unmanaged[Stdcall]<nint, uint, Guid*, byte*, int>)Slot(_reader, 12))(_reader, MediaSource, &duration, value) < 0) return 0;
        var vt = *(ushort*)value;
        var ticks = vt == 21 /* VT_UI8 */ ? *(ulong*)(value + 8) : 0;
        PropVariantClear(value);
        return ticks / 1e7;
    }

    /// <summary>
    /// Reads the next frame (blocking until decoded). A <see cref="VideoRead.Frame"/> must be disposed once used.
    /// </summary>
    public VideoRead Read(out VideoFrame frame)
    {
        frame = default;
        if (_reader == 0) return VideoRead.Failed;
        uint stream, flags;
        long time;
        nint sample;
        // IMFSourceReader: 9 ReadSample.
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(_reader, 9))(
            _reader, FirstVideoStream, 0, &stream, &flags, &time, &sample);
        if (hr < 0 || (flags & ErrorFlag) != 0)
        {
            if (sample != 0) Release(sample);
            return VideoRead.Failed;
        }
        if ((flags & CurrentTypeChangedFlag) != 0)
        {
            try { ReadFormat(); }
            catch (InvalidOperationException) { }
        }
        if (sample == 0) return (flags & EndOfStreamFlag) != 0 ? VideoRead.EndOfStream : VideoRead.Nothing;
        frame.Sample = sample;
        frame.Time = time / 1e7;
        nint buffer;
        // IMFSample (after IMFAttributes' 30 methods): 40 GetBufferByIndex.
        if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(sample, 40))(sample, 0, &buffer) < 0)
        {
            frame.Dispose();
            return VideoRead.Nothing;
        }
        if (Hardware && QueryInterface(buffer, IID_IMFDXGIBuffer) is var dxgi && dxgi != 0)
        {
            nint texture = 0;
            Guid textureIid = IID_ID3D11Texture2D;
            uint index = 0;
            // IMFDXGIBuffer: 3 GetResource, 4 GetSubresourceIndex.
            var got = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(dxgi, 3))(dxgi, &textureIid, &texture) >= 0
                && ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(dxgi, 4))(dxgi, &index) >= 0;
            Release(dxgi);
            Release(buffer);
            if (!got)
            {
                frame.Dispose();
                return VideoRead.Nothing;
            }
            frame.Texture = texture;
            frame.Subresource = index;
            return VideoRead.Frame;
        }
        var twoD = QueryInterface(buffer, IID_IMF2DBuffer);
        Release(buffer);
        if (twoD == 0)
        {
            frame.Dispose();
            return VideoRead.Nothing;
        }
        byte* scan0;
        int pitch;
        // IMF2DBuffer: 3 Lock2D.
        if (((delegate* unmanaged[Stdcall]<nint, byte**, int*, int>)Slot(twoD, 3))(twoD, &scan0, &pitch) < 0)
        {
            Release(twoD);
            frame.Dispose();
            return VideoRead.Nothing;
        }
        frame.Buffer = twoD;
        frame.Pixels = scan0;
        frame.Pitch = pitch;
        return VideoRead.Frame;
    }

    /// <summary>Back to the start, for the next loop.</summary>
    public bool Rewind()
    {
        if (_reader == 0) return false;
        var position = stackalloc byte[24];
        *(ushort*)position = 20; // VT_I8, 0
        var none = Guid.Empty;
        // IMFSourceReader: 8 SetCurrentPosition.
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, byte*, int>)Slot(_reader, 8))(_reader, &none, position) >= 0;
    }

    public void Dispose()
    {
        if (_reader != 0) Release(_reader);
        _reader = 0;
        if (_manager != 0) Release(_manager);
        _manager = 0;
    }

    // ---- COM plumbing ----

    internal static void* Slot(nint com, int index) => (*(void***)com)[index];

    internal static void Release(nint com)
    {
        if (com != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(com, 2))(com);
    }

    internal static void Call(nint com, int index) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(com, index))(com);

    private static nint QueryInterface(nint com, Guid iid)
    {
        nint result;
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(com, 0))(com, &iid, &result) >= 0 ? result : 0;
    }

    // IMFAttributes: 21 SetUINT32, 24 SetGUID, 27 SetUnknown.
    private static void SetUInt32(nint attributes, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attributes, 21))(attributes, &key, value), "SetUINT32");

    private static void SetGuid(nint attributes, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value), "SetGUID");

    private static void SetUnknown(nint attributes, Guid key, nint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)Slot(attributes, 27))(attributes, &key, value), "SetUnknown");

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8}).");
    }

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateAttributes(out nint attributes, uint initialSize);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateMediaType(out nint type);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateDXGIDeviceManager(out uint resetToken, out nint manager);

    [LibraryImport("mfreadwrite.dll")]
    private static partial int MFCreateSourceReaderFromURL(char* url, nint attributes, out nint reader);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(byte* value);
}
