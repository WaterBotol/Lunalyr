import Foundation

/// Bounded, non-network XML parser for exported TTML. Timed leaf spans become
/// words; line-only paragraphs retain estimated timing. DTD entities are refused.
final class TTMLLyricParser: NSObject, XMLParserDelegate {
    private struct Span { var start: Double?; var end: Double?; var text = ""; var timedChild = false }
    private var lines: [LyricsFetcher.Line] = []
    private var paragraph: (start: Double, end: Double)?
    private var text = "", words: [LyricsFetcher.Word] = [], spans: [Span] = []
    private var skipDepth = 0, depth = 0
    private let duration: Double
    private init(duration: Double) { self.duration = duration }
    static func parse(_ text: String, duration: Double) -> [LyricsFetcher.Line] {
        guard text.utf8.count <= 2_000_000, !text.localizedCaseInsensitiveContains("<!DOCTYPE"),
              !text.localizedCaseInsensitiveContains("<!ENTITY"), let data = text.data(using: .utf8) else { return [] }
        let delegate = TTMLLyricParser(duration: duration)
        let parser = XMLParser(data: data)
        parser.shouldResolveExternalEntities = false
        parser.delegate = delegate
        guard parser.parse() else { return [] }
        return delegate.lines.sorted { $0.start < $1.start }
    }
    static func seconds(_ value: String?) -> Double? {
        guard let value else { return nil }
        let text = value.trimmingCharacters(in: .whitespaces)
        var result: Double?
        if text.hasSuffix("ms") { result = Double(text.dropLast(2)).map { $0 / 1000 } }
        else if text.hasSuffix("s") { result = Double(text.dropLast()) }
        else {
            let parts = text.split(separator: ":", omittingEmptySubsequences: false)
            if (1...3).contains(parts.count), parts.allSatisfy({ Double($0) != nil }) {
                result = parts.reduce(0) { $0 * 60 + (Double($1) ?? 0) }
            }
        }
        guard let result, result.isFinite, result >= 0, result <= 864000 else { return nil }
        return result
    }
    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes: [String: String]) {
        depth += 1
        guard depth <= 64, lines.count < 15000 else { parser.abortParsing(); return }
        if skipDepth > 0 { skipDepth += 1; return }
        let name = elementName.split(separator: ":").last.map(String.init) ?? elementName
        let role = attributes.first { $0.key == "role" || $0.key.hasSuffix(":role") }?.value ?? ""
        if name == "metadata" || role == "x-translation" || role == "x-roman" || role == "x-romanization" {
            skipDepth = 1; return
        }
        if name == "p" {
            guard let start = Self.seconds(attributes["begin"]),
                  let end = Self.seconds(attributes["end"]) ?? Self.seconds(attributes["dur"]).map({ start + $0 }),
                  end > start, duration <= 0 || start <= duration + 8 else { return }
            paragraph = (start, end); text = ""; words = []; spans = []
        } else if name == "span", let p = paragraph {
            let start = Self.seconds(attributes["begin"])
            let end = Self.seconds(attributes["end"]) ?? Self.seconds(attributes["dur"]).map { (start ?? p.start) + $0 }
            spans.append(.init(start: start, end: end))
        } else if name == "br", paragraph != nil { text += " " }
    }
    func parser(_ parser: XMLParser, foundCharacters string: String) {
        guard skipDepth == 0, paragraph != nil else { return }
        text += string
        for index in spans.indices { spans[index].text += string }
    }
    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        defer { depth -= 1 }
        if skipDepth > 0 { skipDepth -= 1; return }
        let name = elementName.split(separator: ":").last.map(String.init) ?? elementName
        if name == "span", let span = spans.popLast(), let p = paragraph,
           !span.timedChild, let start = span.start, let end = span.end, end > start,
           start >= p.start - 0.02, end <= p.end + 0.02 {
            let label = span.text.trimmingCharacters(in: .whitespacesAndNewlines)
            if !label.isEmpty {
                words.append(.init(text: label, start: max(start, p.start), end: min(end, p.end)))
                for i in spans.indices { spans[i].timedChild = true }
            }
        } else if name == "p", let p = paragraph {
            let label = text.split(whereSeparator: \.isWhitespace).joined(separator: " ")
            if !label.isEmpty {
                var line = LyricsFetcher.makeLine(text: label, start: p.start, end: p.end)
                // Some TTML spans cover only a subset of a paragraph. Use
                // native timing only when the spans account for all its text.
                let covered = words.map(\.text).joined().filter { !$0.isWhitespace }
                if !words.isEmpty, covered == label.filter({ !$0.isWhitespace }) {
                    line.words = words.sorted { $0.start < $1.start }; line.wordTimed = true
                }
                lines.append(line)
            }
            paragraph = nil; spans = []; words = []; text = ""
        }
    }
}
