import AppKit

/// A music service Lunalyr can read in a browser tab.
enum WebService: String, CaseIterable, Sendable {
    case youTubeMusic = "youtubeMusic"
    case soundCloud = "soundcloud"
    case amazonMusic = "amazonMusic"

    var displayName: String {
        switch self {
        case .youTubeMusic: return "YouTube Music"
        case .soundCloud: return "SoundCloud"
        case .amazonMusic: return "Amazon Music"
        }
    }

    /// The service a page belongs to, by its exact host: music.youtube.com,
    /// soundcloud.com (and its subdomains), music.amazon.<any country>.
    init?(pageURL text: String) {
        guard let host = URLComponents(string: text.trimmingCharacters(in: .whitespacesAndNewlines))?.host?.lowercased(),
              let scheme = URLComponents(string: text)?.scheme?.lowercased(), scheme == "https" || scheme == "http"
        else { return nil }
        if host == "music.youtube.com" {
            self = .youTubeMusic
        } else if host == "soundcloud.com" || host.hasSuffix(".soundcloud.com") {
            self = .soundCloud
        } else if host.hasPrefix("music.amazon."), host.split(separator: ".").count >= 3 {
            self = .amazonMusic
        } else {
            return nil
        }
    }

    /// A search on the service's own site, for the desktop player's search box.
    func searchURL(_ query: String) -> URL? {
        var components: URLComponents
        switch self {
        case .youTubeMusic:
            components = URLComponents(string: "https://music.youtube.com/search")!
            components.queryItems = [URLQueryItem(name: "q", value: query)]
        case .soundCloud:
            components = URLComponents(string: "https://soundcloud.com/search")!
            components.queryItems = [URLQueryItem(name: "q", value: query)]
        case .amazonMusic:
            let allowed = CharacterSet.urlPathAllowed.subtracting(CharacterSet(charactersIn: "/?#"))
            guard let escaped = query.addingPercentEncoding(withAllowedCharacters: allowed) else { return nil }
            return URL(string: "https://music.amazon.com/search/\(escaped)")
        }
        return components.url
    }
}

/// What the page script (`WebPlayerScript.read`) found in one tab.
struct WebReading: Equatable, Sendable {
    var service: WebService?
    var title: String
    var artist: String
    var album: String
    var artworkURL: URL?
    /// Seconds; nil while the page does not know yet.
    var duration: Double?
    var position: Double?
    var state: PlayerSnapshot.State
    var isAdvert: Bool

    var hasTrack: Bool { !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && state != .stopped }

    /// The page script's JSON. Nil for anything else, including the
    /// `{"error": …}` the script returns when the page threw.
    init?(json text: String) {
        guard let data = text.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              object["error"] == nil else { return nil }
        func string(_ key: String) -> String {
            (object[key] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        }
        func seconds(_ key: String) -> Double? {
            guard let value = (object[key] as? NSNumber)?.doubleValue, value.isFinite, value >= 0 else { return nil }
            return value
        }
        service = WebService(rawValue: string("service"))
        title = string("title")
        artist = Self.cleanArtist(string("artist"))
        album = string("album")
        artworkURL = Self.coverURL(string("artwork"))
        let length = seconds("duration").flatMap { $0 > 0 ? $0 : nil }
        duration = length
        position = seconds("position").map { value in length.map { min(value, $0) } ?? value }
        switch string("state") {
        case "playing": state = .playing
        case "paused": state = .paused
        default: state = .stopped
        }
        isAdvert = (object["ad"] as? Bool) ?? false
    }

    init(service: WebService?, title: String, artist: String, album: String = "", artworkURL: URL? = nil,
         duration: Double?, position: Double?, state: PlayerSnapshot.State, isAdvert: Bool = false) {
        self.service = service
        self.title = title
        self.artist = artist
        self.album = album
        self.artworkURL = artworkURL
        self.duration = duration
        self.position = position
        self.state = state
        self.isAdvert = isAdvert
    }

    /// YouTube names an artist's auto-generated channel "Name - Topic".
    static func cleanArtist(_ text: String) -> String {
        let suffix = " - Topic"
        return text.hasSuffix(suffix) ? String(text.dropLast(suffix.count)) : text
    }

    /// Covers only over HTTPS, as for Spotify's.
    static func coverURL(_ text: String) -> URL? {
        guard !text.isEmpty, let components = URLComponents(string: text), components.scheme?.lowercased() == "https",
              components.host != nil else { return nil }
        return components.url
    }
}

/// One music tab the browser reported.
struct WebTab: Equatable, Sendable {
    /// The tab, as commands address it again: Chromium's tab id, or
    /// Safari's "window id:tab index".
    var key: String
    /// The window's place in the browser's window order, frontmost first (1).
    var window: Int
    /// The tab is the one shown in its window.
    var isActive: Bool
    var url: String
    var reading: WebReading?
    /// The error the page script met, when it could not run.
    var failure: Failure?

    struct Failure: Equatable, Sendable {
        var code: Int
        var message: String
    }

    /// The script could not run because the browser does not allow
    /// JavaScript from Apple Events.
    var isJavaScriptOff: Bool {
        failure.map { WebPlayerBackend.isJavaScriptDisabled(code: $0.code, message: $0.message) } ?? false
    }
}

/// Which tab each browser was last followed in, so the same one stays
/// followed while several play, and transport reaches it. Shared by the
/// backends (which hold no state themselves), behind a lock.
final class WebTabMemory: @unchecked Sendable {
    static let shared = WebTabMemory()

    struct Entry: Equatable, Sendable {
        var tabKey: String
        var service: WebService
    }

    private let lock = NSLock()
    private var entries: [PlayerKind: Entry] = [:]

    func entry(for kind: PlayerKind) -> Entry? {
        lock.lock(); defer { lock.unlock() }
        return entries[kind]
    }

    func remember(_ entry: Entry?, for kind: PlayerKind) {
        lock.lock(); defer { lock.unlock() }
        entries[kind] = entry
    }
}

/// YouTube Music, SoundCloud and Amazon Music in a browser. The browser's
/// own AppleScript dictionary lists the tabs; a small script run in each
/// music tab reads what the page plays (the page's Media Session metadata,
/// its media element, and the service's own player bar where those fall
/// short). Commands click the page's own buttons.
///
/// Running a script in a page needs "Allow JavaScript from Apple Events",
/// which every browser leaves off. When it is off the reply says so
/// (`PlayerResponse.javaScriptOff`) and the app explains how to turn it on.
/// Lunalyr never changes that setting.
struct WebPlayerBackend: PlayerBackend {
    let browser: PlayerKind
    var memory: WebTabMemory = .shared

    var kind: PlayerKind { browser }
    /// What someone plays on the web is not sent to Apple's catalogue.
    var usesAppleCatalogue: Bool { false }
    var readScript: PlayerScript { WebPlayerScript.read(browser, name: "web.\(browser.rawValue).read") }
    /// The same reading: one page script answers state, track and position.
    var positionScript: PlayerScript { WebPlayerScript.read(browser, name: "web.\(browser.rawValue).position") }
    /// Covers come by address with the reading.
    var artworkScripts: [PlayerScript] { [] }

    func commandScript(_ command: TransportCommand) -> PlayerScript {
        let action: WebPlayerScript.Action
        switch command {
        case .playPause: action = .playPause
        case .next: action = .next
        case .previous: action = .previous
        }
        return WebPlayerScript.command(browser, tab: memory.entry(for: browser)?.tabKey, action: action)
    }

    func seekScript(to seconds: TimeInterval) -> PlayerScript {
        WebPlayerScript.command(browser, tab: memory.entry(for: browser)?.tabKey, action: .seek(seconds))
    }

    func parseRead(_ reply: NSAppleEventDescriptor) -> PlayerResponse<PlayerSnapshot> {
        switch choose(reply) {
        case .notRunning: return .notRunning
        case .javaScriptOff: return .javaScriptOff
        case .failed(let code): return .failed(code)
        case .blocked(let access): return .blocked(access)
        case .value(let tab):
            guard let tab, let reading = tab.reading else { return .value(PlayerSnapshot(state: .stopped)) }
            return .value(Self.snapshot(reading))
        }
    }

    func parsePosition(_ reply: NSAppleEventDescriptor, at time: TimeInterval) -> PlayerResponse<PositionSample> {
        switch choose(reply) {
        case .notRunning: return .notRunning
        case .javaScriptOff: return .javaScriptOff
        case .failed(let code): return .failed(code)
        case .blocked(let access): return .blocked(access)
        case .value(let tab):
            guard let tab, let reading = tab.reading else {
                return .value(PositionSample(state: .stopped, trackID: nil, position: nil, time: time))
            }
            return .value(PositionSample(state: reading.state, trackID: Self.trackID(reading),
                                         position: reading.duration == nil ? nil : reading.position, time: time))
        }
    }

    func parseArtwork(_ reply: NSAppleEventDescriptor) -> PlayerResponse<ArtworkReply> {
        .value(ArtworkReply(trackID: nil, reading: .none))
    }

    /// Browsers announce nothing.
    func snapshot(fromNotification userInfo: [AnyHashable: Any]?) -> PlayerSnapshot? { nil }

    /// Reads the reply, picks the tab to follow and remembers it.
    private func choose(_ reply: NSAppleEventDescriptor) -> PlayerResponse<WebTab?> {
        let (items, word) = status(of: reply)
        switch word {
        case "notRunning": return .notRunning
        case "tabs": break
        default: return .failed(-1)
        }
        let tabs = items.dropFirst().compactMap(Self.tab)
        let previous = memory.entry(for: browser)?.tabKey
        guard let chosen = Self.chooseTab(tabs, following: previous) else {
            if previous != nil { memory.remember(nil, for: browser) }
            return Self.allJavaScriptOff(tabs) ? .javaScriptOff : .value(nil)
        }
        if let service = chosen.reading?.service ?? WebService(pageURL: chosen.url) {
            memory.remember(WebTabMemory.Entry(tabKey: chosen.key, service: service), for: browser)
        }
        return .value(chosen)
    }

    // MARK: - Parsing, kept static for tests

    /// The tab to follow among several: one that plays, else one with a
    /// song loaded (paused). Within either, the tab followed before stays
    /// followed, so two tabs never take turns; otherwise the frontmost
    /// window's shown tab, then the next window's, and so on.
    static func chooseTab(_ tabs: [WebTab], following previous: String?) -> WebTab? {
        let loaded = tabs.filter { $0.reading?.hasTrack == true }
        let playing = loaded.filter { $0.reading?.state == .playing }
        func best(_ candidates: [WebTab]) -> WebTab? {
            if let previous, let same = candidates.first(where: { $0.key == previous }) { return same }
            return candidates.enumerated().min { lhs, rhs in
                (lhs.element.window, lhs.element.isActive ? 0 : 1, lhs.offset)
                    < (rhs.element.window, rhs.element.isActive ? 0 : 1, rhs.offset)
            }?.element
        }
        return best(playing) ?? best(loaded)
    }

    /// Every music tab failed because the browser does not allow JavaScript
    /// from Apple Events. With no music tab open nothing is known, and
    /// nothing is said.
    static func allJavaScriptOff(_ tabs: [WebTab]) -> Bool {
        let failed = tabs.filter { $0.reading == nil }
        return !failed.isEmpty && failed.count == tabs.count && failed.allSatisfy(\.isJavaScriptOff)
    }

    /// Whether a page script error is the browser refusing to run it. Safari
    /// says "You must enable the 'Allow JavaScript from Apple Events' option
    /// …"; Chrome, Brave, Edge and Arc say "Executing JavaScript through
    /// AppleScript is turned off …". Both name JavaScript and Apple's
    /// scripting, which stay in English in every language.
    static func isJavaScriptDisabled(code: Int, message: String) -> Bool {
        let text = message.lowercased()
        guard text.contains("javascript") else { return false }
        return text.contains("apple event") || text.contains("applescript") || text.contains("apple-event")
            || text.contains("allow javascript") || text.contains("turned off")
    }

    /// One `{key, window, active, url, reply, error number, error message}`.
    static func tab(_ item: NSAppleEventDescriptor) -> WebTab? {
        let fields = item.scriptItems
        guard fields.count >= 7, let key = fields[0].scriptText, !key.isEmpty, let url = fields[3].scriptText else { return nil }
        let window = Int(fields[1].scriptNumber ?? 1)
        let active = fields[2].booleanValue
        let reply = fields[4].scriptText ?? ""
        let code = Int(fields[5].scriptNumber ?? 0)
        let message = fields[6].scriptText ?? ""
        var reading = WebReading(json: reply)
        if reading != nil, reading?.service == nil { reading?.service = WebService(pageURL: url) }
        let failure = code == 0 && message.isEmpty ? nil : WebTab.Failure(code: code, message: message)
        return WebTab(key: key, window: window, isActive: active, url: url, reading: reading, failure: failure)
    }

    /// What the page plays. A page that cannot say how long the song is
    /// yet reads as a stream until it can, so lyrics are looked up once,
    /// with the length.
    static func snapshot(_ reading: WebReading) -> PlayerSnapshot {
        guard reading.hasTrack else { return PlayerSnapshot(state: .stopped) }
        let duration = reading.duration ?? 0
        let isStream = reading.isAdvert || duration <= 0
        let track = NowPlayingMonitor.Track(
            title: reading.title, artist: reading.artist, duration: isStream ? 0 : duration, genre: nil,
            album: isStream || reading.album.isEmpty ? nil : reading.album,
            persistentID: trackID(reading), isStream: isStream, isAdvert: reading.isAdvert)
        return PlayerSnapshot(state: reading.state, track: track,
                              position: isStream ? nil : reading.position, artworkURL: reading.artworkURL)
    }

    /// Pages have no stable track identifier Lunalyr can rely on (the
    /// address changes as someone browses), so the song itself is the id.
    static func trackID(_ reading: WebReading) -> String {
        "\(reading.service?.rawValue ?? "web")|\(reading.title)|\(reading.artist)"
    }
}

/// The AppleScript and page scripts behind `WebPlayerBackend`.
enum WebPlayerScript {
    enum Action: Equatable {
        case playPause, next, previous
        case seek(TimeInterval)

        var name: String {
            switch self {
            case .playPause: return "playpause"
            case .next: return "next"
            case .previous: return "previous"
            case .seek: return "seek"
            }
        }
    }

    /// Lists every tab whose address may be a web player, runs the page
    /// script in each, and returns `{"tabs", {key, window, active, url,
    /// reply, error number, error message}, …}`. Tabs that are not music are
    /// never sent a script. A refused or vanished browser ends the script;
    /// any other error a page script meets -- the browser not allowing
    /// JavaScript from Apple Events above all -- is returned with its tab.
    static func read(_ browser: PlayerKind, name: String) -> PlayerScript {
        let run = runInTab(browser, javaScript: readJavaScript)
        let body: String
        if browser == .safari {
            body = """
            set musicTabs to {}
            repeat with w from 1 to (count of windows)
            set tabURLs to {}
            set activeIndex to 0
            set windowKey to ""
            try
            set theWindow to window w
            set windowKey to (id of theWindow) as text
            set tabURLs to URL of every tab of theWindow
            set activeIndex to index of current tab of theWindow
            on error number errorNumber
            if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
            end try
            repeat with t from 1 to (count of tabURLs)
            set tabURL to item t of tabURLs
            if tabURL is not missing value then
            set tabURL to tabURL as text
            if \(matchCondition) then
            \(run)
            set end of musicTabs to {windowKey & ":" & t, w, (t = activeIndex), tabURL, pageReply, failureNumber, failureMessage}
            end if
            end if
            end repeat
            end repeat
            return {"tabs"} & musicTabs
            """
        } else {
            body = """
            set musicTabs to {}
            repeat with w from 1 to (count of windows)
            set tabURLs to {}
            set tabIDs to {}
            set activeKey to ""
            try
            set theWindow to window w
            set tabURLs to URL of every tab of theWindow
            set tabIDs to id of every tab of theWindow
            set activeKey to (id of active tab of theWindow) as text
            on error number errorNumber
            if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
            end try
            repeat with t from 1 to (count of tabURLs)
            set tabURL to item t of tabURLs
            if tabURL is not missing value and t ≤ (count of tabIDs) then
            set tabURL to tabURL as text
            if \(matchCondition) then
            set tabKey to (item t of tabIDs) as text
            \(run)
            set end of musicTabs to {tabKey, w, (tabKey = activeKey), tabURL, pageReply, failureNumber, failureMessage}
            end if
            end if
            end repeat
            end repeat
            return {"tabs"} & musicTabs
            """
        }
        return PlayerScript(name: name, source: PlayerScriptSource.query(browser.bundleID, body))
    }

    /// Runs `action` in the followed tab (`tab`), or, before any tab was
    /// followed, in the first music tab. Not cached: it names the tab.
    static func command(_ browser: PlayerKind, tab: String?, action: Action) -> PlayerScript {
        let run = runInTab(browser, javaScript: commandJavaScript(action), keepsReply: false)
        let target = ScriptText.quoted(tab ?? "")
        let body: String
        if browser == .safari {
            body = """
            set targetKey to \(target)
            repeat with w from 1 to (count of windows)
            set tabURLs to {}
            set windowKey to ""
            try
            set theWindow to window w
            set windowKey to (id of theWindow) as text
            set tabURLs to URL of every tab of theWindow
            on error number errorNumber
            if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
            end try
            repeat with t from 1 to (count of tabURLs)
            set tabURL to item t of tabURLs
            if tabURL is not missing value then
            set tabURL to tabURL as text
            if (targetKey is (windowKey & ":" & t)) or (targetKey is "" and \(matchCondition)) then
            \(run)
            return "ok"
            end if
            end if
            end repeat
            end repeat
            """
        } else {
            body = """
            set targetKey to \(target)
            repeat with w from 1 to (count of windows)
            set tabURLs to {}
            set tabIDs to {}
            try
            set theWindow to window w
            set tabURLs to URL of every tab of theWindow
            set tabIDs to id of every tab of theWindow
            on error number errorNumber
            if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
            end try
            repeat with t from 1 to (count of tabIDs)
            set tabKey to (item t of tabIDs) as text
            set tabURL to ""
            if t ≤ (count of tabURLs) and item t of tabURLs is not missing value then set tabURL to (item t of tabURLs) as text
            if (targetKey is tabKey) or (targetKey is "" and \(matchCondition)) then
            \(run)
            return "ok"
            end if
            end repeat
            end repeat
            """
        }
        return PlayerScript(name: "web.\(browser.rawValue).\(action.name)",
                            source: PlayerScriptSource.command(browser.bundleID, body), cacheable: false)
    }

    /// AppleScript that tells a tab address apart cheaply; the page script
    /// and `WebService(pageURL:)` decide properly.
    static let matchCondition = #"(tabURL contains "://music.youtube.com/" or tabURL contains "soundcloud.com/" or tabURL contains "://music.amazon.")"#

    /// Runs the page script in tab `t` of `theWindow`, leaving `reply`,
    /// `failureNumber` and `failureMessage`. Errors that end the whole
    /// exchange (refused, quit, timed out) are raised again.
    private static func runInTab(_ browser: PlayerKind, javaScript: String, keepsReply: Bool = true) -> String {
        let literal = appleScriptLiteral(javaScript)
        let call = browser == .safari
            ? "set pageReply to do JavaScript \(literal) in tab t of theWindow"
            : "tell tab t of theWindow to set pageReply to execute javascript \(literal)"
        let rethrown = "{\(AppleEventCode.notPermitted), \(AppleEventCode.wouldRequireConsent), \(AppleEventCode.processNotFound), \(AppleEventCode.connectionInvalid), \(AppleEventCode.timedOut)}"
        guard keepsReply else {
            return """
            \(call)
            """
        }
        return """
        set pageReply to ""
        set failureNumber to 0
        set failureMessage to ""
        try
        \(call)
        on error errorMessage number errorNumber
        if errorNumber is in \(rethrown) then error errorMessage number errorNumber
        set failureNumber to errorNumber
        set failureMessage to errorMessage
        end try
        if pageReply is missing value then set pageReply to ""
        set pageReply to pageReply as text
        """
    }

    /// A JavaScript source as an AppleScript string literal, on one line.
    static func appleScriptLiteral(_ javaScript: String) -> String {
        let oneLine = javaScript.split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .joined(separator: " ")
        let escaped = oneLine.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        return "\"\(escaped)\""
    }

    /// Reads one page. Each statement ends in a semicolon and there are no
    /// line comments: the source is joined onto one line.
    static let readJavaScript = #"""
    (function(){
    try{
    var h=location.hostname;
    var svc=h==='music.youtube.com'?'youtubeMusic':((h==='soundcloud.com'||/\.soundcloud\.com$/.test(h))?'soundcloud':(/^music\.amazon\./.test(h)?'amazonMusic':''));
    var q=function(s){try{return document.querySelector(s);}catch(e){return null;}};
    var tx=function(e){return e?String(e.getAttribute('title')||e.textContent||'').trim():'';};
    var clock=function(s){if(!s){return null;}var p=String(s).trim().split(':');if(p.length<2||p.length>3){return null;}var v=0;for(var i=0;i<p.length;i++){var n=parseInt(p[i],10);if(isNaN(n)){return null;}v=v*60+n;}return v;};
    var r={service:svc,title:'',artist:'',album:'',artwork:'',duration:-1,position:-1,state:'',ad:false};
    var md=null;try{md=navigator.mediaSession&&navigator.mediaSession.metadata;}catch(e){}
    if(md){r.title=String(md.title||'');r.artist=String(md.artist||'');r.album=String(md.album||'');var aw=md.artwork||[],best=-1;for(var i=0;i<aw.length;i++){var z=parseInt(String(aw[i].sizes||'0').split('x')[0],10)||0;if(z>=best&&aw[i].src){best=z;r.artwork=String(aw[i].src);}}}
    var media=null,els=document.querySelectorAll('video,audio');
    for(var j=0;j<els.length;j++){var el=els[j];if(isFinite(el.duration)&&el.duration>0&&(!media||(media.paused&&!el.paused))){media=el;}}
    if(media){r.duration=media.duration;r.position=media.currentTime;r.state=media.paused?'paused':'playing';}
    if(svc==='youtubeMusic'){
    if(!r.title){r.title=tx(q('ytmusic-player-bar .title'));}
    if(!r.artist){r.artist=tx(q('ytmusic-player-bar .byline')).split('•')[0].trim();}
    if(r.duration<0){var ti=tx(q('ytmusic-player-bar .time-info')).split('/');if(ti.length===2){var a=clock(ti[0]),b=clock(ti[1]);if(b){r.duration=b;r.position=a||0;}}}
    var mp=q('#movie_player');r.ad=!!(mp&&mp.classList&&mp.classList.contains('ad-showing'));
    }
    if(svc==='soundcloud'){
    var tl=q('.playbackSoundBadge__titleLink');if(!r.title&&tl){r.title=String(tl.getAttribute('title')||tx(tl));}
    var al=q('.playbackSoundBadge__lightLink');if(!r.artist&&al){r.artist=String(al.getAttribute('title')||tx(al));}
    var pw=q('.playbackTimeline__progressWrapper'),mx=pw?parseFloat(pw.getAttribute('aria-valuemax')):NaN,nw=pw?parseFloat(pw.getAttribute('aria-valuenow')):NaN;
    var dt=clock(tx(q('.playbackTimeline__duration span[aria-hidden]'))),pt=clock(tx(q('.playbackTimeline__timePassed span[aria-hidden]')));
    if(r.duration<0){if(dt){r.duration=dt;r.position=(mx>0&&nw>=0)?nw/mx*dt:(pt||0);}else if(mx>0){var k=mx>20000?1000:1;r.duration=mx/k;r.position=(nw>=0?nw:0)/k;}}
    var pc=q('.playControls__play');if(pc){r.state=pc.classList.contains('playing')?'playing':'paused';}
    var av=q('.playbackSoundBadge__avatar span[style*=background-image]');if(!r.artwork&&av){var m2=/url\(["']?([^"')]+)/.exec(av.getAttribute('style')||'');if(m2){r.artwork=m2[1].replace('-t50x50.','-t500x500.');}}
    }
    if(r.duration<0){var sl=q('[role=slider][aria-valuemax]');if(sl){var smx=parseFloat(sl.getAttribute('aria-valuemax')),snw=parseFloat(sl.getAttribute('aria-valuenow'));if(smx>0){var kk=smx>20000?1000:1;r.duration=smx/kk;r.position=(snw>=0?snw:0)/kk;}}}
    if(!r.state){var ps='';try{ps=navigator.mediaSession.playbackState;}catch(e){}r.state=ps==='playing'?'playing':(ps==='paused'?'paused':'');}
    if(!r.state){r.state=r.title?'paused':'none';}
    if(!r.title){r.state='none';}
    return JSON.stringify(r);
    }catch(e){return JSON.stringify({error:String(e)});}
    })()
    """#

    /// Clicks the page's own button: YouTube Music's and SoundCloud's by
    /// their class, Amazon Music's (and anything else) by its label. A seek
    /// moves the media element, or SoundCloud's timeline.
    static func commandJavaScript(_ action: Action) -> String {
        let seconds: String
        if case .seek(let value) = action { seconds = PlayerScriptSource.number(value) } else { seconds = "0" }
        return #"""
        (function(){
        try{
        var a='\#(action.name)',sec=\#(seconds),h=location.hostname;
        var yt=h==='music.youtube.com',sc=(h==='soundcloud.com'||/\.soundcloud\.com$/.test(h));
        var q=function(s){try{return document.querySelector(s);}catch(e){return null;}};
        var click=function(e){if(e){e.click();return true;}return false;};
        var labelled=function(re){var bs=document.querySelectorAll('button,[role=button]');for(var i=0;i<bs.length;i++){var l=String(bs[i].getAttribute('aria-label')||bs[i].getAttribute('title')||'').trim();if(re.test(l)){return bs[i];}}return null;};
        var media=null,els=document.querySelectorAll('video,audio');
        for(var j=0;j<els.length;j++){var el=els[j];if(isFinite(el.duration)&&el.duration>0&&(!media||(media.paused&&!el.paused))){media=el;}}
        if(a==='playpause'){if(yt&&click(q('#play-pause-button'))){return 'ok';}if(sc&&click(q('.playControls__play'))){return 'ok';}if(media){if(media.paused){media.play();}else{media.pause();}return 'ok';}return click(labelled(/^(play|pause)$/i))?'ok':'none';}
        if(a==='next'){if(yt&&click(q('.next-button')))return 'ok';if(sc&&click(q('.skipControl__next')))return 'ok';return click(labelled(/^next/i))?'ok':'none';}
        if(a==='previous'){if(yt&&click(q('.previous-button')))return 'ok';if(sc&&click(q('.skipControl__previous')))return 'ok';return click(labelled(/^previous/i))?'ok':'none';}
        if(a==='seek'){
        var mp=q('#movie_player');if(yt&&mp&&typeof mp.seekTo==='function'){mp.seekTo(sec,true);return 'ok';}
        if(media){media.currentTime=sec;return 'ok';}
        var pw=sc?q('.playbackTimeline__progressWrapper'):null;
        if(pw){var mx=parseFloat(pw.getAttribute('aria-valuemax')),k=mx>20000?1000:1;if(mx>0){var rc=pw.getBoundingClientRect(),x=rc.left+rc.width*Math.min(1,Math.max(0,sec*k/mx)),y=rc.top+rc.height/2;['mousedown','mouseup','click'].forEach(function(n){pw.dispatchEvent(new MouseEvent(n,{bubbles:true,cancelable:true,clientX:x,clientY:y,view:window}));});return 'ok';}}
        return 'none';}
        return 'none';
        }catch(e){return 'error';}
        })()
        """#
    }
}

/// What to do about a browser that does not allow JavaScript from Apple
/// Events, in the words of its own menus. Lunalyr only ever says this; it
/// never changes the setting.
struct WebScriptingNotice: Equatable {
    var browser: PlayerKind
    /// The service last seen in it, when known.
    var service: WebService?

    var title: String {
        "Turn on JavaScript from Apple Events in \(browser.shortBrowserName)"
    }

    var message: String {
        let what = service?.displayName ?? "YouTube Music, SoundCloud and Amazon Music"
        return "Lunalyr reads \(what) by running a small script in the page, which \(browser.shortBrowserName) allows only with this setting on. \(Self.steps(for: browser)) Lunalyr never changes this setting itself."
    }

    /// Where the setting is, per browser.
    static func steps(for browser: PlayerKind) -> String {
        switch browser {
        case .safari:
            return "In Safari, choose Safari › Settings › Advanced and turn on “Show features for web developers”, then in Settings › Developer turn on “Allow JavaScript from Apple Events”."
        case .chrome:
            return "In Chrome, choose View › Developer › Allow JavaScript from Apple Events."
        case .brave:
            return "In Brave, choose View › Developer › Allow JavaScript from Apple Events."
        case .edge:
            return "In Microsoft Edge, choose View › Developer › Allow JavaScript from Apple Events."
        case .arc:
            return "In Arc, choose View › Developer › Allow JavaScript from Apple Events."
        default:
            return ""
        }
    }

    /// The browser named in a notice: the followed one whose music tabs
    /// could not be read, with what was last playing in it.
    @MainActor static func notice(for playing: NowPlayingMonitor, memory: WebTabMemory = .shared) -> WebScriptingNotice? {
        guard playing.current == nil, let browser = playing.webScriptingBlocked.first else { return nil }
        return WebScriptingNotice(browser: browser, service: memory.entry(for: browser)?.service)
    }
}

extension PlayerKind {
    /// "Chrome", not "Google Chrome", where a sentence names it twice.
    var shortBrowserName: String {
        switch self {
        case .chrome: return "Chrome"
        case .brave: return "Brave"
        case .edge: return "Edge"
        default: return displayName
        }
    }
}
