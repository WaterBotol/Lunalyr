import XCTest
@testable import VersoCustom

/// Apple Music lists a Japanese song that also has an English recording under
/// its English title ("Racing into the Night" for 夜に駆ける), while the English
/// recording says "English Version". Lyrics for the language version, or in
/// another language than the song's, rank as another version, and a song
/// listed under an English title is searched again under its original one,
/// read from a stubbed Apple catalogue (no network).
final class LanguageVersionTests: XCTestCase {
    private let options = LyricSourceOptions.Snapshot(enabled: [.appleMusicCache, .lyricsPlus, .netEase, .lrclib, .qqMusic, .kugou],
                                                      mode: .smart, localFolder: "", localVersion: "")

    // Lyric fixtures in the shape the services return: invented lines, not any song's words.
    static let japanese = ["夜の街を駆けてゆく", "君の声が聞こえた", "静かな部屋の中で", "ふたりの影が揺れる", "明日の光を待って", "まだ眠れないまま"]
    static let english = ["Running through the city night", "I can hear your voice again", "In a quiet little room",
                          "Our shadows sway along", "Waiting for tomorrow's light", "Still awake tonight"]
    static let romaji = ["Yoru no machi wo kakete yuku", "Kimi no koe ga kikoeta", "Shizuka na heya no naka de",
                         "Futari no kage ga yureru", "Ashita no hikari wo matte", "Mada nemurenai mama"]

    private func candidate(_ source: LyricSourceID, _ text: [String], title: String, artist: String = "YOASOBI",
                           words: Bool, reported: Bool = true, duration: Double = 258) -> LyricCandidate {
        let lines = text.enumerated().map { index, row -> LyricsFetcher.Line in
            let start = Double(index) * 4 + 1
            var line = LyricsFetcher.makeLine(text: row, start: start, end: start + 3.5)
            if words {
                // A token a word, or a character in a script written without spaces (`LyricsFetcher.isPhraseTimed`).
                let tokens = LyricScript.keepsSyllables(row) ? row.filter { !$0.isWhitespace }.map(String.init)
                    : row.split(separator: " ").map(String.init)
                let step = 3 / Double(tokens.count)
                line.wordTimed = true
                line.words = tokens.enumerated().map { i, token in .init(text: token, start: start + Double(i) * step, end: start + Double(i + 1) * step) }
            }
            return line
        }
        return .init(source: source, result: .synced(lines, .init(precision: "fixture", source: source.title)),
                     title: title, artist: artist, duration: duration, score: 0, metadataReported: reported)
    }

    private func query(_ title: String, genre: String? = nil, artist: String = "YOASOBI") -> LyricQuery {
        var query = LyricQuery(title: title, artist: artist, album: nil, duration: 258)
        query.genre = genre
        return query
    }

    // MARK: - Tags

    func testLanguageVersionsAreTags() {
        XCTAssertEqual(VersionTags.tags("BABY (English Version)"), ["english"])
        XCTAssertEqual(VersionTags.tags("Idol (English ver.)"), ["english"])
        XCTAssertEqual(VersionTags.tags("Idol - Eng Ver."), ["english"])
        XCTAssertEqual(VersionTags.tags("Idol (English)"), ["english"])
        XCTAssertEqual(VersionTags.tags("アイドル (英語ver.)"), ["english"])
        XCTAssertEqual(VersionTags.tags("Our Song (Japanese Ver.)"), ["japanese"])
        XCTAssertEqual(VersionTags.tags("Our Song [Korean Version]"), ["korean"])
        XCTAssertEqual(VersionTags.tags("Our Song (日本語ver.)"), ["japanese"])
        XCTAssertEqual(VersionTags.tags("Our Song (Live / English Version)"), ["live", "english"])
        for name in ["English Girl", "Our Song (Album Version)", "Our Song (JP)", "Our Song (English Rose Remaster)"] {
            XCTAssertTrue(VersionTags.tags(name).isDisjoint(with: VersionTags.languages), name)
        }
        XCTAssertEqual(VersionTags.describe(["english"]), "English")
    }

    func testScriptOfLyrics() {
        XCTAssertEqual(LyricLanguage.script(of: Self.japanese), .native)
        XCTAssertEqual(LyricLanguage.script(of: Self.english), .latin)
        XCTAssertEqual(LyricLanguage.script(of: Self.romaji), .latin)
        // A K-pop style mix is never held against a song.
        XCTAssertEqual(LyricLanguage.script(of: ["사랑해 baby", "Oh my love", "Hold me tight", "Hold me close", "Never let go",
                                                 "Dance all night", "Feel the beat", "Run away", "널 기다려"]), .mixed)
        // An English intro does not make Japanese lyrics mixed.
        XCTAssertEqual(LyricLanguage.script(of: ["Have a nice day"] + Self.japanese), .native)
        // A one-line "pure music" notice says nothing.
        XCTAssertNil(LyricLanguage.script(of: ["纯音乐，请欣赏"]))
    }

    // MARK: - Ranking

    /// The user's "BABY": Kugou offered "BABY (English Version)", word-timed, in English.
    func testEnglishVersionRanksBelowTheJapaneseLyricsOfAnUntaggedTitle() {
        let english = candidate(.kugou, Self.english, title: "BABY (English Version)", words: true)
        let japanese = candidate(.lrclib, Self.japanese, title: "BABY", words: false)
        let screened = LyricsFetcher.screen([english, japanese], query: query("BABY"), options: options)
        XCTAssertEqual(screened.accepted.map(\.source), [.lrclib, .kugou])
        let states = screened.accepted.map { LyricsFetcher.fitsVersion($0, query("BABY")) }
        XCTAssertEqual(states, [true, false])
    }

    /// The user's "Monotone": Kugou named its English lyrics just "Monotone".
    func testEnglishTitledJapaneseTrackPicksJapaneseLyrics() {
        let english = candidate(.kugou, Self.english, title: "Monotone", words: true)
        let netEase = candidate(.netEase, Self.japanese, title: "Monotone", words: false)
        let qq = candidate(.qqMusic, Self.japanese, title: "Monotone", words: false)
        // Two sources in Japanese against one in English.
        var ranked = LyricsFetcher.ranked([english, netEase, qq], query: query("Monotone"), options: options)
        XCTAssertEqual(ranked.first?.source, .netEase)
        XCTAssertEqual(ranked.last?.source, .kugou)
        XCTAssertTrue(ranked.last?.languageMismatch == true)
        // One against one: an East Asian genre settles it.
        ranked = LyricsFetcher.ranked([english, netEase], query: query("Monotone", genre: "J-Pop"), options: options)
        XCTAssertEqual(ranked.map(\.source), [.netEase, .kugou])
        // Without it nothing is judged, and word timing leads as before.
        ranked = LyricsFetcher.ranked([english, netEase], query: query("Monotone", genre: "Pop"), options: options)
        XCTAssertEqual(ranked.map(\.source), [.kugou, .netEase])
        XCTAssertFalse(ranked.contains { $0.languageMismatch })
    }

    func testLyricsFoundUnderTheOriginalTitleSettleTheLanguage() {
        var found = candidate(.netEase, Self.japanese, title: "Racing into the Night", words: false)
        found.viaOriginalTitle = true
        let lrclib = candidate(.lrclib, Self.english, title: "Racing into the Night", words: true)
        let kugou = candidate(.kugou, Self.english, title: "Racing into the Night", words: true)
        let screened = LyricsFetcher.screen([lrclib, kugou, found], query: query("Racing into the Night"), options: options)
        XCTAssertEqual(screened.accepted.first?.source, .netEase)
        let states = screened.accepted.map { LyricsFetcher.fitsVersion($0, query("Racing into the Night")) }
        XCTAssertEqual(states, [true, false, false])
    }

    func testMusicsOwnLyricsStayFirstAndDecideTheLanguage() {
        let music = candidate(.appleMusicCache, Self.japanese, title: "Idol", words: true)
        let english = candidate(.lrclib, Self.english, title: "Idol", words: true)
        let japanese = candidate(.qqMusic, Self.japanese, title: "Idol", words: false)
        let ranked = LyricsFetcher.ranked([english, japanese, music], query: query("Idol"), options: options)
        XCTAssertEqual(ranked.map(\.source), [.appleMusicCache, .qqMusic, .lrclib])
        // Music's English lyrics for the playing track decide the other way, and stay first.
        let musicEnglish = candidate(.appleMusicCache, Self.english, title: "Idol", words: false)
        let again = LyricsFetcher.ranked([candidate(.lrclib, Self.japanese, title: "Idol", words: true), musicEnglish],
                                         query: query("Idol"), options: options)
        XCTAssertEqual(again.map(\.source), [.appleMusicCache, .lrclib])
        XCTAssertTrue(again.last?.languageMismatch == true)
        XCTAssertFalse(again.first?.languageMismatch == true)
    }

    func testEnglishVersionTitlePrefersEnglishLyrics() {
        let playing = query("Idol (English Version)")
        let english = candidate(.lrclib, Self.english, title: "Idol (English Version)", words: false)
        let japanese = candidate(.kugou, Self.japanese, title: "Idol", words: true)
        // A service that only repeats the search, with Japanese lyrics.
        let unreported = candidate(.lyricsPlus, Self.japanese, title: "Idol (English Version)", words: true, reported: false)
        let screened = LyricsFetcher.screen([japanese, unreported, english], query: playing, options: options)
        XCTAssertEqual(screened.accepted.first?.source, .lrclib)
        XCTAssertTrue(screened.accepted.first { $0.source == .lyricsPlus }?.languageMismatch == true)
        // Listed as what it is.
        let shown = LyricsFetcher.choose(screened.accepted, query: playing, options: options)
        XCTAssertEqual(shown?.source, .lrclib)
    }

    func testWesternSongsAreNotJudgedByAStrayMatch() {
        let playing = query("Our Song", artist: "Our Artist")
        let english = candidate(.lrclib, Self.english, title: "Our Song", artist: "Our Artist", words: true)
        let english2 = candidate(.kugou, Self.english, title: "Our Song", artist: "Our Artist", words: true)
        let stray = candidate(.lyricsPlus, Self.japanese, title: "Our Song", artist: "Our Artist", words: true, reported: false)
        let ranked = LyricsFetcher.ranked([stray, english, english2], query: playing, options: options)
        XCTAssertEqual(ranked.first?.source, .lrclib)
        XCTAssertFalse(ranked.contains { $0.languageMismatch })
        // A Kugou "pure music" notice is not Japanese or Chinese lyrics.
        let notice = LyricCandidate(source: .kugou, result: .synced([LyricsFetcher.makeLine(text: "纯音乐，请欣赏", start: 0, end: 5)],
                                                                    .init(precision: "", source: "")),
                                    title: "Our Song", artist: "Our Artist", duration: 258, score: 0)
        XCTAssertNil(LyricLanguage.expected(playing, candidates: [notice, english]))
    }

    func testTaggedAndOtherLanguageLyricsBothRankAsOtherVersions() {
        let english = candidate(.kugou, Self.english, title: "Monotone", words: true)
        let japanese = candidate(.netEase, Self.japanese, title: "Monotone", words: false)
        let qq = candidate(.qqMusic, Self.japanese, title: "Monotone", words: false)
        let tagged = candidate(.lrclib, Self.english, title: "Monotone (English Version)", words: true)
        let accepted = LyricsFetcher.screen([english, japanese, qq, tagged], query: query("Monotone"), options: options).accepted
        XCTAssertEqual(Set(accepted.prefix(2).map(\.source)), [.netEase, .qqMusic])
        let others = accepted.filter { !LyricsFetcher.fitsVersion($0, query("Monotone")) }.map(\.source)
        XCTAssertEqual(Set(others), [.kugou, .lrclib])
        // A lookup with only another version in hand keeps asking the other sources.
        XCTAssertTrue(LyricsFetcher.couldBeat(accepted.first { $0.source == .kugou }!, from: .qqMusic, query: query("Monotone"), options: options))
    }

    // MARK: - Original title

    /// iTunes (searches in Japan answered apart when `searchJP` is set) and
    /// MusicBrainz, whose requests are counted apart.
    private final class Catalogue: URLProtocol {
        nonisolated(unsafe) static var search = Data()
        nonisolated(unsafe) static var searchJP: Data?
        nonisolated(unsafe) static var lookup = Data()
        nonisolated(unsafe) static var brainz = Data()
        nonisolated(unsafe) static var asked: [URL] = []
        nonisolated(unsafe) static var brainzAsked = 0
        override class func canInit(with request: URLRequest) -> Bool { true }
        override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
        override func startLoading() {
            let url = request.url!
            let body: Data
            if url.host == "musicbrainz.org" {
                Catalogue.brainzAsked += 1
                body = Catalogue.brainz
            } else {
                Catalogue.asked.append(url)
                let japan = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.contains { $0.name == "country" && $0.value == "jp" } == true
                body = url.path == "/lookup" ? Catalogue.lookup : japan ? Catalogue.searchJP ?? Catalogue.search : Catalogue.search
            }
            client?.urlProtocol(self, didReceive: HTTPURLResponse(url: url, statusCode: 200, httpVersion: nil, headerFields: nil)!,
                                cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: body)
            client?.urlProtocolDidFinishLoading(self)
        }
        override func stopLoading() {}
    }

    /// The catalogue's answers in the shape iTunes Search returns (trimmed to
    /// the fields read): the song in this Mac's storefront under its English
    /// name, beside the English recording and another artist's song.
    static let listed = Data("""
    {"resultCount":3,"results":[
     {"wrapperType":"track","kind":"song","trackId":1001,"artistName":"YOASOBI","collectionName":"Racing into the Night - Single",
      "trackName":"Racing into the Night","trackTimeMillis":258000,"country":"USA"},
     {"wrapperType":"track","kind":"song","trackId":1002,"artistName":"YOASOBI","collectionName":"E-SIDE",
      "trackName":"Into The Night","trackTimeMillis":259000,"country":"USA"},
     {"wrapperType":"track","kind":"song","trackId":1003,"artistName":"Someone Else","collectionName":"Night",
      "trackName":"Racing into the Night","trackTimeMillis":258000,"country":"USA"}]}
    """.utf8)
    /// The same entry read from the Japanese storefront.
    static let original = Data("""
    {"resultCount":1,"results":[
     {"wrapperType":"track","kind":"song","trackId":1001,"artistName":"YOASOBI","collectionName":"夜に駆ける - Single",
      "trackName":"夜に駆ける","trackTimeMillis":258000,"country":"JPN"}]}
    """.utf8)
    /// A Western song: the same name in Japan.
    static let unchanged = Data("""
    {"resultCount":1,"results":[{"trackId":1001,"artistName":"YOASOBI","trackName":"Racing into the Night","trackTimeMillis":258000}]}
    """.utf8)

    /// MusicBrainz's answers (trimmed): an artist placed in Japan, and one in the US.
    static let japaneseArtist = Data(#"{"artists":[{"name":"YOASOBI","score":100,"country":"JP","area":{"name":"Japan"},"aliases":[{"name":"요아소비","locale":"ko"}]}]}"#.utf8)
    static let westernArtist = Data(#"{"artists":[{"name":"YOASOBI","score":100,"area":{"name":"Los Angeles"}}]}"#.utf8)

    private var folder: URL!
    private var titles: OriginalTitles!
    private var aliases: ArtistAliases!

    private func session() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [Catalogue.self]
        return URLSession(configuration: config)
    }

    override func setUp() {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        titles = OriginalTitles(folder: folder, session: session(), storefront: "us", spacing: 0)
        aliases = ArtistAliases(folder: folder, session: session())
        Catalogue.asked = []
        Catalogue.brainzAsked = 0
        Catalogue.search = Self.listed
        Catalogue.searchJP = nil
        Catalogue.lookup = Self.original
        Catalogue.brainz = Self.japaneseArtist
    }
    override func tearDown() { try? FileManager.default.removeItem(at: folder) }

    func testReadsTheOriginalTitleOfTheSameCatalogueEntry() async throws {
        let found = await titles.original(for: query("Racing into the Night"))
        XCTAssertEqual(found, OriginalTitles.Found(title: "夜に駆ける", album: "夜に駆ける - Single"))
        XCTAssertEqual(Catalogue.asked.count, 2)
        let search = try XCTUnwrap(URLComponents(url: Catalogue.asked[0], resolvingAgainstBaseURL: false))
        // Only the artist and title are sent, as for cover art.
        XCTAssertEqual(Set(search.queryItems?.map(\.name) ?? []), ["term", "entity", "country", "limit"])
        XCTAssertEqual(search.queryItems?.first { $0.name == "term" }?.value, "YOASOBI Racing into the Night")
        let lookup = try XCTUnwrap(URLComponents(url: Catalogue.asked[1], resolvingAgainstBaseURL: false))
        XCTAssertEqual(lookup.path, "/lookup")
        XCTAssertEqual(lookup.queryItems?.first { $0.name == "id" }?.value, "1001")
        XCTAssertEqual(lookup.queryItems?.first { $0.name == "country" }?.value, "jp")
        XCTAssertEqual(lookup.queryItems?.first { $0.name == "lang" }?.value, "ja_jp")
        // Remembered, also on disk: asked once.
        _ = await titles.original(for: query("racing into the  NIGHT"))
        let reopened = OriginalTitles(folder: folder, session: session(), storefront: "us", spacing: 0)
        let again = await reopened.original(for: query("Racing into the Night"))
        XCTAssertEqual(again?.title, "夜に駆ける")
        XCTAssertEqual(Catalogue.asked.count, 2)
    }

    func testASongWithoutAnotherTitleIsRememberedAsNone() async {
        Catalogue.lookup = Self.unchanged
        let none = await titles.original(for: query("Racing into the Night"))
        XCTAssertNil(none)
        _ = await titles.original(for: query("Racing into the Night"))
        XCTAssertEqual(Catalogue.asked.count, 2)
        // Nothing to ask for a title already in Japanese, or for a song whose length is far off.
        _ = await titles.original(for: query("夜に駆ける"))
        var other = query("Racing into the Night")
        other.duration = 300
        Catalogue.lookup = Self.original
        let far = await titles.original(for: other)
        XCTAssertNil(far)
        XCTAssertEqual(Catalogue.asked.count, 4, "searched here and in Japan, but no entry of that length")
    }

    func testFallbackSearchesUnderTheJapaneseTitle() async throws {
        nonisolated(unsafe) var asked: [String] = []
        let playing = query("Racing into the Night")
        let japanese = candidate(.lrclib, Self.japanese, title: "夜に駆ける", words: true)
        let found = try await LyricsFetcher.withOriginalTitle(.lrclib, query: playing, titles: titles, aliases: aliases, enabled: true) { q in
            asked.append(q.title)
            return q.title == "夜に駆ける" ? japanese : nil
        }
        XCTAssertEqual(asked, ["Racing into the Night", "夜に駆ける"])
        // Recorded under the listed title, so the recording check and the cache key hold.
        XCTAssertEqual(found?.title, "Racing into the Night")
        XCTAssertTrue(found?.viaOriginalTitle == true)
        XCTAssertNil(LyricsFetcher.rejection(try XCTUnwrap(found), query: playing))
    }

    func testFallbackReplacesAnEnglishVersionOrEnglishOnlyLyricsForAnEastAsianGenre() async throws {
        let japanese = candidate(.kugou, Self.japanese, title: "夜に駆ける", words: true)
        // "English Version" under the listed title.
        let tagged = candidate(.kugou, Self.english, title: "Racing into the Night (English Version)", words: true)
        var found = try await LyricsFetcher.withOriginalTitle(.kugou, query: query("Racing into the Night"), titles: titles, aliases: aliases, enabled: true) { q in
            q.title == "夜に駆ける" ? japanese : tagged
        }
        XCTAssertTrue(found?.viaOriginalTitle == true)
        // English lyrics with no tag, for a J-Pop song.
        let untagged = candidate(.kugou, Self.english, title: "Racing into the Night", words: true)
        found = try await LyricsFetcher.withOriginalTitle(.kugou, query: query("Racing into the Night", genre: "J-Pop"),
                                                          titles: titles, aliases: aliases, enabled: true) { q in
            q.title == "夜に駆ける" ? japanese : untagged
        }
        XCTAssertTrue(found?.viaOriginalTitle == true)
        XCTAssertEqual(LyricLanguage.script(of: try XCTUnwrap(found)), .native)
    }

    func testFallbackAsksNothingWhenTheListedTitleFoundTheSong() async throws {
        let japanese = candidate(.netEase, Self.japanese, title: "Racing into the Night", words: true)
        let first = try await LyricsFetcher.withOriginalTitle(.netEase, query: query("Racing into the Night", genre: "J-Pop"),
                                                              titles: titles, aliases: aliases, enabled: true) { _ in japanese }
        XCTAssertFalse(first?.viaOriginalTitle == true)
        // English lyrics for a song that names its English version are wanted.
        nonisolated(unsafe) var searches = 0
        let second = try await LyricsFetcher.withOriginalTitle(.lrclib, query: query("Idol (English Version)", genre: "J-Pop"),
                                                               titles: titles, aliases: aliases, enabled: true) { _ in searches += 1; return nil }
        XCTAssertNil(second)
        // Not a source searched again, or the setting is off.
        _ = try await LyricsFetcher.withOriginalTitle(.lyricsOVH, query: query("Racing into the Night"), titles: titles, aliases: aliases, enabled: true) { _ in nil }
        _ = try await LyricsFetcher.withOriginalTitle(.lrclib, query: query("Racing into the Night"), titles: titles, aliases: aliases, enabled: false) { _ in nil }
        XCTAssertEqual(searches, 1)
        XCTAssertEqual(Catalogue.asked.count, 0)
    }

    func testFallbackKeepsTheFirstAnswerWhenTheOriginalTitleFindsOnlyEnglish() async throws {
        let english = candidate(.lrclib, Self.english, title: "夜に駆ける", words: true)
        let first = candidate(.lrclib, Self.english, title: "Racing into the Night (English Version)", words: true)
        let found = try await LyricsFetcher.withOriginalTitle(.lrclib, query: query("Racing into the Night"), titles: titles, aliases: aliases, enabled: true) { q in
            q.title == "夜に駆ける" ? english : first
        }
        XCTAssertEqual(found?.title, "Racing into the Night (English Version)")
        XCTAssertFalse(found?.viaOriginalTitle == true)
    }

    // MARK: - East Asian artists listed under an English title

    /// iTunes answers in the shapes the live catalogue gave (trimmed; ids and
    /// lengths invented): where the storefront lists the song natively,
    /// search finds it by its English title, first.
    static let listedNatively = Data("""
    {"resultCount":3,"results":[
     {"trackId":2001,"artistName":"YOASOBI","collectionName":"夜に駆ける - Single","trackName":"夜に駆ける","trackTimeMillis":259500},
     {"trackId":2002,"artistName":"YOASOBI","collectionName":"E-SIDE","trackName":"Into The Night (English Version)","trackTimeMillis":259000},
     {"trackId":2003,"artistName":"Someone Else","collectionName":"Night","trackName":"Racing into the Night","trackTimeMillis":258000}]}
    """.utf8)
    /// A storefront that lists neither: a live take first, then covers.
    static let notListed = Data("""
    {"resultCount":3,"results":[
     {"trackId":3001,"artistName":"YOASOBI","collectionName":"Into The Night - From THE FIRST TAKE - Single","trackName":"Into The Night - From THE FIRST TAKE","trackTimeMillis":248000},
     {"trackId":2002,"artistName":"YOASOBI","collectionName":"E-SIDE","trackName":"Into The Night (English Version)","trackTimeMillis":259000},
     {"trackId":2003,"artistName":"Someone Else","collectionName":"Night","trackName":"Racing into the Night","trackTimeMillis":258000}]}
    """.utf8)
    /// Japan's storefront searched by the English title alone.
    static let japanByTitle = Data("""
    {"resultCount":3,"results":[
     {"trackId":4001,"artistName":"Another Band","collectionName":"Racing Into The Night","trackName":"Racing Into The Night","trackTimeMillis":206000},
     {"trackId":2002,"artistName":"YOASOBI","collectionName":"E-SIDE","trackName":"Into The Night (English Version)","trackTimeMillis":259000},
     {"trackId":2001,"artistName":"YOASOBI","collectionName":"夜に駆ける - Single","trackName":"夜に駆ける","trackTimeMillis":259500}]}
    """.utf8)

    func testFindsTheSongWhereTheStorefrontListsItUnderItsNativeName() async {
        Catalogue.search = Self.listedNatively
        let found = await titles.original(for: query("Racing into the Night"))
        XCTAssertEqual(found, OriginalTitles.Found(title: "夜に駆ける", album: "夜に駆ける - Single"))
        XCTAssertEqual(Catalogue.asked.count, 1, "no second request")
    }

    func testSearchesJapanWhenThisStorefrontHasNoEntry() async throws {
        Catalogue.search = Self.notListed
        Catalogue.searchJP = Self.japanByTitle
        let found = await titles.original(for: query("Racing into the Night"))
        XCTAssertEqual(found?.title, "夜に駆ける")
        XCTAssertEqual(Catalogue.asked.count, 2)
        let japan = try XCTUnwrap(URLComponents(url: Catalogue.asked[1], resolvingAgainstBaseURL: false))
        XCTAssertEqual(japan.path, "/search")
        XCTAssertEqual(japan.queryItems?.first { $0.name == "country" }?.value, "jp")
        XCTAssertEqual(japan.queryItems?.first { $0.name == "term" }?.value, "Racing into the Night")
        // A Mac in Japan is no longer turned away: it searches Japan directly.
        Catalogue.asked = []
        let japanese = OriginalTitles(folder: folder.appendingPathComponent("jp"), session: session(), storefront: "jp", spacing: 0)
        let there = await japanese.original(for: query("Racing into the Night"))
        XCTAssertEqual(there?.title, "夜に駆ける")
        XCTAssertEqual(Catalogue.asked.count, 1)
    }

    func testOnlyTheArtistsFirstEntryForThePlayingVersionCounts() {
        func song(_ id: Int, _ name: String, _ seconds: Double, artist: String = "YOASOBI") -> OriginalTitles.Song {
            .init(trackId: id, trackName: name, artistName: artist, collectionName: nil, trackTimeMillis: Int(seconds * 1000))
        }
        // Another song of the artist's first: not guessed from a later entry.
        XCTAssertNil(OriginalTitles.nativeMatch([song(1, "ハルカ", 243), song(2, "夜に駆ける", 258)], query: query("Racing into the Night")))
        // Other versions are passed over; a length too far off is not the song.
        XCTAssertEqual(OriginalTitles.nativeMatch([song(1, "夜に駆ける (Live)", 300), song(2, "夜に駆ける", 260)],
                                                  query: query("Racing into the Night"))?.title, "夜に駆ける")
        XCTAssertNil(OriginalTitles.nativeMatch([song(2, "夜に駆ける", 252)], query: query("Racing into the Night")))
        // Another artist's song, or a Latin name, is not an original title.
        XCTAssertNil(OriginalTitles.nativeMatch([song(2, "夜に駆ける", 258, artist: "Someone Else")], query: query("Racing into the Night")))
        XCTAssertNil(OriginalTitles.nativeMatch([song(2, "Yoru ni kakeru", 258)], query: query("Racing into the Night")))
        // Lengths fit within 3 s, or 2% of a long song, as recordings are matched.
        var long = query("Racing into the Night")
        long.duration = 400
        XCTAssertNotNil(OriginalTitles.match([song(5, "Racing into the Night", 407)], query: long))
        XCTAssertNil(OriginalTitles.match([song(5, "Racing into the Night", 409)], query: long))
        XCTAssertNotNil(OriginalTitles.match([song(5, "Racing into the Night", 263)], query: query("Racing into the Night")))
        XCTAssertNil(OriginalTitles.match([song(5, "Racing into the Night", 264)], query: query("Racing into the Night")))
    }

    func testAnEnglishVersionUnderTheListedTitleGivesWayForAnEastAsianArtist() async throws {
        // LRCLIB's exact match under the English title is the English recording, untagged.
        let english = candidate(.lrclib, Self.english, title: "Racing into the Night", words: true)
        let japanese = candidate(.lrclib, Self.japanese, title: "夜に駆ける", words: true)
        let playing = query("Racing into the Night")
        let found = try await LyricsFetcher.withOriginalTitle(.lrclib, query: playing, titles: titles, aliases: aliases, enabled: true) { q in
            q.title == "夜に駆ける" ? japanese : english
        }
        XCTAssertEqual(Catalogue.brainzAsked, 1)
        XCTAssertTrue(found?.viaOriginalTitle == true)
        XCTAssertEqual(LyricLanguage.script(of: try XCTUnwrap(found)), .native)
        // Another source's English lyrics now rank as another language's.
        let other = candidate(.kugou, Self.english, title: "Racing into the Night", words: true)
        let ranked = LyricsFetcher.ranked([other, try XCTUnwrap(found)], query: playing, options: options)
        XCTAssertEqual(ranked.first?.source, .lrclib)
        XCTAssertTrue(ranked.last?.languageMismatch == true)
    }

    func testWesternArtistsAreNotSearchedUnderAnOriginalTitle() async throws {
        Catalogue.brainz = Self.westernArtist
        let english = candidate(.lrclib, Self.english, title: "Racing into the Night", words: true)
        // Apple's own lyrics, relayed by Lyrics+, for the listed entry: nothing is asked at all.
        let relay = LyricCandidate(source: .lyricsPlus, result: english.result.relabelled("Apple" + LyricsFetcher.lyricsPlusSuffix),
                                   title: "Racing into the Night", artist: "YOASOBI", duration: nil, score: 0)
        _ = try await LyricsFetcher.withOriginalTitle(.lyricsPlus, query: query("Racing into the Night"), titles: titles,
                                                      aliases: aliases, enabled: true) { _ in relay }
        XCTAssertEqual(Catalogue.brainzAsked, 0)
        // Latin-script lyrics, or none, for an artist MusicBrainz places elsewhere: no catalogue search.
        let kept = try await LyricsFetcher.withOriginalTitle(.lrclib, query: query("Racing into the Night"), titles: titles,
                                                             aliases: aliases, enabled: true) { _ in english }
        XCTAssertEqual(kept?.title, "Racing into the Night")
        let none = try await LyricsFetcher.withOriginalTitle(.netEase, query: query("Racing into the Night"), titles: titles,
                                                             aliases: aliases, enabled: true) { _ in nil }
        XCTAssertNil(none)
        XCTAssertEqual(Catalogue.asked.count, 0, "no iTunes request")
        XCTAssertEqual(Catalogue.brainzAsked, 1, "one MusicBrainz request for the artist, kept")
    }
}

private extension LyricsFetcher.Result {
    /// The same lines under another source label.
    func relabelled(_ source: String) -> LyricsFetcher.Result {
        switch self {
        case .synced(let lines, let p): return .synced(lines, .init(precision: p.precision, source: source))
        case .plain(let lines, let p): return .plain(lines, .init(precision: p.precision, source: source))
        }
    }
}
