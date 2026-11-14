import AppKit
import SwiftUI
@testable import VersoCustom

/// The lyric card window (Sources/Extras/LyricCardPopout.swift) against a
/// fixture song: its own lines, artwork and export, the scene's isolated
/// defaults and a private pasteboard. Nothing plays, nothing is fetched, and
/// the card is never made.
@MainActor
extension UISnapshotCatalogue {
    static var lyricCard: [UISnapshotCase] {
        let chrome = UISnapshotCase.Chrome.window(styleMask: [.titled, .closable, .resizable],
                                                  configure: { $0.title = "Lyric Card" })
        // The window's content size (LyricCardPopoutController) plus its title bar.
        let size = UISnapshotCase.Size.frame(CGSize(width: 700, height: 680 + 28))
        // Pulled taller (the window resizes), so the options' whole column shows.
        let tall = UISnapshotCase.Size.frame(CGSize(width: 700, height: 1240 + 28))
        return [
            UISnapshotCase("lyric-card/window", note: "three lines picked, MP4 and PNG", chrome: chrome, size: size,
                           view: { scene in LyricCardPopoutView(model: LyricCardFixture.model(scene)) }),
            UISnapshotCase("lyric-card/song-sound", note: "the song's sound on, a card just copied, every option showing",
                           chrome: chrome, size: tall,
                           view: { scene in
                               let model = LyricCardFixture.model(scene)
                               model.useSong = true
                               model.show("Copied MP4, PNG", tone: .success)
                               return LyricCardPopoutView(model: model)
                           }),
            UISnapshotCase("lyric-card/making", note: "a card being made: the bar and Cancel", chrome: chrome, size: size,
                           view: { scene in
                               let model = LyricCardFixture.model(scene)
                               model.progress = 0.42
                               model.status = "Drawing the frames…"
                               return LyricCardPopoutView(model: model)
                           }),
        ]
    }
}

/// A song, its lines and an export of the snapshot's own.
@MainActor
private enum LyricCardFixture {
    static let lines = [
        "Static on the radio, gold across the bay",
        "We were never meant to stay",
        "Counting headlights on the overpass",
        "Every summer goes too fast",
        "Hold the line until the morning",
        "Golden hour, no warning",
        "Turn it up and let it play",
    ]

    static let artwork = NSImage(size: NSSize(width: 64, height: 64), flipped: false) { rect in
        NSColor(srgbRed: 0.42, green: 0.27, blue: 0.62, alpha: 1).setFill()
        rect.fill()
        NSColor(srgbRed: 0.93, green: 0.62, blue: 0.36, alpha: 1).setFill()
        NSBezierPath(ovalIn: rect.insetBy(dx: 16, dy: 16)).fill()
        return true
    }

    static func content(rows: [Int], aspect: CardAspect) -> LyricCard.Content {
        let pages = rows.filter { lines.indices.contains($0) }.map { row in
            LyricCard.Page(words: lines[row].split(separator: " ").map(String.init))
        }
        var content = LyricCard.Content(pages: pages, title: "Golden Hour Static", artist: "Lunar Tides",
                                        artwork: artwork, palette: LyricCard.palette(for: artwork), reduceMotion: true)
        content.aspect = aspect
        return content
    }

    static func model(_ scene: UISnapshotScene) -> LyricCardPopoutModel {
        let rows = lines.enumerated().map { LyricCardPopoutModel.Row(id: $0.offset, text: $0.element) }
        let source = LyricCardSource(
            trackKey: { "Golden Hour Static|Lunar Tides" }, title: { "Golden Hour Static" },
            artist: { "Lunar Tides" }, hasSong: { true },
            rows: { rows }, activeRow: { 1 },
            content: { rows, _, aspect in content(rows: rows, aspect: aspect) },
            composition: { nil }, setComposition: { _ in },
            export: { _, _, _, _, _, _ in [] },
            pasteboard: NSPasteboard(name: NSPasteboard.Name("LyricCardSnapshots.\(UUID().uuidString)")),
            defaults: scene.defaults,
            followsTheSong: false)
        let model = LyricCardPopoutModel(source: source)
        model.prepareToShow()
        model.selection = [1, 2, 3]
        return model
    }
}
