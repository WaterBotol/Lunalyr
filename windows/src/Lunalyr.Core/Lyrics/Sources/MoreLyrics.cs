using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;

using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// What the sources added on 16 Nov 2026 share (Soda Music, PetitLyrics, Deezer, Migu, VIBE, Genie and LyricFind): the
/// Mac's <c>MoreLyricsProviders</c> helpers (Lunalyr's own code, written from the contracts documented in
/// Yudaotor/lyrimuse's engine). None asks for an account, a cookie or a per-user key. Each source searches once and
/// reads at most two hits, and offers only timed lyrics: word timing as enhanced LRC where the service has it, else
/// line timing.
/// </summary>
public static class MoreLyrics
{
    /// <summary>The browser the requests name unless a service is asked as something else.</summary>
    public const string BrowserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    /// <summary>The Mac's per-request timeout for these services.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// One request's reply, whatever its status. GET parameters are added to <paramref name="address"/>, sorted;
    /// a body is sent whole. A <c>Content-Type</c> header goes on the body.
    /// </summary>
    internal static Task<LyricHttpResponse> RequestAsync(LyricHttpClient http, string service, string address, CancellationToken ct,
        IReadOnlyDictionary<string, string>? parameters = null, HttpMethod? method = null, byte[]? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        Uri uri;
        try
        {
            uri = parameters is { Count: > 0 } ? LyricHttpClient.BuildUri(address, parameters) : new Uri(address);
        }
        catch (UriFormatException exception)
        {
            // An address a service handed back that isn't one.
            throw new LyricSourceException(LyricSourceFailure.BadResponse, $"{service}: invalid service address", inner: exception);
        }
        return http.SendAsync(service, () =>
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri);
            if (body is not null) request.Content = new ByteArrayContent(body);
            var agent = BrowserAgent;
            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            {
                if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) agent = value;
                else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    request.Content ??= new ByteArrayContent([]);
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                }
                else request.Headers.TryAddWithoutValidation(name, value);
            }
            request.Headers.TryAddWithoutValidation("User-Agent", agent);
            return request;
        }, RequestTimeout, ct);
    }

    /// <summary>One request's body; anything but HTTP 200 throws.</summary>
    internal static async Task<byte[]> DataAsync(LyricHttpClient http, string service, string address, CancellationToken ct,
        IReadOnlyDictionary<string, string>? parameters = null, HttpMethod? method = null, byte[]? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = await RequestAsync(http, service, address, ct, parameters, method, body, headers).ConfigureAwait(false);
        LyricHttpClient.EnsureOk(service, response);
        return response.Body;
    }

    /// <summary>One request's JSON object; anything but HTTP 200 with a JSON object throws.</summary>
    internal static async Task<JsonDocument> JsonAsync(LyricHttpClient http, string service, string address, CancellationToken ct,
        IReadOnlyDictionary<string, string>? parameters = null, HttpMethod? method = null, byte[]? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var data = await DataAsync(http, service, address, ct, parameters, method, body, headers).ConfigureAwait(false);
        var document = LyricHttpClient.ParseJson(service, data);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new LyricSourceException(LyricSourceFailure.BadResponse, $"{service}: unexpected response format");
        }
        return document;
    }

    /// <summary><c>application/x-www-form-urlencoded</c>, keys sorted, every other character percent-encoded.</summary>
    public static byte[] Form(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var builder = new StringBuilder();
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (builder.Length > 0) builder.Append('&');
            builder.Append(key).Append('=');
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                var c = (char)b;
                if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '.' or '_' or '*') builder.Append(c);
                else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>One sung piece with its start and end, in seconds.</summary>
    public readonly record struct TimedWord(double Start, double End, string Text);

    /// <summary>
    /// An enhanced-LRC line (<c>[mm:ss.xxx]&lt;mm:ss.xxx&gt;word…&lt;mm:ss.xxx&gt;</c>), the shape the LRC parser reads as
    /// word timing; null without a word.
    /// </summary>
    public static string? EnhancedLine(IEnumerable<TimedWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var kept = words.Where(w => w.Text.Length > 0 && double.IsFinite(w.Start) && double.IsFinite(w.End)).ToList();
        if (kept.Count == 0 || string.Concat(kept.Select(w => w.Text)).Trim().Length == 0) return null;
        var line = new StringBuilder("[").Append(KugouKrc.Clock(kept[0].Start)).Append(']');
        var end = kept[0].Start;
        foreach (var word in kept)
        {
            line.Append('<').Append(KugouKrc.Clock(word.Start)).Append('>').Append(word.Text.Replace('\n', ' '));
            end = Math.Max(end, Math.Max(word.End, word.Start));
        }
        return line.Append('<').Append(KugouKrc.Clock(end)).Append('>').ToString();
    }

    /// <summary>A line-timed LRC row, its text trimmed.</summary>
    public static string LrcLine(double seconds, string text) => "[" + KugouKrc.Clock(seconds) + "]" + text.Trim();

    /// <summary>"4:13" or "1:02:03" as seconds; null for "00:00" and anything else.</summary>
    public static double? ClockSeconds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Trim().Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3) return null;
        var total = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || value < 0) return null;
            total = (total * 60) + value;
        }
        return total > 0 ? total : null;
    }

    /// <summary>A number, or a string holding one.</summary>
    internal static double? Number(JsonElement? value) => value.AsNumber();

    /// <summary>A string, or a number written out (the Mac's <c>LyricHTTP.string</c>).</summary>
    internal static string? Text(JsonElement? value) => value.AsString();

    /// <summary>A search hit: the id its lyrics are fetched by, what it names, and its score.</summary>
    internal sealed record Hit(string Id, string Title, string Artist, double? Duration, double Score);

    /// <summary>The hit, or null without an id or title or when it doesn't match at all. <paramref name="bonus"/> is added to its score.</summary>
    internal static Hit? MakeHit(string? id, string? title, string artist, double? duration, LyricQuery query, double bonus = 0)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) return null;
        var value = LyricMatchScore.HitScore(query, title, artist, duration);
        return value > 0 ? new Hit(id, title, artist, duration, value + bonus) : null;
    }

    /// <summary>Hits that name the playing recording first, then by score (<see cref="SourceHits.Ordered"/>).</summary>
    internal static IReadOnlyList<Hit> Rank(IEnumerable<Hit> hits, LyricSourceId source, LyricQuery query) =>
        SourceHits.Ordered(hits, source, query, h => (h.Title, h.Artist, h.Duration, h.Score));

    /// <summary>The lyrics a hit gave, when they hold a timed line.</summary>
    internal static RawLyrics? Offer(ILyricPayloadProbe probe, LyricSourceId source, string lrc, Hit hit)
    {
        var text = LrcText.DecodeEntities(lrc);
        return probe.IsUsable(LyricFormat.Lrc, text)
            ? new RawLyrics(source, LyricFormat.Lrc, text, hit.Title, hit.Artist, hit.Duration,
                            new Dictionary<string, string> { [RawLyricsExtras.SongId] = hit.Id })
            : null;
    }
}

/// <summary>
/// A small element tree, enough for PetitLyrics' answers: each element's name, children and own text (whitespace
/// included). Read with DTDs prohibited and no resolver, as untrusted network XML.
/// </summary>
public sealed class XmlTree
{
    private readonly List<XmlTree> _children = [];
    private readonly StringBuilder _text = new();

    private XmlTree(string name) => Name = name;

    public string Name { get; }
    public IReadOnlyList<XmlTree> Children => _children;
    public string Text => _text.ToString();

    /// <summary>The first descendant named <paramref name="name"/>, depth first.</summary>
    public XmlTree? First(string name)
    {
        foreach (var child in _children)
        {
            if (child.Name == name) return child;
            if (child.First(name) is { } found) return found;
        }
        return null;
    }

    /// <summary>Every descendant named <paramref name="name"/>, in document order.</summary>
    public IReadOnlyList<XmlTree> All(string name)
    {
        var found = new List<XmlTree>();
        foreach (var child in _children)
        {
            if (child.Name == name) found.Add(child);
            found.AddRange(child.All(name));
        }
        return found;
    }

    /// <summary>The document under an unnamed root, or null when it isn't well-formed XML.</summary>
    public static XmlTree? Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var root = new XmlTree("");
        var path = new Stack<XmlTree>();
        path.Push(root);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false };
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var child = new XmlTree(reader.Name);
                        path.Peek()._children.Add(child);
                        if (!reader.IsEmptyElement) path.Push(child);
                        break;
                    case XmlNodeType.EndElement:
                        if (path.Count > 1) path.Pop();
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        path.Peek()._text.Append(reader.Value);
                        break;
                }
            }
            return root;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
