import AppKit
import Combine
import XCTest
@testable import VersoCustom

// Review round 2: web players (R2-3, R2-4, R2-15). Fakes and script text
// only: nothing here sends an Apple event or runs JavaScript in a browser.

private func text(_ value: String) -> NSAppleEventDescriptor { NSAppleEventDescriptor(string: value) }
private func number(_ value: Double) -> NSAppleEventDescriptor { NSAppleEventDescriptor(double: value) }
private func flag(_ value: Bool) -> NSAppleEventDescriptor { NSAppleEventDescriptor(boolean: value) }

private func playingJSON(service: String = "youtubeMusic", title: String = "Song") -> String {
    let object: [String: Any] = ["service": service, "title": title, "artist": "Artist", "album": "", "artwork": "",
                                 "duration": 200, "position": 10, "state": "playing", "ad": false]
    return String(data: try! JSONSerialization.data(withJSONObject: object), encoding: .utf8)!
}

/// One tab row as the read script returns it, with any window and error number.
private func row(key: String, window: NSAppleEventDescriptor = number(1), url: String, json: String = "",
                 code: NSAppleEventDescriptor = number(0), message: String = "") -> NSAppleEventDescriptor {
    .scriptList([text(key), window, flag(true), text(url), text(json), code, text(message)])
}

private func reply(_ rows: [NSAppleEventDescriptor]) -> NSAppleEventDescriptor {
    .scriptList([text("tabs")] + rows)
}

// MARK: - R2-4: which tabs are web players

final class WebPlayerHostTests: XCTestCase {
    func testOnlyTheServicesOwnHostsOverHTTPSCount() {
        XCTAssertEqual(WebService(pageURL: "https://music.amazon.de/albums/x"), .amazonMusic)
        XCTAssertEqual(WebService(pageURL: "https://music.amazon.co.jp/"), .amazonMusic)
        XCTAssertEqual(WebService(pageURL: "https://MUSIC.YOUTUBE.COM/watch?v=1"), .youTubeMusic)
        for lookalike in [
            "https://music.amazon.attacker.example/", "https://music.amazon.com.evil.net/",
            "https://www.google.com/search?q=soundcloud.com/x", "https://notsoundcloud.com/x",
            "https://soundcloud.com.evil.net/x", "https://evil.example/https://music.youtube.com/",
            "http://music.youtube.com/watch?v=1", "https://music.youtube.com@evil.example/",
        ] {
            XCTAssertNil(WebService(pageURL: lookalike), lookalike)
        }
    }

    func testTheTabFilterMatchesAddressPrefixesNotSubstrings() {
        let condition = WebPlayerScript.matchCondition
        XCTAssertFalse(condition.contains("contains"), "no substring match on the address")
        XCTAssertTrue(condition.contains(#"tabURL starts with "https://music.youtube.com/""#))
        XCTAssertTrue(condition.contains(#"tabURL starts with "https://soundcloud.com/""#))
        for host in WebService.amazonHosts {
            XCTAssertTrue(condition.contains(#"tabURL starts with "https://\#(host)/""#), host)
        }
        XCTAssertFalse(condition.contains(#""https://music.amazon.""#))
        // Every prefix is one Swift recognises as the same service.
        for service in WebService.allCases {
            for prefix in service.urlPrefixes {
                XCTAssertEqual(WebService(pageURL: prefix + "x"), service, prefix)
            }
        }
    }

    func testThePageScriptReadsNothingOnOtherPages() throws {
        let script = WebPlayerScript.readJavaScript
        let bail = try XCTUnwrap(script.range(of: "if(!svc){return JSON.stringify({service:'',title:'',state:'none'});}"))
        let media = try XCTUnwrap(script.range(of: "mediaSession"))
        XCTAssertLessThan(bail.lowerBound, media.lowerBound, "it returns before reading media metadata or elements")
        XCTAssertTrue(script.contains("location.protocol==='https:'"))
        XCTAssertFalse(script.contains(#"/^music\.amazon\./"#), "no open-ended Amazon pattern")
        for host in WebService.amazonHosts { XCTAssertTrue(script.contains("'\(host)'"), host) }
    }

    func testAReadingFromAnotherPageIsNeverFollowed() {
        let memory = WebTabMemory()
        let backend = WebPlayerBackend(browser: .chrome, memory: memory)
        let stray = reply([
            row(key: "1", url: "https://www.google.com/search?q=soundcloud.com/x", json: playingJSON(service: "")),
            row(key: "2", url: "https://music.amazon.attacker.example/", json: playingJSON(service: "amazonMusic")),
        ])
        guard case .value(let snapshot) = backend.parseRead(stray) else { return XCTFail("A reply") }
        XCTAssertEqual(snapshot.state, .stopped)
        XCTAssertNil(snapshot.track)
        XCTAssertNil(memory.entry(for: .chrome))

        let mismatch = reply([row(key: "3", url: "https://music.youtube.com/", json: playingJSON(service: "soundcloud"))])
        XCTAssertNil(WebPlayerBackend.tab(mismatch.scriptItems[1])?.reading,
                     "a page that moved to another service since its address was read")
        let good = reply([row(key: "4", url: "https://music.amazon.de/x", json: playingJSON(service: "amazonMusic"))])
        XCTAssertEqual(backend.parseRead(good).value?.track?.title, "Song")
        XCTAssertEqual(memory.entry(for: .chrome), WebTabMemory.Entry(tabKey: "4", service: .amazonMusic))
    }
}

// MARK: - R2-3: commands reach only the followed tab, and only a web player

final class WebPlayerCommandTests: XCTestCase {
    func testThePageCommandChecksTheHostBeforeTouchingAnything() throws {
        for action in [WebPlayerScript.Action.playPause, .next, .previous, .seek(10)] {
            let script = WebPlayerScript.commandJavaScript(action)
            let guardLine = try XCTUnwrap(script.range(of: "if(!svc||(want&&svc!==want)){return 'none';}"))
            let firstClick = try XCTUnwrap(script.range(of: "click("))
            XCTAssertLessThan(guardLine.lowerBound, firstClick.lowerBound)
            XCTAssertFalse(script.contains("media.play()"), "no fallback onto any media element")
            XCTAssertFalse(script.contains("media.pause()"))
            XCTAssertTrue(script.contains("if(!amz){return null;}"), "buttons by label only on Amazon Music")
        }
        XCTAssertTrue(WebPlayerScript.commandJavaScript(.next, expecting: .soundCloud).contains("want='soundcloud'"))
        XCTAssertTrue(WebPlayerScript.commandJavaScript(.next).contains("want=''"))
    }

    func testChromiumFindsTheFollowedTabByItsIDAndChecksItsAddress() {
        let source = WebPlayerScript.command(.chrome, tab: "22", service: .soundCloud, action: .playPause).source
        XCTAssertTrue(source.contains(#"set targetKey to "22""#))
        XCTAssertTrue(source.contains("set theTab to tab id tabID of theWindow"))
        XCTAssertTrue(source.contains("tell theTab to set pageReply to execute javascript"))
        XCTAssertFalse(source.contains("tab t of theWindow"), "never by a tab's place")
        XCTAssertTrue(source.contains(#"tabURL starts with "https://soundcloud.com/""#), "the address is checked again")
        XCTAssertFalse(source.contains("music.youtube.com/\""), "against the service it was followed in only")
        XCTAssertFalse(source.contains(#"(targetKey is "")"#))
        XCTAssertTrue(source.contains(#"return "none""#), "a tab that is gone or moved on is left alone")
    }

    func testSafariFindsTheFollowedTabsWindowByItsID() {
        let source = WebPlayerScript.command(.safari, tab: "4567:3", service: .youTubeMusic, action: .next).source
        XCTAssertTrue(source.contains("set theWindow to window id 4567"))
        XCTAssertTrue(source.contains("set t to 3"))
        XCTAssertTrue(source.contains("set tabURL to URL of tab t of theWindow"))
        XCTAssertFalse(source.contains("repeat with w"), "never by a window's place in the window order")
        XCTAssertTrue(source.contains(#"tabURL starts with "https://music.youtube.com/""#))
        XCTAssertTrue(source.contains("do JavaScript"))

        let garbled = WebPlayerScript.command(.safari, tab: "front:x", service: .youTubeMusic, action: .playPause).source
        XCTAssertFalse(garbled.contains("do JavaScript"), "an unreadable key reaches no tab at all")
        XCTAssertEqual(WebPlayerScript.SafariTabKey("12:4"), WebPlayerScript.SafariTabKey("12:4"))
        XCTAssertNil(WebPlayerScript.SafariTabKey("12:0"))
        XCTAssertNil(WebPlayerScript.SafariTabKey("12"))
        XCTAssertNil(WebPlayerScript.SafariTabKey("a:1"))
    }

    func testTheBackendAimsAtTheServiceItFollowed() {
        let memory = WebTabMemory()
        memory.remember(WebTabMemory.Entry(tabKey: "9", service: .amazonMusic), for: .brave)
        let backend = WebPlayerBackend(browser: .brave, memory: memory)
        for script in [backend.commandScript(.playPause), backend.seekScript(to: 30)] {
            XCTAssertTrue(script.source.contains(#"set targetKey to "9""#))
            XCTAssertTrue(script.source.contains(#"tabURL starts with "https://music.amazon.de/""#))
            XCTAssertFalse(script.source.contains(#"tabURL starts with "https://soundcloud.com/""#))
            XCTAssertTrue(script.source.contains("want=\\'amazonMusic\\'") || script.source.contains("want='amazonMusic'"))
        }
    }
}

// MARK: - R2-15: one hung tab, and numbers that are not numbers

final class WebPlayerRobustnessTests: XCTestCase {
    func testEachTabHasItsOwnTimeoutThatTheConsentWaitLeavesAlone() {
        for browser in [PlayerKind.safari, .chrome] {
            let source = WebPlayerScript.read(browser, name: "x").source
            XCTAssertTrue(source.contains("set pageTimeout to \(WebPlayerScript.pageTimeout)"))
            XCTAssertTrue(source.contains("with timeout of pageTimeout seconds"))
            let consent = PlayerScriptSource.withTimeout(PlayerScriptSource.consentTimeout, source)
            XCTAssertTrue(consent.contains("with timeout of pageTimeout seconds"), "the consent wait does not stretch a tab")
            XCTAssertTrue(consent.contains("with timeout of 120 seconds"))
            let rethrow = source.split(separator: "\n").first { $0.hasPrefix("if errorNumber is in {") } ?? ""
            XCTAssertFalse(rethrow.isEmpty)
            XCTAssertFalse(rethrow.contains("\(AppleEventCode.timedOut)"), "a tab timing out fails that tab alone")
            XCTAssertTrue(rethrow.contains("\(AppleEventCode.notPermitted)"))
            XCTAssertTrue(source.contains("if timedOutTabs < \(WebPlayerScript.timedOutTabLimit) then"))
        }
    }

    func testAHungTabLeavesTheOthersReadable() {
        let backend = WebPlayerBackend(browser: .chrome, memory: WebTabMemory())
        let hung = row(key: "1", url: "https://soundcloud.com/a", code: number(Double(AppleEventCode.timedOut)),
                       message: "AppleEvent timed out.")
        let playing = row(key: "2", url: "https://music.youtube.com/", json: playingJSON(title: "Still Read"))
        XCTAssertEqual(backend.parseRead(reply([hung, playing])).value?.track?.title, "Still Read")
        guard case .value(let alone) = backend.parseRead(reply([hung])) else { return XCTFail("Not a failure") }
        XCTAssertEqual(alone.state, .stopped)
    }

    func testNumbersThatAreNotNumbersNeverTrap() {
        XCTAssertNil(Int(scriptValue: .nan))
        XCTAssertNil(Int(scriptValue: .infinity))
        XCTAssertNil(Int(scriptValue: -.infinity))
        XCTAssertNil(Int(scriptValue: 1e300))
        XCTAssertNil(Int(scriptValue: nil))
        XCTAssertEqual(Int(scriptValue: 3.9), 3)
        XCTAssertEqual(Int(scriptValue: -1712), -1712)

        for odd in [Double.nan, .infinity, -.infinity, 1e300] {
            let tab = WebPlayerBackend.tab(row(key: "1", window: number(odd), url: "https://music.youtube.com/",
                                               json: playingJSON(), code: number(odd)))
            XCTAssertEqual(tab?.window, 1)
            XCTAssertNil(tab?.failure)
        }
        let queue = NSAppleEventDescriptor.scriptList([text("ok"), .scriptList([
            .scriptList([text("AB12"), text("Song"), text("Artist"), number(.nan), number(.infinity)]),
            .scriptList([text("CD34"), text("Next"), text("Artist"), number(4), number(4)]),
        ])])
        XCTAssertEqual(AppleMusicSearch.parseQueue(queue).map(\.title), ["Next"])
        XCTAssertEqual(AppleMusicSearch.parseQueue(queue).first?.isCurrent, true)
    }
}
