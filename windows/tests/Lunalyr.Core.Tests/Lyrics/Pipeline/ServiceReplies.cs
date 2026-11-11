using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lunalyr.Core.Tests.Lyrics.Pipeline;

/// <summary>One sung line of an invented lyric, with its start and end in seconds.</summary>
internal sealed record SungLine(string Text, double Start, double End);

/// <summary>
/// Service replies in the envelopes recorded under Fixtures/LyricSources (same fields, same nesting), filled with
/// invented lines so a test can vary timing and names. Nothing here reaches the network.
/// </summary>
internal static class ServiceReplies
{
    private static string Json(string text) => JsonSerializer.Serialize(text, ReplyJson.Default.String);

    private static string Ms(double seconds) => ((long)Math.Round(seconds * 1000)).ToString(CultureInfo.InvariantCulture);

    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>LRC with a stamp per line ("[mm:ss.xx]text").</summary>
    public static string Lrc(IEnumerable<SungLine> lines) =>
        string.Join('\n', lines.Select(line => string.Create(CultureInfo.InvariantCulture,
            $"[{(int)(line.Start / 60):00}:{line.Start % 60:00.00}]{line.Text}")));

    private static IEnumerable<(string Word, double Start, double End)> Words(SungLine line)
    {
        var words = line.Text.Split(' ');
        var step = (line.End - line.Start) / words.Length;
        for (var i = 0; i < words.Length; i++)
        {
            yield return (words[i], line.Start + (i * step), line.Start + ((i + 1) * step) - 0.02);
        }
    }

    /// <summary>LRCLIB's /api/get (or one /api/search row): synced LRC, plain text and, when asked, a word-timed Lyricsfile.</summary>
    public static string LrclibRow(long id, string title, string artist, string album, double duration, IReadOnlyList<SungLine> lines, bool wordTimed)
    {
        string? lyricsfile = null;
        if (wordTimed)
        {
            var yaml = new StringBuilder("version: '1.0'\nmetadata:\n  title: ").Append(title).Append("\n  artist: ").Append(artist)
                .Append("\n  instrumental: false\nlines:\n");
            foreach (var line in lines)
            {
                yaml.Append("- text: ").Append(line.Text).Append("\n  start_ms: ").Append(Ms(line.Start)).Append("\n  end_ms: ").Append(Ms(line.End))
                    .Append("\n  words:\n");
                var words = Words(line).ToList();
                for (var i = 0; i < words.Count; i++)
                {
                    var text = i + 1 < words.Count ? words[i].Word + " " : words[i].Word;
                    yaml.Append("  - text: '").Append(text).Append("'\n    start_ms: ").Append(Ms(words[i].Start))
                        .Append("\n    end_ms: ").Append(Ms(words[i].End)).Append('\n');
                }
            }
            lyricsfile = yaml.ToString();
        }
        return $$"""{"id":{{id}},"name":{{Json(title)}},"trackName":{{Json(title)}},"artistName":{{Json(artist)}},"albumName":{{Json(album)}},"duration":{{Num(duration)}},"instrumental":false,"plainLyrics":{{Json(string.Join('\n', lines.Select(l => l.Text)))}},"syncedLyrics":{{Json(Lrc(lines))}},"lyricsfile":{{(lyricsfile is null ? "null" : Json(lyricsfile))}}}""";
    }

    /// <summary>A Lyrics+ node's reply: syllable-timed lines and the matched recording.</summary>
    public static string LyricsPlus(string title, string artist, IReadOnlyList<SungLine> lines)
    {
        var entries = lines.Select(line =>
        {
            var words = Words(line).ToList();
            var syllabus = string.Join(',', words.Select((w, i) =>
                $$"""{"time":{{Ms(w.Start)}},"duration":{{Ms(w.End - w.Start)}},"text":{{Json(i + 1 < words.Count ? w.Word + " " : w.Word)}}}"""));
            return $$"""{"time":{{Ms(line.Start)}},"duration":{{Ms(line.End - line.Start)}},"text":{{Json(line.Text)}},"syllabus":[{{syllabus}}],"element":{"key":"","singer":"v1","songPartIndex":0} }""";
        });
        return $$"""{"KpoeTools":"1.7-1-ConvertTTMLtoJSON-DOMParser","type":"Word","metadata":{"source":"Apple","songWriters":[],"title":{{Json(title)}},"language":"en","artist":{{Json(artist)}}},"lyrics":[{{string.Join(',', entries)}}],"cached":"None"}""";
    }

    public const string LyricsPlusNotFound = """{"error":"Lyrics not found"}""";

    /// <summary>NetEase's song search with one or more songs (id, name, artists, length in seconds).</summary>
    public static string NetEaseSearch(params (long Id, string Name, string Artist, double Duration)[] songs) =>
        $$"""{"result":{"songs":[{{string.Join(',', songs.Select(s =>
            $$"""{"id":{{s.Id}},"name":{{Json(s.Name)}},"artists":[{"name":{{Json(s.Artist)}},"id":0}],"album":{"name":"","id":0},"duration":{{Ms(s.Duration)}},"fee":8,"status":0}"""))}}],"songCount":{{songs.Length}}},"code":200}""";

    /// <summary>NetEase's lyric reply: line LRC and, when given, word-timed YRC.</summary>
    public static string NetEaseLyric(string lrc, string? yrc = null) =>
        $$"""{"sgc":false,"sfy":false,"qfy":false,"lrc":{"version":1,"lyric":{{Json(lrc)}}},"klyric":{"version":0,"lyric":""},"tlyric":{"version":0,"lyric":""},"romalrc":{"version":0,"lyric":""},"yrc":{"version":1,"lyric":{{Json(yrc ?? "")}}},"code":200}""";

    /// <summary>QQ Music's search with one song.</summary>
    public static string QQSearch(string mid, string name, string singer, int seconds) =>
        $$"""{"code":0,"data":{"keyword":"","song":{"curnum":1,"curpage":1,"list":[{"albumname":"","interval":{{seconds}},"singer":[{"id":1,"mid":"x","name":{{Json(singer)}}}],"songid":1,"songmid":{{Json(mid)}},"songname":{{Json(name)}}}],"totalnum":1} },"message":"","subcode":0}""";

    public const string QQNoSongs = """{"code":0,"data":{"keyword":"","song":{"curnum":0,"curpage":1,"list":[],"totalnum":0}},"message":"","subcode":0}""";

    public static string QQLyric(string lrc) => $$"""{"retcode":0,"code":0,"subcode":0,"lyric":{{Json(lrc)}},"trans":""}""";

    public const string NetEaseNoSongs = """{"result":{"songs":[],"songCount":0},"code":200}""";
    public const string KugouNoSongs = """{"status":1,"error":"","errcode":0,"data":{"timestamp":1762872000,"total":0,"info":[]}}""";
    public const string KuwoNoSongs = """{"HIT":"0","PN":"0","RN":"8","SHOW":"0","TOTAL":"0","abslist":[]}""";
    public const string LrclibNotFound = """{"code":404,"name":"TrackNotFound","message":"Failed to find specified track"}""";

    public static string LyricsOvh(IEnumerable<string> lines) => $$"""{"lyrics":{{Json(string.Join('\n', lines))}}}""";
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class ReplyJson : System.Text.Json.Serialization.JsonSerializerContext;
