using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Migu Music (咪咕音乐): its unsigned web search (<c>search_all.do</c>); each hit links its line LRC file and, often, an
/// encrypted word-timed MRC file. The signed app search Lyrimuse falls back to (a fixed device ID and signing key) is not
/// used. Ported from the Mac's <c>MoreLyricsProviders.migu</c> (dc56f8a).
/// </summary>
public sealed partial class MiguSource(LyricHttpClient http, ILyricPayloadProbe? probe = null) : ILyricSource
{
    private const string Service = "Migu Music";
    private static readonly Dictionary<string, string> Browser = new() { ["User-Agent"] = "Mozilla/5.0" };
    private readonly ILyricPayloadProbe _probe = probe ?? ParsedLyricPayloadProbe.Instance;

    public LyricSourceId Id => LyricSourceId.Migu;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parameters = new Dictionary<string, string>
        {
            ["text"] = query.Artist + " " + query.Title, ["pageNo"] = "1", ["pageSize"] = "10", ["searchSwitch"] = """{"song":1}""",
            ["isCorrect"] = "1",
        };
        IReadOnlyList<Item>? items = null;
        Exception failure = new LyricSourceException(LyricSourceFailure.BadResponse, $"{Service}: no answer");
        foreach (var host in new[] { "pd.musicapp.migu.cn", "app.c.nf.migu.cn" })
        {
            JsonDocument search;
            try
            {
                search = await MoreLyrics.JsonAsync(http, Service, $"https://{host}/MIGUM2.0/v1.0/content/search_all.do", ct, parameters,
                    headers: new Dictionary<string, string> { ["Referer"] = "https://m.music.migu.cn/", ["User-Agent"] = "Mozilla/5.0" })
                    .ConfigureAwait(false);
            }
            catch (LyricSourceException exception)
            {
                failure = exception;
                continue;
            }
            using (search)
            {
                if (search.RootElement.Get("code") is { ValueKind: JsonValueKind.String } code && code.GetString() != "000000")
                {
                    throw new LyricSourceException(LyricSourceFailure.BadResponse, $"{Service}: answered {code.GetString()}");
                }
                items = Items(search.RootElement);
            }
            break;
        }
        if (items is null) throw failure;
        var hits = items.Select(i => MoreLyrics.MakeHit(i.Id, i.Title, i.Artist, i.Duration, query)).OfType<MoreLyrics.Hit>().ToList();
        var files = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (var item in items) files.TryAdd(item.Id, item);
        foreach (var hit in MoreLyrics.Rank(hits, Id, query).Take(2))
        {
            ct.ThrowIfCancellationRequested();
            if (!files.TryGetValue(hit.Id, out var file)) continue;
            // Word timing first; a failed MRC request just leaves the line file.
            if (file.Mrc.Length > 0 && await MrcAsync(file.Mrc, ct).ConfigureAwait(false) is { } words
                && MoreLyrics.Offer(_probe, Id, words, hit) is { } timed)
            {
                return timed;
            }
            ct.ThrowIfCancellationRequested();
            var raw = await MoreLyrics.DataAsync(http, Service, file.Lrc, ct, headers: Browser).ConfigureAwait(false);
            if (LyricTextDecoder.Decode(raw) is not { } text) continue;
            if (MoreLyrics.Offer(_probe, Id, StripHeader(text), hit) is { } found) return found;
        }
        return null;
    }

    private async Task<string?> MrcAsync(string address, CancellationToken ct)
    {
        try
        {
            var raw = await MoreLyrics.DataAsync(http, Service, address, ct, headers: Browser).ConfigureAwait(false);
            return Decrypt(LyricHttpClient.Utf8Text(raw)) is { } text ? EnhancedLrc(text) : null;
        }
        catch (LyricSourceException)
        {
            return null;
        }
    }

    /// <summary>A search hit and its lyric files.</summary>
    public sealed record Item(string Id, string Title, string Artist, double? Duration, string Lrc, string Mrc);

    /// <summary>The search's songs that link a line file.</summary>
    public static IReadOnlyList<Item> Items(JsonElement search)
    {
        var items = new List<Item>();
        var index = 0;
        foreach (var item in search.Get("songResultData").Items("result"))
        {
            var position = index++;
            if (item.Get("name") is not { ValueKind: JsonValueKind.String } title
                || item.Get("lyricUrl") is not { ValueKind: JsonValueKind.String } lrc || lrc.GetString()!.Length == 0)
            {
                continue;
            }
            var id = MoreLyrics.Text(item.Get("copyrightId")) ?? MoreLyrics.Text(item.Get("id")) ?? position.ToString(CultureInfo.InvariantCulture);
            var artist = string.Join(" & ", item.Items("singers").Select(s => s.Get("name")).Where(n => n?.ValueKind == JsonValueKind.String)
                .Select(n => n!.Value.GetString()));
            var mrc = item.Get("mrcurl") is { ValueKind: JsonValueKind.String } m ? m.GetString()! : "";
            items.Add(new Item(id, title.GetString()!, artist, Duration(item), lrc.GetString()!, mrc));
        }
        return items;
    }

    /// <summary>The search gives no length; an MP3 rendition's size at its bit rate does.</summary>
    public static double? Duration(JsonElement item)
    {
        foreach (var (format, kbps) in new[] { ("020007", 128.0), ("020010", 320.0), ("000019", 64.0) })
        {
            foreach (var key in new[] { "newRateFormats", "rateFormats" })
            {
                foreach (var rate in item.Items(key))
                {
                    if (rate.Get("format") is { ValueKind: JsonValueKind.String } f && f.GetString() == format
                        && rate.Get("fileType") is { ValueKind: JsonValueKind.String } type
                        && string.Equals(type.GetString(), "mp3", StringComparison.OrdinalIgnoreCase)
                        && MoreLyrics.Number(rate.Get("size")) is { } size and > 0)
                    {
                        return size * 8 / (kbps * 1000);
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Migu's LRC opens with "歌曲名 …" and "歌手名 …" lines on real timestamps: dropped.</summary>
    public static string StripHeader(string lrc)
    {
        ArgumentNullException.ThrowIfNull(lrc);
        return string.Join("\n", LrcParser.SplitNewlines(lrc).Where(line =>
        {
            var text = Stamps().Replace(line, "").Trim();
            return !(text.StartsWith("歌曲名", StringComparison.Ordinal) || text.StartsWith("歌手名", StringComparison.Ordinal) || text == "@migu music@");
        }));
    }

    private static readonly long[] Key = [27303562373562475, 18014862372307051, 22799692160172081, 34058940340699235];

    /// <summary>An MRC file: hex text of 64-bit words, XXTEA-encrypted with the key Migu's player uses, holding UTF-16LE text.</summary>
    public static string? Decrypt(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var digits = hex.Trim();
        var count = digits.Length / 16;
        if (count is < 2 or > 200_000) return null;
        var v = new long[count];
        for (var i = 0; i < count; i++)
        {
            if (!ulong.TryParse(digits.AsSpan(i * 16, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var word)) return null;
            v[i] = unchecked((long)word);
        }
        const long Delta = 0x9E37_79B9;
        static long Mix(long sum, long y, long z, int p, long e) => unchecked(
            (((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4))) ^ ((sum ^ y) + (Key[(int)((p & 3) ^ e)] ^ z)));
        unchecked
        {
            var sum = (6 + (52 / count)) * Delta;
            var y = v[0];
            while (sum != 0)
            {
                var e = (sum >> 2) & 3;
                for (var p = count - 1; p > 0; p--)
                {
                    v[p] -= Mix(sum, y, v[p - 1], p, e);
                    y = v[p];
                }
                v[0] -= Mix(sum, y, v[count - 1], 0, e);
                y = v[0];
                sum -= Delta;
            }
        }
        var units = new List<char>(count * 4);
        foreach (var word in v)
        {
            var bits = unchecked((ulong)word);
            for (var shift = 0; shift < 64; shift += 16) units.Add((char)(ushort)(bits >> shift));
        }
        while (units.Count > 0 && units[^1] == '\0') units.RemoveAt(units.Count - 1);
        return new string([.. units]);
    }

    /// <summary>
    /// MRC lines read <c>[lineMs,lengthMs]word(startMs,lengthMs)…</c>, word first, absolute times, as QQ's QRC does. The
    /// <c>[0,0]</c> title and credit lines are dropped.
    /// </summary>
    public static string? EnhancedLrc(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var output = new List<string>();
        foreach (var raw in LrcParser.SplitNewlines(text))
        {
            var head = MrcHead().Match(raw);
            if (!head.Success || (head.Groups[1].Value == "0" && head.Groups[2].Value == "0")) continue;
            var body = raw[head.Length..];
            var words = new List<MoreLyrics.TimedWord>();
            foreach (Match mark in MrcPiece().Matches(body))
            {
                if (!double.TryParse(mark.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                    || !double.TryParse(mark.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var span))
                {
                    continue;
                }
                words.Add(new MoreLyrics.TimedWord(start / 1000, (start + span) / 1000, mark.Groups[1].Value));
            }
            if (MoreLyrics.EnhancedLine(words) is { } row) output.Add(row);
        }
        return output.Count == 0 ? null : string.Join("\n", output);
    }

    [GeneratedRegex(@"\[[^\]]*\]", RegexOptions.CultureInvariant)]
    private static partial Regex Stamps();

    [GeneratedRegex(@"^\[([0-9]+),([0-9]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex MrcHead();

    [GeneratedRegex(@"(.*?)\(([0-9]+),([0-9]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MrcPiece();
}
