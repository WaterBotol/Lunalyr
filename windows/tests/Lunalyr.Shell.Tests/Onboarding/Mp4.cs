using System.Buffers.Binary;
using System.Text;

namespace Lunalyr.Shell.Tests.Onboarding;

/// <summary>
/// Just enough of an MP4 reader to check the welcome's media without playing them (ISO/IEC 14496-12 boxes): each track's
/// handler type (<c>vide</c>, <c>soun</c>…) from <c>moov/trak/mdia/hdlr</c>, the movie's length from <c>moov/mvhd</c>, and
/// the picture's size from the video track's <c>tkhd</c>.
/// </summary>
internal sealed record Mp4(IReadOnlyList<string> Handlers, double Duration, double Width, double Height)
{
    public static Mp4 Read(string path)
    {
        var data = File.ReadAllBytes(path);
        var moov = Children(data, 0, data.Length).FirstOrDefault(b => b.Type == "moov");
        if (moov.Type is null) throw new InvalidDataException($"{path}: no moov box.");
        var handlers = new List<string>();
        double duration = 0, width = 0, height = 0;
        foreach (var box in Children(data, moov.Body, moov.End))
        {
            if (box.Type == "mvhd")
            {
                var version = data[box.Body];
                var at = box.Body + 4 + (version == 1 ? 16 : 8);
                var timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
                var length = version == 1 ? BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at + 4)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 4));
                duration = timescale == 0 ? 0 : (double)length / timescale;
            }
            else if (box.Type == "trak")
            {
                var track = Children(data, box.Body, box.End).ToList();
                var mdia = track.FirstOrDefault(b => b.Type == "mdia");
                var hdlr = mdia.Type is null ? default : Children(data, mdia.Body, mdia.End).FirstOrDefault(b => b.Type == "hdlr");
                var handler = hdlr.Type is null ? "?" : Encoding.ASCII.GetString(data, hdlr.Body + 8, 4);
                handlers.Add(handler);
                var tkhd = track.FirstOrDefault(b => b.Type == "tkhd");
                if (handler == "vide" && tkhd.Type is not null)
                {
                    // Width and height are the box's last two 16.16 fixed-point numbers.
                    width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(tkhd.End - 8)) / 65536.0;
                    height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(tkhd.End - 4)) / 65536.0;
                }
            }
        }
        return new Mp4(handlers, duration, width, height);
    }

    private readonly record struct Box(string Type, int Body, int End);

    private static IEnumerable<Box> Children(byte[] data, int start, int end)
    {
        var at = start;
        while (at + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
            var type = Encoding.ASCII.GetString(data, at + 4, 4);
            var header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at + 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - at;
            }
            if (size < header || at + size > end) yield break;
            yield return new Box(type, at + header, (int)(at + size));
            at += (int)size;
        }
    }
}
