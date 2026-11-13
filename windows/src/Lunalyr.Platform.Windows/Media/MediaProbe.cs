// What a media file holds, read with a Source Reader without decoding (compressed samples are counted, not opened):
// for checking the cards Lunalyr writes (tests, previews). Nothing is played.

namespace Lunalyr.Platform.Media;

/// <summary>A media file's streams as a Source Reader sees them.</summary>
/// <param name="VideoSubtype">The first video stream's format (H.264's GUID for a card), or null without one.</param>
/// <param name="Width">Its frame width, or 0.</param>
/// <param name="Height">Its frame height, or 0.</param>
/// <param name="VideoFrames">How many compressed video samples it has.</param>
/// <param name="VideoSeconds">From its first sample's start to its last sample's end.</param>
/// <param name="AudioSubtype">The first audio stream's format, or null without one.</param>
/// <param name="AudioSeconds">From its first sample's start to its last sample's end, or 0.</param>
/// <param name="Duration">The file's own duration (MF_PD_DURATION).</param>
public sealed record MediaInfo(Guid? VideoSubtype, int Width, int Height, int VideoFrames, double VideoSeconds, Guid? AudioSubtype,
                               double AudioSeconds, double Duration)
{
    public bool IsH264 => VideoSubtype == MfInterop.MFVideoFormat_H264;

    public bool IsAac => AudioSubtype == MfInterop.MFAudioFormat_AAC;

    public bool HasAudio => AudioSubtype is not null;

    /// <summary>Frames a second over the video's length.</summary>
    public double FrameRate => VideoSeconds > 0 ? VideoFrames / VideoSeconds : 0;
}

public static unsafe class MediaProbe
{
    public static MediaInfo? Read(string path)
    {
        var duration = 0.0;
        var (videoType, width, height, frames, videoSeconds) = Stream(path, MfInterop.FirstVideoStream, ref duration, video: true);
        var (audioType, _, _, _, audioSeconds) = Stream(path, MfInterop.FirstAudioStream, ref duration, video: false);
        if (videoType is null && audioType is null) return null;
        return new MediaInfo(videoType, width, height, frames, videoSeconds, audioType, audioSeconds, duration);
    }

    private static (Guid? Subtype, int Width, int Height, int Samples, double Seconds) Stream(string path, uint stream, ref double duration, bool video)
    {
        var reader = MfInterop.OpenReader(path, out _);
        if (reader == 0) return default;
        try
        {
            duration = Math.Max(duration, MfInterop.Duration(reader));
            if (!MfInterop.SelectOnly(reader, stream)) return default;
            nint native;
            if (((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)MfInterop.Slot(reader, 5))(reader, stream, 0, &native) < 0) return default;
            Guid? subtype;
            int width = 0, height = 0;
            try
            {
                subtype = MfInterop.GetGuid(native, MfInterop.MF_MT_SUBTYPE);
                if (video && MfInterop.GetUInt64(native, MfInterop.MF_MT_FRAME_SIZE) is { } size)
                {
                    width = (int)(size >> 32);
                    height = (int)(size & 0xFFFFFFFF);
                }
                // Read as stored: nothing is decoded.
                ((delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)MfInterop.Slot(reader, 7))(reader, stream, null, native);
            }
            finally
            {
                MfInterop.Release(native);
            }
            var samples = 0;
            long first = long.MaxValue, last = 0;
            while (true)
            {
                uint index, flags;
                long time;
                nint sample;
                var hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)MfInterop.Slot(reader, 9))(
                    reader, stream, 0, &index, &flags, &time, &sample);
                if (hr < 0 || (flags & MfInterop.ReadErrorFlag) != 0)
                {
                    MfInterop.Release(sample);
                    break;
                }
                if (sample != 0)
                {
                    long length;
                    // IMFSample: 37 GetSampleDuration.
                    if (((delegate* unmanaged[Stdcall]<nint, long*, int>)MfInterop.Slot(sample, 37))(sample, &length) < 0) length = 0;
                    first = Math.Min(first, time);
                    last = Math.Max(last, time + length);
                    samples++;
                    MfInterop.Release(sample);
                }
                if ((flags & MfInterop.ReadEndOfStreamFlag) != 0) break;
            }
            return (subtype, width, height, samples, samples > 0 ? (last - first) / 1e7 : 0);
        }
        finally
        {
            MfInterop.Release(reader);
        }
    }
}
