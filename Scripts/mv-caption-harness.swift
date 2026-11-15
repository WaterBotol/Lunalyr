// Measures how well a YouTube video's captions sync it to its song:
// captures the caption track from inside YouTube's own embedded player (a
// hidden, MUTED WKWebView: WebKit plays nothing audible without a click,
// and the player is told to mute on every state change), fetches the
// song's synced lyrics from LRCLIB, and prints what MusicVideoCaptionSync
// makes of them. Not part of the app or its tests (it needs the network).
//
// Build:
//   swiftc -O -parse-as-library Sources/MusicVideo/MusicVideoCaptionSync.swift \
//       Scripts/mv-caption-harness.swift -o Build/mv-caption-harness
// Run:
//   Build/mv-caption-harness <videoID> "<title>" "<artist>" <seconds> [captions-file]
// With a captions file (a timed-text reply saved earlier) nothing is captured.
// MV_HARNESS_SAVE=<dir> keeps each capture and the lyrics there.
// MV_HARNESS_VERBOSE=1 prints each matched line.

import AppKit
import WebKit

@main
enum Harness {
    static func main() {
        let args = CommandLine.arguments
        guard args.count >= 5, let duration = Double(args[4]) else {
            print("usage: mv-caption-harness <videoID> <title> <artist> <seconds> [captions-file]")
            exit(64)
        }
        let videoID = args[1], title = args[2], artist = args[3]
        let env = ProcessInfo.processInfo.environment
        let save = env["MV_HARNESS_SAVE"]
        guard let lyrics = lrclib(title: title, artist: artist, duration: duration, save: save.map { "\($0)/\(videoID).lrc" }) else {
            print("\(videoID)\tno synced lyrics on LRCLIB")
            exit(3)
        }
        let finish: (Data?, String) -> Void = { data, source in
            guard let data else { print("\(videoID)\tno captions captured (\(source))"); exit(2) }
            if let save, source != "file" { try? data.write(to: URL(fileURLWithPath: "\(save)/\(videoID).timedtext")) }
            report(videoID: videoID, title: title, data: data, source: source, lines: lyrics,
                   verbose: env["MV_HARNESS_VERBOSE"] == "1")
            exit(0)
        }
        if args.count > 5 {
            finish(FileManager.default.contents(atPath: args[5]), "file")
        }
        Capturer.run(videoID: videoID, language: "en", seconds: 40) { finish($0, $1) }
    }

    static func lrclib(title: String, artist: String, duration: Double, save: String?) -> [MusicVideoLyricLine]? {
        var components = URLComponents(string: "https://lrclib.net/api/get")!
        components.queryItems = [URLQueryItem(name: "track_name", value: title),
                                 URLQueryItem(name: "artist_name", value: artist),
                                 URLQueryItem(name: "duration", value: String(Int(duration.rounded())))]
        var request = URLRequest(url: components.url!)
        request.setValue("mv-caption-harness/1.0", forHTTPHeaderField: "User-Agent")
        let semaphore = DispatchSemaphore(value: 0)
        var body: Data?
        URLSession.shared.dataTask(with: request) { data, _, _ in body = data; semaphore.signal() }.resume()
        semaphore.wait()
        guard let body, let object = try? JSONSerialization.jsonObject(with: body) as? [String: Any],
              let synced = object["syncedLyrics"] as? String else { return nil }
        if let save { try? synced.write(toFile: save, atomically: true, encoding: .utf8) }
        return parseLRC(synced)
    }

    static func parseLRC(_ text: String) -> [MusicVideoLyricLine] {
        text.split(separator: "\n").compactMap { row in
            guard let close = row.firstIndex(of: "]"), row.first == "[" else { return nil }
            let stamp = row[row.index(after: row.startIndex)..<close].split(separator: ":")
            guard stamp.count == 2, let m = Double(stamp[0]), let s = Double(stamp[1]) else { return nil }
            let words = row[row.index(after: close)...].trimmingCharacters(in: .whitespaces)
            return words.isEmpty ? nil : MusicVideoLyricLine(start: m * 60 + s, text: words)
        }
    }

    static func report(videoID: String, title: String, data: Data, source: String, lines: [MusicVideoLyricLine],
                       verbose: Bool) {
        let cues = MusicVideoCaptionSync.cues(from: data)
        let candidates = MusicVideoCaptionSync.candidates(cues: cues, lines: lines)
        let result = MusicVideoCaptionSync.consensus(candidates)
        let offsets = result.matches.map(\.offset)
        let median = MusicVideoCaptionSync.median(offsets)
        let maxDev = result.matches.filter { abs($0.offset - median) <= MusicVideoCaptionSync.tolerance }
            .map { abs($0.offset - median) }.max() ?? 0
        let offset = result.offset.map { String(format: "%+.2f", $0) } ?? "nil"
        print("\(videoID)\t\(title)\ttrack=\(source)\tcues=\(cues.count)\tlyricLines=\(lines.count)\t"
              + "matched=\(result.matches.count)\tagreeing=\(result.agreeing)\t"
              + String(format: "spread(MAD)=%.2fs\tmaxDev=%.2fs\t", result.spread, maxDev)
              + "offset=\(offset)\tmedianAll=\(String(format: "%+.2f", median))")
        for segment in result.segments {
            print(String(format: "  segment song %.1f-%.1f s: offset %+.2f (%d lines)",
                         segment.songStart, segment.songEnd, segment.offset, segment.lines))
        }
        if verbose {
            for match in result.matches {
                print(String(format: "  %7.2f -> %7.2f  %+6.2f  %.2f  %@", match.songTime, match.videoTime, match.offset,
                             match.score, lines[match.line].text))
            }
        }
    }
}

/// A hidden, muted YouTube embed that hands back the first caption reply.
@MainActor
final class Capturer: NSObject, WKScriptMessageHandler {
    static var current: Capturer?
    let videoID: String
    let done: (Data?, String) -> Void
    var window: NSWindow?
    var finished = false

    init(videoID: String, done: @escaping (Data?, String) -> Void) {
        self.videoID = videoID
        self.done = done
    }

    nonisolated static func run(videoID: String, language: String, seconds: Double,
                                done: @escaping (Data?, String) -> Void) {
        MainActor.assumeIsolated {
            let app = NSApplication.shared
            app.setActivationPolicy(.prohibited)
            let capturer = Capturer(videoID: videoID, done: done)
            current = capturer
            capturer.start(language: language)
            DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { capturer.finish(nil, "timeout") }
            app.run()
        }
    }

    func start(language: String) {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.mediaTypesRequiringUserActionForPlayback = .audio
        configuration.allowsAirPlayForMediaPlayback = false
        configuration.userContentController.add(self, name: MusicVideoCaptionSync.messageName)
        configuration.userContentController.addUserScript(WKUserScript(source: MusicVideoCaptionSync.captureScript,
                                                                       injectionTime: .atDocumentStart,
                                                                       forMainFrameOnly: false))
        let window = NSWindow(contentRect: NSRect(x: -4000, y: -4000, width: 480, height: 270),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        let web = WKWebView(frame: NSRect(x: 0, y: 0, width: 480, height: 270), configuration: configuration)
        window.contentView = web
        window.alphaValue = 0
        window.orderFrontRegardless()
        self.window = window
        let page = """
        <!doctype html><html><body style="margin:0;background:#000"><div id="player"></div><script>
        var player = null;
        function onYouTubeIframeAPIReady() {
          player = new YT.Player('player', {width: '480', height: '270', videoId: '\(videoID)',
            host: 'https://www.youtube-nocookie.com',
            playerVars: {autoplay: 1, mute: 1, controls: 0, playsinline: 1, cc_load_policy: 1,
                         cc_lang_pref: '\(language)', origin: window.location.origin},
            events: {onReady: function (e) { e.target.mute(); e.target.playVideo(); },
                     onStateChange: function () { player.mute(); }}});
        }
        setInterval(function () { try { if (player && !player.isMuted()) player.mute(); } catch (e) {} }, 200);
        </script><script src="https://www.youtube.com/iframe_api"></script></body></html>
        """
        web.loadHTMLString(page, baseURL: URL(string: "https://local.verso.custom/")!)
    }

    func finish(_ data: Data?, _ source: String) {
        guard !finished else { return }
        finished = true
        window?.orderOut(nil)
        done(data, source)
    }

    nonisolated func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        MainActor.assumeIsolated {
            guard let body = message.body as? [String: Any], let url = body["url"] as? String,
                  let text = body["text"] as? String else { return }
            let origin = message.frameInfo.securityOrigin
            guard let capture = MusicVideoCaptionSync.capture(frameHost: origin.host, frameScheme: origin.protocol,
                                                              url: url, videoID: videoID) else { return }
            finish(Data(text.utf8), capture.automatic ? "asr:\(capture.language ?? "?")" : "manual:\(capture.language ?? "?")")
        }
    }
}
