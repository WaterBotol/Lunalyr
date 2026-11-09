import AppKit
import Combine
import XCTest
@testable import VersoCustom

// Web players in browsers and local-file players, all against fakes. Nothing
// here sends an Apple event, runs JavaScript in a real browser, opens a tab,
// touches the network or reads real preferences.

private func text(_ value: String) -> NSAppleEventDescriptor { NSAppleEventDescriptor(string: value) }
private func number(_ value: Double) -> NSAppleEventDescriptor { NSAppleEventDescriptor(double: value) }
private func flag(_ value: Bool) -> NSAppleEventDescriptor { NSAppleEventDescriptor(boolean: value) }

/// The page script's JSON for a tab.
private func pageJSON(service: String = "youtubeMusic", title: String, artist: String = "Artist", album: String = "",
                      artwork: String = "", duration: Double = 200, position: Double = 10, state: String = "playing",
                      ad: Bool = false) -> String {
    let object: [String: Any] = ["service": service, "title": title, "artist": artist, "album": album, "artwork": artwork,
                                 "duration": duration, "position": position, "state": state, "ad": ad]
    let data = try! JSONSerialization.data(withJSONObject: object)
    return String(data: data, encoding: .utf8)!
}

/// A browser's scripting interface: its music tabs, answered from plain state.
final class FakeBrowser: AppleScriptRunning {
    struct Tab {
        var key: String
        var window = 1
        var active = true
        var url = "https://music.youtube.com/watch?v=abc"
        var json: String?
        var failure: (code: Int, message: String)?
    }

    let kind: PlayerKind
    var running = true
    var tabs: [Tab] = []
    /// When set, every script fails with this AppleScript error number.
    var failure: Int?
    private(set) var log: [String] = []
    private(set) var sources: [String] = []

    init(_ kind: PlayerKind) { self.kind = kind }

    func count(_ name: String) -> Int { log.filter { $0 == name }.count }

    func run(_ script: PlayerScript) -> Result<NSAppleEventDescriptor, ScriptFailure> {
        log.append(script.name)
        sources.append(script.source)
        if let failure { return .failure(ScriptFailure(code: failure)) }
        let prefix = "web.\(kind.rawValue)."
        let isQuery = script.name == prefix + "read" || script.name == prefix + "position"
        guard running else { return .success(isQuery ? .scriptList([text("notRunning")]) : text("notRunning")) }
        guard isQuery else { return .success(text("ok")) }
        return .success(Self.reply(tabs))
    }

    static func reply(_ tabs: [Tab]) -> NSAppleEventDescriptor {
        .scriptList([text("tabs")] + tabs.map { tab in
            .scriptList([text(tab.key), number(Double(tab.window)), flag(tab.active), text(tab.url),
                         text(tab.json ?? ""), number(Double(tab.failure?.code ?? 0)), text(tab.failure?.message ?? "")])
        })
    }
}

/// VLC, Swinsian or VOX, answered from plain state in each one's reply shape.
final class FakeLocalPlayer: AppleScriptRunning {
    let kind: PlayerKind
    var running = true
    var playing = true
    var name: String? = "Artist - Song"
    var path: String? = "/Music/Artist - Song.flac"
    var duration: Double = 180
    var position: Double = 30
    private(set) var log: [String] = []
    private(set) var sources: [String] = []

    init(_ kind: PlayerKind) { self.kind = kind }

    func run(_ script: PlayerScript) -> Result<NSAppleEventDescriptor, ScriptFailure> {
        log.append(script.name)
        sources.append(script.source)
        guard script.name.hasPrefix(kind.rawValue + ".") else { return .success(text("unexpected")) }
        let isQuery = script.name.hasSuffix(".read") || script.name.hasSuffix(".position")
        guard running else { return .success(isQuery ? .scriptList([text("notRunning")]) : text("notRunning")) }
        let state = playing ? "playing" : "paused"
        switch script.name {
        case "vlc.read":
            guard name != nil || path != nil else { return .success(.scriptList([text("stopped")])) }
            return .success(.scriptList([text(state), text(path ?? name ?? ""), name.map(text) ?? .missingValue,
                                         path.map(text) ?? .missingValue, number(duration)]))
        case "vlc.position":
            guard name != nil || path != nil else { return .success(.scriptList([text("stopped")])) }
            return .success(.scriptList([text(state), text(path ?? name ?? ""), number(position)]))
        case "vlc.playpause":
            playing.toggle()
        default:
            break
        }
        return .success(text("ok"))
    }
}

/// Routes each script to the fake its name belongs to.
final class RoutingRunner: AppleScriptRunning {
    var routes: [String: AppleScriptRunning] = [:]
    private(set) var unrouted: [String] = []

    func run(_ script: PlayerScript) -> Result<NSAppleEventDescriptor, ScriptFailure> {
        if let route = routes.first(where: { script.name.hasPrefix($0.key) })?.value { return route.run(script) }
        unrouted.append(script.name)
        return .failure(ScriptFailure(code: AppleEventCode.processNotFound))
    }
}

@MainActor
private final class Rig {
    let runner = RoutingRunner()
    let environment: FakePlayerEnvironment
    let scheduler = ManualScheduler()
    let clock = TestClock()
    let followed: CurrentValueSubject<[PlayerKind], Never>
    let monitor: NowPlayingMonitor

    init(running: Set<String>, installed: Set<String>? = nil, followed: [PlayerKind]) {
        environment = FakePlayerEnvironment(running: running, installed: installed)
        self.followed = CurrentValueSubject(followed)
        var services = NowPlayingServices.offline
        services.followedPlayers = self.followed.eraseToAnyPublisher()
        let clock = clock
        monitor = NowPlayingMonitor(runner: runner, environment: environment, scheduler: scheduler,
                                    permission: { _, _ in 0 }, services: services, uptime: { clock.now }, pollInterval: nil)
    }

    func start() {
        monitor.start()
        scheduler.drain()
    }
}

// MARK: - Web players

final class WebPlayerParsingTests: XCTestCase {
    func testServicesAreKnownByTheirExactHost() {
        XCTAssertEqual(WebService(pageURL: "https://music.youtube.com/watch?v=1"), .youTubeMusic)
        XCTAssertEqual(WebService(pageURL: "https://soundcloud.com/artist/track"), .soundCloud)
        XCTAssertEqual(WebService(pageURL: "https://m.soundcloud.com/artist/track"), .soundCloud)
        XCTAssertEqual(WebService(pageURL: "https://music.amazon.co.uk/albums/x"), .amazonMusic)
        XCTAssertEqual(WebService(pageURL: "https://music.amazon.com/"), .amazonMusic)
        XCTAssertNil(WebService(pageURL: "https://www.youtube.com/watch?v=1"), "Plain YouTube is not YouTube Music")
        XCTAssertNil(WebService(pageURL: "https://notsoundcloud.com/x"))
        XCTAssertNil(WebService(pageURL: "https://example.com/?q=music.youtube.com"))
        XCTAssertNil(WebService(pageURL: "file:///music.youtube.com"))
        XCTAssertEqual(WebService.youTubeMusic.searchURL("a b")?.absoluteString, "https://music.youtube.com/search?q=a%20b")
    }

    func testThePageScriptsReadingIsParsed() throws {
        let youTube = try XCTUnwrap(WebReading(json: pageJSON(title: "Song", artist: "Band - Topic", album: "LP",
                                                              artwork: "https://lh3.googleusercontent.com/x", duration: 200, position: 12.5)))
        XCTAssertEqual(youTube.service, .youTubeMusic)
        XCTAssertEqual(youTube.artist, "Band", "YouTube's \"- Topic\" channels are the artist")
        XCTAssertEqual(youTube.album, "LP")
        XCTAssertEqual(youTube.duration, 200)
        XCTAssertEqual(youTube.position, 12.5)
        XCTAssertEqual(youTube.state, .playing)
        XCTAssertEqual(youTube.artworkURL?.host, "lh3.googleusercontent.com")
        XCTAssertTrue(youTube.hasTrack)

        let soundCloud = try XCTUnwrap(WebReading(json: pageJSON(service: "soundcloud", title: "Mix", duration: 300,
                                                                 position: 400, state: "paused")))
        XCTAssertEqual(soundCloud.position, 300, "A position past the end is the end")
        XCTAssertEqual(soundCloud.state, .paused)

        let unknownLength = try XCTUnwrap(WebReading(json: pageJSON(service: "amazonMusic", title: "Amz", artwork: "http://x/y.jpg",
                                                                    duration: -1, position: -1)))
        XCTAssertNil(unknownLength.duration)
        XCTAssertNil(unknownLength.position)
        XCTAssertNil(unknownLength.artworkURL, "Covers only over HTTPS")

        let empty = try XCTUnwrap(WebReading(json: pageJSON(title: "", state: "none")))
        XCTAssertFalse(empty.hasTrack)
        XCTAssertNil(WebReading(json: #"{"error":"TypeError"}"#))
        XCTAssertNil(WebReading(json: "not json"))
        XCTAssertNil(WebReading(json: ""))
    }

    func testASnapshotIsASongOnlyOnceTheLengthIsKnown() {
        var reading = WebReading(service: .youTubeMusic, title: "Song", artist: "A", album: "LP", duration: 200, position: 5, state: .playing)
        var snapshot = WebPlayerBackend.snapshot(reading)
        XCTAssertEqual(snapshot.track?.duration, 200)
        XCTAssertEqual(snapshot.track?.album, "LP")
        XCTAssertEqual(snapshot.track?.isStream, false)
        XCTAssertEqual(snapshot.position, 5)
        XCTAssertEqual(snapshot.track?.persistentID, "youtubeMusic|Song|A")
        reading.duration = nil
        snapshot = WebPlayerBackend.snapshot(reading)
        XCTAssertEqual(snapshot.track?.isStream, true, "No lyric lookup without a length")
        reading.duration = 30
        reading.isAdvert = true
        XCTAssertEqual(WebPlayerBackend.snapshot(reading).track?.isAdvert, true)
        reading.title = ""
        XCTAssertEqual(WebPlayerBackend.snapshot(reading).state, .stopped)
    }

    func testTheTabFollowedIsOneThatPlaysAndStaysTheSame() {
        func tab(_ key: String, window: Int, active: Bool, state: PlayerSnapshot.State, title: String = "Song") -> WebTab {
            WebTab(key: key, window: window, isActive: active, url: "https://music.youtube.com/",
                   reading: WebReading(service: .youTubeMusic, title: title, artist: "A", duration: 100, position: 1, state: state))
        }
        let pausedFront = tab("1", window: 1, active: true, state: .paused)
        let playingBack = tab("2", window: 2, active: false, state: .playing)
        let playingFront = tab("3", window: 1, active: false, state: .playing)
        XCTAssertEqual(WebPlayerBackend.chooseTab([pausedFront, playingBack], following: nil)?.key, "2", "Playing beats paused")
        XCTAssertEqual(WebPlayerBackend.chooseTab([pausedFront, playingBack, playingFront], following: nil)?.key, "3",
                       "The frontmost window first")
        XCTAssertEqual(WebPlayerBackend.chooseTab([pausedFront, playingBack, playingFront], following: "2")?.key, "2",
                       "Two playing tabs never take turns")
        XCTAssertEqual(WebPlayerBackend.chooseTab([pausedFront, playingBack], following: "1")?.key, "2",
                       "A followed tab that paused gives way to one that plays")
        let pausedBackActive = tab("4", window: 2, active: true, state: .paused)
        let pausedFrontHidden = tab("5", window: 1, active: false, state: .paused)
        XCTAssertEqual(WebPlayerBackend.chooseTab([pausedBackActive, pausedFrontHidden, pausedFront], following: nil)?.key, "1",
                       "Nothing plays: the shown tab of the front window")
        let blank = tab("6", window: 1, active: true, state: .stopped, title: "")
        XCTAssertNil(WebPlayerBackend.chooseTab([blank], following: nil))
        XCTAssertNil(WebPlayerBackend.chooseTab([], following: "1"))
    }

    func testJavaScriptFromAppleEventsBeingOffIsRecognised() {
        let safari = "You must enable the 'Allow JavaScript from Apple Events' option in Safari's Develop menu to use 'do JavaScript'."
        let chrome = "Executing JavaScript through AppleScript is turned off. To turn it on, from the menu bar, go to View > Developer > Allow JavaScript from Apple Events."
        XCTAssertTrue(WebPlayerBackend.isJavaScriptDisabled(code: 8, message: safari))
        XCTAssertTrue(WebPlayerBackend.isJavaScriptDisabled(code: 12, message: chrome))
        XCTAssertTrue(WebPlayerBackend.isJavaScriptDisabled(code: 12, message: "JavaScript über AppleScript ist deaktiviert."))
        XCTAssertFalse(WebPlayerBackend.isJavaScriptDisabled(code: -1728, message: "Can’t get tab 3 of window 1."))
        XCTAssertFalse(WebPlayerBackend.isJavaScriptDisabled(code: 0, message: ""))

        func failing(_ message: String) -> WebTab {
            WebTab(key: "1", window: 1, isActive: true, url: "https://music.youtube.com/", reading: nil,
                   failure: WebTab.Failure(code: 12, message: message))
        }
        XCTAssertTrue(WebPlayerBackend.allJavaScriptOff([failing(chrome), failing(chrome)]))
        XCTAssertFalse(WebPlayerBackend.allJavaScriptOff([]), "No music tab, nothing to say")
        XCTAssertFalse(WebPlayerBackend.allJavaScriptOff([failing("Can’t get tab")]))
        var reading = failing(chrome)
        reading.failure = nil
        reading.reading = WebReading(service: .soundCloud, title: "x", artist: "", duration: 1, position: 0, state: .paused)
        XCTAssertFalse(WebPlayerBackend.allJavaScriptOff([failing(chrome), reading]), "One readable tab is enough")
    }

    func testABrowsersReplyBecomesASnapshotAndRemembersItsTab() {
        let memory = WebTabMemory()
        let backend = WebPlayerBackend(browser: .chrome, memory: memory)
        let reply = FakeBrowser.reply([
            FakeBrowser.Tab(key: "11", window: 1, active: true, url: "https://www.youtube.com/", json: nil),
            FakeBrowser.Tab(key: "22", window: 1, active: false, url: "https://soundcloud.com/a/b",
                            json: pageJSON(service: "soundcloud", title: "Cloud", artist: "Someone", duration: 240, position: 60)),
        ])
        guard case .value(let snapshot) = backend.parseRead(reply) else { return XCTFail("No reading") }
        XCTAssertEqual(snapshot.state, .playing)
        XCTAssertEqual(snapshot.track?.title, "Cloud")
        XCTAssertEqual(snapshot.track?.artist, "Someone")
        XCTAssertEqual(snapshot.track?.duration, 240)
        XCTAssertEqual(memory.entry(for: .chrome), WebTabMemory.Entry(tabKey: "22", service: .soundCloud))

        guard case .value(let sample) = backend.parsePosition(reply, at: 7) else { return XCTFail("No sample") }
        XCTAssertEqual(sample.position, 60)
        XCTAssertEqual(sample.time, 7)
        XCTAssertEqual(sample.trackID, snapshot.track?.persistentID)

        XCTAssertTrue(backend.commandScript(.next).source.contains(#""22""#), "Commands reach the followed tab")

        let off = FakeBrowser.reply([FakeBrowser.Tab(key: "22", url: "https://soundcloud.com/a/b", json: nil,
                                                     failure: (12, "Executing JavaScript through AppleScript is turned off."))])
        XCTAssertEqual(backend.parseRead(off).value, nil)
        if case .javaScriptOff = backend.parseRead(off) {} else { XCTFail("JavaScript off is said as such") }
        XCTAssertNil(memory.entry(for: .chrome), "No tab is followed any more")

        guard case .value(let idle) = backend.parseRead(FakeBrowser.reply([])) else { return XCTFail("No music tab is idle") }
        XCTAssertEqual(idle.state, .stopped)
        if case .notRunning = backend.parseRead(.scriptList([text("notRunning")])) {} else { XCTFail("Not running") }
        if case .failed = backend.parseRead(text("garbage")) {} else { XCTFail("Garbage fails") }
    }

    func testTheScriptsSuitEachBrowser() {
        let safari = WebPlayerBackend(browser: .safari, memory: WebTabMemory())
        XCTAssertTrue(safari.readScript.source.contains("do JavaScript"))
        XCTAssertFalse(safari.readScript.source.contains("execute javascript"))
        XCTAssertTrue(safari.readScript.source.contains(#"application id "com.apple.Safari" is not running"#),
                      "A browser that is not open is never launched")
        for browser in [PlayerKind.chrome, .arc, .brave, .edge] {
            let backend = WebPlayerBackend(browser: browser, memory: WebTabMemory())
            XCTAssertTrue(backend.readScript.source.contains("execute javascript"), "\(browser)")
            XCTAssertTrue(backend.readScript.source.contains(browser.bundleID))
            XCTAssertTrue(backend.readScript.cacheable)
            XCTAssertFalse(backend.commandScript(.playPause).cacheable)
        }
        // The page script sits on one line, its quotes escaped for AppleScript.
        let literal = WebPlayerScript.appleScriptLiteral(WebPlayerScript.readJavaScript)
        XCTAssertFalse(literal.contains("\n"))
        let inner = literal.dropFirst().dropLast()
        let unescaped = inner.replacingOccurrences(of: "\\\\", with: "").replacingOccurrences(of: "\\\"", with: "")
        XCTAssertFalse(unescaped.contains("\""), "Every quote inside is escaped")
        XCTAssertTrue(WebPlayerScript.readJavaScript.contains("mediaSession"))

        let seek = WebPlayerScript.command(.chrome, tab: "5", action: .seek(83.25))
        XCTAssertEqual(seek.name, "web.chrome.seek")
        XCTAssertTrue(seek.source.contains("sec=83.250"))
        XCTAssertTrue(seek.source.contains(#"set targetKey to "5""#))
        let first = WebPlayerScript.command(.safari, tab: nil, action: .playPause)
        XCTAssertTrue(first.source.contains(#"set targetKey to """#), "Before any tab is followed, the first music tab")
    }

    func testEachBrowserIsToldHowToAllowJavaScript() {
        for browser in PlayerKind.browsers {
            let notice = WebScriptingNotice(browser: browser, service: .youTubeMusic)
            XCTAssertTrue(notice.message.contains("Allow JavaScript from Apple Events"), "\(browser)")
            XCTAssertTrue(notice.message.contains("YouTube Music"))
            XCTAssertTrue(notice.message.contains("never changes this setting"))
            XCTAssertTrue(notice.title.contains(browser.shortBrowserName))
        }
        XCTAssertTrue(WebScriptingNotice.steps(for: .safari).contains("Show features for web developers"))
        XCTAssertTrue(WebScriptingNotice.steps(for: .chrome).contains("View › Developer"))
        XCTAssertEqual(WebScriptingNotice.steps(for: .vlc), "")
    }

    func testBrowsersAreNotJudgedByTheirOwnAudioProcesses() {
        for kind in PlayerKind.allCases {
            XCTAssertEqual(kind.outputBundleID == nil, kind.isBrowser)
        }
    }
}

// MARK: - Web players in the monitor

@MainActor
final class WebPlayerMonitorTests: XCTestCase {
    private let chromeID = "com.google.Chrome"

    override func setUp() {
        super.setUp()
        for kind in PlayerKind.browsers { WebTabMemory.shared.remember(nil, for: kind) }
    }

    override func tearDown() {
        for kind in PlayerKind.browsers { WebTabMemory.shared.remember(nil, for: kind) }
        super.tearDown()
    }

    private func chromeRig(_ tabs: [FakeBrowser.Tab], followed: [PlayerKind] = [.chrome]) -> (Rig, FakeBrowser) {
        let rig = Rig(running: [chromeID], installed: [chromeID, "com.apple.Music"], followed: followed)
        let chrome = FakeBrowser(.chrome)
        chrome.tabs = tabs
        rig.runner.routes["web.chrome."] = chrome
        return (rig, chrome)
    }

    func testAYouTubeMusicTabIsFollowedAndControlled() {
        let (rig, chrome) = chromeRig([
            FakeBrowser.Tab(key: "7", url: "https://music.youtube.com/watch?v=x",
                            json: pageJSON(title: "Midnight City", artist: "M83", duration: 243, position: 42)),
        ])
        rig.start()
        XCTAssertEqual(rig.monitor.activePlayer, .chrome)
        XCTAssertEqual(rig.monitor.current?.title, "Midnight City")
        XCTAssertEqual(rig.monitor.current?.artist, "M83")
        XCTAssertEqual(rig.monitor.current?.duration, 243)
        XCTAssertTrue(rig.monitor.isPlaying)
        XCTAssertEqual(rig.monitor.playbackTime(), 42.16, accuracy: 0.001)
        XCTAssertEqual(WebTabMemory.shared.entry(for: .chrome)?.service, .youTubeMusic)

        rig.monitor.playPause()
        rig.scheduler.drain()
        XCTAssertEqual(chrome.count("web.chrome.playpause"), 1)
        XCTAssertTrue(chrome.sources.last(where: { $0.contains("playpause") })?.contains(#"set targetKey to "7""#) ?? false)
        rig.monitor.seek(to: 100)
        rig.scheduler.drain()
        XCTAssertEqual(chrome.count("web.chrome.seek"), 1)
        XCTAssertEqual(rig.runner.unrouted, [], "Nothing else was addressed")
    }

    func testANewSongIsNoticedByPollingAlone() {
        let (rig, chrome) = chromeRig([
            FakeBrowser.Tab(key: "7", json: pageJSON(title: "One", duration: 200, position: 10)),
        ])
        rig.start()
        XCTAssertEqual(rig.monitor.current?.title, "One")
        chrome.tabs[0].json = pageJSON(title: "Two", duration: 180, position: 1)
        rig.monitor.tick()
        rig.scheduler.drain()
        XCTAssertEqual(rig.monitor.current?.title, "Two")
        chrome.tabs = []
        rig.monitor.tick()
        rig.scheduler.drain()
        XCTAssertNil(rig.monitor.current, "Closing the tab stops it")
    }

    func testJavaScriptBeingOffIsReportedUntilTheUserTurnsItOn() {
        let off = (code: 12, message: "Executing JavaScript through AppleScript is turned off.")
        let (rig, chrome) = chromeRig([FakeBrowser.Tab(key: "7", json: nil, failure: off)])
        rig.start()
        XCTAssertNil(rig.monitor.current)
        XCTAssertEqual(rig.monitor.javaScriptOff, [.chrome])
        XCTAssertEqual(rig.monitor.webScriptingBlocked, [.chrome])
        XCTAssertEqual(rig.monitor.automation[.chrome], .granted, "Automation itself is fine")
        XCTAssertNil(AutomationStatusBanner.content(for: rig.monitor))
        let notice = WebScriptingNotice.notice(for: rig.monitor)
        XCTAssertEqual(notice?.browser, .chrome)
        XCTAssertTrue(notice?.message.contains("View › Developer › Allow JavaScript from Apple Events") ?? false)
        XCTAssertTrue(rig.monitor.isPolling, "Still asked now and then, to notice the change")
        XCTAssertFalse(chrome.log.contains { !$0.hasSuffix(".read") && !$0.hasSuffix(".position") },
                       "Nothing but reads: the setting is never touched")

        chrome.tabs = [FakeBrowser.Tab(key: "7", json: pageJSON(title: "Back", duration: 100, position: 3))]
        rig.monitor.tick()
        rig.monitor.tick()
        rig.scheduler.drain()
        XCTAssertEqual(rig.monitor.javaScriptOff, [])
        XCTAssertNil(WebScriptingNotice.notice(for: rig.monitor))
        XCTAssertEqual(rig.monitor.current?.title, "Back")
    }

    func testBrowsersAreFollowedOnlyOnceWebPlayersAreOn() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let isolated = UserDefaults(suiteName: name)!
        defer { isolated.removePersistentDomain(forName: name) }
        let options = PlayerOptions(defaults: isolated)
        XCTAssertFalse(options.followsWebPlayers)
        XCTAssertFalse(options.followedPlayers.contains(where: \.isBrowser))

        let (rig, chrome) = chromeRig([FakeBrowser.Tab(key: "7", json: pageJSON(title: "Song"))], followed: options.followedPlayers)
        let watch = options.playersPublisher.sink { rig.followed.send($0) }
        defer { watch.cancel() }
        rig.start()
        XCTAssertEqual(chrome.log, [], "A browser that is merely open is never sent anything")
        XCTAssertNil(rig.monitor.current)

        options.followsWebPlayers = true
        rig.scheduler.drain()
        XCTAssertEqual(isolated.object(forKey: PlayerOptions.webKey) as? Bool, true)
        XCTAssertEqual(rig.monitor.activePlayer, .chrome)
        XCTAssertEqual(rig.monitor.current?.title, "Song")

        options.choice = .appleMusic
        rig.scheduler.drain()
        let sent = chrome.log.count
        rig.monitor.tick()
        rig.monitor.playPause()
        rig.scheduler.drain()
        XCTAssertEqual(chrome.log.count, sent, "Choosing Music alone leaves the browser alone")
        XCTAssertNil(rig.monitor.current)
    }
}

// MARK: - Local-file players

final class LocalPlayerParsingTests: XCTestCase {
    func testVLCsTitleLineAndFileNamesBecomeTitleAndArtist() {
        XCTAssertTrue(VLCBackend.titleAndArtist(name: "Daft Punk - One More Time", path: nil) == ("One More Time", "Daft Punk"))
        XCTAssertTrue(VLCBackend.titleAndArtist(name: "Untitled", path: "/x/Untitled.mp3") == ("Untitled", ""))
        XCTAssertTrue(VLCBackend.titleAndArtist(name: "03 Song.flac", path: "/Music/03 Song.flac") == ("Song", ""))
        XCTAssertTrue(VLCBackend.titleAndArtist(name: nil, path: "file:///Music/Air%20-%20La%20Femme%20d'Argent.m4a")
                      == ("La Femme d'Argent", "Air"))
        XCTAssertTrue(VLCBackend.titleAndArtist(name: "", path: "/Music/01. Intro.wav") == ("Intro", ""))
        XCTAssertTrue(VLCBackend.titleAndArtist(name: "1999", path: "/a/1999.mp3") == ("1999", ""),
                      "A title that is only a number keeps it")
        XCTAssertTrue(VLCBackend.titleAndArtist(name: nil, path: nil) == ("", ""))
        XCTAssertEqual(VLCBackend.midpoint(41, playing: true), 41.5, "Whole seconds, cut down")
        XCTAssertEqual(VLCBackend.midpoint(41, playing: false), 41)
        XCTAssertEqual(VLCBackend.midpoint(41.25, playing: true), 41.25)
        XCTAssertEqual(VLCBackend.midpoint(0, playing: true), 0)
    }

    func testVLCsRepliesAreParsed() {
        let backend = VLCBackend()
        let reply = NSAppleEventDescriptor.scriptList([text("playing"), text("/M/A - B.mp3"), text("A - B"), text("/M/A - B.mp3"), number(200)])
        let snapshot = backend.parseRead(reply).value
        XCTAssertEqual(snapshot?.track?.title, "B")
        XCTAssertEqual(snapshot?.track?.artist, "A")
        XCTAssertEqual(snapshot?.track?.duration, 200)
        XCTAssertEqual(snapshot?.track?.persistentID, "/M/A - B.mp3")
        XCTAssertEqual(backend.parseRead(.scriptList([text("stopped")])).value?.state, .stopped)
        let sample = backend.parsePosition(.scriptList([text("playing"), text("/M/A - B.mp3"), number(12)]), at: 3).value
        XCTAssertEqual(sample?.position, 12.5)
        XCTAssertEqual(backend.commandScript(.playPause).name, "vlc.playpause")
        XCTAssertTrue(backend.seekScript(to: 61.6).source.contains("set current time to 62"))
        XCTAssertTrue(VLCBackend.read.source.contains(#"application id "org.videolan.vlc" is not running"#))
    }

    func testSwinsiansRepliesAreParsed() {
        let backend = SwinsianBackend()
        let reply = NSAppleEventDescriptor.scriptList([text("paused"), text("/a.flac"), text("Song"), text("Artist"), text("LP"), number(211.5)])
        let track = backend.parseRead(reply).value?.track
        XCTAssertEqual(track?.title, "Song")
        XCTAssertEqual(track?.album, "LP")
        XCTAssertEqual(track?.duration, 211.5)
        XCTAssertEqual(backend.parseRead(reply).value?.state, .paused)
        XCTAssertEqual(backend.parsePosition(.scriptList([text("playing"), text("/a.flac"), number(9.25)]), at: 1).value?.position, 9.25)
        XCTAssertTrue(backend.seekScript(to: 9).source.contains("set player position to 9.000"))
        if case .notRunning = backend.parseRead(.scriptList([text("notRunning")])) {} else { XCTFail("Not running") }
    }

    func testVOXsRepliesAreParsed() {
        let backend = VoxBackend()
        func read(_ state: NSAppleEventDescriptor, title: String) -> PlayerSnapshot? {
            backend.parseRead(.scriptList([text("vox"), state, text("id-1"), text(title), text("Artist"), text(""), number(150)])).value
        }
        XCTAssertEqual(read(number(1), title: "Song")?.state, .playing)
        XCTAssertEqual(read(number(0), title: "Song")?.state, .paused)
        XCTAssertEqual(read(number(1), title: "")?.state, .stopped, "No track loaded")
        XCTAssertEqual(read(number(1), title: "Song")?.track?.album, nil)
        XCTAssertEqual(read(number(1), title: "Song")?.track?.duration, 150)
        let sample = backend.parsePosition(.scriptList([text("vox"), number(1), text("id-1"), text("Song"), number(33.3)]), at: 2).value
        XCTAssertEqual(sample?.position, 33.3)
        XCTAssertEqual(sample?.trackID, "id-1")
        XCTAssertEqual(backend.commandScript(.next).name, "vox.next")
    }
}

@MainActor
final class LocalPlayerMonitorTests: XCTestCase {
    func testVLCIsFollowedAndASongChangeIsNoticedByPolling() {
        let rig = Rig(running: ["org.videolan.vlc"], installed: ["org.videolan.vlc"], followed: PlayerKind.apps)
        let vlc = FakeLocalPlayer(.vlc)
        rig.runner.routes["vlc."] = vlc
        rig.start()
        XCTAssertEqual(rig.monitor.activePlayer, .vlc)
        XCTAssertEqual(rig.monitor.current?.title, "Song")
        XCTAssertEqual(rig.monitor.current?.artist, "Artist")
        XCTAssertEqual(rig.monitor.playbackTime(), 30.66, accuracy: 0.001)
        vlc.name = "Other - Next One"
        vlc.path = "/Music/Other - Next One.mp3"
        rig.monitor.tick()
        rig.scheduler.drain()
        XCTAssertEqual(rig.monitor.current?.title, "Next One")
        rig.monitor.playPause()
        rig.scheduler.drain()
        XCTAssertFalse(vlc.playing)
        XCTAssertFalse(rig.monitor.isPlaying)
        XCTAssertEqual(rig.runner.unrouted, [], "Only VLC, the one running, was addressed")
    }
}

// MARK: - Choosing sources

final class PlayerSourceChoiceTests: XCTestCase {
    func testEveryPlayerCanBeChosenAlone() {
        for kind in PlayerKind.allCases {
            XCTAssertEqual(PlayerOptions.Choice.only(kind).kind, kind)
            XCTAssertEqual(PlayerOptions.Choice.only(kind).players, [kind])
        }
        XCTAssertEqual(PlayerOptions.players(for: .automatic, webPlayers: false), PlayerKind.apps)
        XCTAssertEqual(PlayerOptions.players(for: .automatic, webPlayers: true), PlayerKind.allCases)
        XCTAssertEqual(PlayerOptions.players(for: .safari, webPlayers: false), [.safari])
    }

    func testPickersOfferOnlyWhatIsInstalled() {
        let offered = PlayerOptions.Choice.offered(installed: [.appleMusic, .vlc, .safari, .chrome], current: .automatic, webPlayers: false)
        XCTAssertEqual(offered, [.automatic, .appleMusic, .spotify, .vlc])
        let web = PlayerOptions.Choice.offered(installed: [.appleMusic, .vlc, .safari, .chrome], current: .automatic, webPlayers: true)
        XCTAssertEqual(web, [.automatic, .appleMusic, .spotify, .vlc, .safari, .chrome])
        XCTAssertTrue(PlayerOptions.Choice.offered(installed: [], current: .vox, webPlayers: false).contains(.vox),
                      "The current choice stays offered")
    }

    func testTheDesktopPlayersSwitchShowsWhatIsThere() {
        let kinds = DesktopPlayerModel.switchKinds(
            installed: [.appleMusic, .spotify, .vlc, .swinsian, .safari, .chrome], running: [.vlc, .safari, .chrome],
            followed: PlayerKind.allCases, webSeen: [.chrome], active: nil, selected: .appleMusic)
        XCTAssertEqual(kinds, [.appleMusic, .spotify, .vlc, .chrome],
                       "Swinsian is not open, and no web player was seen in Safari")
        let notFollowed = DesktopPlayerModel.switchKinds(
            installed: [.appleMusic, .chrome], running: [.chrome], followed: PlayerKind.apps, webSeen: [.chrome],
            active: nil, selected: .appleMusic)
        XCTAssertEqual(notFollowed, [.appleMusic])
        let playing = DesktopPlayerModel.switchKinds(installed: [], running: [], followed: [], webSeen: [],
                                                     active: .vox, selected: .appleMusic)
        XCTAssertEqual(playing, [.appleMusic, .vox], "The one playing is always there")
        XCTAssertEqual(DesktopPlayerModel.emptyQueueMessage(for: .vlc), "VLC doesn\u{2019}t share what plays next.")
        XCTAssertEqual(DesktopPlayerModel.emptyQueueMessage(for: .appleMusic), "Nothing is playing.")
        XCTAssertEqual(DesktopPlayerModel.words(for: PlayerSearchError.unsupported(.vox), player: .vox),
                       "VOX has no search Lunalyr can use.")
    }

    func testBrowsersGetAnAccessRowOnlyWhileFollowed() {
        XCTAssertEqual(PlayerAccessList.shown(installed: [.appleMusic, .vlc, .safari], followed: PlayerKind.apps),
                       [.appleMusic, .vlc])
        XCTAssertEqual(PlayerAccessList.shown(installed: [.appleMusic, .vlc, .safari], followed: PlayerKind.allCases),
                       [.appleMusic, .vlc, .safari])
    }

    func testPlayersAreNamedReadably() {
        XCTAssertEqual(PlayerKind.names([.appleMusic, .safari, .chrome]), "Music or a web player")
        XCTAssertTrue(PlayerKind.allCases.filter(\.pollsForChanges).allSatisfy { $0 != .appleMusic && $0 != .spotify })
        XCTAssertEqual(Set(PlayerKind.allCases.map(\.bundleID)).count, PlayerKind.allCases.count)
    }
}
