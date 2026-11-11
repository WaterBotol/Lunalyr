// A small, strict YAML reader for the subset LRCLIB's Lyricsfile uses, written for this port (the Mac decodes the
// file with the Yams package). It reads block mappings and sequences (sequences may sit at their key's indentation,
// as LRCLIB writes them), compact "- key: value" items, plain, single- and double-quoted scalars (also over several
// lines), literal and folded block scalars, comments, a leading "---", and single-line flow collections ("[]", "{}",
// "[a, b]"). Anchors, aliases, tags, directives, complex keys and duplicate keys are refused: the reader returns null
// and the caller treats the file as unreadable, as a failed decode does on the Mac. No reflection, no allocation
// beyond the node tree, so it is AOT-safe.

using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Lyrics.Parsing;

internal abstract record YamlNode;

internal sealed record YamlScalar(string Value, bool Plain) : YamlNode
{
    /// <summary>A plain "~", "null" or empty scalar.</summary>
    public bool IsNull => Plain && Value is "" or "~" or "null" or "Null" or "NULL";
}

internal sealed record YamlSequence(IReadOnlyList<YamlNode> Items) : YamlNode;

internal sealed record YamlMapping(IReadOnlyDictionary<string, YamlNode> Entries) : YamlNode
{
    public YamlNode? this[string key] => Entries.GetValueOrDefault(key);
}

internal sealed class MiniYaml
{
    private sealed class YamlException : Exception;

    private sealed record Line(int Indent, string Content, string Raw);

    private readonly List<Line> _lines = [];
    private int _index;

    private MiniYaml(string text)
    {
        foreach (var piece in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var raw = piece.TrimEnd('\r');
            var indent = 0;
            while (indent < raw.Length && raw[indent] == ' ') indent++;
            _lines.Add(new Line(indent, raw[indent..], raw));
        }
    }

    /// <summary>The document's root node, or null when it is not in the supported subset.</summary>
    public static YamlNode? Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var reader = new MiniYaml(text);
        try
        {
            return reader.ParseDocument();
        }
        catch (YamlException)
        {
            return null;
        }
    }

    private static YamlException Fail() => new();

    private YamlNode ParseDocument()
    {
        SkipBlank();
        if (_index < _lines.Count)
        {
            var first = _lines[_index];
            if (first.Content.StartsWith('%')) throw Fail();
            if (first.Indent == 0 && (first.Content == "---" || first.Content.StartsWith("--- ", StringComparison.Ordinal)))
            {
                var rest = first.Content[3..].TrimStart();
                if (rest.Length > 0 && rest[0] != '#') throw Fail();
                _index++;
            }
        }
        var root = ParseNode(0) ?? new YamlScalar("", true);
        SkipBlank();
        if (_index < _lines.Count && !(_lines[_index].Indent == 0 && _lines[_index].Content.StartsWith("...", StringComparison.Ordinal))) throw Fail();
        return root;
    }

    private void SkipBlank()
    {
        while (_index < _lines.Count && IsBlank(_lines[_index])) _index++;
    }

    private static bool IsBlank(Line line)
    {
        var content = line.Content.TrimStart(' ', '\t');
        return content.Length == 0 || content[0] == '#';
    }

    private static bool IsDash(string content) => content == "-" || content.StartsWith("- ", StringComparison.Ordinal) || content.StartsWith("-\t", StringComparison.Ordinal);

    /// <summary>The node starting at the next significant line, which must be indented at least <paramref name="minIndent"/>.</summary>
    private YamlNode? ParseNode(int minIndent)
    {
        SkipBlank();
        if (_index >= _lines.Count) return null;
        var line = _lines[_index];
        if (line.Indent < minIndent) return null;
        if (line.Content.StartsWith('\t')) throw Fail();
        if (IsDash(line.Content)) return ParseSequence(line.Indent);
        if (KeyEnd(line.Content) >= 0) return ParseMapping(line.Indent);
        _index++;
        return ParseInlineValue(line.Content, line.Indent - 1);
    }

    private YamlSequence ParseSequence(int indent)
    {
        var items = new List<YamlNode>();
        while (true)
        {
            SkipBlank();
            if (_index >= _lines.Count) break;
            var line = _lines[_index];
            if (line.Indent < indent || (line.Indent == indent && !IsDash(line.Content))) break;
            if (line.Indent > indent) throw Fail();
            var rest = line.Content[1..];
            var gap = 0;
            while (gap < rest.Length && rest[gap] is ' ' or '\t') gap++;
            rest = rest[gap..];
            if (rest.Length == 0 || rest[0] == '#')
            {
                _index++;
                items.Add(ParseNode(indent + 1) ?? new YamlScalar("", true));
                continue;
            }
            // A compact item ("- text: …", "- - a"): read the rest of the line as a node at its own column.
            _lines[_index] = new Line(indent + 1 + gap, rest, line.Raw);
            items.Add(ParseNode(indent + 1) ?? throw Fail());
        }
        return new YamlSequence(items);
    }

    private YamlMapping ParseMapping(int indent)
    {
        var entries = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        while (true)
        {
            SkipBlank();
            if (_index >= _lines.Count) break;
            var line = _lines[_index];
            if (line.Indent < indent) break;
            if (line.Indent > indent || line.Content.StartsWith('\t')) throw Fail();
            if (IsDash(line.Content)) break;
            var keyEnd = KeyEnd(line.Content);
            if (keyEnd < 0) throw Fail();
            var key = ReadKey(line.Content[..keyEnd]);
            if (!entries.TryAdd(key, YamlScalarNull)) throw Fail();
            var value = line.Content[(keyEnd + 1)..].TrimStart(' ', '\t');
            _index++;
            YamlNode node;
            if (value.Length == 0 || value[0] == '#')
            {
                SkipBlank();
                if (_index < _lines.Count && _lines[_index].Indent > indent) node = ParseNode(indent + 1) ?? YamlScalarNull;
                else if (_index < _lines.Count && _lines[_index].Indent == indent && IsDash(_lines[_index].Content)) node = ParseSequence(indent);
                else node = YamlScalarNull;
            }
            else
            {
                node = ParseInlineValue(value, indent);
            }
            entries[key] = node;
        }
        return new YamlMapping(entries);
    }

    private static readonly YamlScalar YamlScalarNull = new("", true);

    /// <summary>Where a mapping key ends (the index of its ':'), or -1 when the content is not "key: value".</summary>
    private static int KeyEnd(string content)
    {
        if (content.Length == 0 || content[0] is '#' or '[' or '{' or '|' or '>' or '&' or '*' or '!') return -1;
        if (content[0] == '?' && (content.Length == 1 || content[1] == ' ')) throw Fail();
        if (content[0] is '"' or '\'')
        {
            var close = QuoteEnd(content, 0);
            if (close < 0) return -1;
            var after = close + 1;
            while (after < content.Length && content[after] == ' ') after++;
            return after < content.Length && content[after] == ':' && (after + 1 == content.Length || content[after + 1] is ' ' or '\t') ? after : -1;
        }
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == ':' && (i + 1 == content.Length || content[i + 1] is ' ' or '\t')) return i;
            if (content[i] == '#' && i > 0 && content[i - 1] is ' ' or '\t') return -1;
        }
        return -1;
    }

    private static string ReadKey(string text)
    {
        text = text.TrimEnd(' ', '\t');
        if (text.Length > 0 && text[0] is '"' or '\'')
        {
            var close = QuoteEnd(text, 0);
            if (close != text.Length - 1) throw Fail();
            return text[0] == '"' ? Unescape(text[1..close]) : text[1..close].Replace("''", "'", StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>The index of the quote closing the one at <paramref name="open"/> on this text, or -1.</summary>
    private static int QuoteEnd(string text, int open)
    {
        var quote = text[open];
        for (var i = open + 1; i < text.Length; i++)
        {
            if (quote == '\'' && text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'') { i++; continue; }
                return i;
            }
            if (quote == '"')
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') return i;
            }
        }
        return -1;
    }

    /// <summary>A value written after "key:" or "- ", with continuation lines indented more than <paramref name="parentIndent"/>.</summary>
    private YamlNode ParseInlineValue(string value, int parentIndent)
    {
        switch (value[0])
        {
            case '&' or '*' or '!' or '%' or '@' or '`' or '?':
                throw Fail();
            case '|' or '>':
                return ParseBlockScalar(value, parentIndent);
            case '[' or '{':
                return ParseFlowLine(value);
            case '"' or '\'':
                return ParseQuoted(value, parentIndent);
            case '-' when value.Length > 1 && value[1] is ' ' or '\t':
                throw Fail();
            default:
                return ParsePlain(value, parentIndent);
        }
    }

    private YamlScalar ParsePlain(string value, int parentIndent)
    {
        var text = StripComment(value, out var commented);
        if (text.Contains(": ", StringComparison.Ordinal) || text.EndsWith(':')) throw Fail();
        var builder = new StringBuilder(text);
        // Continuation lines fold into one value, blank lines into line breaks.
        while (!commented)
        {
            var blanks = 0;
            var probe = _index;
            while (probe < _lines.Count && _lines[probe].Content.Trim().Length == 0) { blanks++; probe++; }
            if (probe >= _lines.Count || _lines[probe].Indent <= parentIndent) break;
            var next = _lines[probe].Content;
            if (next[0] == '#' || IsDash(next) || KeyEnd(next) >= 0) break;
            _index = probe + 1;
            var piece = StripComment(next, out commented);
            if (piece.Contains(": ", StringComparison.Ordinal)) throw Fail();
            builder.Append(blanks > 0 ? new string('\n', blanks) : " ").Append(piece);
        }
        return new YamlScalar(builder.ToString(), true);
    }

    private static string StripComment(string text, out bool commented)
    {
        commented = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '#' && (i == 0 || text[i - 1] is ' ' or '\t'))
            {
                commented = true;
                return text[..i].TrimEnd(' ', '\t');
            }
        }
        return text.TrimEnd(' ', '\t');
    }

    private YamlScalar ParseQuoted(string value, int parentIndent)
    {
        var text = value;
        var close = QuoteEnd(text, 0);
        while (close < 0)
        {
            if (_index >= _lines.Count || (_lines[_index].Indent <= parentIndent && _lines[_index].Content.Length > 0)) throw Fail();
            text += "\n" + _lines[_index].Raw;
            _index++;
            close = QuoteEnd(text, 0);
        }
        var after = text[(close + 1)..].TrimStart(' ', '\t');
        if (after.Length > 0 && after[0] != '#') throw Fail();
        var body = Fold(text[1..close], text[0] == '"');
        return new YamlScalar(text[0] == '"' ? Unescape(body) : body.Replace("''", "'", StringComparison.Ordinal), false);
    }

    /// <summary>Line breaks inside a quoted scalar: a single break becomes a space, each blank line a line break.</summary>
    private static string Fold(string body, bool doubleQuoted)
    {
        if (!body.Contains('\n')) return body;
        var lines = body.Split('\n');
        var builder = new StringBuilder(lines[0].TrimEnd(' ', '\t'));
        var blanks = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var piece = i == lines.Length - 1 ? lines[i].TrimStart(' ', '\t') : lines[i].Trim(' ', '\t');
            if (piece.Length == 0 && i < lines.Length - 1) { blanks++; continue; }
            var escapedBreak = doubleQuoted && builder.Length > 0 && builder[^1] == '\\' && !EndsWithEscapedBackslash(builder);
            if (escapedBreak) builder.Length--;
            else builder.Append(blanks > 0 ? new string('\n', blanks) : " ");
            blanks = 0;
            builder.Append(piece);
        }
        return builder.ToString();
    }

    private static bool EndsWithEscapedBackslash(StringBuilder builder)
    {
        var count = 0;
        for (var i = builder.Length - 1; i >= 0 && builder[i] == '\\'; i--) count++;
        return count % 2 == 0;
    }

    private static string Unescape(string body)
    {
        if (!body.Contains('\\')) return body;
        var builder = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\') { builder.Append(body[i]); continue; }
            if (++i >= body.Length) throw Fail();
            switch (body[i])
            {
                case '0': builder.Append('\0'); break;
                case 'a': builder.Append('\a'); break;
                case 'b': builder.Append('\b'); break;
                case 't' or '\t': builder.Append('\t'); break;
                case 'n': builder.Append('\n'); break;
                case 'v': builder.Append('\v'); break;
                case 'f': builder.Append('\f'); break;
                case 'r': builder.Append('\r'); break;
                case 'e': builder.Append('\u001B'); break;
                case ' ': builder.Append(' '); break;
                case '"': builder.Append('"'); break;
                case '/': builder.Append('/'); break;
                case '\\': builder.Append('\\'); break;
                case 'N': builder.Append('\u0085'); break;
                case '_': builder.Append((char)0xA0); break;
                case 'L': builder.Append((char)0x2028); break;
                case 'P': builder.Append((char)0x2029); break;
                case 'x': builder.Append(Hex(body, ref i, 2)); break;
                case 'u': builder.Append(Hex(body, ref i, 4)); break;
                case 'U': builder.Append(Hex(body, ref i, 8)); break;
                default: throw Fail();
            }
        }
        return builder.ToString();
    }

    private static string Hex(string body, ref int i, int digits)
    {
        if (i + digits >= body.Length) throw Fail();
        if (!int.TryParse(body.AsSpan(i + 1, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            || value > 0x10FFFF || value is >= 0xD800 and <= 0xDFFF) throw Fail();
        i += digits;
        return char.ConvertFromUtf32(value);
    }

    private YamlScalar ParseBlockScalar(string header, int parentIndent)
    {
        var literal = header[0] == '|';
        var chomp = ' ';
        var explicitIndent = 0;
        var rest = StripComment(header[1..], out _);
        foreach (var c in rest)
        {
            if (c is '+' or '-' && chomp == ' ') chomp = c;
            else if (c is >= '1' and <= '9' && explicitIndent == 0) explicitIndent = c - '0';
            else throw Fail();
        }
        var indent = explicitIndent > 0 ? Math.Max(parentIndent, 0) + explicitIndent : -1;
        var collected = new List<string>();
        while (_index < _lines.Count)
        {
            var line = _lines[_index];
            if (line.Content.Trim().Length == 0)
            {
                collected.Add("");
                _index++;
                continue;
            }
            if (indent < 0)
            {
                if (line.Indent <= parentIndent) break;
                indent = line.Indent;
            }
            if (line.Indent < indent) break;
            collected.Add(line.Raw[indent..]);
            _index++;
        }
        // Trailing blank lines belong to the chomping, not the text.
        var trailing = 0;
        while (collected.Count > 0 && collected[^1].Length == 0) { collected.RemoveAt(collected.Count - 1); trailing++; }
        string body;
        if (literal) body = string.Join('\n', collected);
        else
        {
            var builder = new StringBuilder();
            for (var i = 0; i < collected.Count; i++)
            {
                if (i > 0)
                {
                    var previous = collected[i - 1];
                    var moreIndented = previous.StartsWith(' ') || collected[i].StartsWith(' ');
                    builder.Append(collected[i].Length == 0 || previous.Length == 0 || moreIndented ? "\n" : " ");
                }
                builder.Append(collected[i]);
            }
            body = builder.ToString();
        }
        if (collected.Count == 0) return new YamlScalar(chomp == '+' ? new string('\n', trailing) : "", false);
        return new YamlScalar(chomp switch
        {
            '-' => body,
            '+' => body + "\n" + new string('\n', trailing),
            _ => body + "\n",
        }, false);
    }

    /// <summary>A flow collection that opens and closes on its line: "[]", "{}", "[1, 2]", "{a: 1}".</summary>
    private static YamlNode ParseFlowLine(string value)
    {
        var position = 0;
        var node = ParseFlow(value, ref position);
        var after = value[position..].TrimStart(' ', '\t');
        if (after.Length > 0 && after[0] != '#') throw Fail();
        return node;
    }

    private static YamlNode ParseFlow(string text, ref int position)
    {
        SkipSpaces(text, ref position);
        if (position >= text.Length) throw Fail();
        var open = text[position];
        if (open is '[' or '{')
        {
            position++;
            var close = open == '[' ? ']' : '}';
            var items = new List<YamlNode>();
            var entries = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
            while (true)
            {
                SkipSpaces(text, ref position);
                if (position >= text.Length) throw Fail();
                if (text[position] == close) { position++; break; }
                var item = ParseFlow(text, ref position);
                SkipSpaces(text, ref position);
                if (open == '{')
                {
                    if (item is not YamlScalar key || position >= text.Length || text[position] != ':') throw Fail();
                    position++;
                    var value = ParseFlow(text, ref position);
                    if (!entries.TryAdd(key.Value, value)) throw Fail();
                }
                else items.Add(item);
                SkipSpaces(text, ref position);
                if (position < text.Length && text[position] == ',') { position++; continue; }
                if (position < text.Length && text[position] == close) { position++; break; }
                throw Fail();
            }
            return open == '[' ? new YamlSequence(items) : new YamlMapping(entries);
        }
        if (open is '"' or '\'')
        {
            var end = QuoteEnd(text, position);
            if (end < 0) throw Fail();
            var body = text[(position + 1)..end];
            position = end + 1;
            return new YamlScalar(open == '"' ? Unescape(body) : body.Replace("''", "'", StringComparison.Ordinal), false);
        }
        if (open is '&' or '*' or '!' or '|' or '>') throw Fail();
        var start = position;
        while (position < text.Length && text[position] is not (',' or ']' or '}' or '[' or '{')
               && !(text[position] == ':' && (position + 1 >= text.Length || text[position + 1] is ' ' or ',' or ']' or '}'))) position++;
        return new YamlScalar(text[start..position].Trim(' ', '\t'), true);
    }

    private static void SkipSpaces(string text, ref int position)
    {
        while (position < text.Length && text[position] is ' ' or '\t') position++;
    }

    /// <summary>A YAML number in a plain scalar: integers, decimals with an exponent, .inf and .nan.</summary>
    public static bool TryNumber(YamlNode? node, out double value)
    {
        value = 0;
        if (node is not YamlScalar { Plain: true } scalar) return false;
        var text = scalar.Value;
        switch (text)
        {
            case ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF": value = double.PositiveInfinity; return true;
            case "-.inf" or "-.Inf" or "-.INF": value = double.NegativeInfinity; return true;
            case ".nan" or ".NaN" or ".NAN": value = double.NaN; return true;
        }
        var digits = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c)) { digits = true; continue; }
            if (c is '.' or 'e' or 'E') continue;
            if (c is '+' or '-' && (i == 0 || text[i - 1] is 'e' or 'E')) continue;
            return false;
        }
        return digits && double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out value);
    }
}
