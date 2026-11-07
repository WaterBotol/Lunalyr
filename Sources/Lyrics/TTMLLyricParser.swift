import Foundation

/// Bounded, non-network XML parser for exported TTML. Timed leaf spans become
/// words; line-only paragraphs retain estimated timing. DTD entities are refused.
///
/// Apple-style syllable spans written without whitespace between them form one
/// word in spaced scripts ("Ev" "'ry" → "Ev'ry") and stay separate timed tokens
/// in CJK; a line's text keeps the source spacing exactly. Background vocals
/// (`ttm:role="x-bg"`) become a parenthesised segment beside the main words so
/// the main words keep their timing. Supplied readings and translations, inline
/// (`x-roman`, `x-translation`) or in Apple's `<iTunesMetadata>` keyed by
/// `itunes:key`, attach to their line instead of being shown as lyrics.
final class TTMLLyricParser: NSObject, XMLParserDelegate {
    private enum Part { case main, background }
    private enum Reading { case romanization, translation }
    private struct Segment {
        var text = ""
        var tokens: [LyricScript.TimedToken] = []
    }
    private struct OpenSpan {
        var start: Double?, end: Double?
        var text = ""
        var timedChild = false
        var glued = false
        var part = Part.main
        var reading: Reading?
        /// The inline reading this span adds to.
        var entry: Int?
        var opensBackground = false
    }
    /// One inline `x-translation` or `x-roman` span's text.
    private struct Inline {
        var language: String
        var text = ""
    }
    private struct Paragraph {
        var start: Double, end: Double
        var key: String?
        var main = Segment(), background = Segment()
        var readings: [Reading: [Part: [Inline]]] = [:]
    }
    /// A `<text for="…">` entry of a translation or transliteration.
    private struct Supplied {
        var language: String
        var main = ""
        var background = ""
    }
    private var lines: [LyricsFetcher.Line] = []
    /// No sung line has come yet, or the line before was a credit.
    private var inCredits = true
    private var keys: [String?] = []
    private var leadingBackground: [Bool] = []
    private var paragraph: Paragraph?
    private var spans: [OpenSpan] = []
    private var depth = 0
    private var metadataDepth = 0
    private var block: (reading: Reading, language: String)?
    private var entry: (key: String, value: Supplied)?
    private var entrySpans: [Bool] = []
    private var supplied: [Reading: [String: [Supplied]]] = [:]
    /// Each line's inline translations, kept until the whole document is
    /// read: which language a listener can read is decided once per song.
    private var inlineTranslations: [(index: Int, options: [Supplied], leads: Bool)] = []
    /// The translation languages the listener reads, in the order the
    /// song's lines first offer them; see `readableTranslationLanguages`.
    private var readableTranslations: [String] = []
    private let duration: Double
    private let preferredLanguages: [String]
    private init(duration: Double, preferredLanguages: [String]) {
        self.duration = duration
        self.preferredLanguages = preferredLanguages
    }
    static func parse(_ text: String, duration: Double,
                      preferredLanguages: [String] = Locale.preferredLanguages) -> [LyricsFetcher.Line] {
        guard text.utf8.count <= 2_000_000, !text.localizedCaseInsensitiveContains("<!DOCTYPE"),
              !text.localizedCaseInsensitiveContains("<!ENTITY"), let data = text.data(using: .utf8) else { return [] }
        let delegate = TTMLLyricParser(duration: duration, preferredLanguages: preferredLanguages)
        let parser = XMLParser(data: data)
        parser.shouldResolveExternalEntities = false
        parser.delegate = delegate
        guard parser.parse() else { return [] }
        delegate.attachReadings()
        return LyricCredits.markingTitleHeader(
            delegate.lines.enumerated().sorted { ($0.element.start, $0.offset) < ($1.element.start, $1.offset) }.map(\.element))
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
    private static func localName(_ name: String) -> String {
        name.split(separator: ":").last.map(String.init) ?? name
    }
    /// `name` with any namespace prefix (`ttm:role`, `itunes:key`, `xml:lang`).
    private static func attribute(_ name: String, in attributes: [String: String]) -> String? {
        attributes[name] ?? attributes.keys.sorted().first { $0.hasSuffix(":" + name) }.flatMap { attributes[$0] }
    }
    private static func reading(forRole role: String) -> Reading? {
        switch role {
        case "x-translation": return .translation
        case "x-roman", "x-romanization", "x-romanisation", "x-transliteration", "x-pronunciation": return .romanization
        default: return nil
        }
    }

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes: [String: String]) {
        depth += 1
        guard depth <= 64, lines.count < 15000 else { parser.abortParsing(); return }
        let name = Self.localName(elementName)
        if metadataDepth > 0 || name == "metadata" {
            startMetadata(name, attributes: attributes)
            return
        }
        let role = Self.attribute("role", in: attributes) ?? ""
        if name == "p" {
            guard let start = Self.seconds(attributes["begin"]),
                  let end = Self.seconds(attributes["end"]) ?? Self.seconds(attributes["dur"]).map({ start + $0 }),
                  end > start, duration <= 0 || start <= duration + 8 else { return }
            paragraph = Paragraph(start: start, end: end, key: attributes["itunes:key"] ?? Self.attribute("key", in: attributes))
            spans = []
        } else if name == "span", let p = paragraph {
            let inherited = spans.last
            let part = inherited?.part ?? .main
            if let reading = inherited?.reading ?? Self.reading(forRole: role) {
                // Each reading span is one entry in its own language; spans
                // inside it add to that entry.
                var entry = inherited?.entry
                if inherited?.reading == nil {
                    entry = p.readings[reading]?[part]?.count ?? 0
                    paragraph?.readings[reading, default: [:]][part, default: []]
                        .append(Inline(language: Self.attribute("lang", in: attributes) ?? ""))
                }
                spans.append(OpenSpan(part: part, reading: reading, entry: entry))
                return
            }
            let opensBackground = role == "x-bg" && part == .main
            let spanPart = opensBackground ? .background : part
            // Separate background parts never run together.
            if opensBackground, !p.background.text.isEmpty { paragraph?.background.text += " " }
            let text = spanPart == .main ? p.main.text : (paragraph?.background.text ?? "")
            let start = Self.seconds(attributes["begin"])
            let end = Self.seconds(attributes["end"]) ?? Self.seconds(attributes["dur"]).map { (start ?? p.start) + $0 }
            spans.append(OpenSpan(start: start, end: end, glued: text.last.map { !$0.isWhitespace } ?? false,
                                  part: spanPart, opensBackground: opensBackground))
        } else if name == "br", paragraph != nil {
            append(" ")
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        if metadataDepth > 0 {
            guard var current = entry else { return }
            if entrySpans.contains(true) { current.value.background += string } else { current.value.main += string }
            entry = current
            return
        }
        append(string)
    }

    func parser(_ parser: XMLParser, foundCDATA CDATABlock: Data) {
        if let string = String(data: CDATABlock, encoding: .utf8) { self.parser(parser, foundCharacters: string) }
    }

    private func append(_ string: String) {
        guard paragraph != nil else { return }
        if let span = spans.last, let reading = span.reading {
            if let entry = span.entry { paragraph?.readings[reading]?[span.part]?[entry].text += string }
            return
        }
        if spans.last?.part == .background { paragraph?.background.text += string } else { paragraph?.main.text += string }
        for index in spans.indices where spans[index].reading == nil { spans[index].text += string }
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        defer { depth -= 1 }
        let name = Self.localName(elementName)
        if metadataDepth > 0 {
            endMetadata(name)
            return
        }
        if name == "span", let span = spans.popLast(), let p = paragraph {
            // Background vocals are often timed a little outside their
            // paragraph: one starting early begins with the line, and an echo
            // after it extends the line, though never past the end of the track.
            let background = span.part == .background
            let limit = background ? (duration > 0 ? min(p.end + 8, max(p.end, duration)) : p.end + 8) : p.end
            guard span.reading == nil, !span.timedChild, let start = span.start, let end = span.end,
                  end > max(start, p.start), start >= p.start - (background ? 2 : 0.02), end <= limit + 0.02 else { return }
            let label = span.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !label.isEmpty else { return }
            let token = LyricScript.TimedToken(text: label, start: max(start, p.start), end: background ? end : min(end, p.end),
                                               glued: span.glued && !(span.text.first?.isWhitespace ?? true))
            if span.part == .main { paragraph?.main.tokens.append(token) } else { paragraph?.background.tokens.append(token) }
            for index in spans.indices where spans[index].reading == nil { spans[index].timedChild = true }
        } else if name == "p", let p = paragraph {
            finish(p)
            paragraph = nil; spans = []
        }
    }

    // MARK: - Lines

    private func finish(_ p: Paragraph) {
        let mainText = Self.collapsed(p.main.text)
        var backgroundText = Self.collapsed(p.background.text)
        let mainWords = LyricScript.words(from: p.main.tokens)
        var backgroundWords = LyricScript.words(from: p.background.tokens)
        // Some TTML spans cover only a subset of a paragraph. Use native
        // timing only when the spans account for all of its text.
        let timed = Self.covers(p.main.tokens, mainText) && Self.covers(p.background.tokens, backgroundText)
            && !(mainWords.isEmpty && backgroundWords.isEmpty)
        if !backgroundText.isEmpty, !Self.isBracketed(backgroundText) {
            backgroundText = "(" + backgroundText + ")"
            if !backgroundWords.isEmpty {
                backgroundWords[0].text = "(" + backgroundWords[0].text
                backgroundWords[backgroundWords.count - 1].text += ")"
            }
        }
        // Background vocals sung entirely before the main words lead the line;
        // otherwise they follow it, never interleaved, so text order and time
        // order agree and the main words keep their native timing.
        let leads = !mainWords.isEmpty && !backgroundWords.isEmpty
            && (backgroundWords.last?.start ?? 0) <= (mainWords.first?.start ?? 0)
        let text = (leads ? [backgroundText, mainText] : [mainText, backgroundText]).filter { !$0.isEmpty }.joined(separator: " ")
        guard !text.isEmpty else { return }
        let end = timed ? max(p.end, backgroundWords.map(\.end).max() ?? p.end) : p.end
        if LyricCredits.isCredit(text, inCredits: inCredits) {
            inCredits = true
            lines.append(LyricsFetcher.Line(start: p.start, end: p.start + 0.01, text: text, words: [], isInstrumental: true, credit: true))
            keys.append(nil); leadingBackground.append(false)
            return
        }
        var line = LyricsFetcher.makeLine(text: text, start: p.start, end: end)
        if !line.isInstrumental { inCredits = false }
        guard !line.isInstrumental else { lines.append(line); keys.append(nil); leadingBackground.append(false); return }
        if timed {
            line.words = leads ? backgroundWords + mainWords
                : mainWords + Self.following(backgroundWords, after: mainWords.last?.start, end: end)
            line.wordTimed = true
        }
        line.romanization = reading(p.readings[.romanization], kind: .romanization, leads: leads, line: line)
        let translations = Self.options(p.readings[.translation])
        if !translations.isEmpty { inlineTranslations.append((lines.count, translations, leads)) }
        lines.append(line)
        keys.append(p.key)
        leadingBackground.append(leads)
    }

    /// Background words overlapping the end of the main line start no earlier
    /// than the word before them, which keeps word timing valid.
    private static func following(_ words: [LyricsFetcher.Word], after previous: Double?, end: Double) -> [LyricsFetcher.Word] {
        var floor = previous ?? -.infinity
        return words.map { word in
            let start = max(word.start, floor)
            floor = start
            return .init(text: word.text, start: start, end: word.end > start ? word.end : min(end, start + 0.05))
        }
    }

    /// Inline readings can come in several languages, one span each: one is
    /// chosen as for Apple's supplied translations and transliterations, and
    /// a background entry goes with the main entry in its language.
    private func reading(_ parts: [Part: [Inline]]?, kind: Reading, leads: Bool, line: LyricsFetcher.Line) -> String? {
        guard let choice = choose(Self.options(parts), kind) else { return nil }
        return Self.joined(main: choice.main, background: choice.background, leads: leads, line: line)
    }

    /// A paragraph's inline entries of one kind, one option per language.
    private static func options(_ parts: [Part: [Inline]]?) -> [Supplied] {
        guard let parts else { return [] }
        let main = parts[.main] ?? [], background = parts[.background] ?? []
        var languages: [String] = []
        for entry in main + background where !languages.contains(entry.language) { languages.append(entry.language) }
        return languages.map { language in
            Supplied(language: language, main: text(of: main, in: language), background: text(of: background, in: language))
        }
    }

    /// The entries in `language`, or else the ones that name no language.
    private static func text(of entries: [Inline], in language: String) -> String {
        let exact = entries.filter { $0.language == language }
        return (exact.isEmpty ? entries.filter { $0.language.isEmpty } : exact).map(\.text).joined(separator: " ")
    }

    /// A Latin-script transliteration if there is one; a translation in the
    /// listener's preferred language, else the first they can read. Empty
    /// entries never win.
    private func choose(_ options: [Supplied], _ kind: Reading) -> Supplied? {
        let usable = options.filter { !Self.collapsed($0.main + " " + $0.background).isEmpty }
        guard kind == .translation else { return usable.first { $0.language.lowercased().contains("latn") } ?? usable.first }
        return preferredTranslation(usable)
    }

    private static func joined(main: String, background: String, leads: Bool, line: LyricsFetcher.Line) -> String? {
        let main = collapsed(main)
        var background = collapsed(background)
        if !background.isEmpty, !isBracketed(background) { background = "(" + background + ")" }
        let text = (leads ? [background, main] : [main, background]).filter { !$0.isEmpty }.joined(separator: " ")
        guard !text.isEmpty, text.lowercased() != line.text.lowercased() else { return nil }
        return text
    }

    private static func collapsed(_ text: String) -> String {
        text.split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }

    private static func covers(_ tokens: [LyricScript.TimedToken], _ text: String) -> Bool {
        tokens.map(\.text).joined().filter { !$0.isWhitespace } == text.filter { !$0.isWhitespace }
    }

    private static func isBracketed(_ text: String) -> Bool {
        guard let first = text.first, let last = text.last else { return false }
        return "(（[［".contains(first) || ")）]］".contains(last)
    }

    // MARK: - Supplied readings (Apple's <iTunesMetadata>)

    private func startMetadata(_ name: String, attributes: [String: String]) {
        if name == "metadata" { metadataDepth += 1 }
        switch name {
        case "translation", "transliteration":
            block = (name == "translation" ? .translation : .romanization, Self.attribute("lang", in: attributes) ?? "")
        case "text":
            if let block, let key = attributes["for"], !key.isEmpty {
                entry = (key, Supplied(language: block.language)); entrySpans = []
            }
        case "span":
            if entry != nil { entrySpans.append(Self.attribute("role", in: attributes) == "x-bg" || entrySpans.contains(true)) }
        default: break
        }
    }

    private func endMetadata(_ name: String) {
        switch name {
        case "metadata":
            metadataDepth -= 1
            if metadataDepth == 0 { block = nil; entry = nil; entrySpans = [] }
        case "translation", "transliteration": block = nil
        case "text":
            if let current = entry, let kind = block, supplied[kind.reading, default: [:]][current.key, default: []].count < 8 {
                supplied[kind.reading, default: [:]][current.key, default: []].append(current.value)
            }
            entry = nil; entrySpans = []
        case "span": if entry != nil, !entrySpans.isEmpty { entrySpans.removeLast() }
        default: break
        }
    }

    /// What needs the whole document: inline translations first, then
    /// Apple's supplied readings and translations where a line has none.
    private func attachReadings() {
        readableTranslations = readableTranslationLanguages()
        for pending in inlineTranslations where lines.indices.contains(pending.index) {
            guard let choice = choose(pending.options, .translation) else { continue }
            lines[pending.index].translation = Self.joined(main: choice.main, background: choice.background,
                                                           leads: pending.leads, line: lines[pending.index])
        }
        attachSuppliedReadings()
    }

    /// The translation languages the listener reads, in the order the
    /// song's lines first offer them. Each is judged on all of its entries
    /// together, so every line agrees: a Chinese translation stays unread
    /// for an English listener even on the lines where it happens to be a
    /// name in Latin letters, rather than appearing under those alone.
    private func readableTranslationLanguages() -> [String] {
        var order: [String] = []
        var texts: [String: [String]] = [:]
        let inline = Dictionary(inlineTranslations.map { ($0.index, $0.options) }, uniquingKeysWith: { first, _ in first })
        for index in lines.indices {
            var options = inline[index] ?? []
            if keys.indices.contains(index), let key = keys[index] { options += supplied[.translation]?[key] ?? [] }
            for option in options {
                if texts[option.language] == nil { order.append(option.language) }
                texts[option.language, default: []].append(option.main + " " + option.background)
            }
        }
        return order.filter { TranslationLanguages.readable(texts[$0] ?? [], languages: preferredLanguages) }
    }

    private func attachSuppliedReadings() {
        guard !supplied.isEmpty else { return }
        for index in lines.indices {
            guard let key = keys[index] else { continue }
            if lines[index].romanization == nil, let options = supplied[.romanization]?[key],
               let choice = choose(options, .romanization) {
                lines[index].romanization = Self.joined(main: choice.main, background: choice.background,
                                                        leads: leadingBackground[index], line: lines[index])
            }
            if lines[index].translation == nil, let options = supplied[.translation]?[key],
               let choice = choose(options, .translation) {
                lines[index].translation = Self.joined(main: choice.main, background: choice.background,
                                                       leads: leadingBackground[index], line: lines[index])
            }
        }
    }

    /// The translation in the listener's preferred language; else the one in
    /// the first language, decided for the whole song, written in scripts
    /// they read (an English translation for a French listener); else none,
    /// rather than a translation they could not read under every line.
    private func preferredTranslation(_ options: [Supplied]) -> Supplied? {
        func rank(_ language: String) -> Int {
            let tag = language.lowercased().replacingOccurrences(of: "_", with: "-")
            for (index, preferred) in preferredLanguages.enumerated() {
                let wanted = preferred.lowercased().replacingOccurrences(of: "_", with: "-")
                if tag == wanted || wanted.hasPrefix(tag + "-") || tag.hasPrefix(wanted + "-") { return index * 2 }
                if tag.split(separator: "-").first == wanted.split(separator: "-").first { return index * 2 + 1 }
            }
            return Int.max
        }
        guard let best = options.enumerated()
            .min(by: { (rank($0.element.language), $0.offset) < (rank($1.element.language), $1.offset) })?.element
        else { return nil }
        if rank(best.language) != Int.max { return best }
        for language in readableTranslations {
            if let option = options.first(where: { $0.language == language }) { return option }
        }
        return nil
    }
}
