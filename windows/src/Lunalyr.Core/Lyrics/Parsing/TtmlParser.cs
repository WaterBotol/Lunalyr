// TTML lyrics: Apple Music's syllable TTML, AMLL TTML DB files and local exports.
//
// Provenance: the Mac's TTMLLyricParser.swift is Lunalyr's work apart from its XMLParser scaffolding, the time-value
// reader (`seconds`) and the begin/end/dur guards on <p> and <span>, which trace to the Verso import. Those parts are
// re-implemented here on XmlReader from this description:
//   * A time is "12.5s", "1250ms" or a clock value "m:ss.fff" / "h:mm:ss.fff" (one to three ":"-separated numbers,
//     each part times 60 plus the next). Anything else, negative, non-finite or past ten days is no time.
//   * A <p> needs a begin and an end (or begin + dur) with end after begin, and, when the track length is known, a
//     begin no more than 8 s past it; otherwise it is skipped. A <span>'s end may likewise come from its dur.
//   * The document is read without fetching anything: a DOCTYPE or ENTITY declaration rejects the file, as do files
//     over 2 MB, nesting deeper than 64 elements or more than 15,000 lines.
// Everything else (syllable gluing, background vocals, inline and Apple-supplied readings and translations, the
// translation language choice, credits) is Lunalyr's and ported as written.

using System.Globalization;
using System.Text;
using System.Xml;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>
/// Bounded, non-network TTML reader. Timed leaf spans become words; line-only paragraphs get estimated timing.
/// Apple-style syllable spans written without whitespace between them form one word in spaced scripts ("Ev" "'ry" →
/// "Ev'ry") and stay separate timed tokens in CJK; a line's text keeps the source spacing exactly. Background vocals
/// (<c>ttm:role="x-bg"</c>) become a parenthesised segment beside the main words so the main words keep their timing.
/// Supplied readings and translations, inline (<c>x-roman</c>, <c>x-translation</c>) or in Apple's
/// <c>&lt;iTunesMetadata&gt;</c> keyed by <c>itunes:key</c>, attach to their line instead of being shown as lyrics.
/// </summary>
public static class TtmlParser
{
    /// <summary>
    /// The lines of a TTML document. <paramref name="duration"/> is the playing track's length (0 when unknown).
    /// <paramref name="preferredLanguages"/> are the listener's languages, most preferred first (BCP 47 tags); they
    /// choose among supplied translations. Null means the current UI culture.
    /// </summary>
    public static IReadOnlyList<LyricLine> Parse(string text, double duration, IReadOnlyList<string>? preferredLanguages = null,
        WordDistributor? distribute = null)
    {
        if (Encoding.UTF8.GetByteCount(text) > 2_000_000 || text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase)) return [];
        var languages = preferredLanguages ?? [CultureInfo.CurrentUICulture.Name];
        var reader = new Reader(duration, languages, distribute);
        try
        {
            if (!reader.Read(text)) return [];
        }
        catch (XmlException)
        {
            return [];
        }
        return reader.Finish();
    }

    /// <summary>A TTML time value in seconds, or null (see the file header).</summary>
    public static double? Seconds(string? value)
    {
        if (value is null) return null;
        var text = TextRules.TrimSpaces(value);
        double result;
        if (text.EndsWith("ms", StringComparison.Ordinal))
        {
            if (!TextRules.TryParseDouble(text.AsSpan(0, text.Length - 2), out var ms)) return null;
            result = ms / 1000;
        }
        else if (text.EndsWith('s'))
        {
            if (!TextRules.TryParseDouble(text.AsSpan(0, text.Length - 1), out result)) return null;
        }
        else
        {
            var parts = text.Split(':');
            if (parts.Length is < 1 or > 3) return null;
            result = 0;
            foreach (var part in parts)
            {
                if (!TextRules.TryParseDouble(part, out var number)) return null;
                result = result * 60 + number;
            }
        }
        return double.IsFinite(result) && result is >= 0 and <= 864000 ? result : null;
    }

    private enum Part { Main, Background }

    private enum Reading { Romanization, Translation }

    private sealed class Segment
    {
        public readonly StringBuilder Text = new();
        public readonly List<TimedToken> Tokens = [];
    }

    private sealed class OpenSpan
    {
        public double? Start, End;
        public readonly StringBuilder Text = new();
        public bool TimedChild;
        public bool Glued;
        public Part Part = Part.Main;
        public Reading? Reading;
        /// <summary>The inline reading this span adds to.</summary>
        public int? Entry;
    }

    /// <summary>One inline x-translation or x-roman span's text.</summary>
    private sealed class Inline(string language)
    {
        public readonly string Language = language;
        public readonly StringBuilder Text = new();
    }

    private sealed class Paragraph(double start, double end, string? key)
    {
        public readonly double Start = start, End = end;
        public readonly string? Key = key;
        public readonly Segment Main = new(), Background = new();
        public readonly Dictionary<Reading, Dictionary<Part, List<Inline>>> Readings = [];
    }

    /// <summary>A text entry of a translation or transliteration, or one language of a paragraph's inline readings.</summary>
    private sealed class Supplied(string language, string main = "", string background = "")
    {
        public readonly string Language = language;
        public string Main = main;
        public string Background = background;
    }

    private sealed class Reader(double duration, IReadOnlyList<string> preferredLanguages, WordDistributor? distribute)
    {
        private readonly List<LyricLine> _lines = [];
        /// <summary>No sung line has come yet, or the line before was a credit.</summary>
        private bool _inCredits = true;
        private readonly List<string?> _keys = [];
        private readonly List<bool> _leadingBackground = [];
        private Paragraph? _paragraph;
        private readonly List<OpenSpan> _spans = [];
        private int _depth;
        private int _metadataDepth;
        private (Reading Reading, string Language)? _block;
        private (string Key, Supplied Value)? _entry;
        private readonly List<bool> _entrySpans = [];
        private readonly Dictionary<Reading, Dictionary<string, List<Supplied>>> _supplied = [];
        /// <summary>Each line's inline translations, kept until the whole document is read.</summary>
        private readonly List<(int Index, List<Supplied> Options, bool Leads)> _inlineTranslations = [];
        private List<string> _readableTranslations = [];

        /// <summary>Reads the document; false when it had to stop (too deep or too many lines).</summary>
        public bool Read(string text)
        {
            using var xml = new XmlTextReader(new StringReader(text))
            {
                Namespaces = false, // prefixes are matched by name, as the Mac's parser does; undeclared ones are fine
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                WhitespaceHandling = WhitespaceHandling.All,
            };
            while (xml.Read())
            {
                switch (xml.NodeType)
                {
                    case XmlNodeType.Element:
                        var name = xml.Name;
                        var empty = xml.IsEmptyElement;
                        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (xml.MoveToFirstAttribute())
                        {
                            do attributes[xml.Name] = xml.Value;
                            while (xml.MoveToNextAttribute());
                            xml.MoveToElement();
                        }
                        if (!StartElement(name, attributes)) return false;
                        if (empty) EndElement(name);
                        break;
                    case XmlNodeType.EndElement:
                        EndElement(xml.Name);
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        Characters(xml.Value);
                        break;
                    case XmlNodeType.EntityReference:
                        // An entity no DTD declares: the document is malformed, as the Mac's parser finds it.
                        return false;
                }
            }
            return true;
        }

        private static string LocalName(string name)
        {
            var colon = name.LastIndexOf(':');
            return colon < 0 ? name : name[(colon + 1)..];
        }

        /// <summary><paramref name="name"/> with any namespace prefix (ttm:role, itunes:key, xml:lang).</summary>
        private static string? Attribute(string name, Dictionary<string, string> attributes)
        {
            if (attributes.TryGetValue(name, out var value)) return value;
            var suffix = ":" + name;
            var key = attributes.Keys.Where(key => key.EndsWith(suffix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).FirstOrDefault();
            return key is null ? null : attributes[key];
        }

        private static Reading? ReadingForRole(string role) => role switch
        {
            "x-translation" => TtmlParser.Reading.Translation,
            "x-roman" or "x-romanization" or "x-romanisation" or "x-transliteration" or "x-pronunciation" => TtmlParser.Reading.Romanization,
            _ => null,
        };

        private bool StartElement(string elementName, Dictionary<string, string> attributes)
        {
            _depth++;
            if (_depth > 64 || _lines.Count >= 15000) return false;
            var name = LocalName(elementName);
            if (_metadataDepth > 0 || name == "metadata")
            {
                StartMetadata(name, attributes);
                return true;
            }
            var role = Attribute("role", attributes) ?? "";
            if (name == "p")
            {
                var start = Seconds(attributes.GetValueOrDefault("begin"));
                if (start is null) return true;
                var end = Seconds(attributes.GetValueOrDefault("end")) ?? (Seconds(attributes.GetValueOrDefault("dur")) is { } dur ? start + dur : null);
                if (end is null || !(end > start) || !(duration <= 0 || start <= duration + 8)) return true;
                _paragraph = new Paragraph(start.Value, end.Value, attributes.GetValueOrDefault("itunes:key") ?? Attribute("key", attributes));
                _spans.Clear();
            }
            else if (name == "span" && _paragraph is { } p)
            {
                var inherited = _spans.Count > 0 ? _spans[^1] : null;
                var part = inherited?.Part ?? Part.Main;
                if ((inherited?.Reading ?? ReadingForRole(role)) is { } reading)
                {
                    // Each reading span is one entry in its own language; spans inside it add to that entry.
                    var entry = inherited?.Entry;
                    if (inherited?.Reading is null)
                    {
                        var byPart = GetOrAdd(p.Readings, reading);
                        var entries = GetOrAdd(byPart, part);
                        entry = entries.Count;
                        entries.Add(new Inline(Attribute("lang", attributes) ?? ""));
                    }
                    _spans.Add(new OpenSpan { Part = part, Reading = reading, Entry = entry });
                    return true;
                }
                var opensBackground = role == "x-bg" && part == Part.Main;
                var spanPart = opensBackground ? Part.Background : part;
                // Separate background parts never run together.
                if (opensBackground && p.Background.Text.Length > 0) p.Background.Text.Append(' ');
                var text = spanPart == Part.Main ? p.Main.Text : p.Background.Text;
                var spanStart = Seconds(attributes.GetValueOrDefault("begin"));
                var spanEnd = Seconds(attributes.GetValueOrDefault("end"))
                    ?? (Seconds(attributes.GetValueOrDefault("dur")) is { } spanDur ? (spanStart ?? p.Start) + spanDur : null);
                _spans.Add(new OpenSpan
                {
                    Start = spanStart, End = spanEnd, Glued = text.Length > 0 && !char.IsWhiteSpace(text[^1]), Part = spanPart,
                });
            }
            else if (name == "br" && _paragraph is not null)
            {
                Append(" ");
            }
            return true;
        }

        private void Characters(string text)
        {
            if (_metadataDepth > 0)
            {
                if (_entry is not { } current) return;
                if (_entrySpans.Contains(true)) current.Value.Background += text;
                else current.Value.Main += text;
                return;
            }
            Append(text);
        }

        private void Append(string text)
        {
            if (_paragraph is not { } p) return;
            var last = _spans.Count > 0 ? _spans[^1] : null;
            if (last?.Reading is { } reading)
            {
                if (last.Entry is { } entry && p.Readings.TryGetValue(reading, out var byPart) && byPart.TryGetValue(last.Part, out var entries))
                {
                    entries[entry].Text.Append(text);
                }
                return;
            }
            (last?.Part == Part.Background ? p.Background : p.Main).Text.Append(text);
            foreach (var span in _spans)
            {
                if (span.Reading is null) span.Text.Append(text);
            }
        }

        private void EndElement(string elementName)
        {
            try
            {
                var name = LocalName(elementName);
                if (_metadataDepth > 0)
                {
                    EndMetadata(name);
                    return;
                }
                if (name == "span" && _spans.Count > 0 && _paragraph is { } p)
                {
                    var span = _spans[^1];
                    _spans.RemoveAt(_spans.Count - 1);
                    // Background vocals are often timed a little outside their paragraph: one starting early begins
                    // with the line, and an echo after it extends the line, though never past the end of the track.
                    var background = span.Part == Part.Background;
                    var limit = background ? (duration > 0 ? Math.Min(p.End + 8, Math.Max(p.End, duration)) : p.End + 8) : p.End;
                    if (span.Reading is not null || span.TimedChild || span.Start is not { } start || span.End is not { } end) return;
                    if (!(end > Math.Max(start, p.Start)) || !(start >= p.Start - (background ? 2 : 0.02)) || !(end <= limit + 0.02)) return;
                    var raw = span.Text.ToString();
                    var label = raw.Trim();
                    if (label.Length == 0) return;
                    var token = new TimedToken(label, Math.Max(start, p.Start), background ? end : Math.Min(end, p.End),
                        span.Glued && !(raw.Length == 0 || char.IsWhiteSpace(raw[0])));
                    (span.Part == Part.Main ? p.Main : p.Background).Tokens.Add(token);
                    foreach (var open in _spans)
                    {
                        if (open.Reading is null) open.TimedChild = true;
                    }
                }
                else if (name == "span" && _spans.Count > 0)
                {
                    _spans.RemoveAt(_spans.Count - 1);
                }
                else if (name == "p" && _paragraph is { } paragraph)
                {
                    FinishParagraph(paragraph);
                    _paragraph = null;
                    _spans.Clear();
                }
            }
            finally
            {
                _depth--;
            }
        }

        // MARK: Lines

        private void FinishParagraph(Paragraph p)
        {
            var mainText = Collapsed(p.Main.Text.ToString());
            var backgroundText = Collapsed(p.Background.Text.ToString());
            var mainWords = LyricScript.Words(p.Main.Tokens);
            var backgroundWords = LyricScript.Words(p.Background.Tokens);
            // Some TTML spans cover only a subset of a paragraph. Use native timing only when the spans account for
            // all of its text.
            var timed = Covers(p.Main.Tokens, mainText) && Covers(p.Background.Tokens, backgroundText)
                && !(mainWords.Count == 0 && backgroundWords.Count == 0);
            if (backgroundText.Length > 0 && !IsBracketed(backgroundText))
            {
                backgroundText = "(" + backgroundText + ")";
                if (backgroundWords.Count > 0)
                {
                    backgroundWords[0] = backgroundWords[0] with { Text = "(" + backgroundWords[0].Text };
                    backgroundWords[^1] = backgroundWords[^1] with { Text = backgroundWords[^1].Text + ")" };
                }
            }
            // Background vocals sung entirely before the main words lead the line; otherwise they follow it, never
            // interleaved, so text order and time order agree and the main words keep their native timing.
            var leads = mainWords.Count > 0 && backgroundWords.Count > 0 && backgroundWords[^1].Start <= mainWords[0].Start;
            var text = string.Join(' ', (leads ? new[] { backgroundText, mainText } : [mainText, backgroundText]).Where(t => t.Length > 0));
            if (text.Length == 0) return;
            var end = timed ? Math.Max(p.End, backgroundWords.Count > 0 ? backgroundWords.Max(word => word.End) : p.End) : p.End;
            if (LyricCredits.IsCredit(text, _inCredits))
            {
                _inCredits = true;
                _lines.Add(new LyricLine { Start = p.Start, End = p.Start + 0.01, Text = text, IsInstrumental = true, Credit = true });
                _keys.Add(null);
                _leadingBackground.Add(false);
                return;
            }
            var line = LyricLines.MakeLine(text, p.Start, end, distribute);
            if (!line.IsInstrumental) _inCredits = false;
            if (line.IsInstrumental)
            {
                _lines.Add(line);
                _keys.Add(null);
                _leadingBackground.Add(false);
                return;
            }
            if (timed)
            {
                var words = leads ? [.. backgroundWords, .. mainWords]
                    : mainWords.Concat(Following(backgroundWords, mainWords.Count > 0 ? mainWords[^1].Start : null, end)).ToList();
                line = line with { Words = words, WordTimed = true };
            }
            line = line with { Romanization = ChooseReading(p.Readings.GetValueOrDefault(TtmlParser.Reading.Romanization), leads, line) };
            var translations = Options(p.Readings.GetValueOrDefault(TtmlParser.Reading.Translation));
            if (translations.Count > 0) _inlineTranslations.Add((_lines.Count, translations, leads));
            _lines.Add(line);
            _keys.Add(p.Key);
            _leadingBackground.Add(leads);
        }

        /// <summary>Background words overlapping the end of the main line start no earlier than the word before them.</summary>
        private static IEnumerable<LyricWord> Following(List<LyricWord> words, double? previous, double end)
        {
            var floor = previous ?? double.NegativeInfinity;
            foreach (var word in words)
            {
                var start = Math.Max(word.Start, floor);
                floor = start;
                yield return new LyricWord(word.Text, start, word.End > start ? word.End : Math.Min(end, start + 0.05));
            }
        }

        /// <summary>Inline readings can come in several languages, one span each: one is chosen as for Apple's supplied ones.</summary>
        private string? ChooseReading(Dictionary<Part, List<Inline>>? parts, bool leads, LyricLine line) =>
            Choose(Options(parts), TtmlParser.Reading.Romanization) is { } choice ? Joined(choice.Main, choice.Background, leads, line) : null;

        /// <summary>A paragraph's inline entries of one kind, one option per language.</summary>
        private static List<Supplied> Options(Dictionary<Part, List<Inline>>? parts)
        {
            if (parts is null) return [];
            var main = parts.GetValueOrDefault(Part.Main) ?? [];
            var background = parts.GetValueOrDefault(Part.Background) ?? [];
            var languages = new List<string>();
            foreach (var entry in main.Concat(background))
            {
                if (!languages.Contains(entry.Language)) languages.Add(entry.Language);
            }
            return languages.Select(language => new Supplied(language, TextOf(main, language), TextOf(background, language))).ToList();
        }

        /// <summary>The entries in <paramref name="language"/>, or else the ones that name no language.</summary>
        private static string TextOf(List<Inline> entries, string language)
        {
            var exact = entries.Where(entry => entry.Language == language).ToList();
            return string.Join(' ', (exact.Count == 0 ? entries.Where(entry => entry.Language.Length == 0) : exact).Select(entry => entry.Text.ToString()));
        }

        /// <summary>A Latin-script transliteration if there is one; a translation the listener can read. Empty entries never win.</summary>
        private Supplied? Choose(List<Supplied> options, Reading kind)
        {
            var usable = options.Where(option => Collapsed(option.Main + " " + option.Background).Length > 0).ToList();
            if (kind != TtmlParser.Reading.Translation)
            {
                return usable.FirstOrDefault(option => option.Language.ToLowerInvariant().Contains("latn", StringComparison.Ordinal)) ?? usable.FirstOrDefault();
            }
            return PreferredTranslation(usable);
        }

        private static string? Joined(string main, string background, bool leads, LyricLine line)
        {
            main = Collapsed(main);
            background = Collapsed(background);
            if (background.Length > 0 && !IsBracketed(background)) background = "(" + background + ")";
            var text = string.Join(' ', (leads ? new[] { background, main } : [main, background]).Where(t => t.Length > 0));
            if (text.Length == 0 || text.ToLowerInvariant() == line.Text.ToLowerInvariant()) return null;
            return text;
        }

        private static string Collapsed(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        private static bool Covers(List<TimedToken> tokens, string text) =>
            TextRules.WithoutWhitespace(string.Concat(tokens.Select(token => token.Text))) == TextRules.WithoutWhitespace(text);

        private static bool IsBracketed(string text) =>
            text.Length > 0 && ("(（[［".Contains(text[0]) || ")）]］".Contains(text[^1]));

        // MARK: Supplied readings (Apple's <iTunesMetadata>)

        private void StartMetadata(string name, Dictionary<string, string> attributes)
        {
            if (name == "metadata") _metadataDepth++;
            switch (name)
            {
                case "translation" or "transliteration":
                    _block = (name == "translation" ? TtmlParser.Reading.Translation : TtmlParser.Reading.Romanization, Attribute("lang", attributes) ?? "");
                    break;
                case "text":
                    if (_block is { } block && attributes.GetValueOrDefault("for") is { Length: > 0 } key)
                    {
                        _entry = (key, new Supplied(block.Language));
                        _entrySpans.Clear();
                    }
                    break;
                case "span":
                    if (_entry is not null) _entrySpans.Add(Attribute("role", attributes) == "x-bg" || _entrySpans.Contains(true));
                    break;
            }
        }

        private void EndMetadata(string name)
        {
            switch (name)
            {
                case "metadata":
                    _metadataDepth--;
                    if (_metadataDepth == 0) { _block = null; _entry = null; _entrySpans.Clear(); }
                    break;
                case "translation" or "transliteration":
                    _block = null;
                    break;
                case "text":
                    if (_entry is { } current && _block is { } kind)
                    {
                        var entries = GetOrAdd(GetOrAdd(_supplied, kind.Reading), current.Key);
                        if (entries.Count < 8) entries.Add(current.Value);
                    }
                    _entry = null;
                    _entrySpans.Clear();
                    break;
                case "span":
                    if (_entry is not null && _entrySpans.Count > 0) _entrySpans.RemoveAt(_entrySpans.Count - 1);
                    break;
            }
        }

        /// <summary>What needs the whole document: inline translations first, then Apple's supplied readings and translations where a line has none.</summary>
        public List<LyricLine> Finish()
        {
            _readableTranslations = ReadableTranslationLanguages();
            foreach (var pending in _inlineTranslations)
            {
                if (pending.Index >= _lines.Count || Choose(pending.Options, TtmlParser.Reading.Translation) is not { } choice) continue;
                _lines[pending.Index] = _lines[pending.Index] with
                {
                    Translation = Joined(choice.Main, choice.Background, pending.Leads, _lines[pending.Index]),
                };
            }
            AttachSuppliedReadings();
            var ordered = _lines.Select((line, index) => (line, index)).OrderBy(pair => pair.line.Start).ThenBy(pair => pair.index)
                .Select(pair => pair.line).ToList();
            return LyricCredits.MarkingTitleHeader(ordered);
        }

        /// <summary>
        /// The translation languages the listener reads, in the order the song's lines first offer them. Each is judged
        /// on all of its entries together, so every line agrees.
        /// </summary>
        private List<string> ReadableTranslationLanguages()
        {
            var order = new List<string>();
            var texts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var inline = new Dictionary<int, List<Supplied>>();
            foreach (var pending in _inlineTranslations) inline.TryAdd(pending.Index, pending.Options);
            var supplied = _supplied.GetValueOrDefault(TtmlParser.Reading.Translation);
            for (var index = 0; index < _lines.Count; index++)
            {
                var options = new List<Supplied>(inline.GetValueOrDefault(index) ?? []);
                if (index < _keys.Count && _keys[index] is { } key && supplied?.GetValueOrDefault(key) is { } more) options.AddRange(more);
                foreach (var option in options)
                {
                    if (!texts.ContainsKey(option.Language)) order.Add(option.Language);
                    GetOrAdd(texts, option.Language).Add(option.Main + " " + option.Background);
                }
            }
            return order.Where(language => TranslationLanguages.Readable(texts.GetValueOrDefault(language) ?? [], preferredLanguages)).ToList();
        }

        private void AttachSuppliedReadings()
        {
            if (_supplied.Count == 0) return;
            for (var index = 0; index < _lines.Count; index++)
            {
                if (_keys[index] is not { } key) continue;
                var line = _lines[index];
                if (line.Romanization is null && _supplied.GetValueOrDefault(TtmlParser.Reading.Romanization)?.GetValueOrDefault(key) is { } readings
                    && Choose(readings, TtmlParser.Reading.Romanization) is { } reading)
                {
                    line = line with { Romanization = Joined(reading.Main, reading.Background, _leadingBackground[index], line) };
                }
                if (line.Translation is null && _supplied.GetValueOrDefault(TtmlParser.Reading.Translation)?.GetValueOrDefault(key) is { } translations
                    && Choose(translations, TtmlParser.Reading.Translation) is { } translation)
                {
                    line = line with { Translation = Joined(translation.Main, translation.Background, _leadingBackground[index], line) };
                }
                _lines[index] = line;
            }
        }

        /// <summary>
        /// The translation in the listener's preferred language; else the one in the first language, decided for the
        /// whole song, written in scripts they read; else none.
        /// </summary>
        private Supplied? PreferredTranslation(List<Supplied> options)
        {
            int Rank(string language)
            {
                var tag = language.ToLowerInvariant().Replace('_', '-');
                var tagBase = tag.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                for (var index = 0; index < preferredLanguages.Count; index++)
                {
                    var wanted = preferredLanguages[index].ToLowerInvariant().Replace('_', '-');
                    if (tag == wanted || wanted.StartsWith(tag + "-", StringComparison.Ordinal) || tag.StartsWith(wanted + "-", StringComparison.Ordinal)) return index * 2;
                    if (tagBase == wanted.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()) return index * 2 + 1;
                }
                return int.MaxValue;
            }
            if (options.Count == 0) return null;
            var best = options.Select((option, index) => (option, index, rank: Rank(option.Language)))
                .OrderBy(item => item.rank).ThenBy(item => item.index).First();
            if (best.rank != int.MaxValue) return best.option;
            foreach (var language in _readableTranslations)
            {
                if (options.FirstOrDefault(option => option.Language == language) is { } option) return option;
            }
            return null;
        }

        private static TValue GetOrAdd<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key) where TKey : notnull where TValue : new()
        {
            if (!dictionary.TryGetValue(key, out var value)) dictionary[key] = value = new TValue();
            return value;
        }
    }
}
