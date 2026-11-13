// The H.264 clip writer (Media Foundation's own encoder, no audio track), shared with tools/WallpaperCost, which links
// this file to make its long 1080p test clip.

using System.Runtime.InteropServices;

internal static unsafe partial class Video
{
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d"), FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"),
        Aspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
        Bitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"),
        VideoType = new("73646976-0000-0010-8000-00AA00389B71"), H264 = new("34363248-0000-0010-8000-00AA00389B71"),
        Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");

    /// <param name="fast">Paint the gradient loop from a colour table (for long full-HD clips); otherwise as always.</param>
    public static void Write(string path, int w, int h, int fps, int seconds, bool timecode = false, bool fast = false,
        int bitrate = 4_000_000)
    {
        Check(MFStartup(0x00020070, 0));
        if (File.Exists(path)) File.Delete(path);
        nint writer;
        fixed (char* url = path) Check(MFCreateSinkWriterFromURL(url, 0, 0, &writer));
        nint output = Type(H264, w, h, fps, stride: 0, bitrate), input = Type(Rgb32, w, h, fps, stride: w * 4, bitrate);
        uint stream;
        // IMFSinkWriter: 3 AddStream, 4 SetInputMediaType, 5 BeginWriting, 6 WriteSample, 11 Finalize.
        Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Slot(writer, 3))(writer, output, &stream));
        Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(writer, 4))(writer, stream, input, 0));
        Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(writer, 5))(writer));
        var frames = fps * seconds;
        long duration = 10_000_000 / fps;
        for (var f = 0; f < frames; f++)
        {
            nint buffer, sample;
            Check(MFCreateMemoryBuffer((uint)(w * h * 4), &buffer));
            byte* data;
            // IMFMediaBuffer: 3 Lock, 4 Unlock, 6 SetCurrentLength.
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, null, null));
            if (timecode) PaintTimecode(data, w, h, f, frames);
            else if (fast) PaintFast(data, w, h, f / (double)frames);
            else Paint(data, w, h, f / (double)frames);
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer);
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, 6))(buffer, (uint)(w * h * 4)));
            Check(MFCreateSample(&sample));
            // IMFSample: 42 AddBuffer, 36 SetSampleTime, 38 SetSampleDuration.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, 42))(sample, buffer));
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 36))(sample, f * duration));
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 38))(sample, duration));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Slot(writer, 6))(writer, stream, sample));
            Release(sample);
            Release(buffer);
        }
        Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(writer, 11))(writer));
        Release(writer);
        Release(output);
        Release(input);
    }

    // A loop: hues sweep across diagonally and a bright disc travels once round, back where it started at the end.
    private static void Paint(byte* data, int w, int h, double t)
    {
        double cx = w * (0.5 + 0.32 * Math.Cos(t * 2 * Math.PI)), cy = h * (0.5 + 0.3 * Math.Sin(t * 2 * Math.PI));
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                double phase = (x + y) / (double)(w + h) + t;
                double r = 0.5 + 0.5 * Math.Sin(2 * Math.PI * phase), g = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (phase + 1 / 3.0)),
                    b = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (phase + 2 / 3.0));
                double dx = x - cx, dy = y - cy, disc = dx * dx + dy * dy < 90 * 90 ? 1 : 0;
                var p = data + (y * w + x) * 4;
                p[0] = (byte)(255 * Math.Min(1, b * 0.7 + disc));
                p[1] = (byte)(255 * Math.Min(1, g * 0.7 + disc));
                p[2] = (byte)(255 * Math.Min(1, r * 0.7 + disc));
                p[3] = 255;
            }
    }

    // Paint's picture, its hues read from a table of 1024 steps instead of three sines a pixel: about forty times faster,
    // for full-HD clips long enough to cover a song. The disc's size follows the height (90 px at 720 lines).
    private static void PaintFast(byte* data, int w, int h, double t)
    {
        const int Steps = 1024;
        var table = stackalloc uint[Steps];
        for (var i = 0; i < Steps; i++)
        {
            double phase = i / (double)Steps;
            double r = 0.5 + 0.5 * Math.Sin(2 * Math.PI * phase), g = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (phase + 1 / 3.0)),
                b = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (phase + 2 / 3.0));
            table[i] = 0xFF000000u | ((uint)(255 * r * 0.7) << 16) | ((uint)(255 * g * 0.7) << 8) | (uint)(255 * b * 0.7);
        }
        double cx = w * (0.5 + 0.32 * Math.Cos(t * 2 * Math.PI)), cy = h * (0.5 + 0.3 * Math.Sin(t * 2 * Math.PI));
        double radius = 90.0 * h / 720, step = Steps / (double)(w + h);
        var pixels = (uint*)data;
        for (var y = 0; y < h; y++)
        {
            double start = (y / (double)(w + h) + t) * Steps;
            double dy = y - cy, half = radius * radius - dy * dy;
            int discFrom = int.MaxValue, discTo = int.MinValue;
            if (half > 0)
            {
                var reach = Math.Sqrt(half);
                discFrom = (int)Math.Ceiling(cx - reach);
                discTo = (int)Math.Floor(cx + reach);
            }
            var row = pixels + (long)y * w;
            for (var x = 0; x < w; x++)
                row[x] = x >= discFrom && x <= discTo ? 0xFFFFFFFFu : table[(int)(start + x * step) & (Steps - 1)];
        }
    }

    /// <summary>Bits of the frame number shown (up to 4095 frames).</summary>
    public const int TimecodeBits = 12;

    // Dark grey; the top third a row of TimecodeBits blocks, white for a 1 bit (bit 0 on the left); under it a white bar
    // sweeping from left to right over the clip's length.
    private static void PaintTimecode(byte* data, int w, int h, int frame, int frames)
    {
        var bar = (int)((w - 4) * (frame / (double)Math.Max(1, frames - 1)));
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                byte v = 40;
                if (y < h / 3)
                {
                    var bit = x * TimecodeBits / w;
                    v = ((frame >> bit) & 1) == 1 ? (byte)255 : (byte)0;
                }
                else if (y > h / 2 && x >= bar && x < bar + 4)
                {
                    v = 255;
                }
                var p = data + (y * w + x) * 4;
                p[0] = p[1] = p[2] = v;
                p[3] = 255;
            }
    }

    private static nint Type(Guid subtype, int w, int h, int fps, int stride, int bitrate)
    {
        nint type;
        Check(MFCreateMediaType(&type));
        SetGuid(type, MajorType, VideoType);
        SetGuid(type, Subtype, subtype);
        SetU64(type, FrameSize, ((ulong)w << 32) | (uint)h);
        SetU64(type, FrameRate, ((ulong)fps << 32) | 1);
        SetU64(type, Aspect, (1UL << 32) | 1);
        SetU32(type, Interlace, 2);
        if (stride != 0) SetU32(type, DefaultStride, (uint)stride);
        else SetU32(type, Bitrate, (uint)bitrate);
        return type;
    }

    private static void SetGuid(nint a, Guid k, Guid v) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(a, 24))(a, &k, &v));
    private static void SetU32(nint a, Guid k, uint v) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(a, 21))(a, &k, v));
    private static void SetU64(nint a, Guid k, ulong v) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(a, 22))(a, &k, v));
    private static void* Slot(nint com, int i) => (*(void***)com)[i];
    private static void Release(nint com) { if (com != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(com, 2))(com); }
    private static void Check(int hr) { if (hr < 0) throw new COMException("Media Foundation", hr); }

    [LibraryImport("mfplat.dll")] private static partial int MFStartup(uint version, uint flags);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateMediaType(nint* type);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateMemoryBuffer(uint length, nint* buffer);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateSample(nint* sample);
    [LibraryImport("mfreadwrite.dll")] private static partial int MFCreateSinkWriterFromURL(char* url, nint stream, nint attributes, nint* writer);
}
